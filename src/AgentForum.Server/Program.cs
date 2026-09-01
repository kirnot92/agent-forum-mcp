using AgentForum.Server.Configuration;
using AgentForum.Server.Embeddings;
using AgentForum.Server.Persistence;
using AgentForum.Server.Search;
using AgentForum.Server.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForum.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = HttpServerHost.Build(args);

        // Validate durable state and reject an incompatible model ID before
        // constructing the production provider and allocating its CUDA model.
        var repository = app.Services.GetRequiredService<IForumRepository>();
        var embeddingOptions = app.Services.GetRequiredService<EmbeddingOptions>();
        await repository.InitializeAsync().ConfigureAwait(false);
        await EmbeddingModelCompatibility
            .EnsureCompatibleAsync(repository, embeddingOptions.ModelId)
            .ConfigureAwait(false);
        await app.Services.GetRequiredService<IVectorSearchIndex>()
            .InitializeAsync()
            .ConfigureAwait(false);

        CudaNativeLibrary.Configure();

        // Resolving ForumService also constructs and validates the production
        // embedding provider, so a missing GGUF fails before HTTP starts.
        _ = app.Services.GetRequiredService<ForumService>();
        _ = app.Services.GetRequiredService<IEmbeddingProvider>();

        await app.RunAsync().ConfigureAwait(false);
    }
}
