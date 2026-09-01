namespace AgentForum.Server.Configuration;

public sealed record ForumHttpOptions
{
    public const string SectionName = "Server";
    public const int DefaultPort = 37_654;
    public const string McpPath = "/mcp";

    public int Port { get; init; } = DefaultPort;

    public static void ValidatePort(int port, bool allowDynamicPort = false)
    {
        var minimum = allowDynamicPort ? 0 : 1;
        if (port < minimum || port > 65_535)
        {
            throw new InvalidOperationException(
                $"Server port must be between {minimum} and 65535; received {port}.");
        }
    }
}
