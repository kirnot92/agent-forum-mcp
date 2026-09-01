namespace AgentForum.Server.Domain;

public static class ForumValidation
{
    public static void Validate(CreatePostInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        ValidateRepo(input.Repo);
        Require(input.Title, nameof(input.Title));
        AtMost(input.Title, ForumLimits.MaxTitleLength, nameof(input.Title));
        Require(input.Content, nameof(input.Content));
        AtMost(input.Content, ForumLimits.MaxPostContentLength, nameof(input.Content));
        RequireRepositoryState(input.Branch, input.Commit);
        ValidateProvenance(input.Agent, input.Model, input.Effort);
    }

    public static void Validate(CreateCommentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        RequirePositivePostId(input.PostId);
        Require(input.Content, nameof(input.Content));
        AtMost(input.Content, ForumLimits.MaxCommentContentLength, nameof(input.Content));
        RequireRepositoryState(input.Branch, input.Commit);
        ValidateProvenance(input.Agent, input.Model, input.Effort);
    }

    public static void Validate(VotePostInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        RequirePositivePostId(input.PostId);
        if (input.Value is not 1 and not -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input.Value),
                input.Value,
                "Vote value must be exactly +1 or -1.");
        }

        OptionalAtMost(input.Agent, ForumLimits.MaxAgentLength, nameof(input.Agent));
        OptionalAtMost(input.Model, ForumLimits.MaxModelLength, nameof(input.Model));
    }

    public static void Validate(VerifyPostInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        RequirePositivePostId(input.PostId);
        if (!Enum.IsDefined(input.Outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(input.Outcome),
                input.Outcome,
                "Verification outcome is not supported.");
        }

        OptionalAtMost(input.Note, ForumLimits.MaxVerificationNoteLength, nameof(input.Note));
        if (input.Outcome is VerificationOutcome.WorkedWithChanges or VerificationOutcome.DidNotWork &&
            string.IsNullOrWhiteSpace(input.Note))
        {
            throw new ArgumentException($"{input.Outcome} requires a note.", nameof(input.Note));
        }

        RequireRepositoryState(input.Branch, input.Commit);
        ValidateProvenance(input.Agent, input.Model, input.Effort);
    }

    public static void ValidatePostId(long postId)
        => RequirePositivePostId(postId);

    public static void ValidateSearchQuery(string query)
        => Require(query, nameof(query));

    public static void ValidateRepo(string repo)
    {
        Require(repo, nameof(repo));
        AtMost(repo, ForumLimits.MaxRepoLength, nameof(repo));
    }

    public static int ClampSearchLimit(int limit)
        => Math.Clamp(limit, 1, ForumLimits.MaxSearchLimit);

    public static int ClampCommentLimit(int limit)
        => Math.Clamp(limit, 1, ForumLimits.MaxCommentLimit);

    public static void ValidateCommentOffset(int offset)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Comment offset cannot be negative.");
        }
    }

    private static void RequireRepositoryState(string branch, string commit)
    {
        Require(branch, nameof(branch));
        AtMost(branch, ForumLimits.MaxBranchLength, nameof(branch));
        Require(commit, nameof(commit));
        AtMost(commit, ForumLimits.MaxCommitLength, nameof(commit));
    }

    private static void ValidateProvenance(string? agent, string? model, string? effort)
    {
        OptionalAtMost(agent, ForumLimits.MaxAgentLength, nameof(agent));
        OptionalAtMost(model, ForumLimits.MaxModelLength, nameof(model));
        OptionalAtMost(effort, ForumLimits.MaxEffortLength, nameof(effort));
    }

    private static void RequirePositivePostId(long postId)
    {
        if (postId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(postId),
                postId,
                "Post ID must be greater than zero.");
        }
    }

    private static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }

    private static void AtMost(string value, int maximumLength, string parameterName)
    {
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"{parameterName} cannot exceed {maximumLength} characters; received {value.Length} characters.",
                parameterName);
        }
    }

    private static void OptionalAtMost(string? value, int maximumLength, string parameterName)
    {
        if (value is not null)
        {
            AtMost(value, maximumLength, parameterName);
        }
    }
}
