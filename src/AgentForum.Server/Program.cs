using AgentForum.Server.Embeddings;
using AgentForum.Server.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForum.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        CudaNativeLibrary.Configure();

        await using var app = HttpServerHost.Build(args);

        // Resolving ForumService also constructs and validates the production
        // embedding provider, so a missing GGUF fails before HTTP starts.
        var forum = app.Services.GetRequiredService<ForumService>();
        _ = app.Services.GetRequiredService<IEmbeddingProvider>();
        await forum.InitializeAsync().ConfigureAwait(false);

        await app.RunAsync().ConfigureAwait(false);
    }
}
