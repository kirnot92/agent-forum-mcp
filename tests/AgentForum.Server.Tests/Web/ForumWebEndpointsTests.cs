using System.Net;
using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using AgentForum.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForum.Server.Tests.Web;

public sealed class ForumWebEndpointsTests
{
    [Fact]
    public async Task OverviewAndStylesheet_AreReadableSecureAndResponsive()
    {
        await using var fixture = await WebFixture.StartAsync();
        await fixture.CreatePostAsync("acme/widget", "Recent report", "Useful context");
        var embeddingCalls = fixture.Embeddings.CallCount;

        using var response = await fixture.Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        AssertSecurityHeaders(response);
        Assert.Contains("<form class=\"search-form\"", html);
        Assert.Contains("name=\"q\"", html);
        Assert.Contains("Recent activity", html);
        Assert.Contains("Recent report", html);
        Assert.DoesNotContain("Read-only forum", html);
        Assert.DoesNotContain("Recent agent experience", html);
        Assert.DoesNotContain("Browse and search all posts", html);
        Assert.DoesNotContain("fallible reports", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<nav", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<footer", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name=\"repo\"", html);
        Assert.Equal(embeddingCalls, fixture.Embeddings.CallCount);

        using var cssResponse = await fixture.Client.GetAsync("/forum.css");
        var css = await cssResponse.Content.ReadAsStringAsync();
        Assert.Equal("text/css", cssResponse.Content.Headers.ContentType!.MediaType);
        AssertSecurityHeaders(cssResponse);
        Assert.Contains("@media (max-width: 640px)", css);
        Assert.Contains(".post-meta, .activity-summary { display: flex; flex-wrap: wrap;", css);
        Assert.Contains(".compact-item dd { min-width: 0; margin: 0; color: #4b4b47; overflow-wrap: anywhere; }", css);
        Assert.Contains(".compact-item { flex: 1 1 10rem; }", css);
        Assert.Contains(".timeline-head > time { display: block; margin-top: .25rem; }", css);
        Assert.DoesNotContain(".context-grid", css);
        Assert.DoesNotContain(".count-grid", css);
        Assert.DoesNotContain(".notice", css);
        Assert.Contains(":focus-visible", css);
        Assert.DoesNotContain("javascript", css, StringComparison.OrdinalIgnoreCase);

        using var mcpResponse = await fixture.Client.GetAsync(ForumHttpOptions.McpPath);
        Assert.NotEqual(HttpStatusCode.NotFound, mcpResponse.StatusCode);
    }

    [Fact]
    public async Task Posts_SupportsGlobalAndRepositoryBrowseAndSearch()
    {
        await using var fixture = await WebFixture.StartAsync();
        await fixture.CreatePostAsync("acme/alpha", "Alpha report", "Needle appears in this report.");
        await fixture.CreatePostAsync("acme/beta", "Beta report", "Different context.");
        var embeddingCalls = fixture.Embeddings.CallCount;

        var allHtml = await fixture.Client.GetStringAsync("/posts");
        Assert.Contains("Alpha report", allHtml);
        Assert.Contains("Beta report", allHtml);
        Assert.Contains("Recent activity", allHtml);
        Assert.DoesNotContain("name=\"repo\"", allHtml);
        Assert.Equal(embeddingCalls, fixture.Embeddings.CallCount);

        var repoHtml = await fixture.Client.GetStringAsync("/posts?repo=acme%2Falpha");
        Assert.Contains("Alpha report", repoHtml);
        Assert.DoesNotContain("Beta report", repoHtml);
        Assert.Contains("type=\"hidden\" name=\"repo\" value=\"acme/alpha\"", repoHtml);
        Assert.DoesNotContain("id=\"repo\"", repoHtml);
        Assert.DoesNotContain("owner/repository", repoHtml);
        Assert.Equal(embeddingCalls, fixture.Embeddings.CallCount);

        var globalSearchHtml = await fixture.Client.GetStringAsync("/posts?q=Needle");
        Assert.Contains("Results for “Needle”", globalSearchHtml);
        Assert.Contains("Alpha report", globalSearchHtml);
        Assert.Equal(embeddingCalls + 1, fixture.Embeddings.CallCount);

        var searchHtml = await fixture.Client.GetStringAsync("/posts?repo=acme%2Falpha&q=Needle");
        Assert.Contains("Results for “Needle”", searchHtml);
        Assert.Contains("Alpha report", searchHtml);
        Assert.DoesNotContain("Beta report", searchHtml);
        Assert.Contains("type=\"hidden\" name=\"repo\" value=\"acme/alpha\"", searchHtml);
        Assert.Equal(embeddingCalls + 2, fixture.Embeddings.CallCount);
    }

    [Fact]
    public async Task Posts_TreatsWhitespaceQueryAsRecentAndRejectsOversizedQuery()
    {
        await using var fixture = await WebFixture.StartAsync();
        await fixture.CreatePostAsync("acme/alpha", "Alpha report", "Recent context.");
        var embeddingCalls = fixture.Embeddings.CallCount;

        using var blank = await fixture.Client.GetAsync("/posts?q=%20%20%20");
        var blankHtml = await blank.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Contains("Recent activity", blankHtml);
        Assert.Contains("Alpha report", blankHtml);
        Assert.DoesNotContain("Results for", blankHtml);
        Assert.Equal(embeddingCalls, fixture.Embeddings.CallCount);

        var oversizedQuery = new string('q', 501);
        using var oversized = await fixture.Client.GetAsync("/posts?q=" + oversizedQuery);
        var oversizedHtml = await oversized.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Contains("at most 500 characters", oversizedHtml);
        Assert.DoesNotContain(oversizedQuery, oversizedHtml);
    }

    [Fact]
    public async Task PostDetail_EncodesPersistedValuesPreservesLinesAndShowsChronologicalActivity()
    {
        await using var fixture = await WebFixture.StartAsync();
        var post = await fixture.Service.CreatePostAsync(new CreatePostInput(
            "repo\"><img src=x onerror=alert(1)>",
            "<script>alert(\"title\")</script>",
            "line one\nline two <b>not markup</b>",
            "branch\" onfocus=\"alert(2)",
            "commit<&>",
            "agent<&>",
            "model\"bad",
            "effort<script>"));

        await fixture.Service.VotePostAsync(new VotePostInput(post.Id, 1, "vote-agent-1", "vote-model-1"));
        await fixture.Service.VotePostAsync(new VotePostInput(post.Id, 1, "vote-agent-2", "vote-model-2"));
        await fixture.Service.VotePostAsync(new VotePostInput(post.Id, -1, "vote-agent-3", "vote-model-3"));

        await fixture.Service.CreateCommentAsync(new CreateCommentInput(
            post.Id,
            "comment <img src=x onerror=alert(3)>\nsecond line",
            "comment-branch",
            "comment-commit",
            "comment-agent",
            "comment-model",
            "comment-effort"));

        for (var index = 1; index <= 11; index++)
        {
            await fixture.Service.VerifyPostAsync(new VerifyPostInput(
                post.Id,
                index == 11 ? VerificationOutcome.WorkedWithChanges : VerificationOutcome.WorkedAsWritten,
                index == 11 ? "changed <script>alert(4)</script>" : $"verification {index}",
                $"verify-branch-{index}",
                $"verify-commit-{index}",
                $"verify-agent-{index}",
                $"verify-model-{index}",
                $"verify-effort-{index}"));
        }

        using var response = await fixture.Client.GetAsync($"/posts/{post.Id}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(&quot;title&quot;)&lt;/script&gt;", html);
        Assert.Contains("line one\nline two &lt;b&gt;not markup&lt;/b&gt;", html);
        Assert.Contains("class=\"prose\"", html);
        Assert.Contains("repo%22%3E%3Cimg%20src%3Dx%20onerror%3Dalert%281%29%3E", html);
        Assert.Contains("branch&quot; onfocus=&quot;alert(2)", html);
        Assert.Contains("latest 10 of 11 verification records", html);
        Assert.DoesNotContain("verification 1<", html);
        Assert.Contains("verification 2", html);
        Assert.Contains("WorkedWithChanges", html);
        Assert.Contains("changed &lt;script&gt;alert(4)&lt;/script&gt;", html);
        Assert.Contains("comment-model", html);
        Assert.Contains("verify-effort-11", html);
        Assert.Contains("<dt>Post ID</dt>", html);
        Assert.Contains("<dt>Repository</dt>", html);
        Assert.Contains("<dt>Branch</dt>", html);
        Assert.Contains("<dt>Commit</dt>", html);
        Assert.Contains("<dt>Agent</dt>", html);
        Assert.Contains("<dt>Model</dt>", html);
        Assert.Contains("<dt>Effort</dt>", html);
        Assert.Contains("<dt>Created</dt>", html);
        Assert.Contains("<dt>Last activity</dt>", html);
        Assert.Contains("<dt>Upvotes</dt><dd>2</dd>", html);
        Assert.Contains("<dt>Downvotes</dt><dd>1</dd>", html);
        Assert.Contains("<dt>WorkedAsWritten</dt><dd>10</dd>", html);
        Assert.Contains("<dt>WorkedWithChanges</dt><dd>1</dd>", html);
        Assert.Contains("<dt>DidNotWork</dt><dd>0</dd>", html);
        Assert.Contains("<dt>Comments</dt><dd>1</dd>", html);
        Assert.DoesNotContain("Supporting context", html);
        Assert.DoesNotContain("context-grid", html);
        Assert.DoesNotContain("count-grid", html);
        Assert.DoesNotContain("class=\"notice\"", html);

        var titleIndex = html.IndexOf("&lt;script&gt;alert(&quot;title&quot;)&lt;/script&gt;", StringComparison.Ordinal);
        var bodyIndex = html.IndexOf("line one\nline two", StringComparison.Ordinal);
        var metadataIndex = html.IndexOf("aria-label=\"Post metadata\"", StringComparison.Ordinal);
        var summaryIndex = html.IndexOf("aria-label=\"Post activity summary\"", StringComparison.Ordinal);
        var epistemicIndex = html.IndexOf("class=\"epistemic-note\"", StringComparison.Ordinal);
        var activityIndex = html.IndexOf("Thread activity", StringComparison.Ordinal);
        Assert.True(
            titleIndex >= 0 && titleIndex < bodyIndex && bodyIndex < metadataIndex &&
            metadataIndex < summaryIndex && summaryIndex < epistemicIndex && epistemicIndex < activityIndex,
            "Detail hierarchy should prioritize title and original content before compact secondary context and activity.");
        Assert.True(
            html.IndexOf("Comment <span", StringComparison.Ordinal) <
            html.IndexOf("Verification <span", StringComparison.Ordinal),
            "Same-timestamp activity should use the stable comment-before-verification tie-break.");
        Assert.True(
            html.IndexOf("verification 2", StringComparison.Ordinal) <
            html.IndexOf("changed &lt;script&gt;alert(4)&lt;/script&gt;", StringComparison.Ordinal),
            "Same-type activity should use the stable ID tie-break.");
    }

    [Fact]
    public async Task ReflectedFieldsAreEscapedAndMissingPostUsesHtml404()
    {
        await using var fixture = await WebFixture.StartAsync();
        const string malicious = "\"><input autofocus onfocus=alert(1)>";

        using var reflected = await fixture.Client.GetAsync(
            "/posts?repo=" + Uri.EscapeDataString(malicious));
        var reflectedHtml = await reflected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reflected.StatusCode);
        Assert.DoesNotContain("<input autofocus", reflectedHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&quot;&gt;&lt;input autofocus onfocus=alert(1)&gt;", reflectedHtml);
        Assert.Contains("type=\"hidden\" name=\"repo\" value=\"&quot;&gt;&lt;input autofocus onfocus=alert(1)&gt;\"", reflectedHtml);
        Assert.DoesNotContain("id=\"repo\"", reflectedHtml);

        await fixture.CreatePostAsync("acme/reflected", "Safe title", "needle script alert");
        const string maliciousQuery = "needle\"><script>alert(5)</script>";
        using var queryResponse = await fixture.Client.GetAsync(
            "/posts?q=" + Uri.EscapeDataString(maliciousQuery));
        var queryHtml = await queryResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.DoesNotContain("<script>alert(5)</script>", queryHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needle&quot;&gt;&lt;script&gt;alert(5)&lt;/script&gt;", queryHtml);

        using var missing = await fixture.Client.GetAsync("/posts/99999");
        var missingHtml = await missing.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("text/html", missing.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Post not found", missingHtml);
        Assert.DoesNotContain("KeyNotFoundException", missingHtml);
        AssertSecurityHeaders(missing);
    }

    [Fact]
    public async Task UnexpectedStorageFailureUsesGenericHtml500()
    {
        await using var fixture = await WebFixture.StartAsync();
        fixture.RemoveDatabaseForFailureTest();

        using var response = await fixture.Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Forum unavailable", html);
        Assert.DoesNotContain("SQLite", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", html, StringComparison.OrdinalIgnoreCase);
        AssertSecurityHeaders(response);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            "default-src 'none'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    private sealed class WebFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly WebApplication _app;

        private WebFixture(
            string databasePath,
            WebApplication app,
            HttpClient client,
            ForumService service,
            CountingEmbeddingProvider embeddings)
        {
            _databasePath = databasePath;
            _app = app;
            Client = client;
            Service = service;
            Embeddings = embeddings;
        }

        public HttpClient Client { get; }

        public ForumService Service { get; }

        public CountingEmbeddingProvider Embeddings { get; }

        public static async Task<WebFixture> StartAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"agent-forum-web-{Guid.NewGuid():N}.db");
            var embeddings = new CountingEmbeddingProvider();
            var clock = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 8, 9, 10, 11, TimeSpan.Zero));
            var app = HttpServerHost.Build(
                [],
                portOverride: 0,
                services =>
                {
                    services.AddSingleton(new DatabaseOptions { Path = databasePath });
                    services.AddSingleton(new EmbeddingOptions { ModelId = "test/web" });
                    services.AddSingleton<IEmbeddingProvider>(embeddings);
                    services.AddSingleton<TimeProvider>(clock);
                });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var service = app.Services.GetRequiredService<ForumService>();
            await service.InitializeAsync(timeout.Token);
            await app.StartAsync(timeout.Token);
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new WebFixture(databasePath, app, client, service, embeddings);
        }

        public Task<Post> CreatePostAsync(string repo, string title, string content)
            => Service.CreatePostAsync(new CreatePostInput(
                repo,
                title,
                content,
                "main",
                "0123456789abcdef",
                "web-test-agent",
                "web-test-model",
                "medium"));

        public void RemoveDatabaseForFailureTest()
        {
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                var path = _databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                var path = _databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public int CallCount { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(text);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new[] { 1f, 0f, 0f });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
