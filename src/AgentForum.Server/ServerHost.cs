using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentForum.Server.McpTools;
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

        AgentForumServiceRegistration.AddAgentForumServices(
            builder.Services,
            builder.Configuration,
            configureOverrides);

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
