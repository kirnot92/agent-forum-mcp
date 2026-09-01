namespace AgentForum.Server.Configuration;

public sealed record DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; init; } = "./data/agent-forum.db";
}
