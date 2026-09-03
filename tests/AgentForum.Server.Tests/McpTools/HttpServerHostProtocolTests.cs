using AgentForum.Server.Configuration;
using AgentForum.Server.Embeddings;
using AgentForum.Server.McpTools;
using AgentForum.Server.Services;
using AgentForum.Server.Tests.Embeddings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentForum.Server.Tests.McpTools;

public sealed class HttpServerHostProtocolTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-forum-http-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task OneHttpServerSharesPostsBetweenTwoMcpClients()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var app = HttpServerHost.Build(
            [],
            portOverride: 0,
            services =>
            {
                services.AddSingleton(new DatabaseOptions { Path = _databasePath });
                services.AddSingleton(new EmbeddingOptions { ModelId = "test/deterministic" });
                services.AddSingleton<IEmbeddingProvider>(
                    new DeterministicFakeEmbeddingProvider());
            });

        await app.Services
            .GetRequiredService<ForumService>()
            .InitializeAsync(timeout.Token);
        await app.StartAsync(timeout.Token);

        try
        {
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single()
                .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
            var endpoint = new Uri(new Uri(address), ForumHttpOptions.McpPath);

            var firstClientTask = CreateClientAsync(endpoint, "client-alpha", timeout.Token);
            var secondClientTask = CreateClientAsync(endpoint, "client-beta", timeout.Token);
            await Task.WhenAll(firstClientTask, secondClientTask);

            await using var firstClient = await firstClientTask;
            await using var secondClient = await secondClientTask;

            Assert.Equal(ToolContract.ServerInstructions, firstClient.ServerInstructions);
            Assert.Equal(ToolContract.ServerInstructions, secondClient.ServerInstructions);

            var firstTools = await firstClient.ListToolsAsync(cancellationToken: timeout.Token);
            var secondTools = await secondClient.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Equal(ToolContract.ToolNames.Order(), firstTools.Select(tool => tool.Name).Order());
            Assert.Equal(ToolContract.ToolNames.Order(), secondTools.Select(tool => tool.Name).Order());

            foreach (var toolName in new[] { "create_post", "create_comment", "vote_post", "verify_post" })
            {
                var properties = Assert.Single(firstTools, tool => tool.Name == toolName)
                    .ProtocolTool.InputSchema.GetProperty("properties");
                Assert.False(properties.TryGetProperty("agent", out _));
                Assert.False(properties.TryGetProperty("model", out _));
                Assert.False(properties.TryGetProperty("effort", out _));
            }

            var createResult = await firstClient.CallToolAsync(
                "create_post",
                new Dictionary<string, object?>
                {
                    ["repo"] = "acme/shared-http",
                    ["title"] = "One process shares forum state",
                    ["content"] = "A second MCP client can read a post created by the first client.",
                    ["branch"] = "main",
                    ["commit"] = "abc1234",
                },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, createResult.IsError);

            var secondCreateResult = await secondClient.CallToolAsync(
                "create_post",
                new Dictionary<string, object?>
                {
                    ["repo"] = "acme/shared-http",
                    ["title"] = "The second client's post",
                    ["content"] = "Its client info must remain separate from the first session.",
                    ["branch"] = "main",
                    ["commit"] = "def5678",
                },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, secondCreateResult.IsError);

            Assert.NotEqual(true, (await firstClient.CallToolAsync(
                "create_comment",
                new Dictionary<string, object?>
                {
                    ["post_id"] = 2,
                    ["content"] = "alpha comment",
                    ["branch"] = "main",
                    ["commit"] = "abc1234",
                },
                cancellationToken: timeout.Token)).IsError);
            Assert.NotEqual(true, (await secondClient.CallToolAsync(
                "create_comment",
                new Dictionary<string, object?>
                {
                    ["post_id"] = 1,
                    ["content"] = "beta comment",
                    ["branch"] = "main",
                    ["commit"] = "def5678",
                },
                cancellationToken: timeout.Token)).IsError);

            Assert.NotEqual(true, (await firstClient.CallToolAsync(
                "vote_post",
                new Dictionary<string, object?> { ["post_id"] = 2, ["value"] = 1 },
                cancellationToken: timeout.Token)).IsError);
            Assert.NotEqual(true, (await secondClient.CallToolAsync(
                "vote_post",
                new Dictionary<string, object?> { ["post_id"] = 1, ["value"] = -1 },
                cancellationToken: timeout.Token)).IsError);

            Assert.NotEqual(true, (await firstClient.CallToolAsync(
                "verify_post",
                new Dictionary<string, object?>
                {
                    ["post_id"] = 2,
                    ["outcome"] = "WorkedAsWritten",
                    ["branch"] = "main",
                    ["commit"] = "abc1234",
                },
                cancellationToken: timeout.Token)).IsError);
            Assert.NotEqual(true, (await secondClient.CallToolAsync(
                "verify_post",
                new Dictionary<string, object?>
                {
                    ["post_id"] = 1,
                    ["outcome"] = "DidNotWork",
                    ["note"] = "beta verification",
                    ["branch"] = "main",
                    ["commit"] = "def5678",
                },
                cancellationToken: timeout.Token)).IsError);

            var readResult = await secondClient.CallToolAsync(
                "read_post",
                new Dictionary<string, object?> { ["post_id"] = 1 },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, readResult.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(readResult.Content));
            Assert.Contains("One process shares forum state", content.Text);

            // Only the comment on post 1 contains both query terms in one text, so
            // the wire response must attribute the hit to the comment while post 2
            // arrives through the vector channel with an empty source list.
            var searchResult = await secondClient.CallToolAsync(
                "search_posts",
                new Dictionary<string, object?>
                {
                    ["repo"] = "acme/shared-http",
                    ["query"] = "beta comment",
                },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, searchResult.IsError);
            var searchContent = Assert.IsType<TextContentBlock>(Assert.Single(searchResult.Content));
            Assert.Contains("\"lexical_match_types\":[\"Comment\"]", searchContent.Text, StringComparison.Ordinal);
            Assert.Contains("\"lexical_match_types\":[]", searchContent.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"lexical_match\"", searchContent.Text, StringComparison.Ordinal);

            var service = app.Services.GetRequiredService<ForumService>();
            var firstPost = await service.ReadPostAsync(1, timeout.Token);
            var secondPost = await service.ReadPostAsync(2, timeout.Token);
            Assert.Equal("client-alpha", firstPost.Post.Agent);
            Assert.Equal("client-beta", secondPost.Post.Agent);
            Assert.Equal("client-beta", Assert.Single(firstPost.RecentComments).Agent);
            Assert.Equal("client-alpha", Assert.Single(secondPost.RecentComments).Agent);
            Assert.Equal("client-beta", Assert.Single(firstPost.RecentVerifications).Agent);
            Assert.Equal("client-alpha", Assert.Single(secondPost.RecentVerifications).Agent);

            await using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT post_id, agent FROM votes ORDER BY post_id;";
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            Assert.True(await reader.ReadAsync(timeout.Token));
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal("client-beta", reader.GetString(1));
            Assert.True(await reader.ReadAsync(timeout.Token));
            Assert.Equal(2, reader.GetInt64(0));
            Assert.Equal("client-alpha", reader.GetString(1));
            Assert.False(await reader.ReadAsync(timeout.Token));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
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

    private static async Task<McpClient> CreateClientAsync(
        Uri endpoint,
        string clientName,
        CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            NullLoggerFactory.Instance);

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = clientName, Version = "test" },
            },
            loggerFactory: NullLoggerFactory.Instance,
            cancellationToken: cancellationToken);
    }
}
