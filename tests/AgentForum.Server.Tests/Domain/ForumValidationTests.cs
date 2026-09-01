using AgentForum.Server.Domain;

namespace AgentForum.Server.Tests.Domain;

public sealed class ForumValidationTests
{
    [Fact]
    public void CreatePost_AcceptsExactLengthBoundaries()
    {
        var input = ValidPost() with
        {
            Title = new string('t', ForumLimits.MaxTitleLength),
            Content = new string('c', ForumLimits.MaxPostContentLength)
        };

        ForumValidation.Validate(input);
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Content")]
    [InlineData("Branch")]
    [InlineData("Commit")]
    [InlineData("Repo")]
    public void CreatePost_RejectsWhitespaceRequiredFields(string field)
    {
        var input = field switch
        {
            "Title" => ValidPost() with { Title = " " },
            "Content" => ValidPost() with { Content = "\t" },
            "Branch" => ValidPost() with { Branch = "\r\n" },
            "Commit" => ValidPost() with { Commit = string.Empty },
            "Repo" => ValidPost() with { Repo = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Throws<ArgumentException>(() => ForumValidation.Validate(input));
    }

    [Fact]
    public void CreatePost_RejectsTitleOverMaximum()
    {
        var input = ValidPost() with { Title = new string('t', ForumLimits.MaxTitleLength + 1) };

        Assert.Throws<ArgumentException>(() => ForumValidation.Validate(input));
    }

    [Fact]
    public void CreatePost_RejectsContentOverMaximum()
    {
        var input = ValidPost() with { Content = new string('c', ForumLimits.MaxPostContentLength + 1) };

        var exception = Assert.Throws<ArgumentException>(() => ForumValidation.Validate(input));

        Assert.Contains("Content cannot exceed 3000 characters", exception.Message);
        Assert.Contains("received 3001 characters", exception.Message);
    }

    [Fact]
    public void CreateComment_AcceptsExactLengthBoundary()
    {
        var input = ValidComment() with
        {
            Content = new string('c', ForumLimits.MaxCommentContentLength)
        };

        ForumValidation.Validate(input);
    }

    [Fact]
    public void CreateComment_RejectsContentOverMaximum()
    {
        var input = ValidComment() with
        {
            Content = new string('c', ForumLimits.MaxCommentContentLength + 1)
        };

        Assert.Throws<ArgumentException>(() => ForumValidation.Validate(input));
    }

    [Theory]
    [InlineData("Content")]
    [InlineData("Branch")]
    [InlineData("Commit")]
    public void CreateComment_RejectsWhitespaceRequiredFields(string field)
    {
        var input = field switch
        {
            "Content" => ValidComment() with { Content = "\t" },
            "Branch" => ValidComment() with { Branch = "\r\n" },
            "Commit" => ValidComment() with { Commit = string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Throws<ArgumentException>(() => ForumValidation.Validate(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateComment_RequiresPositivePostId(long postId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ForumValidation.Validate(ValidComment() with { PostId = postId }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Vote_AcceptsOnlySpecifiedValues(int value)
    {
        ForumValidation.Validate(new VotePostInput(1, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public void Vote_RejectsOtherValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ForumValidation.Validate(new VotePostInput(1, value)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Vote_RequiresPositivePostId(long postId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ForumValidation.Validate(new VotePostInput(postId, 1)));
    }

    [Theory]
    [InlineData(VerificationOutcome.WorkedAsWritten)]
    [InlineData(VerificationOutcome.WorkedWithChanges)]
    [InlineData(VerificationOutcome.DidNotWork)]
    public void Verification_AcceptsEveryDefinedOutcome(VerificationOutcome outcome)
    {
        ForumValidation.Validate(ValidVerification() with { Outcome = outcome });
    }

    [Fact]
    public void Verification_RejectsUndefinedOutcome()
    {
        var input = ValidVerification() with { Outcome = (VerificationOutcome)99 };

        Assert.Throws<ArgumentOutOfRangeException>(() => ForumValidation.Validate(input));
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("commit")]
    public void Verification_RequiresRepositoryState(string field)
    {
        var input = field == "branch"
            ? ValidVerification() with { Branch = " " }
            : ValidVerification() with { Commit = string.Empty };

        Assert.Throws<ArgumentException>(() => ForumValidation.Validate(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Verification_RequiresPositivePostId(long postId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ForumValidation.Validate(ValidVerification() with { PostId = postId }));
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(500, ForumLimits.MaxSearchLimit)]
    public void SearchLimit_IsClamped(int requested, int expected)
    {
        Assert.Equal(expected, ForumValidation.ClampSearchLimit(requested));
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(500, ForumLimits.MaxCommentLimit)]
    public void CommentLimit_IsClamped(int requested, int expected)
    {
        Assert.Equal(expected, ForumValidation.ClampCommentLimit(requested));
    }

    [Fact]
    public void SearchQuery_RequiresNonWhitespaceText()
    {
        Assert.Throws<ArgumentException>(() => ForumValidation.ValidateSearchQuery(" "));
    }

    [Fact]
    public void SearchRepo_RequiresNonWhitespaceText()
    {
        Assert.Throws<ArgumentException>(() => ForumValidation.ValidateRepo(" "));
    }

    [Fact]
    public void CommentOffset_CannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ForumValidation.ValidateCommentOffset(-1));
    }

    private static CreatePostInput ValidPost() =>
        new("agent-forum-mcp", "Useful observation", "Inspect A before B.", "main", "abc123", "codex", "gpt", "high");

    private static CreateCommentInput ValidComment() =>
        new(1, "This also applies to C.", "main", "abc123", "codex", "gpt", "high");

    private static VerifyPostInput ValidVerification() =>
        new(
            1,
            VerificationOutcome.WorkedAsWritten,
            null,
            "main",
            "abc123",
            "codex",
            "gpt",
            "high");
}
