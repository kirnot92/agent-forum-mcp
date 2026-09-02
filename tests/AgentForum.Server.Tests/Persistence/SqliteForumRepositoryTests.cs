using System.Buffers.Binary;
using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentForum.Server.Tests.Persistence;

public sealed class SqliteForumRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-forum-repository-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Initialize_creates_fts5_schema_foreign_keys_and_indexes()
    {
        var repository = CreateRepository();

        await repository.InitializeAsync();

        await using var connection = OpenInspectionConnection();
        var objects = await ReadSchemaObjectsAsync(connection);

        Assert.Contains(("table", "posts"), objects);
        Assert.Contains(("table", "comments"), objects);
        Assert.Contains(("table", "votes"), objects);
        Assert.Contains(("table", "verifications"), objects);
        Assert.Contains(("table", "post_embeddings"), objects);
        Assert.Contains(("table", "posts_fts"), objects);
        Assert.Contains(("table", "post_activity_fts"), objects);
        Assert.Contains(("index", "ix_posts_repo"), objects);
        Assert.Contains(("index", "ix_comments_post_created"), objects);
        Assert.Contains(("index", "ix_votes_post"), objects);
        Assert.Contains(("index", "ix_verifications_post_created"), objects);

        await using var ftsCommand = connection.CreateCommand();
        ftsCommand.CommandText = "SELECT COUNT(*) FROM posts_fts WHERE posts_fts MATCH 'availability';";
        Assert.Equal(0L, (long)(await ftsCommand.ExecuteScalarAsync())!);

        await using var foreignKeyCommand = connection.CreateCommand();
        foreignKeyCommand.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, (long)(await foreignKeyCommand.ExecuteScalarAsync())!);

        await using var versionsCommand = connection.CreateCommand();
        versionsCommand.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        await using var versionReader = await versionsCommand.ExecuteReaderAsync();
        var versions = new List<long>();
        while (await versionReader.ReadAsync())
        {
            versions.Add(versionReader.GetInt64(0));
        }

        Assert.Equal([0L], versions);

        foreach (var table in new[] { "posts", "comments", "votes", "verifications" })
        {
            var columns = await ReadColumnNamesAsync(connection, table);
            Assert.Contains("agent", columns);
            Assert.DoesNotContain("model", columns);
            Assert.DoesNotContain("effort", columns);
        }
    }

    [Fact]
    public async Task Reopening_compatible_version_zero_database_does_not_mutate_schema_or_rebuild_fts()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("owner/repo", "fts sentinel", "body"), [1f], "model-a");

        await using (var connection = OpenInspectionConnection())
        {
            await ExecuteInspectionSqlAsync(connection, """
                UPDATE schema_migrations
                SET applied_at = 'reopen-sentinel'
                WHERE version = 0;
                """);
        }

        await repository.InitializeAsync();
        Assert.Equal([post.Id], await repository.SearchLexicalPostIdsAsync("owner/repo", "fts sentinel", 10));

        await using var reopened = OpenInspectionConnection();
        await using var check = reopened.CreateCommand();
        check.CommandText = "SELECT applied_at FROM schema_migrations WHERE version = 0;";
        Assert.Equal("reopen-sentinel", (string)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_zero_database_missing_required_object_fails_without_repair()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        await using (var connection = OpenInspectionConnection())
        {
            await ExecuteInspectionSqlAsync(connection, "DROP INDEX ix_posts_repo;");
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.InitializeAsync());
        Assert.Contains("version 0", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ix_posts_repo", exception.Message, StringComparison.Ordinal);
        Assert.Contains("recreate", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var inspection = OpenInspectionConnection();
        var objects = await ReadSchemaObjectsAsync(inspection);
        Assert.DoesNotContain(("index", "ix_posts_repo"), objects);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Existing_database_with_different_version_fails_without_mutation(int databaseVersion)
    {
        await using (var connection = OpenInspectionConnection())
        {
            await ExecuteInspectionSqlAsync(connection, $"""
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL) STRICT;
                INSERT INTO schema_migrations(version, applied_at) VALUES ({databaseVersion}, 'sentinel');
                CREATE TABLE legacy_sentinel(value TEXT NOT NULL) STRICT;
                INSERT INTO legacy_sentinel(value) VALUES ('unchanged');
                """);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository().InitializeAsync());
        Assert.Contains($"version {databaseVersion}", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expects version 0", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recreate", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var inspection = OpenInspectionConnection();
        var objects = await ReadSchemaObjectsAsync(inspection);
        Assert.DoesNotContain(("table", "posts"), objects);
        await using var sentinelCommand = inspection.CreateCommand();
        sentinelCommand.CommandText = "SELECT value FROM legacy_sentinel;";
        Assert.Equal("unchanged", (string)(await sentinelCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Unversioned_nonempty_database_fails_without_mutation()
    {
        await using (var connection = OpenInspectionConnection())
        {
            await ExecuteInspectionSqlAsync(connection, """
                CREATE TABLE posts(id INTEGER PRIMARY KEY, legacy_value TEXT NOT NULL) STRICT;
                INSERT INTO posts(legacy_value) VALUES ('unchanged');
                """);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRepository().InitializeAsync());
        Assert.Contains("nonempty", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version 0", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recreate", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var inspection = OpenInspectionConnection();
        var objects = await ReadSchemaObjectsAsync(inspection);
        Assert.DoesNotContain(("table", "schema_migrations"), objects);
        await using var sentinelCommand = inspection.CreateCommand();
        sentinelCommand.CommandText = "SELECT legacy_value FROM posts;";
        Assert.Equal("unchanged", (string)(await sentinelCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Entities_use_independent_natural_one_based_ids_and_persist_after_reopen()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var firstPost = await repository.CreatePostAsync(PostInput("repo-a", "Same", "Content"), [1f, 0f], "model-a");
        var duplicatePost = await repository.CreatePostAsync(PostInput("repo-a", "Same", "Content"), [0f, 1f], "model-a");
        var comment = await repository.CreateCommentAsync(CommentInput(firstPost.Id, "caveat"));
        var verification = await repository.AddVerificationAsync(VerificationInput(firstPost.Id, VerificationOutcome.WorkedAsWritten));

        Assert.Equal(1, firstPost.Id);
        Assert.Equal(2, duplicatePost.Id);
        Assert.Equal(1, comment.Id);
        Assert.Equal(1, verification.Id);

        var reopened = CreateRepository();
        await reopened.InitializeAsync();

        var result = await reopened.ReadPostAsync(firstPost.Id);
        Assert.Equal("repo-a", result.Post.Repo);
        Assert.Equal("Same", result.Post.Title);
        Assert.Equal("Content", result.Post.Content);
        Assert.Equal(1, result.CommentCount);
        Assert.Equal(1, result.Verifications.WorkedAsWrittenCount);

        var search = await reopened.SearchLexicalPostIdsAsync("repo-a", "Same Content", 10);
        Assert.Equal([1L, 2L], search);
    }

    [Fact]
    public async Task Agent_provenance_may_be_null_when_client_info_is_unavailable()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var post = await repository.CreatePostAsync(
            new CreatePostInput("repo-a", "post", "content", "main", "abc123"),
            [1f],
            "model-a");
        var comment = await repository.CreateCommentAsync(
            new CreateCommentInput(post.Id, "comment", "main", "def456"));
        var vote = await repository.AddVoteAsync(new VotePostInput(post.Id, 1));
        var verification = await repository.AddVerificationAsync(
            new VerifyPostInput(
                post.Id,
                VerificationOutcome.WorkedAsWritten,
                null,
                "main",
                "fed987"));

        Assert.Null(post.Agent);
        Assert.Null(comment.Agent);
        Assert.Null(vote.Agent);
        Assert.Null(verification.Agent);
        Assert.Null((await repository.ReadPostAsync(post.Id)).Post.Agent);
    }

    [Fact]
    public async Task CreatePost_stores_vector_and_fts_atomically_and_validates_vector_before_insert()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreatePostAsync(
            PostInput("repo-a", "invalid", "must not persist"),
            [float.NaN],
            "model-a"));

        Assert.Empty(await repository.SearchLexicalPostIdsAsync("repo-a", "invalid", 10));
        Assert.Empty(await repository.ReadAllStoredEmbeddingsAsync("model-a"));

        var vector = new[] { 0.25f, -0.5f, 0.75f };
        var post = await repository.CreatePostAsync(
            PostInput("repo-a", "Parser.cs failure", "Build emitted CS1002: ; expected"),
            vector,
            "model-a");

        Assert.Equal(1, post.Id);
        Assert.Equal([post.Id], await repository.SearchLexicalPostIdsAsync("repo-a", "Parser.cs CS1002", 10));

        var stored = Assert.Single(await repository.ReadAllStoredEmbeddingsAsync("model-a"));
        Assert.Equal("repo-a", stored.Repo);
        Assert.Equal(post.Id, stored.PostId);
        Assert.Equal("model-a", stored.ModelId);
        Assert.Equal(3, stored.Dimensions);
        Assert.Equal(vector, stored.Vector);

        await using var connection = OpenInspectionConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT vector_blob FROM post_embeddings WHERE post_id = $postId;";
        command.Parameters.AddWithValue("$postId", post.Id);
        var blob = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.Equal(BitConverter.SingleToInt32Bits(vector[0]), BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0, 4)));
        Assert.Equal(BitConverter.SingleToInt32Bits(vector[1]), BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4)));
        Assert.Equal(BitConverter.SingleToInt32Bits(vector[2]), BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8, 4)));
    }

    [Theory]
    [InlineData("Parser.cs CS1002", "Parser.cs", "Compiler emitted CS1002: ; expected")]
    [InlineData("한글검색 오류", "한글검색", "오류 원인과 해결 경로")]
    [InlineData("\"quoted\"", "quoted", "literal quote handling")]
    [InlineData("alpha OR beta", "alpha", "OR beta is written literally")]
    [InlineData("snake_case", "snake_case", "identifier token")]
    public async Task Lexical_search_handles_code_and_untrusted_fts_syntax(
        string query,
        string title,
        string content)
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("repo-a", title, content), [1f], "model-a");

        var result = await repository.SearchLexicalPostIdsAsync("repo-a", query, 10);

        Assert.Equal([post.Id], result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n")]
    [InlineData("\"'()[]{}:+-!@#$%^&*.,;?/\\|")]
    public async Task Lexical_search_returns_no_candidates_for_punctuation_only_queries(string query)
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("repo-a", "anything", "everything"), [1f], "model-a");

        Assert.Empty(await repository.SearchLexicalPostIdsAsync("repo-a", query, 10));
    }

    [Fact]
    public async Task Lexical_search_appends_deduplicated_activity_matches_after_original_post_matches()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var activityOnly = await repository.CreatePostAsync(PostInput("Owner/Repo", "activity", "plain body"), [1f], "model-a");
        await repository.CreateCommentAsync(CommentInput(activityOnly.Id, "precedence_token fresh_comment_only_token"));
        await repository.AddVerificationAsync(new VerifyPostInput(
            activityOnly.Id,
            VerificationOutcome.WorkedAsWritten,
            "verification_only_token",
            "main",
            "fed987"));

        var originalAndActivity = await repository.CreatePostAsync(
            PostInput("owner/repo", "precedence_token", "original post match"),
            [1f],
            "model-a");
        await repository.CreateCommentAsync(CommentInput(originalAndActivity.Id, "precedence_token repeated"));

        var otherRepo = await repository.CreatePostAsync(PostInput("other/repo", "other", "plain"), [1f], "model-a");
        await repository.CreateCommentAsync(CommentInput(otherRepo.Id, "precedence_token"));

        Assert.Equal(
            [originalAndActivity.Id, activityOnly.Id],
            await repository.SearchLexicalPostIdsAsync("https://github.com/OWNER/REPO.git", "precedence_token", 10));
        Assert.Equal(
            [activityOnly.Id],
            await repository.SearchLexicalPostIdsAsync("owner/repo", "verification_only_token", 10));
        Assert.Equal(
            [activityOnly.Id],
            await repository.SearchLexicalPostIdsAsync("owner/repo", "fresh_comment_only_token", 10));
    }

    [Fact]
    public async Task Global_lexical_search_finds_post_comment_and_verification_text_and_deduplicates_parent()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var bodyMatch = await repository.CreatePostAsync(
            PostInput("owner/one", "global_body_token", "original post text"),
            [1f],
            "model-a");
        var activityMatch = await repository.CreatePostAsync(
            PostInput("owner/two", "activity", "plain body"),
            [1f],
            "model-a");
        await repository.CreateCommentAsync(CommentInput(
            activityMatch.Id,
            "global_comment_token shared_activity_token"));
        await repository.AddVerificationAsync(new VerifyPostInput(
            activityMatch.Id,
            VerificationOutcome.WorkedWithChanges,
            "global_verification_token shared_activity_token",
            "main",
            "fed987"));

        Assert.Equal(
            [bodyMatch.Id],
            await repository.SearchLexicalPostIdsAsync(null, "global_body_token", 10));
        Assert.Equal(
            [activityMatch.Id],
            await repository.SearchLexicalPostIdsAsync(null, "global_comment_token", 10));
        Assert.Equal(
            [activityMatch.Id],
            await repository.SearchLexicalPostIdsAsync(null, "global_verification_token", 10));
        Assert.Equal(
            [activityMatch.Id],
            await repository.SearchLexicalPostIdsAsync(null, "shared_activity_token", 10));
    }

    [Fact]
    public async Task Blank_verification_notes_are_not_added_to_activity_search()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("owner/repo", "post", "body"), [1f], "model-a");
        await repository.AddVerificationAsync(new VerifyPostInput(
            post.Id,
            VerificationOutcome.WorkedAsWritten,
            "   ",
            "main",
            "fed987"));

        await using var connection = OpenInspectionConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM post_activity_fts;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Search_storage_and_hydration_are_strictly_scoped_to_repo()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var repoA = await repository.CreatePostAsync(
            PostInput("https://github.com/Owner/Repo-A.git", "shared token", "a"),
            [1f, 0f],
            "model-a");
        var repoB = await repository.CreatePostAsync(PostInput("owner/repo-b", "shared token", "b"), [0f, 1f], "model-a");

        Assert.Equal("owner/repo-a", repoA.Repo);
        Assert.Equal([repoA.Id], await repository.SearchLexicalPostIdsAsync("OWNER/REPO-A", "shared", 10));
        Assert.Equal([repoB.Id], await repository.SearchLexicalPostIdsAsync("owner/repo-b", "shared", 10));
        var storedEmbeddings = await repository.ReadAllStoredEmbeddingsAsync("model-a");
        Assert.Equal(repoA.Id, Assert.Single(storedEmbeddings, embedding => embedding.Repo == "owner/repo-a").PostId);
        Assert.Equal(repoB.Id, Assert.Single(storedEmbeddings, embedding => embedding.Repo == "owner/repo-b").PostId);

        var hydrated = await repository.ReadSearchResultsAsync("https://github.com/owner/repo-a", [repoB.Id, repoA.Id]);
        var result = Assert.Single(hydrated);
        Assert.Equal(repoA.Id, result.PostId);
        Assert.Equal("owner/repo-a", result.Repo);

        Assert.Equal(
            [repoA.Id, repoB.Id],
            await repository.SearchLexicalPostIdsAsync(null, "shared", 10));
        Assert.Equal(
            [repoA.Id, repoB.Id],
            storedEmbeddings.Select(embedding => embedding.PostId));
        Assert.Equal(
            [repoB.Id, repoA.Id],
            (await repository.ReadSearchResultsAsync(null, [repoB.Id, repoA.Id])).Select(post => post.PostId));
    }

    [Fact]
    public async Task Comments_and_verifications_update_activity_but_votes_do_not_mutate_post()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(
            new CreatePostInput("repo-a", "immutable title", "immutable content", "feature/a", "abc123", "codex"),
            [1f],
            "embedding-model");

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var vote = await repository.AddVoteAsync(new VotePostInput(post.Id, 1, "reader"));
        var afterVote = await repository.ReadPostAsync(post.Id);
        Assert.Equal(post.CreatedAt, afterVote.Post.LastActivityAt);
        Assert.Equal(1, afterVote.Votes.Upvotes);
        Assert.Equal(0, afterVote.Votes.Downvotes);
        Assert.Equal("reader", vote.Agent);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var comment = await repository.CreateCommentAsync(
            new CreateCommentInput(post.Id, "a caveat", "feature/b", "def456", "codex"));
        var afterComment = await repository.ReadPostAsync(post.Id);
        Assert.Equal(clock.UtcNow, afterComment.Post.LastActivityAt);
        Assert.Equal("immutable title", afterComment.Post.Title);
        Assert.Equal("immutable content", afterComment.Post.Content);
        Assert.Equal("feature/b", comment.Branch);
        Assert.Equal("def456", comment.Commit);
        Assert.Equal("codex", comment.Agent);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var verification = await repository.AddVerificationAsync(new VerifyPostInput(
            post.Id,
            VerificationOutcome.WorkedWithChanges,
            "needed one extra flag",
            "feature/c",
            "fed789",
            "codex"));
        var afterVerification = await repository.ReadPostAsync(post.Id);
        Assert.Equal(clock.UtcNow, afterVerification.Post.LastActivityAt);
        Assert.Equal(1, afterVerification.Verifications.WorkedWithChangesCount);
        Assert.Equal("needed one extra flag", verification.Note);
        Assert.Equal("feature/c", verification.Branch);
        Assert.Equal("fed789", verification.Commit);
    }

    [Fact]
    public async Task ReadComments_is_chronological_and_reports_deterministic_pagination()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("repo-a", "post", "content"), [1f], "model-a");

        // Equal timestamps exercise the numeric ID tie-breaker.
        await repository.CreateCommentAsync(CommentInput(post.Id, "first"));
        await repository.CreateCommentAsync(CommentInput(post.Id, "second"));
        await repository.CreateCommentAsync(CommentInput(post.Id, "third"));

        var page = await repository.ReadCommentsAsync(post.Id, 2, 1);

        Assert.Equal(post.Id, page.PostId);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Limit);
        Assert.Equal(1, page.Offset);
        Assert.Equal(["second", "third"], page.Comments.Select(comment => comment.Content));
        Assert.Equal([2L, 3L], page.Comments.Select(comment => comment.Id));
    }

    [Fact]
    public async Task ReadPost_returns_bounded_recent_details_newest_first_and_total_counts()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("owner/repo", "post", "content"), [1f], "model-a");

        for (var index = 1; index <= 5; index++)
        {
            await repository.CreateCommentAsync(CommentInput(post.Id, $"comment-{index}"));
        }

        for (var index = 1; index <= 12; index++)
        {
            await repository.AddVerificationAsync(new VerifyPostInput(
                post.Id,
                VerificationOutcome.WorkedAsWritten,
                $"verification-{index}",
                "main",
                $"commit-{index}"));
        }

        var result = await repository.ReadPostAsync(post.Id);

        Assert.Equal(5, result.CommentCount);
        Assert.Equal(12, result.VerificationCount);
        Assert.Equal(["comment-5", "comment-4", "comment-3"], result.RecentComments.Select(comment => comment.Content));
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(index => $"verification-{index}"),
            result.RecentVerifications.Select(verification => verification.Note));
        Assert.All(result.RecentComments, comment =>
        {
            Assert.Equal("main", comment.Branch);
            Assert.Equal("def456", comment.Commit);
            Assert.Equal("codex", comment.Agent);
        });

        var commentPage = await repository.ReadCommentsAsync(post.Id, 5, 0);
        Assert.Equal(5, commentPage.TotalCount);
        Assert.Equal(["comment-1", "comment-2", "comment-3", "comment-4", "comment-5"],
            commentPage.Comments.Select(comment => comment.Content));
    }

    [Fact]
    public async Task Distinct_embedding_model_ids_are_sorted_and_not_repo_scoped()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        await repository.CreatePostAsync(PostInput("owner/one", "one", "body"), [1f], "z-model");
        await repository.CreatePostAsync(PostInput("owner/two", "two", "body"), [1f], "a-model");
        await repository.CreatePostAsync(PostInput("owner/three", "three", "body"), [1f], "z-model");

        Assert.Equal(["a-model", "z-model"], await repository.ReadDistinctEmbeddingModelIdsAsync());
    }

    [Fact]
    public async Task Vote_and_verification_values_are_stored_as_raw_counts()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("repo-a", "post", "content"), [1f], "model-a");

        await repository.AddVoteAsync(new VotePostInput(post.Id, 1));
        await repository.AddVoteAsync(new VotePostInput(post.Id, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.AddVoteAsync(new VotePostInput(post.Id, 0)));

        foreach (var outcome in Enum.GetValues<VerificationOutcome>())
        {
            await repository.AddVerificationAsync(VerificationInput(post.Id, outcome));
        }

        var result = await repository.ReadPostAsync(post.Id);
        Assert.Equal(new VoteSummary(1, 1), result.Votes);
        Assert.Equal(new VerificationSummary(1, 1, 1, 1), result.Verifications);
    }

    [Fact]
    public async Task ReadRecentPosts_global_browse_orders_by_activity_then_descending_id()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var first = await repository.CreatePostAsync(PostInput("owner/one", "first", "body"), [1f], "model-a");
        var second = await repository.CreatePostAsync(PostInput("owner/two", "second", "body"), [1f], "model-a");
        var third = await repository.CreatePostAsync(PostInput("owner/three", "third", "body"), [1f], "model-a");

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await repository.CreateCommentAsync(CommentInput(first.Id, "new activity"));

        var results = await repository.ReadRecentPostsAsync(null, 10);

        Assert.Equal([first.Id, third.Id, second.Id], results.Select(result => result.PostId));
        Assert.Equal(["owner/one", "owner/three", "owner/two"], results.Select(result => result.Repo));
    }

    [Fact]
    public async Task ReadRecentPosts_normalizes_repository_scope()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var expected = await repository.CreatePostAsync(
            PostInput("owner/repo", "expected", "body"),
            [1f],
            "model-a");
        await repository.CreatePostAsync(PostInput("owner/other", "other", "body"), [1f], "model-a");

        var result = Assert.Single(await repository.ReadRecentPostsAsync(
            " git@github.com:OWNER/REPO.git ",
            10));

        Assert.Equal(expected.Id, result.PostId);
        Assert.Equal("owner/repo", result.Repo);
    }

    [Fact]
    public async Task ReadRecentPosts_honors_hard_limit_and_returns_compact_summaries()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var longContent = new string('x', 400);
        var post = await repository.CreatePostAsync(PostInput("repo-a", "summarized", longContent), [1f], "model-a");
        await repository.CreatePostAsync(PostInput("repo-a", "older", "body"), [1f], "model-a");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await repository.CreateCommentAsync(CommentInput(post.Id, "caveat"));
        await repository.AddVoteAsync(new VotePostInput(post.Id, 1));
        await repository.AddVerificationAsync(VerificationInput(post.Id, VerificationOutcome.DidNotWork));

        var compact = Assert.Single(await repository.ReadRecentPostsAsync(null, 1));

        Assert.Equal(post.Id, compact.PostId);
        Assert.Equal(1, compact.CommentCount);
        Assert.Equal(1, compact.Upvotes);
        Assert.Equal(1, compact.DidNotWorkCount);
        Assert.True(compact.Snippet.Length < longContent.Length);
        Assert.EndsWith("…", compact.Snippet);
    }

    [Fact]
    public async Task Compact_hydration_preserves_requested_order_counts_and_duplicates_without_full_threads()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var longContent = new string('x', 400) + " full tail";
        var first = await repository.CreatePostAsync(PostInput("repo-a", "first", longContent), [1f], "model-a");
        var second = await repository.CreatePostAsync(PostInput("repo-a", "second", "short"), [1f], "model-a");
        await repository.CreateCommentAsync(CommentInput(first.Id, "hidden comment"));
        await repository.AddVoteAsync(new VotePostInput(first.Id, 1));
        await repository.AddVerificationAsync(VerificationInput(first.Id, VerificationOutcome.DidNotWork));

        var results = await repository.ReadSearchResultsAsync("repo-a", [second.Id, first.Id, second.Id]);

        Assert.Equal([second.Id, first.Id, second.Id], results.Select(result => result.PostId));
        var compact = results[1];
        Assert.True(compact.Snippet.Length < longContent.Length);
        Assert.EndsWith("…", compact.Snippet);
        Assert.Equal(1, compact.CommentCount);
        Assert.Equal(1, compact.Upvotes);
        Assert.Equal(1, compact.DidNotWorkCount);
        Assert.DoesNotContain(typeof(PostSearchResult).GetProperties(), property => property.Name is "Content" or "Comments" or "Verifications");
    }

    [Fact]
    public async Task Compact_hydration_does_not_split_a_utf16_surrogate_pair_at_snippet_boundary()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var content = new string('x', 239) + "😀" + new string('y', 20);
        var post = await repository.CreatePostAsync(PostInput("repo-a", "unicode", content), [1f], "model-a");

        var result = Assert.Single(await repository.ReadSearchResultsAsync("repo-a", [post.Id]));

        Assert.Equal(new string('x', 239) + "…", result.Snippet);
        Assert.DoesNotContain(result.Snippet, char.IsSurrogate);
    }

    [Fact]
    public async Task Missing_parent_fails_clearly_for_reads_and_all_child_writes()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.ReadPostAsync(999));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.ReadCommentsAsync(999, 10, 0));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.CreateCommentAsync(CommentInput(999, "missing")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.AddVoteAsync(new VotePostInput(999, 1)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.AddVerificationAsync(
            VerificationInput(999, VerificationOutcome.DidNotWork)));
    }

    [Fact]
    public async Task Lexical_search_falls_back_to_any_term_after_all_term_matches()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var partialPost = await repository.CreatePostAsync(
            PostInput("owner/repo", "BindingExpression 런타임 타입", "compiled binding does not imply reflection"),
            [1f],
            "model-a");
        var fullPost = await repository.CreatePostAsync(
            PostInput("owner/repo", "왜 BindingExpression 이 나타나는가", "reflection 폴백과 무관하다"),
            [1f],
            "model-a");
        var activityOnly = await repository.CreatePostAsync(PostInput("owner/repo", "unrelated", "plain body"), [1f], "model-a");
        await repository.CreateCommentAsync(CommentInput(activityOnly.Id, "reflection is only mentioned in this comment"));
        await repository.CreatePostAsync(PostInput("owner/repo", "noise", "nothing shared"), [1f], "model-a");

        // Tier 1: every term in post text. Tier 2: any term in post text.
        // Tier 3 and 4: the same over comment and verification text.
        Assert.Equal(
            [fullPost.Id, partialPost.Id, activityOnly.Id],
            await repository.SearchLexicalPostIdsAsync("owner/repo", "왜 BindingExpression reflection", 10));

        // The limit still bounds the combined tiers.
        Assert.Equal(
            [fullPost.Id],
            await repository.SearchLexicalPostIdsAsync("owner/repo", "왜 BindingExpression reflection", 1));

        // A term that appears nowhere still yields partial matches instead of nothing.
        // Both posts match only through the OR tier, so their mutual order is BM25's.
        var partialMatches = await repository.SearchLexicalPostIdsAsync("owner/repo", "missing_token reflection", 10);
        Assert.Equal(3, partialMatches.Count);
        Assert.Equal([partialPost.Id, fullPost.Id], partialMatches.Take(2).Order());
        Assert.Equal(activityOnly.Id, partialMatches[2]);
    }

    [Fact]
    public void Fts_match_expressions_use_and_or_or_between_quoted_tokens()
    {
        Assert.Equal("\"alpha\" AND \"beta\"", SqliteForumRepository.BuildFtsMatchExpression("alpha beta"));
        Assert.Equal(
            "\"alpha\" OR \"beta\"",
            SqliteForumRepository.BuildFtsMatchExpression("alpha beta", SqliteForumRepository.FtsMatchMode.AnyTerm));
        Assert.Null(SqliteForumRepository.BuildFtsMatchExpression("!!!", SqliteForumRepository.FtsMatchMode.AnyTerm));
    }

    [Fact]
    public async Task NoLongerApplicable_is_counted_separately_and_latest_verification_is_exposed()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(PostInput("owner/repo", "post", "body"), [1f], "model-a");
        var untouched = await repository.CreatePostAsync(PostInput("owner/repo", "untouched", "body"), [1f], "model-a");

        await repository.AddVerificationAsync(new VerifyPostInput(
            post.Id, VerificationOutcome.WorkedAsWritten, null, "main", "commit-old"));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var latest = await repository.AddVerificationAsync(new VerifyPostInput(
            post.Id, VerificationOutcome.NoLongerApplicable, "X singleton was removed in commit B", "main", "commit-new"));

        var read = await repository.ReadPostAsync(post.Id);
        Assert.Equal(new VerificationSummary(1, 0, 0, 1), read.Verifications);
        Assert.Equal(2, read.VerificationCount);

        var results = await repository.ReadSearchResultsAsync("owner/repo", [post.Id, untouched.Id]);
        Assert.Equal(1, results[0].NoLongerApplicableCount);
        Assert.Equal(
            new LatestVerification(VerificationOutcome.NoLongerApplicable, "commit-new", latest.CreatedAt),
            results[0].LatestVerification);
        Assert.Null(results[1].LatestVerification);
        Assert.False(results[0].LexicalMatch);
        Assert.Null(results[0].VectorSimilarity);

        Assert.Equal(
            [post.Id],
            await repository.SearchLexicalPostIdsAsync("owner/repo", "singleton removed", 10));
    }

    private SqliteForumRepository CreateRepository(TimeProvider? timeProvider = null) =>
        new(new DatabaseOptions { Path = _databasePath }, timeProvider);

    private SqliteConnection OpenInspectionConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static CreatePostInput PostInput(string repo, string title, string content) =>
        new(repo, title, content, "main", "abc123", "codex");

    private static CreateCommentInput CommentInput(long postId, string content) =>
        new(postId, content, "main", "def456", "codex");

    private static VerifyPostInput VerificationInput(long postId, VerificationOutcome outcome) =>
        new(postId, outcome, "checked", "main", "fed987", "codex");

    private static async Task<HashSet<(string Type, string Name)>> ReadSchemaObjectsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name FROM sqlite_master;";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new HashSet<(string Type, string Name)>();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }

    private static async Task ExecuteInspectionSqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-journal");
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
