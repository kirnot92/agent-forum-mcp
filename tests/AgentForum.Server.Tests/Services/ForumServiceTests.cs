using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using AgentForum.Server.Persistence;
using AgentForum.Server.Services;

namespace AgentForum.Server.Tests.Services;

public sealed class ForumServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-forum-service-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task CreatePost_EmbedsOnlyTitleBlankLineAndContentBeforePersistence()
    {
        var provider = new RecordingEmbeddingProvider(_ => [3f, 4f]);
        var service = await CreateServiceAsync(provider);

        var post = await service.CreatePostAsync(PostInput("repo-a", "A title", "Observed content"));

        Assert.Equal("A title\n\nObserved content", Assert.Single(provider.Texts));
        var stored = Assert.Single(await CreateRepository().ReadStoredEmbeddingsAsync("repo-a", ModelId));
        Assert.Equal([0.6f, 0.8f], stored.Vector);
        Assert.Equal(post.Id, stored.PostId);
    }

    [Fact]
    public async Task CreatePost_DoesNotPersistAnythingWhenEmbeddingFails()
    {
        var provider = new RecordingEmbeddingProvider(_ => throw new InvalidOperationException("embedding failed"));
        var service = await CreateServiceAsync(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreatePostAsync(PostInput("repo-a", "not stored", "not stored")));

        Assert.Empty(await CreateRepository().ReadStoredEmbeddingsAsync("repo-a", ModelId));
        Assert.Empty(await CreateRepository().SearchLexicalPostIdsAsync("repo-a", "stored", 10));
    }

    [Fact]
    public async Task SearchPosts_CombinesLexicalAndVectorCandidatesWithinRequestedRepo()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["Lexical hit\n\ncontains parser token"] = [0f, 1f],
            ["Semantic hit\n\ndifferent words"] = [1f, 0f],
            ["Other repo\n\nparser token"] = [1f, 0f],
            ["parser"] = [1f, 0f],
        };
        var service = await CreateServiceAsync(new RecordingEmbeddingProvider(text => vectors[text]));
        var lexical = await service.CreatePostAsync(PostInput("repo-a", "Lexical hit", "contains parser token"));
        var semantic = await service.CreatePostAsync(PostInput("repo-a", "Semantic hit", "different words"));
        await service.CreatePostAsync(PostInput("repo-b", "Other repo", "parser token"));

        var results = await service.SearchPostsAsync("repo-a", "parser", 10);

        Assert.Equal([lexical.Id, semantic.Id], results.Select(result => result.PostId));
        Assert.All(results, result => Assert.Equal("repo-a", result.Repo));
    }

    [Fact]
    public async Task SearchPosts_IsDeterministicAndClampsLimit()
    {
        var service = await CreateServiceAsync(new RecordingEmbeddingProvider(_ => [1f]));
        for (var index = 0; index < 3; index++)
        {
            await service.CreatePostAsync(PostInput("repo-a", $"post {index}", "shared"));
        }

        var first = await service.SearchPostsAsync("repo-a", "shared", limit: 2);
        var second = await service.SearchPostsAsync("repo-a", "shared", limit: 2);

        Assert.Equal(first, second);
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public async Task SearchPosts_RejectsStoredVectorWithIncompatibleDimensions()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("repo-a", "post", "content"), [1f], ModelId);
        var service = new ForumService(
            repository,
            new RecordingEmbeddingProvider(_ => [1f, 0f]),
            new EmbeddingOptions { ModelId = ModelId });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.SearchPostsAsync("repo-a", "query"));

        Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1-dimension", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2-dimension", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildOperationsRemainAppendOnlyAndExposeRawSummaries()
    {
        var service = await CreateServiceAsync(new RecordingEmbeddingProvider(_ => [1f]));
        var post = await service.CreatePostAsync(PostInput("repo-a", "title", "content"));

        await service.CreateCommentAsync(new CreateCommentInput(post.Id, "caveat", "main", "def"));
        await service.VotePostAsync(new VotePostInput(post.Id, 1));
        await service.VerifyPostAsync(new VerifyPostInput(
            post.Id,
            VerificationOutcome.WorkedWithChanges,
            "extra flag",
            "main",
            "fed"));

        var read = await service.ReadPostAsync(post.Id);
        var comments = await service.ReadCommentsAsync(post.Id);

        Assert.Equal("title", read.Post.Title);
        Assert.Equal("content", read.Post.Content);
        Assert.Equal(new VoteSummary(1, 0), read.Votes);
        Assert.Equal(new VerificationSummary(0, 1, 0), read.Verifications);
        Assert.Equal("caveat", Assert.Single(comments.Comments).Content);
    }

    private async Task<ForumService> CreateServiceAsync(IEmbeddingProvider provider)
    {
        var repository = CreateRepository();
        var service = new ForumService(
            repository,
            provider,
            new EmbeddingOptions { ModelId = ModelId });
        await service.InitializeAsync();
        return service;
    }

    private SqliteForumRepository CreateRepository() =>
        new(new DatabaseOptions { Path = _databasePath });

    private static CreatePostInput PostInput(string repo, string title, string content) =>
        new(repo, title, content, "main", "abc123", "codex", "model", "high");

    public void Dispose()
    {
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-journal");
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private const string ModelId = "test-embedding-model";

    private sealed class RecordingEmbeddingProvider(Func<string, float[]> embed) : IEmbeddingProvider
    {
        public List<string> Texts { get; } = [];

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Texts.Add(text);
            return Task.FromResult(embed(text));
        }
    }
}
