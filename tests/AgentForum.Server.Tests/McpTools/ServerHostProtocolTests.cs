using System.IO.Pipelines;
using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using AgentForum.Server.McpTools;
using AgentForum.Server.Services;
using AgentForum.Server.Tests.Embeddings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentForum.Server.Tests.McpTools;

public sealed class ServerHostProtocolTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-forum-mcp-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task StreamTransportExposesSevenToolsAndExecutesCreateAndRepoScopedSearch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverInput = clientToServer.Reader.AsStream();
        await using var serverOutput = serverToClient.Writer.AsStream();
        await using var clientInput = clientToServer.Writer.AsStream();
        await using var clientOutput = serverToClient.Reader.AsStream();

        using var host = ServerHost.Build(
            [],
            serverInput,
            serverOutput,
            services =>
            {
                services.AddSingleton(new DatabaseOptions { Path = _databasePath });
                services.AddSingleton(new EmbeddingOptions { ModelId = "test/deterministic" });
                services.AddSingleton<IEmbeddingProvider>(
                    new DeterministicFakeEmbeddingProvider());
            });

        await host.Services
            .GetRequiredService<ForumService>()
            .InitializeAsync(timeout.Token);
        await host.StartAsync(timeout.Token);

        try
        {
            var transport = new StreamClientTransport(
                clientInput,
                clientOutput,
                NullLoggerFactory.Instance);

            await using var client = await McpClient.CreateAsync(
                transport,
                loggerFactory: NullLoggerFactory.Instance,
                cancellationToken: timeout.Token);

            Assert.Equal(ToolContract.ServerInstructions, client.ServerInstructions);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

            Assert.Equal(ToolContract.ToolNames.Order(), tools.Select(tool => tool.Name).Order());
            var createPost = Assert.Single(tools, tool => tool.Name == "create_post");
            Assert.Equal(ToolContract.CreatePostDescription, createPost.Description);

            var createSchema = createPost.ProtocolTool.InputSchema;
            Assert.Equal(
                ToolContract.RepoDescription,
                createSchema.GetProperty("properties")
                    .GetProperty("repo")
                    .GetProperty("description")
                    .GetString());
            Assert.Equal(
                ForumLimits.MaxPostContentLength,
                createSchema.GetProperty("properties")
                    .GetProperty("content")
                    .GetProperty("maxLength")
                    .GetInt32());

            var oversizedResult = await client.CallToolAsync(
                "create_post",
                new Dictionary<string, object?>
                {
                    ["repo"] = "acme/widgets",
                    ["title"] = "Too long",
                    ["content"] = new string('x', ForumLimits.MaxPostContentLength + 1),
                    ["branch"] = "main",
                    ["commit"] = "abc1234",
                },
                cancellationToken: timeout.Token);
            Assert.True(oversizedResult.IsError);
            var error = Assert.IsType<TextContentBlock>(Assert.Single(oversizedResult.Content));
            Assert.Contains("Content cannot exceed 3000 characters", error.Text);
            Assert.Contains("received 3001 characters", error.Text);

            var createResult = await client.CallToolAsync(
                "create_post",
                new Dictionary<string, object?>
                {
                    ["repo"] = "acme/widgets",
                    ["title"] = "FTS trigger rebuild order",
                    ["content"] = "Create the content table before the external-content FTS table and its triggers.",
                    ["branch"] = "main",
                    ["commit"] = "abc1234",
                },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, createResult.IsError);
            Assert.NotEmpty(createResult.Content);

            var searchResult = await client.CallToolAsync(
                "search_posts",
                new Dictionary<string, object?>
                {
                    ["repo"] = "acme/widgets",
                    ["query"] = "FTS trigger order",
                    ["limit"] = 10,
                },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, searchResult.IsError);
            Assert.NotEmpty(searchResult.Content);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
