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
    public async Task Initialize_WithNoStoredEmbeddingModelIds_Continues()
    {
        var provider = new RecordingEmbeddingProvider(_ => [1f]);
        var repository = CreateRepository();
        var service = CreateService(repository, provider);

        await service.InitializeAsync();

        Assert.Empty(provider.Texts);
    }

    [Fact]
    public async Task Initialize_WithMatchingEmbeddingModelId_Continues()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("repo-a", "post", "content"), [1f], ModelId);
        var provider = new RecordingEmbeddingProvider(_ => [1f]);
        var service = CreateService(repository, provider);

        await service.InitializeAsync();

        Assert.Empty(provider.Texts);
    }

    [Fact]
    public async Task Initialize_WithDifferentEmbeddingModelId_FailsClearly()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("repo-a", "post", "content"), [1f], "previous-model");
        var service = CreateService(repository, new RecordingEmbeddingProvider(_ => [1f]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InitializeAsync());

        Assert.Contains("previous-model", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModelId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("original embedding model", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild/reindex", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initialize_WithMixedEmbeddingModelIds_FailsClearly()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("repo-a", "matching", "content"), [1f], ModelId);
        await repository.CreatePostAsync(PostInput("repo-a", "different", "content"), [1f], "previous-model");
        var service = CreateService(repository, new RecordingEmbeddingProvider(_ => [1f]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InitializeAsync());

        Assert.Contains(ModelId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("previous-model", exception.Message, StringComparison.Ordinal);
    }

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
    public async Task CreateAndSearch_NormalizeEquivalentGitHubRepositoryForms()
    {
        var provider = new RecordingEmbeddingProvider(_ => [1f]);
        var service = await CreateServiceAsync(provider);

        var post = await service.CreatePostAsync(
            PostInput(" git@github.com:Owner/Repo.git ", "canonical", "repository key"));
        var results = await service.SearchPostsAsync(
            "https://github.com/OWNER/REPO/",
            "repository");

        Assert.Equal("owner/repo", post.Repo);
        Assert.Equal(post.Id, Assert.Single(results).PostId);
        Assert.Equal("owner/repo", results[0].Repo);
        Assert.Equal(
            "canonical\n\nrepository key",
            provider.Texts[0]);
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
    public async Task BrowsePosts_normalizes_repository_scope_without_embedding()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var expected = await repository.CreatePostAsync(
            PostInput("owner/repo", "expected", "body"),
            [1f],
            ModelId);
        await repository.CreatePostAsync(PostInput("owner/other", "other", "body"), [1f], ModelId);
        var provider = new RecordingEmbeddingProvider(_ => throw new InvalidOperationException("must not embed"));
        var service = CreateService(repository, provider);

        var result = Assert.Single(await service.BrowsePostsAsync("https://github.com/OWNER/REPO.git"));

        Assert.Equal(expected.Id, result.PostId);
        Assert.Equal("owner/repo", result.Repo);
        Assert.Empty(provider.Texts);
    }

    [Fact]
    public async Task BrowsePosts_null_repo_reads_all_repositories_and_clamps_limit_to_fifty()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        for (var index = 0; index < 55; index++)
        {
            var repo = index % 2 == 0 ? "owner/one" : "owner/two";
            await repository.CreatePostAsync(PostInput(repo, $"post {index}", "body"), [1f], ModelId);
        }

        var provider = new RecordingEmbeddingProvider(_ => throw new InvalidOperationException("must not embed"));
        var service = CreateService(repository, provider);

        var results = await service.BrowsePostsAsync(null, int.MaxValue);

        Assert.Equal(50, results.Count);
        Assert.Contains(results, result => result.Repo == "owner/one");
        Assert.Contains(results, result => result.Repo == "owner/two");
        Assert.Equal(Enumerable.Range(6, 50).Reverse().Select(index => (long)index), results.Select(result => result.PostId));
        Assert.Empty(provider.Texts);
    }

    [Fact]
    public async Task BrowsePosts_rejects_non_positive_limit()
    {
        var provider = new RecordingEmbeddingProvider(_ => [1f]);
        var service = await CreateServiceAsync(provider);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.BrowsePostsAsync(null, 0));

        Assert.Empty(provider.Texts);
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
        var service = CreateService(repository, provider);
        await service.InitializeAsync();
        return service;
    }

    private static ForumService CreateService(
        IForumRepository repository,
        IEmbeddingProvider provider) =>
        new(
            repository,
            provider,
            new EmbeddingOptions { ModelId = ModelId });

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
