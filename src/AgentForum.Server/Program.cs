using AgentForum.Server.Embeddings;
using AgentForum.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentForum.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = ServerHost.Build(args);

        // Resolving ForumService also constructs and validates the production
        // embedding provider, so a missing GGUF fails before stdio starts.
        var forum = host.Services.GetRequiredService<ForumService>();
        _ = host.Services.GetRequiredService<IEmbeddingProvider>();
        await forum.InitializeAsync().ConfigureAwait(false);

        await host.RunAsync().ConfigureAwait(false);
    }
}
