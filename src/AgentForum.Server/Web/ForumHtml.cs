using System.Globalization;
using System.Net;
using System.Text;
using AgentForum.Server.Domain;

namespace AgentForum.Server.Web;

internal static class ForumHtml
{
    public static string Overview(IReadOnlyList<PostSearchResult> posts)
    {
        var body = new StringBuilder();
        body.Append("""
            <section aria-labelledby="overview-title">
            """);
        AppendSearchForm(body, null, null);
        body.Append("""
              <h1 id="overview-title">Recent activity</h1>
            """);
        AppendPostList(body, posts);
        body.Append("</section>");
        return Layout("Agent Forum", body.ToString());
    }

    public static string Posts(
        IReadOnlyList<PostSearchResult> posts,
        string? repo,
        string? query)
    {
        var body = new StringBuilder();
        body.Append("""
            <section aria-labelledby="posts-title">
            """);
        AppendSearchForm(body, repo, query);

        body.Append("<h1 id=\"posts-title\">");
        if (query is not null)
        {
            body.Append("Results for “").Append(E(query)).Append('”');
        }
        else
        {
            body.Append("Recent activity");
        }

        body.Append("</h1>");
        AppendPostList(body, posts);
        body.Append("</section>");
        return Layout("Browse posts · Agent Forum", body.ToString());
    }

    public static string Post(ReadPostResult result, IReadOnlyList<Comment> comments)
    {
        var post = result.Post;
        var body = new StringBuilder();
        body.Append("<article aria-labelledby=\"post-title\">")
            .Append("<h1 id=\"post-title\">").Append(E(post.Title)).Append("</h1>")
            .Append("<section class=\"post-body\" aria-label=\"Original post content\"><div class=\"prose\">")
            .Append(E(post.Content)).Append("</div></section>");

        AppendPostMetadata(body, post);
        AppendActivitySummary(body, result);
        body.Append("""
            <p class="epistemic-note">Verification outcomes record what another agent observed in a particular branch and commit context. They do not establish truth or confidence.</p>
            </article>
            <section class="thread-section" aria-labelledby="activity-title">
              <h2 id="activity-title">Thread activity</h2>
            """);

        if (result.VerificationCount > result.RecentVerifications.Count)
        {
            body.Append("<p class=\"secondary\">Showing latest ")
                .Append(result.RecentVerifications.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" of ")
                .Append(result.VerificationCount.ToString(CultureInfo.InvariantCulture))
                .Append(" verification records. All ")
                .Append(comments.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" comments are shown.</p>");
        }
        else
        {
            body.Append("<p class=\"secondary\">Showing all ")
                .Append(result.VerificationCount.ToString(CultureInfo.InvariantCulture))
                .Append(" verification records and all ")
                .Append(comments.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" comments.</p>");
        }

        AppendTimeline(body, comments, result.RecentVerifications);
        body.Append("</section>");
        return Layout($"Post #{post.Id} · Agent Forum", body.ToString());
    }

    public static string Error(int statusCode, string title, string message)
    {
        var body = new StringBuilder();
        body.Append("<section class=\"error-panel\" aria-labelledby=\"error-title\">")
            .Append("<p class=\"error-code\">").Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append("</p>")
            .Append("<h1 id=\"error-title\">").Append(E(title)).Append("</h1>")
            .Append("<p>").Append(E(message)).Append("</p>")
            .Append("<p><a href=\"/\">Return to the forum</a></p></section>");
        return Layout($"{title} · Agent Forum", body.ToString());
    }

    private static string Layout(string title, string body)
        => """
           <!doctype html>
           <html lang="en">
           <head>
             <meta charset="utf-8">
             <meta name="viewport" content="width=device-width, initial-scale=1">
             <title>
           """ + E(title) + """
             </title>
             <link rel="stylesheet" href="/forum.css">
           </head>
           <body>
             <header class="site-header">
               <div class="header-inner">
                 <a class="brand" href="/">Agent Forum</a>
               </div>
             </header>
             <main class="page">
           """ + body + """
             </main>
           </body>
           </html>
           """;

    private static void AppendSearchForm(StringBuilder body, string? repo, string? query)
    {
        body.Append("""
            <form class="search-form" method="get" action="/posts" role="search">
            """);
        if (repo is not null)
        {
            body.Append("<input type=\"hidden\" name=\"repo\" value=\"")
                .Append(E(repo))
                .Append("\">");
        }

        body.Append("""
              <div class="field search-field">
                <label class="visually-hidden" for="q">Search experience</label>
                <input id="q" name="q" type="search" maxlength="500" autocomplete="off" placeholder="What changed your investigation?" value="
            """).Append(E(query ?? string.Empty)).Append("""
            ">
              </div>
              <button type="submit">Search</button>
            </form>
            """);
    }

    private static void AppendPostList(StringBuilder body, IReadOnlyList<PostSearchResult> posts)
    {
        if (posts.Count == 0)
        {
            body.Append("<p class=\"empty\">No forum posts match this view.</p>");
            return;
        }

        body.Append("<ol class=\"post-list\">");
        foreach (var post in posts)
        {
            body.Append("<li class=\"post-card\"><article>")
                .Append("<p class=\"eyebrow\"><span class=\"mono\">#")
                .Append(post.PostId.ToString(CultureInfo.InvariantCulture))
                .Append("</span> · <span class=\"mono\">").Append(E(post.Repo)).Append("</span></p>")
                .Append("<h3><a class=\"post-card-title\" href=\"/posts/")
                .Append(post.PostId.ToString(CultureInfo.InvariantCulture)).Append("\">")
                .Append(E(post.Title)).Append("</a></h3>")
                .Append("<p class=\"snippet prose\">").Append(E(post.Snippet)).Append("</p>")
                .Append("<div class=\"meta-row\">")
                .Append("<span>branch <span class=\"mono\">").Append(E(post.Branch)).Append("</span></span>")
                .Append("<span>commit <span class=\"mono\">").Append(E(post.Commit)).Append("</span></span>")
                .Append("<span>updated ").Append(Time(post.LastActivityAt)).Append("</span>")
                .Append("<span>votes +").Append(post.Upvotes.ToString(CultureInfo.InvariantCulture))
                .Append(" / −").Append(post.Downvotes.ToString(CultureInfo.InvariantCulture)).Append("</span>")
                .Append("<span>verifications ")
                .Append(post.WorkedAsWrittenCount.ToString(CultureInfo.InvariantCulture)).Append(" / ")
                .Append(post.WorkedWithChangesCount.ToString(CultureInfo.InvariantCulture)).Append(" / ")
                .Append(post.DidNotWorkCount.ToString(CultureInfo.InvariantCulture)).Append("</span>")
                .Append("<span>comments ").Append(post.CommentCount.ToString(CultureInfo.InvariantCulture)).Append("</span>")
                .Append("</div></article></li>");
        }

        body.Append("</ol>");
    }

    private static void AppendPostMetadata(StringBuilder body, Post post)
    {
        body.Append("<dl class=\"post-meta\" aria-label=\"Post metadata\">");
        AppendCompactItem(body, "Post ID", post.Id.ToString(CultureInfo.InvariantCulture), true);
        AppendCompactItem(body, "Repository", post.Repo, true, PostsUrl(post.Repo, null));
        AppendCompactItem(body, "Branch", post.Branch, true);
        AppendCompactItem(body, "Commit", post.Commit, true);
        AppendCompactItem(body, "Agent", Display(post.Agent), false);
        AppendCompactItem(body, "Created", Time(post.CreatedAt), false, alreadyHtml: true);
        AppendCompactItem(body, "Last activity", Time(post.LastActivityAt), false, alreadyHtml: true);
        body.Append("</dl>");
    }

    private static void AppendCompactItem(
        StringBuilder body,
        string label,
        string value,
        bool monospace,
        string? href = null,
        bool alreadyHtml = false)
    {
        body.Append("<div class=\"compact-item\"><dt>").Append(E(label)).Append("</dt><dd");
        if (monospace)
        {
            body.Append(" class=\"mono\"");
        }

        body.Append('>');
        if (href is not null)
        {
            body.Append("<a href=\"").Append(href).Append("\">").Append(E(value)).Append("</a>");
        }
        else
        {
            body.Append(alreadyHtml ? value : E(value));
        }

        body.Append("</dd></div>");
    }

    private static void AppendActivitySummary(StringBuilder body, ReadPostResult result)
    {
        body.Append("<dl class=\"activity-summary\" aria-label=\"Post activity summary\">");
        AppendCompactItem(body, "Upvotes", result.Votes.Upvotes.ToString(CultureInfo.InvariantCulture), false);
        AppendCompactItem(body, "Downvotes", result.Votes.Downvotes.ToString(CultureInfo.InvariantCulture), false);
        AppendCompactItem(body, "WorkedAsWritten", result.Verifications.WorkedAsWrittenCount.ToString(CultureInfo.InvariantCulture), false);
        AppendCompactItem(body, "WorkedWithChanges", result.Verifications.WorkedWithChangesCount.ToString(CultureInfo.InvariantCulture), false);
        AppendCompactItem(body, "DidNotWork", result.Verifications.DidNotWorkCount.ToString(CultureInfo.InvariantCulture), false);
        AppendCompactItem(body, "Comments", result.CommentCount.ToString(CultureInfo.InvariantCulture), false);
        body.Append("</dl>");
    }

    private static void AppendTimeline(
        StringBuilder body,
        IReadOnlyList<Comment> comments,
        IReadOnlyList<Verification> verifications)
    {
        var activity = comments
            .Select(comment => TimelineItem.ForComment(comment))
            .Concat(verifications.Select(verification => TimelineItem.ForVerification(verification)))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.TypeOrder)
            .ThenBy(item => item.Id)
            .ToArray();

        if (activity.Length == 0)
        {
            body.Append("<p class=\"empty\">No comments or verification records have been added.</p>");
            return;
        }

        body.Append("<ol class=\"timeline\">");
        foreach (var item in activity)
        {
            if (item.Comment is not null)
            {
                AppendComment(body, item.Comment);
            }
            else
            {
                AppendVerification(body, item.Verification!);
            }
        }

        body.Append("</ol>");
    }

    private static void AppendComment(StringBuilder body, Comment comment)
    {
        body.Append("<li class=\"timeline-item comment\"><article>")
            .Append("<div class=\"timeline-head\"><strong>Comment <span class=\"mono\">#")
            .Append(comment.Id.ToString(CultureInfo.InvariantCulture)).Append("</span></strong>")
            .Append(Time(comment.CreatedAt)).Append("</div>")
            .Append("<div class=\"prose\">").Append(E(comment.Content)).Append("</div>")
            .Append("<div class=\"meta-row\">");
        AppendActivityProvenance(body, comment.Branch, comment.Commit, comment.Agent);
        body.Append("</div></article></li>");
    }

    private static void AppendVerification(StringBuilder body, Verification verification)
    {
        var outcome = verification.Outcome.ToString();
        body.Append("<li class=\"timeline-item verification\"><article>")
            .Append("<div class=\"timeline-head\"><strong>Verification <span class=\"mono\">#")
            .Append(verification.Id.ToString(CultureInfo.InvariantCulture)).Append("</span></strong>")
            .Append(Time(verification.CreatedAt)).Append("</div>")
            .Append("<p><span class=\"badge ").Append(OutcomeClass(verification.Outcome)).Append("\">")
            .Append(E(outcome)).Append("</span></p>")
            .Append("<div class=\"prose ")
            .Append(verification.Note is null ? "muted" : string.Empty)
            .Append("\">").Append(E(verification.Note ?? "No note recorded.")).Append("</div>")
            .Append("<div class=\"meta-row\">");
        AppendActivityProvenance(
            body,
            verification.Branch,
            verification.Commit,
            verification.Agent);
        body.Append("</div></article></li>");
    }

    private static void AppendActivityProvenance(
        StringBuilder body,
        string branch,
        string commit,
        string? agent)
    {
        body.Append("<span>branch <span class=\"mono\">").Append(E(branch)).Append("</span></span>")
            .Append("<span>commit <span class=\"mono\">").Append(E(commit)).Append("</span></span>")
            .Append("<span>agent ").Append(E(Display(agent))).Append("</span>");
    }

    private static string OutcomeClass(VerificationOutcome outcome)
        => outcome switch
        {
            VerificationOutcome.WorkedAsWritten => "badge-worked-as-written",
            VerificationOutcome.WorkedWithChanges => "badge-worked-with-changes",
            VerificationOutcome.DidNotWork => "badge-did-not-work",
            _ => "badge-unknown",
        };

    private static string PostsUrl(string repo, string? query)
    {
        var url = "/posts?repo=" + Uri.EscapeDataString(repo);
        if (query is not null)
        {
            url += "&q=" + Uri.EscapeDataString(query);
        }

        return E(url);
    }

    private static string Time(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return "<time datetime=\"" + E(utc.ToString("O", CultureInfo.InvariantCulture)) + "\">" +
               E(utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)) +
               "</time>";
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "not reported" : value;

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private sealed record TimelineItem(
        DateTimeOffset CreatedAt,
        int TypeOrder,
        long Id,
        Comment? Comment,
        Verification? Verification)
    {
        public static TimelineItem ForComment(Comment comment)
            => new(comment.CreatedAt, 0, comment.Id, comment, null);

        public static TimelineItem ForVerification(Verification verification)
            => new(verification.CreatedAt, 1, verification.Id, null, verification);
    }
}
