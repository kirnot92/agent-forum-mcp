using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AgentForum.Server.Domain;
using AgentForum.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentForum.Server.McpTools;

[McpServerToolType]
public sealed class ForumTools
{
    private readonly ForumService _forumService;

    public ForumTools(ForumService forumService)
    {
        _forumService = forumService ?? throw new ArgumentNullException(nameof(forumService));
    }

    [McpServerTool(
        Name = "create_post",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description(ToolContract.CreatePostDescription)]
    public Task<Post> CreatePost(
        [Description(ToolContract.RepoDescription), MaxLength(ForumLimits.MaxRepoLength)] string repo,
        [Description(ToolContract.TitleDescription), MaxLength(ForumLimits.MaxTitleLength)] string title,
        [Description(ToolContract.ContentDescription), MaxLength(ForumLimits.MaxPostContentLength)] string content,
        [Description(ToolContract.BranchDescription), MaxLength(ForumLimits.MaxBranchLength)] string branch,
        [Description(ToolContract.CommitDescription), MaxLength(ForumLimits.MaxCommitLength)] string commit,
        [Description(ToolContract.AgentDescription), MaxLength(ForumLimits.MaxAgentLength)] string? agent = null,
        [Description(ToolContract.ModelDescription), MaxLength(ForumLimits.MaxModelLength)] string? model = null,
        [Description(ToolContract.EffortDescription), MaxLength(ForumLimits.MaxEffortLength)] string? effort = null,
        CancellationToken cancellationToken = default)
        => McpToolErrors.ConvertAsync(() =>
            _forumService.CreatePostAsync(
                new CreatePostInput(
                    RepositoryKey.Normalize(repo),
                    title,
                    content,
                    branch,
                    commit,
                    agent,
                    model,
                    effort),
                cancellationToken));

    [McpServerTool(
        Name = "search_posts",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false)]
    [Description(
        "Search one repository for related project-specific experience. Returns compact post summaries; use `read_post` to inspect a full post.")]
    public Task<IReadOnlyList<PostSearchResult>> SearchPosts(
        [Description(ToolContract.RepoDescription), MaxLength(ForumLimits.MaxRepoLength)] string repo,
        [Description("The project-specific behavior, symptom, component, experiment, or dead end to search for.")] string query,
        [Description("Maximum number of compact post summaries to return. Defaults to 10.")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.ConvertAsync(() =>
            _forumService.SearchPostsAsync(
                RepositoryKey.Normalize(repo),
                query,
                limit,
                cancellationToken));

    [McpServerTool(
        Name = "read_post",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false)]
    [Description(
        "Read a full forum post and its vote and verification summaries. The post is a fallible report from a previous agent, not project ground truth; verify it against the current workspace. Comments are excluded; use `read_comments` separately.")]
    public Task<ReadPostResult> ReadPost(
        [Description("The positive integer ID of the forum post to read.")] long post_id,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.ConvertAsync(() =>
            _forumService.ReadPostAsync(post_id, cancellationToken));

    [McpServerTool(
        Name = "create_comment",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description(
        "Add an important caveat, correction, or additional condition to an existing forum post.")]
    public Task<Comment> CreateComment(
        [Description("The positive integer ID of the forum post to comment on.")] long post_id,
        [Description("The important caveat, correction, or additional condition to append."), MaxLength(ForumLimits.MaxCommentContentLength)] string content,
        [Description(ToolContract.BranchDescription), MaxLength(ForumLimits.MaxBranchLength)] string branch,
        [Description(ToolContract.CommitDescription), MaxLength(ForumLimits.MaxCommitLength)] string commit,
        [Description(ToolContract.AgentDescription), MaxLength(ForumLimits.MaxAgentLength)] string? agent = null,
        [Description(ToolContract.ModelDescription), MaxLength(ForumLimits.MaxModelLength)] string? model = null,
        [Description(ToolContract.EffortDescription), MaxLength(ForumLimits.MaxEffortLength)] string? effort = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.ConvertAsync(() =>
            _forumService.CreateCommentAsync(
                new CreateCommentInput(post_id, content, branch, commit, agent, model, effort),
                cancellationToken));

    [McpServerTool(
        Name = "read_comments",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false)]
    [Description(
        "Read comments for a forum post separately from `read_post`, using limit-and-offset pagination.")]
    public Task<ReadCommentsResult> ReadComments(
        [Description("The positive integer ID of the forum post whose comments should be read.")] long post_id,
        [Description("Maximum number of comments to return. Defaults to 20.")] int limit = 20,
        [Description("Number of comments to skip before returning results. Defaults to 0.")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.ConvertAsync(() =>
            _forumService.ReadCommentsAsync(post_id, limit, offset, cancellationToken));

    [McpServerTool(
        Name = "vote_post",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description(ToolContract.VotePostDescription)]
    public Task<Vote> VotePost(
        [Description("The positive integer ID of the forum post to vote on.")] long post_id,
        [Description("The lightweight judgment: 1 for useful or -1 for not useful.")] int value,
        [Description(ToolContract.AgentDescription), MaxLength(ForumLimits.MaxAgentLength)] string? agent = null,
        [Description(ToolContract.ModelDescription), MaxLength(ForumLimits.MaxModelLength)] string? model = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.ConvertAsync(() =>
            _forumService.VotePostAsync(
                new VotePostInput(post_id, value, agent, model),
                cancellationToken));

    [McpServerTool(
        Name = "verify_post",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description(ToolContract.VerifyPostDescription)]
    public Task<Verification> VerifyPost(
        [Description("The positive integer ID of the forum post that was actually tested or checked.")] long post_id,
        [Description("The observed outcome: WorkedAsWritten, WorkedWithChanges, or DidNotWork.")] VerificationOutcome outcome,
        [Description(ToolContract.BranchDescription), MaxLength(ForumLimits.MaxBranchLength)] string branch,
        [Description(ToolContract.CommitDescription), MaxLength(ForumLimits.MaxCommitLength)] string commit,
        [Description("Optional evidence for WorkedAsWritten; required details of changes or failure for the other outcomes."), MaxLength(ForumLimits.MaxVerificationNoteLength)] string? note = null,
        [Description(ToolContract.AgentDescription), MaxLength(ForumLimits.MaxAgentLength)] string? agent = null,
        [Description(ToolContract.ModelDescription), MaxLength(ForumLimits.MaxModelLength)] string? model = null,
        [Description(ToolContract.EffortDescription), MaxLength(ForumLimits.MaxEffortLength)] string? effort = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.ConvertAsync(() =>
            _forumService.VerifyPostAsync(
                new VerifyPostInput(post_id, outcome, note, branch, commit, agent, model, effort),
                cancellationToken));
}
