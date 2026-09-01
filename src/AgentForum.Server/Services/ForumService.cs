using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using AgentForum.Server.Persistence;
using AgentForum.Server.Search;

namespace AgentForum.Server.Services;

public sealed class ForumService
{
    private readonly IForumRepository _repository;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly string _embeddingModelId;
    private readonly TimeProvider _timeProvider;

    public ForumService(
        IForumRepository repository,
        IEmbeddingProvider embeddingProvider,
        EmbeddingOptions embeddingOptions,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        ArgumentNullException.ThrowIfNull(embeddingOptions);

        if (string.IsNullOrWhiteSpace(embeddingOptions.ModelId))
        {
            throw new ArgumentException("A non-empty embedding model ID is required.", nameof(embeddingOptions));
        }

        _embeddingModelId = embeddingOptions.ModelId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EmbeddingModelCompatibility
            .EnsureCompatibleAsync(_repository, _embeddingModelId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Post> CreatePostAsync(
        CreatePostInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        var normalizedInput = input with { Repo = RepositoryKey.Normalize(input.Repo) };

        var embedding = await _embeddingProvider
            .EmbedAsync(PostEmbeddingText.Compose(input.Title, input.Content), cancellationToken)
            .ConfigureAwait(false);
        var normalizedEmbedding = VectorMath.Normalize(embedding);

        return await _repository
            .CreatePostAsync(normalizedInput, normalizedEmbedding, _embeddingModelId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PostSearchResult>> SearchPostsAsync(
        string repo,
        string query,
        int limit = ForumLimits.DefaultSearchLimit,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.ValidateRepo(repo);
        ForumValidation.ValidateSearchQuery(query);
        var normalizedRepo = RepositoryKey.Normalize(repo);
        var clampedLimit = ForumValidation.ClampSearchLimit(limit);

        var queryEmbedding = await _embeddingProvider
            .EmbedAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var normalizedQueryEmbedding = VectorMath.Normalize(queryEmbedding);

        var lexicalTask = _repository.SearchLexicalPostIdsAsync(
            normalizedRepo,
            query,
            HybridSearchRanker.CandidateLimit,
            cancellationToken);
        var embeddingsTask = _repository.ReadStoredEmbeddingsAsync(
            normalizedRepo,
            _embeddingModelId,
            cancellationToken);

        await Task.WhenAll(lexicalTask, embeddingsTask).ConfigureAwait(false);

        var lexicalIds = await lexicalTask.ConfigureAwait(false);
        var vectorIds = RankVectorCandidates(
            normalizedQueryEmbedding,
            await embeddingsTask.ConfigureAwait(false));

        var candidateIds = lexicalIds.Concat(vectorIds).Distinct().ToArray();
        if (candidateIds.Length == 0)
        {
            return Array.Empty<PostSearchResult>();
        }

        var candidates = await _repository
            .ReadSearchResultsAsync(normalizedRepo, candidateIds, cancellationToken)
            .ConfigureAwait(false);
        var candidatesById = candidates.ToDictionary(candidate => candidate.PostId);
        var signals = candidates.ToDictionary(
            candidate => candidate.PostId,
            candidate => new RankingSignals(
                candidate.Upvotes,
                candidate.Downvotes,
                candidate.WorkedAsWrittenCount,
                candidate.WorkedWithChangesCount,
                candidate.DidNotWorkCount,
                candidate.LastActivityAt));

        var ranking = HybridSearchRanker.Fuse(
            lexicalIds,
            vectorIds,
            signals,
            _timeProvider.GetUtcNow(),
            clampedLimit);

        return ranking
            .Where(item => candidatesById.ContainsKey(item.PostId))
            .Select(item => candidatesById[item.PostId])
            .ToArray();
    }

    public Task<ReadPostResult> ReadPostAsync(
        long postId,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.ValidatePostId(postId);
        return _repository.ReadPostAsync(postId, cancellationToken);
    }

    public Task<Comment> CreateCommentAsync(
        CreateCommentInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        return _repository.CreateCommentAsync(input, cancellationToken);
    }

    public Task<ReadCommentsResult> ReadCommentsAsync(
        long postId,
        int limit = ForumLimits.DefaultCommentLimit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.ValidatePostId(postId);
        ForumValidation.ValidateCommentOffset(offset);
        return _repository.ReadCommentsAsync(
            postId,
            ForumValidation.ClampCommentLimit(limit),
            offset,
            cancellationToken);
    }

    public Task<Vote> VotePostAsync(
        VotePostInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        return _repository.AddVoteAsync(input, cancellationToken);
    }

    public Task<Verification> VerifyPostAsync(
        VerifyPostInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        return _repository.AddVerificationAsync(input, cancellationToken);
    }

    private static IReadOnlyList<long> RankVectorCandidates(
        float[] queryEmbedding,
        IReadOnlyList<StoredPostEmbedding> storedEmbeddings)
    {
        foreach (var stored in storedEmbeddings)
        {
            if (stored.Dimensions != queryEmbedding.Length || stored.Vector.Length != stored.Dimensions)
            {
                throw new InvalidDataException(
                    $"Post {stored.PostId} has an incompatible {FormatDimensions(stored.Dimensions)} embedding; " +
                    $"the configured model produced {FormatDimensions(queryEmbedding.Length)}.");
            }
        }

        return storedEmbeddings
            .Select(stored => new
            {
                stored.PostId,
                Similarity = VectorMath.CosineSimilarity(queryEmbedding, stored.Vector),
            })
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.PostId)
            .Take(HybridSearchRanker.CandidateLimit)
            .Select(candidate => candidate.PostId)
            .ToArray();
    }

    private static string FormatDimensions(int dimensions) => $"{dimensions}-dimension";
}
