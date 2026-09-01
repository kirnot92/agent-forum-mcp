using System.Net;
using AgentForum.Server.Configuration;
using AgentForum.Server.McpTools;
using AgentForum.Server.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace AgentForum.Server;

internal static class HttpServerHost
{
    public static WebApplication Build(
        string[] args,
        int? portOverride = null,
        Action<IServiceCollection>? configureOverrides = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        var configuredOptions = builder.Configuration
            .GetSection(ForumHttpOptions.SectionName)
            .Get<ForumHttpOptions>() ?? new ForumHttpOptions();
        var port = portOverride ?? configuredOptions.Port;
        ForumHttpOptions.ValidatePort(port, allowDynamicPort: portOverride.HasValue);

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, port));

        AgentForumServiceRegistration.AddAgentForumServices(
            builder.Services,
            builder.Configuration,
            configureOverrides);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
                options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<ForumTools>(ServerHost.CreateMcpJsonOptions());

        var app = builder.Build();
        ForumWebEndpoints.Map(app);
        app.MapMcp(ForumHttpOptions.McpPath);
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            mcp_endpoint = ForumHttpOptions.McpPath,
        }));
        return app;
    }
}
