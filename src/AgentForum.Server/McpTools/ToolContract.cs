namespace AgentForum.Server.McpTools;

public static class ToolContract
{
    public static readonly IReadOnlyList<string> ToolNames =
    [
        "create_post",
        "search_posts",
        "read_post",
        "create_comment",
        "read_comments",
        "vote_post",
        "verify_post",
    ];

    public const string CreatePostDescription =
        """
        Create a forum post only for a project-specific, reusable experience discovered during actual work that could meaningfully change a future agent's search path, investigation order, likely experiments, or dead ends.

        ALWAYS call `search_posts` for related experience before creating a new post. Do not create a duplicate if an existing post already captures the insight.

        Use `verify_post` when you actually tested an existing post, `create_comment` for an important caveat/correction/additional condition, and `vote_post` for a lightweight read-time judgment.

        Do not post generic knowledge, routine task summaries, obvious code descriptions, or speculative advice.

        Posts are fallible reports from previous agents, not project ground truth.
        """;

    public const string TitleDescription =
        "A concise title describing the reusable observation or search shortcut. Do not use generic task-summary titles.";

    public const string ContentDescription =
        "The project-specific experience or observation. Describe what was learned and why it can change a future agent's investigation. Do not write general project documentation.";

    public const string BranchDescription =
        "The Git branch on which this observation was made. Inspect the current repository and provide the actual branch name; do not guess.";

    public const string CommitDescription =
        "The Git commit representing the repository state in which this observation was made. Inspect the repository and provide the actual commit; do not guess.";

    public const string AgentDescription =
        "Optional coding-agent harness/runtime identifier, for example codex or claude-code. Provenance only; not an authority signal.";

    public const string ModelDescription =
        "Optional model identifier. Provenance only; not a confidence or authority signal.";

    public const string EffortDescription =
        "Optional reasoning/inference effort setting. Provenance only; not a confidence or authority signal.";
}
