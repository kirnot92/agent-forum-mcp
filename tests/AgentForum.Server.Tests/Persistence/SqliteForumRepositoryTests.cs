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
    public async Task CreatePost_stores_vector_and_fts_atomically_and_validates_vector_before_insert()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreatePostAsync(
            PostInput("repo-a", "invalid", "must not persist"),
            [float.NaN],
            "model-a"));

        Assert.Empty(await repository.SearchLexicalPostIdsAsync("repo-a", "invalid", 10));
        Assert.Empty(await repository.ReadStoredEmbeddingsAsync("repo-a", "model-a"));

        var vector = new[] { 0.25f, -0.5f, 0.75f };
        var post = await repository.CreatePostAsync(
            PostInput("repo-a", "Parser.cs failure", "Build emitted CS1002: ; expected"),
            vector,
            "model-a");

        Assert.Equal(1, post.Id);
        Assert.Equal([post.Id], await repository.SearchLexicalPostIdsAsync("repo-a", "Parser.cs CS1002", 10));

        var stored = Assert.Single(await repository.ReadStoredEmbeddingsAsync("repo-a", "model-a"));
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
    public async Task Search_storage_and_hydration_are_strictly_scoped_to_repo()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();

        var repoA = await repository.CreatePostAsync(PostInput("repo-a", "shared token", "a"), [1f, 0f], "model-a");
        var repoB = await repository.CreatePostAsync(PostInput("repo-b", "shared token", "b"), [0f, 1f], "model-a");

        Assert.Equal([repoA.Id], await repository.SearchLexicalPostIdsAsync("repo-a", "shared", 10));
        Assert.Equal([repoB.Id], await repository.SearchLexicalPostIdsAsync("repo-b", "shared", 10));
        Assert.Equal(repoA.Id, Assert.Single(await repository.ReadStoredEmbeddingsAsync("repo-a", "model-a")).PostId);
        Assert.Equal(repoB.Id, Assert.Single(await repository.ReadStoredEmbeddingsAsync("repo-b", "model-a")).PostId);

        var hydrated = await repository.ReadSearchResultsAsync("repo-a", [repoB.Id, repoA.Id]);
        var result = Assert.Single(hydrated);
        Assert.Equal(repoA.Id, result.PostId);
        Assert.Equal("repo-a", result.Repo);
    }

    [Fact]
    public async Task Comments_and_verifications_update_activity_but_votes_do_not_mutate_post()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var repository = CreateRepository(clock);
        await repository.InitializeAsync();
        var post = await repository.CreatePostAsync(
            new CreatePostInput("repo-a", "immutable title", "immutable content", "feature/a", "abc123", "codex", "model-x", "high"),
            [1f],
            "embedding-model");

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var vote = await repository.AddVoteAsync(new VotePostInput(post.Id, 1, "reader", "model-y"));
        var afterVote = await repository.ReadPostAsync(post.Id);
        Assert.Equal(post.CreatedAt, afterVote.Post.LastActivityAt);
        Assert.Equal(1, afterVote.Votes.Upvotes);
        Assert.Equal(0, afterVote.Votes.Downvotes);
        Assert.Equal("reader", vote.Agent);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var comment = await repository.CreateCommentAsync(
            new CreateCommentInput(post.Id, "a caveat", "feature/b", "def456", "codex", "model-z", "medium"));
        var afterComment = await repository.ReadPostAsync(post.Id);
        Assert.Equal(clock.UtcNow, afterComment.Post.LastActivityAt);
        Assert.Equal("immutable title", afterComment.Post.Title);
        Assert.Equal("immutable content", afterComment.Post.Content);
        Assert.Equal("feature/b", comment.Branch);
        Assert.Equal("def456", comment.Commit);
        Assert.Equal("codex", comment.Agent);
        Assert.Equal("model-z", comment.Model);
        Assert.Equal("medium", comment.Effort);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var verification = await repository.AddVerificationAsync(new VerifyPostInput(
            post.Id,
            VerificationOutcome.WorkedWithChanges,
            "needed one extra flag",
            "feature/c",
            "fed789",
            "codex",
            "model-v",
            "low"));
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
        Assert.Equal(new VerificationSummary(1, 1, 1), result.Verifications);
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
        new(repo, title, content, "main", "abc123", "codex", "model", "high");

    private static CreateCommentInput CommentInput(long postId, string content) =>
        new(postId, content, "main", "def456", "codex", "model", "medium");

    private static VerifyPostInput VerificationInput(long postId, VerificationOutcome outcome) =>
        new(postId, outcome, "checked", "main", "fed987", "codex", "model", "low");

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
