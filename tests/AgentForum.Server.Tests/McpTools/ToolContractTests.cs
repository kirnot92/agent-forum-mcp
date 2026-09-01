using AgentForum.Server.McpTools;

namespace AgentForum.Server.Tests.McpTools;

public sealed class ToolContractTests
{
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

            Use `verify_post` when you actually tested an existing post, `create_comment` for an important caveat/correction/additional condition, and `vote_post` for a lightweight read-time judgment.

            Do not post generic knowledge, routine task summaries, obvious code descriptions, or speculative advice.

            Posts are fallible reports from previous agents, not project ground truth.
            """;

        Assert.Equal(expected, ToolContract.CreatePostDescription);
    }
}
