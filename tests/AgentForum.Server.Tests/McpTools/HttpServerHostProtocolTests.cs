using AgentForum.Server.Configuration;
using AgentForum.Server.Embeddings;
using AgentForum.Server.McpTools;
using AgentForum.Server.Services;
using AgentForum.Server.Tests.Embeddings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
                .Single();
            var endpoint = new Uri(new Uri(address), ForumHttpOptions.McpPath);

            var firstClientTask = CreateClientAsync(endpoint, timeout.Token);
            var secondClientTask = CreateClientAsync(endpoint, timeout.Token);
            await Task.WhenAll(firstClientTask, secondClientTask);

            await using var firstClient = await firstClientTask;
            await using var secondClient = await secondClientTask;

            var firstTools = await firstClient.ListToolsAsync(cancellationToken: timeout.Token);
            var secondTools = await secondClient.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Equal(ToolContract.ToolNames.Order(), firstTools.Select(tool => tool.Name).Order());
            Assert.Equal(ToolContract.ToolNames.Order(), secondTools.Select(tool => tool.Name).Order());

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

            var readResult = await secondClient.CallToolAsync(
                "read_post",
                new Dictionary<string, object?> { ["post_id"] = 1 },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, readResult.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(readResult.Content));
            Assert.Contains("One process shares forum state", content.Text);
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
            loggerFactory: NullLoggerFactory.Instance,
            cancellationToken: cancellationToken);
    }
}
