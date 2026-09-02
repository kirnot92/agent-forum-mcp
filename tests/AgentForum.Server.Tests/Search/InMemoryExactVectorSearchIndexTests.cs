using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Persistence;
using AgentForum.Server.Search;

namespace AgentForum.Server.Tests.Search;

public sealed class InMemoryExactVectorSearchIndexTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-forum-vector-index-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Initialize_IsIdempotentAndSearchesRestoredVectorsWithoutFurtherDatabaseReads()
    {
        var inner = CreateRepository();
        await inner.InitializeAsync();
        var first = await inner.CreatePostAsync(PostInput("owner/one", "first"), [1f, 0f], ModelId);
        var second = await inner.CreatePostAsync(PostInput("owner/two", "second"), [0f, 1f], ModelId);
        var repository = new CountingRepository(inner);
        using var index = CreateIndex(repository);

        await index.InitializeAsync();
        await index.InitializeAsync();

        Assert.Equal(1, repository.EmbeddingReadCount);
        Assert.Equal([first.Id], PostIds(index.Search("git@github.com:OWNER/ONE.git", [1f, 0f], 50)));
        Assert.Equal([second.Id, first.Id], PostIds(index.Search(null, [0f, 1f], 50)));
        Assert.Equal([first.Id], PostIds(index.Search("owner/one", [1f, 0f], 50)));
        Assert.Equal(1, repository.EmbeddingReadCount);
    }

    [Fact]
    public async Task Search_UsesBoundedTopKAndDeterministicTieOrdering()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        for (var index = 0; index < 55; index++)
        {
            await repository.CreatePostAsync(PostInput("owner/repo", $"post-{index}"), [1f], ModelId);
        }

        using var vectorIndex = CreateIndex(repository);
        await vectorIndex.InitializeAsync();

        Assert.Equal(
            Enumerable.Range(1, 50).Select(value => (long)value),
            PostIds(vectorIndex.Search("owner/repo", [1f], 50)));
    }

    [Fact]
    public async Task Search_ReportsSimilarityAndComputeSimilaritiesCoversLexicalOnlyCandidates()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var aligned = await repository.CreatePostAsync(PostInput("owner/repo", "aligned"), [1f, 0f], ModelId);
        var orthogonal = await repository.CreatePostAsync(PostInput("owner/repo", "orthogonal"), [0f, 1f], ModelId);
        var opposite = await repository.CreatePostAsync(PostInput("owner/repo", "opposite"), [-1f, 0f], ModelId);
        using var index = CreateIndex(repository);
        await index.InitializeAsync();

        var hits = index.Search("owner/repo", [1f, 0f], 2);
        Assert.Equal([aligned.Id, orthogonal.Id], PostIds(hits));
        Assert.Equal(1d, hits[0].Similarity, 6);
        Assert.Equal(0d, hits[1].Similarity, 6);

        var similarities = index.ComputeSimilarities([opposite.Id, aligned.Id, 999L], [1f, 0f]);
        Assert.Equal(2, similarities.Count);
        Assert.Equal(-1d, similarities[opposite.Id], 6);
        Assert.Equal(1d, similarities[aligned.Id], 6);
        Assert.False(similarities.ContainsKey(999L));
        Assert.Empty(index.ComputeSimilarities([], [1f, 0f]));
    }

    [Fact]
    public async Task Add_CopiesCallerMemoryAndMakesPostImmediatelyVisible()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        using var index = CreateIndex(repository);
        await index.InitializeAsync();
        var callerOwned = new[] { 1f, 0f };

        index.Add("owner/repo", 1, callerOwned);
        callerOwned[0] = -1f;
        index.Add("owner/repo", 2, [0f, 1f]);

        Assert.Equal([1L, 2L], PostIds(index.Search("owner/repo", [1f, 0f], 2)));
    }

    private static IEnumerable<long> PostIds(IReadOnlyList<VectorSearchHit> hits) =>
        hits.Select(hit => hit.PostId);

    [Fact]
    public async Task Search_RejectsDimensionMismatchAndCancellation()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("owner/repo", "post"), [1f], ModelId);
        using var index = CreateIndex(repository);
        await index.InitializeAsync();

        var exception = Assert.Throws<InvalidDataException>(
            () => index.Search("owner/repo", [1f, 0f], 50));
        Assert.Contains("1-dimension", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2-dimension", exception.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => index.Search("owner/repo", [1f], 50, cancellation.Token));
    }

    [Fact]
    public async Task MarkStale_PreventsSearchUntilACompleteRebuildPublishes()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        using var index = CreateIndex(repository);
        await index.InitializeAsync();
        var cause = new InvalidOperationException("add failed");

        index.MarkStale(cause);

        var stale = Assert.Throws<InvalidOperationException>(() => index.Search(null, [1f], 50));
        Assert.Same(cause, stale.InnerException);

        await index.InitializeAsync();
        Assert.Empty(index.Search(null, [1f], 50));
    }

    [Fact]
    public async Task ConcurrentSearchAndAdd_AreSafeAndPublishAllCompletedAdds()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        using var index = CreateIndex(repository);
        await index.InitializeAsync();

        var searches = Enumerable.Range(0, 8)
            .Select(taskIndex => Task.Run(() =>
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    index.Search("owner/repo", [1f], 50);
                }
            }))
            .ToArray();
        var adds = Task.Run(() =>
        {
            for (var postId = 1; postId <= 100; postId++)
            {
                index.Add("owner/repo", postId, [1f]);
            }
        });

        await Task.WhenAll(searches.Append(adds));

        Assert.Equal(
            Enumerable.Range(1, 50).Select(value => (long)value),
            PostIds(index.Search("owner/repo", [1f], 50)));
    }

    private InMemoryExactVectorSearchIndex CreateIndex(IForumRepository repository) =>
        new(repository, new EmbeddingOptions { ModelId = ModelId });

    private SqliteForumRepository CreateRepository() =>
        new(new DatabaseOptions { Path = _databasePath });

    private static CreatePostInput PostInput(string repo, string title) =>
        new(repo, title, "content", "main", "abc123", "codex");

    public void Dispose()
    {
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-journal");
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private const string ModelId = "test-embedding-model";

    private sealed class CountingRepository(IForumRepository inner) : IForumRepository
    {
        public int EmbeddingReadCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<Post> CreatePostAsync(
            CreatePostInput input,
            float[] normalizedEmbedding,
            string modelId,
            CancellationToken cancellationToken = default) =>
            inner.CreatePostAsync(input, normalizedEmbedding, modelId, cancellationToken);

        public Task<ReadPostResult> ReadPostAsync(long postId, CancellationToken cancellationToken = default) =>
            inner.ReadPostAsync(postId, cancellationToken);

        public Task<Comment> CreateCommentAsync(
            CreateCommentInput input,
            CancellationToken cancellationToken = default) =>
            inner.CreateCommentAsync(input, cancellationToken);

        public Task<ReadCommentsResult> ReadCommentsAsync(
            long postId,
            int limit,
            int offset,
            CancellationToken cancellationToken = default) =>
            inner.ReadCommentsAsync(postId, limit, offset, cancellationToken);

        public Task<Vote> AddVoteAsync(VotePostInput input, CancellationToken cancellationToken = default) =>
            inner.AddVoteAsync(input, cancellationToken);

        public Task<Verification> AddVerificationAsync(
            VerifyPostInput input,
            CancellationToken cancellationToken = default) =>
            inner.AddVerificationAsync(input, cancellationToken);

        public Task<IReadOnlyList<long>> SearchLexicalPostIdsAsync(
            string? repo,
            string query,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.SearchLexicalPostIdsAsync(repo, query, limit, cancellationToken);

        public Task<IReadOnlyList<StoredPostEmbedding>> ReadAllStoredEmbeddingsAsync(
            string modelId,
            CancellationToken cancellationToken = default)
        {
            EmbeddingReadCount++;
            return inner.ReadAllStoredEmbeddingsAsync(modelId, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ReadDistinctEmbeddingModelIdsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadDistinctEmbeddingModelIdsAsync(cancellationToken);

        public Task<IReadOnlyList<PostSearchResult>> ReadRecentPostsAsync(
            string? repo,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadRecentPostsAsync(repo, limit, cancellationToken);

        public Task<IReadOnlyList<PostSearchResult>> ReadSearchResultsAsync(
            string? repo,
            IReadOnlyCollection<long> postIds,
            CancellationToken cancellationToken = default) =>
            inner.ReadSearchResultsAsync(repo, postIds, cancellationToken);
    }
}
