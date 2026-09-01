using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using AgentForum.Server.McpTools;
using AgentForum.Server.Persistence;
using AgentForum.Server.Services;
using ModelContextProtocol;

namespace AgentForum.Server.Tests.McpTools;

public sealed class ForumToolsErrorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-forum-tools-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task EveryToolConvertsExpectedValidationFailuresToConciseMcpErrors()
    {
        var tools = CreateTools();

        await AssertConciseError(() => tools.CreatePost("C:\\local\\repo", "title", "content", "main", "abc", null!));
        await AssertConciseError(() => tools.SearchPosts("owner/repo", " "));
        await AssertConciseError(() => tools.ReadPost(0));
        await AssertConciseError(() => tools.CreateComment(0, "content", "main", "abc", null!));
        await AssertConciseError(() => tools.ReadComments(0));
        await AssertConciseError(() => tools.VotePost(1, 0, null!));
        await AssertConciseError(() => tools.VerifyPost(
            1,
            VerificationOutcome.WorkedWithChanges,
            "main",
            "abc",
            null!));
    }

    [Fact]
    public async Task MissingResourceErrorIsConvertedWithoutImplementationDetails()
    {
        var repository = new SqliteForumRepository(new DatabaseOptions { Path = _databasePath });
        var service = CreateService(repository);
        await service.InitializeAsync();
        var tools = new ForumTools(service);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.ReadPost(42));

        Assert.Equal("Forum post 42 does not exist.", exception.Message);
    }

    public void Dispose()
    {
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-journal");
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private ForumTools CreateTools() =>
        new(CreateService(new SqliteForumRepository(new DatabaseOptions { Path = _databasePath })));

    private static ForumService CreateService(IForumRepository repository) =>
        new(
            repository,
            new StubEmbeddingProvider(),
            new EmbeddingOptions { ModelId = "test-model" });

    private static async Task AssertConciseError(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<McpException>(action);
        Assert.DoesNotContain("Parameter", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Actual value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
    }

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f });
    }
}
