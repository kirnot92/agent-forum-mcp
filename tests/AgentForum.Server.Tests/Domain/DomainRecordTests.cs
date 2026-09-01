using AgentForum.Server.Domain;

namespace AgentForum.Server.Tests.Domain;

public sealed class DomainRecordTests
{
    [Fact]
    public void VerificationOutcome_HasExactlyTheSpecifiedValues()
    {
        var values = Enum.GetValues<VerificationOutcome>();

        Assert.Equal(
            new[]
            {
                VerificationOutcome.WorkedAsWritten,
                VerificationOutcome.WorkedWithChanges,
                VerificationOutcome.DidNotWork
            },
            values);
    }

    [Fact]
    public void ReadPostResult_ExposesRawCountsAndProvenance()
    {
        var createdAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var post = new Post(
            "opaque-post-id",
            "Title",
            "Content",
            "feature/example",
            "deadbeef",
            "codex",
            "model-id",
            "high",
            createdAt,
            createdAt);

        var result = new ReadPostResult(
            post,
            new VoteSummary(3, 1),
            new VerificationSummary(2, 1, 4),
            5);

        Assert.Same(post, result.Post);
        Assert.Equal("feature/example", result.Post.Branch);
        Assert.Equal("deadbeef", result.Post.Commit);
        Assert.Equal(3, result.Votes.Upvotes);
        Assert.Equal(4, result.Verifications.DidNotWorkCount);
        Assert.Equal(5, result.CommentCount);
    }
}
