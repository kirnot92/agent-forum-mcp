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
                VerificationOutcome.DidNotWork,
                VerificationOutcome.NoLongerApplicable
            },
            values);
    }

    [Fact]
    public void VerificationOutcome_UsesStableIntegerValues()
    {
        Assert.Equal(0, (int)VerificationOutcome.WorkedAsWritten);
        Assert.Equal(1, (int)VerificationOutcome.WorkedWithChanges);
        Assert.Equal(2, (int)VerificationOutcome.DidNotWork);
        Assert.Equal(3, (int)VerificationOutcome.NoLongerApplicable);
    }

    [Fact]
    public void EntityIdentifiers_AreIntegers()
    {
        Assert.Equal(typeof(long), typeof(Post).GetProperty(nameof(Post.Id))!.PropertyType);
        Assert.Equal(typeof(long), typeof(Comment).GetProperty(nameof(Comment.Id))!.PropertyType);
        Assert.Equal(typeof(long), typeof(Comment).GetProperty(nameof(Comment.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(Vote).GetProperty(nameof(Vote.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(Verification).GetProperty(nameof(Verification.Id))!.PropertyType);
        Assert.Equal(typeof(long), typeof(Verification).GetProperty(nameof(Verification.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(CreateCommentInput).GetProperty(nameof(CreateCommentInput.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(VotePostInput).GetProperty(nameof(VotePostInput.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(VerifyPostInput).GetProperty(nameof(VerifyPostInput.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(PostSearchResult).GetProperty(nameof(PostSearchResult.PostId))!.PropertyType);
        Assert.Equal(typeof(long), typeof(ReadCommentsResult).GetProperty(nameof(ReadCommentsResult.PostId))!.PropertyType);
    }

    [Fact]
    public void ReadPostResult_ExposesRawCountsAndAgentProvenance()
    {
        var createdAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var post = new Post(
            42,
            "agent-forum-mcp",
            "Title",
            "Content",
            "feature/example",
            "deadbeef",
            "codex",
            createdAt,
            createdAt);

        var result = new ReadPostResult(
            post,
            new VoteSummary(3, 1),
            new VerificationSummary(2, 1, 4, 3),
            Array.Empty<Verification>(),
            Array.Empty<Comment>(),
            5,
            7);

        Assert.Same(post, result.Post);
        Assert.Equal(42, result.Post.Id);
        Assert.Equal("agent-forum-mcp", result.Post.Repo);
        Assert.Equal("feature/example", result.Post.Branch);
        Assert.Equal("deadbeef", result.Post.Commit);
        Assert.Equal(3, result.Votes.Upvotes);
        Assert.Equal(4, result.Verifications.DidNotWorkCount);
        Assert.Equal(3, result.Verifications.NoLongerApplicableCount);
        Assert.Equal(5, result.CommentCount);
        Assert.Equal(7, result.VerificationCount);
    }
}
