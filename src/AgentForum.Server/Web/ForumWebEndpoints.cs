using AgentForum.Server.Domain;
using AgentForum.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentForum.Server.Web;

internal static class ForumWebEndpoints
{
    internal const int MaxWebQueryLength = 500;
    private const int BrowseLimit = 20;
    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (IsHumanRoute(context.Request.Path))
            {
                context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
                context.Response.Headers.XContentTypeOptions = "nosniff";
            }

            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/forum.css", WriteStylesheetAsync);
        app.MapGet("/", WriteOverviewAsync);
        app.MapGet("/posts", WritePostsAsync);
        app.MapGet("/posts/{id:long}", WritePostAsync);
        app.MapGet("/posts/{*invalidPath}", (HttpContext context) =>
            WriteErrorAsync(context, StatusCodes.Status404NotFound, "Post not found",
                "No forum post exists at this address."));
    }

    private static bool IsHumanRoute(PathString path)
        => path == "/" ||
           path == "/forum.css" ||
           path == "/posts" ||
           path.StartsWithSegments("/posts");

    private static Task WriteStylesheetAsync(HttpContext context)
        => WriteAsync(context, ForumStylesheet.Content, "text/css; charset=utf-8");

    private static async Task WriteOverviewAsync(HttpContext context, ForumService forum)
    {
        try
        {
            var recentPosts = await forum
                .BrowsePostsAsync(null, BrowseLimit, context.RequestAborted)
                .ConfigureAwait(false);
            await WriteHtmlAsync(context, ForumHtml.Overview(recentPosts)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "Forum unavailable",
                "The forum could not be loaded right now.").ConfigureAwait(false);
        }
    }

    private static async Task WritePostsAsync(HttpContext context, ForumService forum)
    {
        var repo = NormalizeOptional(context.Request.Query["repo"].ToString());
        var query = NormalizeOptional(context.Request.Query["q"].ToString());

        if (query is not null && query.Length > MaxWebQueryLength)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Query is too long",
                $"Search queries may contain at most {MaxWebQueryLength} characters.").ConfigureAwait(false);
            return;
        }

        try
        {
            IReadOnlyList<PostSearchResult> posts;
            if (query is null)
            {
                posts = await forum
                    .BrowsePostsAsync(repo, BrowseLimit, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            else if (repo is null)
            {
                posts = await forum
                    .SearchPostsAsync(query, BrowseLimit, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            else
            {
                posts = await forum
                    .SearchPostsAsync(repo, query, BrowseLimit, context.RequestAborted)
                    .ConfigureAwait(false);
            }

            await WriteHtmlAsync(context, ForumHtml.Posts(posts, repo, query)).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Invalid request",
                "The repository or search query is not valid.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "Forum unavailable",
                "The posts could not be loaded right now.").ConfigureAwait(false);
        }
    }

    private static async Task WritePostAsync(HttpContext context, ForumService forum, long id)
    {
        if (id <= 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Post not found",
                "No forum post exists at this address.").ConfigureAwait(false);
            return;
        }

        try
        {
            var post = await forum.ReadPostAsync(id, context.RequestAborted).ConfigureAwait(false);
            var comments = await ReadAllCommentsAsync(forum, id, context.RequestAborted).ConfigureAwait(false);
            await WriteHtmlAsync(context, ForumHtml.Post(post, comments)).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Post not found",
                "No forum post exists at this address.").ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Post not found",
                "No forum post exists at this address.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "Forum unavailable",
                "The post could not be loaded right now.").ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<Comment>> ReadAllCommentsAsync(
        ForumService forum,
        long postId,
        CancellationToken cancellationToken)
    {
        var comments = new List<Comment>();
        var offset = 0;
        int total;

        while (true)
        {
            var page = await forum
                .ReadCommentsAsync(postId, ForumLimits.MaxCommentLimit, offset, cancellationToken)
                .ConfigureAwait(false);
            comments.AddRange(page.Comments);
            total = page.TotalCount;
            offset += page.Comments.Count;

            if (offset >= total || page.Comments.Count == 0)
            {
                break;
            }
        }

        return comments;
    }

    private static string? NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Task WriteHtmlAsync(HttpContext context, string html, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        return WriteAsync(context, html, "text/html; charset=utf-8");
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string title, string message)
        => WriteHtmlAsync(context, ForumHtml.Error(statusCode, title, message), statusCode);

    private static Task WriteAsync(HttpContext context, string content, string contentType)
    {
        context.Response.ContentType = contentType;
        return context.Response.WriteAsync(content, context.RequestAborted);
    }
}
