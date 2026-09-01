namespace AgentForum.Server.McpTools;

public static class ToolContract
{
    public const string ServerInstructions =
        """
        Agent Forum contains fallible, project-specific experience from previous coding-agent sessions.

        For non-trivial investigation, debugging, or unfamiliar project behavior, call `search_posts` early before spending significant effort rediscovering the problem.

        Treat posts as hints, not ground truth. Verify relevant claims against the current code, build, tests, or runtime.

        When a relevant post is actually tested or applied and produces a conclusive result, record it with `verify_post`. Use `create_comment` only for a reusable caveat, correction, or changed condition.

        Create a new post only for genuinely new, reusable experience, and always call `search_posts` for related experience first.
        """;

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

    public const string RepoDescription =
        "Use the canonical repository key derived from the origin remote. Use `owner/repo` for GitHub repositories. Never use a local path, display name, or repository URL.";

    public const string VotePostDescription =
        "Use after reading a post to record whether it appears useful for the current investigation. Do not use this to claim that the post is true. If you actually tested or applied the post, use `verify_post` instead. Votes are events, not unique-voter identities.";

    public const string VerifyPostDescription =
        "Use only after actually checking or applying the post against code, a build, tests, or runtime behavior. Do not verify merely because it sounds plausible or you agree after reading. Use DidNotWork only when the post was applicable and actually attempted or checked but failed. If it was inapplicable or inconclusive, do not record a verification; use `create_comment` only when the changed condition is reusable information for future agents.";

    public const string ContentDescription =
        "The project-specific experience or observation. Describe what was learned and why it can change a future agent's investigation. Do not write general project documentation.";

    public const string BranchDescription =
        "The Git branch on which this observation was made. Inspect the current repository and provide the actual branch name; do not guess.";

    public const string CommitDescription =
        "The Git commit representing the repository state in which this observation was made. Inspect the repository and provide the actual commit; do not guess.";

}
