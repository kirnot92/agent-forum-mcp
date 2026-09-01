using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentForum.Server.Configuration;
using AgentForum.Server.Embeddings;
using AgentForum.Server.McpTools;
using AgentForum.Server.Persistence;
using AgentForum.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentForum.Server;

internal static class ServerHost
{
    public static IHost Build(
        string[] args,
        Stream? inputStream = null,
        Stream? outputStream = null,
        Action<IServiceCollection>? configureOverrides = null)
    {
        if ((inputStream is null) != (outputStream is null))
        {
            throw new ArgumentException("Input and output streams must be provided together.");
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        var databaseOptions = builder.Configuration
            .GetSection(DatabaseOptions.SectionName)
            .Get<DatabaseOptions>() ?? new DatabaseOptions();
        var embeddingOptions = builder.Configuration
            .GetSection(EmbeddingOptions.SectionName)
            .Get<EmbeddingOptions>() ?? new EmbeddingOptions();

        builder.Services.AddSingleton(databaseOptions);
        builder.Services.AddSingleton(embeddingOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SqliteForumRepository>();
        builder.Services.AddSingleton<IForumRepository>(services =>
            services.GetRequiredService<SqliteForumRepository>());
        builder.Services.AddSingleton<IEmbeddingProvider, LlamaSharpQwenEmbeddingProvider>();
        builder.Services.AddSingleton<ForumService>();

        configureOverrides?.Invoke(builder.Services);

        var mcpBuilder = builder.Services.AddMcpServer();
        if (inputStream is null)
        {
            mcpBuilder.WithStdioServerTransport();
        }
        else
        {
            mcpBuilder.WithStreamServerTransport(inputStream, outputStream!);
        }

        mcpBuilder.WithTools<ForumTools>(CreateMcpJsonOptions());
        return builder.Build();
    }

    internal static JsonSerializerOptions CreateMcpJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
