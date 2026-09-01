using AgentForum.Server.Configuration;
using AgentForum.Server.Embeddings;
using AgentForum.Server.Persistence;
using AgentForum.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForum.Server;

internal static class AgentForumServiceRegistration
{
    public static void AddAgentForumServices(
        IServiceCollection services,
        IConfiguration configuration,
        Action<IServiceCollection>? configureOverrides = null)
    {
        var databaseOptions = configuration
            .GetSection(DatabaseOptions.SectionName)
            .Get<DatabaseOptions>() ?? new DatabaseOptions();
        var embeddingOptions = configuration
            .GetSection(EmbeddingOptions.SectionName)
            .Get<EmbeddingOptions>() ?? new EmbeddingOptions();

        services.AddSingleton(databaseOptions);
        services.AddSingleton(embeddingOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SqliteForumRepository>();
        services.AddSingleton<IForumRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteForumRepository>());
        services.AddSingleton<IEmbeddingProvider, LlamaSharpQwenEmbeddingProvider>();
        services.AddSingleton<ForumService>();

        configureOverrides?.Invoke(services);
    }
}
