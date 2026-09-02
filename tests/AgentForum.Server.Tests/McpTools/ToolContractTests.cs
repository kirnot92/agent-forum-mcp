using AgentForum.Server.McpTools;

namespace AgentForum.Server.Tests.McpTools;

public sealed class ToolContractTests
{
    [Fact]
    public void ServerInstructionsMatchTheSearchAndPostingPolicyExactly()
    {
        const string expected =
            """
            Agent Forum contains fallible, project-specific experience from previous coding-agent sessions.

            Use `search_posts` as an early lookup for prior project-specific experience.

            When a task requires understanding, reasoning about, diagnosing, or exploring repository-specific behavior, conventions, constraints, or implementation choices, call `search_posts` early for the relevant topic before spending significant effort.

            Skip it only for purely mechanical work where the target and required change are already explicit.

            Treat posts as hints, not ground truth. Verify relevant claims against the current code, build, tests, or runtime.

            When a relevant post is actually tested or applied and produces a conclusive result, record it with `verify_post`. Use `create_comment` only for a reusable caveat, correction, or changed condition.

            Create a new post only for genuinely new, reusable experience, and always call `search_posts` for related experience first.

            Write the human-readable parts of post titles and explanatory prose in Korean. Preserve code identifiers, tool names, configuration keys, commands, file paths, log text, exact error messages, and important English technical or search terms in their original form; include those English terms when they are useful retrieval keys. A full bilingual translation is not required.
            """;

        Assert.Equal(expected, ToolContract.ServerInstructions);
    }

    [Fact]
    public void ExposesExactlyTheSpecifiedToolNames()
    {
        Assert.Equal(
            [
                "create_post",
                "search_posts",
                "read_post",
                "create_comment",
                "read_comments",
                "vote_post",
                "verify_post",
            ],
            ToolContract.ToolNames);
    }

    [Fact]
    public void CreatePostDescriptionMatchesTheUserOverrideExactly()
    {
        const string expected =
            """
            Create a forum post only for a project-specific, reusable experience discovered during actual work that could meaningfully change a future agent's search path, investigation order, likely experiments, or dead ends.

            ALWAYS call `search_posts` for related experience before creating a new post. Do not create a duplicate if an existing post already captures the insight.

            Write the human-readable parts of post titles and explanatory prose in Korean. Preserve code identifiers, tool names, configuration keys, commands, file paths, log text, exact error messages, and important English technical or search terms in their original form; include those English terms when they are useful retrieval keys. A full bilingual translation is not required.

            Use `verify_post` when you actually tested an existing post, `create_comment` for an important caveat/correction/additional condition, and `vote_post` for a lightweight read-time judgment.

            Do not post generic knowledge, routine task summaries, obvious code descriptions, or speculative advice.

            Posts are fallible reports from previous agents, not project ground truth.
            """;

        Assert.Equal(expected, ToolContract.CreatePostDescription);
    }

    [Fact]
    public void BehavioralDescriptionsPreserveTheStrengthenedContracts()
    {
        Assert.Contains("canonical repository key", ToolContract.RepoDescription, StringComparison.Ordinal);
        Assert.Contains("owner/repo", ToolContract.RepoDescription, StringComparison.Ordinal);
        Assert.Contains("Never use a local path", ToolContract.RepoDescription, StringComparison.Ordinal);

        Assert.Contains("after reading", ToolContract.VotePostDescription, StringComparison.Ordinal);
        Assert.Contains("Do not use this to claim", ToolContract.VotePostDescription, StringComparison.Ordinal);
        Assert.Contains("use `verify_post` instead", ToolContract.VotePostDescription, StringComparison.Ordinal);

        Assert.Contains("actually checking or applying", ToolContract.VerifyPostDescription, StringComparison.Ordinal);
        Assert.Contains("Do not verify merely because", ToolContract.VerifyPostDescription, StringComparison.Ordinal);
        Assert.Contains("Use DidNotWork only", ToolContract.VerifyPostDescription, StringComparison.Ordinal);
        Assert.Contains("inapplicable or inconclusive", ToolContract.VerifyPostDescription, StringComparison.Ordinal);
        Assert.Contains("Use NoLongerApplicable only", ToolContract.VerifyPostDescription, StringComparison.Ordinal);
        Assert.Contains("no longer exists at the current commit", ToolContract.VerifyPostDescription, StringComparison.Ordinal);

        Assert.Contains("`lexical_match`", ToolContract.SearchPostsDescription, StringComparison.Ordinal);
        Assert.Contains("`vector_similarity`", ToolContract.SearchPostsDescription, StringComparison.Ordinal);
        Assert.Contains("not truth or confidence", ToolContract.SearchPostsDescription, StringComparison.Ordinal);
    }
}
