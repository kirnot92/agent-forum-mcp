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
    private readonly IVectorSearchIndex _vectorSearchIndex;
    private readonly string _embeddingModelId;
    private readonly TimeProvider _timeProvider;

    public ForumService(
        IForumRepository repository,
        IEmbeddingProvider embeddingProvider,
        IVectorSearchIndex vectorSearchIndex,
        EmbeddingOptions embeddingOptions,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _vectorSearchIndex = vectorSearchIndex ?? throw new ArgumentNullException(nameof(vectorSearchIndex));
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
        await _vectorSearchIndex.InitializeAsync(cancellationToken).ConfigureAwait(false);
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

        var post = await _repository
            .CreatePostAsync(normalizedInput, normalizedEmbedding, _embeddingModelId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // The database is already committed. Do not honor request cancellation
            // while making the corresponding in-memory entry visible.
            _vectorSearchIndex.Add(post.Repo, post.Id, normalizedEmbedding);
        }
        catch (Exception exception)
        {
            _vectorSearchIndex.MarkStale(exception);
            throw new InvalidOperationException(
                "The post was stored, but the in-memory vector index could not be updated and must be rebuilt.",
                exception);
        }

        return post;
    }

    public Task<IReadOnlyList<PostSearchResult>> SearchPostsAsync(
        string repo,
        string query,
        int limit = ForumLimits.DefaultSearchLimit,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.ValidateRepo(repo);
        var normalizedRepo = RepositoryKey.Normalize(repo);

        return SearchPostsCoreAsync(normalizedRepo, query, limit, cancellationToken);
    }

    public Task<IReadOnlyList<PostSearchResult>> SearchPostsAsync(
        string query,
        int limit = ForumLimits.DefaultSearchLimit,
        CancellationToken cancellationToken = default) =>
        SearchPostsCoreAsync(null, query, limit, cancellationToken);

    private async Task<IReadOnlyList<PostSearchResult>> SearchPostsCoreAsync(
        string? repo,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ForumValidation.ValidateSearchQuery(query);
        var clampedLimit = ForumValidation.ClampSearchLimit(limit);

        // Only the query side receives the retrieval instruction; stored post
        // vectors are embedded from plain title and content.
        var queryEmbedding = await _embeddingProvider
            .EmbedAsync(QueryEmbeddingText.Compose(query), cancellationToken)
            .ConfigureAwait(false);
        var normalizedQueryEmbedding = VectorMath.Normalize(queryEmbedding);

        var lexicalTask = _repository.SearchLexicalPostIdsAsync(
            repo,
            query,
            HybridSearchRanker.CandidateLimit,
            cancellationToken);
        var vectorHits = _vectorSearchIndex.Search(
            repo,
            normalizedQueryEmbedding,
            HybridSearchRanker.CandidateLimit,
            cancellationToken);
        var vectorIds = vectorHits.Select(hit => hit.PostId).ToArray();
        var similarityById = vectorHits.ToDictionary(hit => hit.PostId, hit => hit.Similarity);

        var lexicalIds = await lexicalTask.ConfigureAwait(false);
        var lexicalIdSet = lexicalIds.ToHashSet();

        var candidateIds = lexicalIds.Concat(vectorIds).Distinct().ToArray();
        if (candidateIds.Length == 0)
        {
            return Array.Empty<PostSearchResult>();
        }

        // Lexical-only candidates fell outside the bounded vector ranking, so
        // their similarity is computed directly to give every result the same
        // retrieval information.
        var lexicalOnlyIds = lexicalIds.Where(id => !similarityById.ContainsKey(id)).ToArray();
        if (lexicalOnlyIds.Length > 0)
        {
            foreach (var (postId, similarity) in _vectorSearchIndex.ComputeSimilarities(
                lexicalOnlyIds,
                normalizedQueryEmbedding,
                cancellationToken))
            {
                similarityById[postId] = similarity;
            }
        }

        var candidates = await _repository
            .ReadSearchResultsAsync(repo, candidateIds, cancellationToken)
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
            .Select(item => candidatesById[item.PostId] with
            {
                LexicalMatch = lexicalIdSet.Contains(item.PostId),
                VectorSimilarity = similarityById.TryGetValue(item.PostId, out var similarity)
                    ? similarity
                    : null,
            })
            .ToArray();
    }

    public Task<ReadPostResult> ReadPostAsync(
        long postId,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.ValidatePostId(postId);
        return _repository.ReadPostAsync(postId, cancellationToken);
    }

    public Task<IReadOnlyList<PostSearchResult>> BrowsePostsAsync(
        string? repo,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The browse result limit must be positive.");
        }

        var normalizedRepo = repo;
        if (repo is not null)
        {
            ForumValidation.ValidateRepo(repo);
            normalizedRepo = RepositoryKey.Normalize(repo);
        }

        return _repository.ReadRecentPostsAsync(
            normalizedRepo,
            Math.Min(limit, ForumLimits.MaxSearchLimit),
            cancellationToken);
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

}
