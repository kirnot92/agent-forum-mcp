using System.Globalization;
using System.Text.RegularExpressions;
using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using Microsoft.Data.Sqlite;

namespace AgentForum.Server.Persistence;

public sealed partial class SqliteForumRepository : IForumRepository
{
    private const int CurrentSchemaVersion = 0;
    private const int SnippetLength = 240;
    private const int HydrationBatchSize = 500;

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    public SqliteForumRepository(DatabaseOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            throw new ArgumentException("A non-empty SQLite database path is required.", nameof(options));
        }

        var databasePath = Path.GetFullPath(options.Path);
        var parentDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var schemaState = await ReadSchemaStateAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            if (schemaState.IsEmpty)
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    await ExecuteNonQueryAsync(connection, transaction, CurrentSchemaSql, cancellationToken)
                        .ConfigureAwait(false);
                    await ExecuteNonQueryAsync(
                            connection,
                            transaction,
                            "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $appliedAt);",
                            cancellationToken,
                            ("$version", CurrentSchemaVersion),
                            ("$appliedAt", FormatTimestamp(GetUtcNow())))
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "The configured SQLite runtime does not provide the required FTS5 module.",
                        exception);
                }

                return;
            }

            if (schemaState.Version is null)
            {
                throw IncompatibleSchema(
                    "The database is nonempty but does not contain one readable schema version.");
            }

            if (schemaState.Version != CurrentSchemaVersion)
            {
                throw IncompatibleSchema(
                    $"The database is version {schemaState.Version}, but the server expects version {CurrentSchemaVersion}.");
            }

            var missingObjects = RequiredSchemaObjects
                .Where(required => !schemaState.Objects.Contains(required))
                .Select(required => required.Name)
                .ToArray();
            if (missingObjects.Length > 0)
            {
                throw IncompatibleSchema(
                    $"The database claims version {CurrentSchemaVersion} but is missing required schema objects: " +
                    string.Join(", ", missingObjects) + ".");
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<Post> CreatePostAsync(
        CreatePostInput input,
        float[] normalizedEmbedding,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        ArgumentNullException.ThrowIfNull(normalizedEmbedding);
        RequireText(modelId, nameof(modelId));

        // Encoding performs complete dimension/finite-value validation before the
        // transaction starts. The caller is responsible for normalization.
        var vectorBlob = SqliteFloat32VectorCodec.Encode(normalizedEmbedding);
        var normalizedRepo = RepositoryKey.Normalize(input.Repo);
        var now = GetUtcNow();
        var timestamp = FormatTimestamp(now);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var postId = await InsertAndReadIdAsync(
                connection,
                transaction,
                """
                INSERT INTO posts(
                    repo, title, content, branch, commit_hash, agent,
                    created_at, last_activity_at)
                VALUES (
                    $repo, $title, $content, $branch, $commit, $agent,
                    $createdAt, $lastActivityAt);
                """,
                cancellationToken,
                ("$repo", normalizedRepo),
                ("$title", input.Title),
                ("$content", input.Content),
                ("$branch", input.Branch),
                ("$commit", input.Commit),
                ("$agent", input.Agent),
                ("$createdAt", timestamp),
                ("$lastActivityAt", timestamp))
            .ConfigureAwait(false);

        await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                INSERT INTO post_embeddings(post_id, model_id, dimensions, vector_blob)
                VALUES ($postId, $modelId, $dimensions, $vectorBlob);
                """,
                cancellationToken,
                ("$postId", postId),
                ("$modelId", modelId),
                ("$dimensions", normalizedEmbedding.Length),
                ("$vectorBlob", vectorBlob))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Post(
            postId,
            normalizedRepo,
            input.Title,
            input.Content,
            input.Branch,
            input.Commit,
            input.Agent,
            now,
            now);
    }

    public async Task<ReadPostResult> ReadPostAsync(
        long postId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(postId, nameof(postId));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReadPostSql;
        command.Parameters.AddWithValue("$postId", postId);

        ReadPostResult result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw MissingPost(postId);
            }

            var post = ReadPost(reader);
            result = new ReadPostResult(
                post,
                new VoteSummary(reader.GetInt32(9), reader.GetInt32(10)),
                new VerificationSummary(reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13)),
                Array.Empty<Verification>(),
                Array.Empty<Comment>(),
                reader.GetInt32(14),
                reader.GetInt32(15));
        }

        var recentVerifications = await ReadRecentVerificationsAsync(
                connection,
                transaction,
                postId,
                cancellationToken)
            .ConfigureAwait(false);
        var recentComments = await ReadRecentCommentsAsync(
                connection,
                transaction,
                postId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result with
        {
            RecentVerifications = recentVerifications,
            RecentComments = recentComments
        };
    }

    public async Task<Comment> CreateCommentAsync(
        CreateCommentInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        var now = GetUtcNow();
        var timestamp = FormatTimestamp(now);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsurePostExistsAsync(connection, transaction, input.PostId, cancellationToken)
            .ConfigureAwait(false);

        var commentId = await InsertAndReadIdAsync(
                connection,
                transaction,
                """
                INSERT INTO comments(
                    post_id, content, branch, commit_hash, agent, created_at)
                VALUES (
                    $postId, $content, $branch, $commit, $agent, $createdAt);
                """,
                cancellationToken,
                ("$postId", input.PostId),
                ("$content", input.Content),
                ("$branch", input.Branch),
                ("$commit", input.Commit),
                ("$agent", input.Agent),
                ("$createdAt", timestamp))
            .ConfigureAwait(false);

        await UpdateLastActivityAsync(connection, transaction, input.PostId, timestamp, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Comment(
            commentId,
            input.PostId,
            input.Content,
            input.Branch,
            input.Commit,
            input.Agent,
            now);
    }

    public async Task<ReadCommentsResult> ReadCommentsAsync(
        long postId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ValidateId(postId, nameof(postId));
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The comment limit must be positive.");
        }

        ForumValidation.ValidateCommentOffset(offset);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsurePostExistsAsync(connection, transaction, postId, cancellationToken).ConfigureAwait(false);

        var totalCount = await ExecuteInt32ScalarAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM comments WHERE post_id = $postId;",
                cancellationToken,
                ("$postId", postId))
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, post_id, content, branch, commit_hash, agent, created_at
            FROM comments
            WHERE post_id = $postId
            ORDER BY created_at, id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$postId", postId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var comments = new List<Comment>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                comments.Add(new Comment(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    GetNullableString(reader, 5),
                    ParseTimestamp(reader.GetString(6))));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReadCommentsResult(postId, comments, totalCount, limit, offset);
    }

    public async Task<Vote> AddVoteAsync(
        VotePostInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        var now = GetUtcNow();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsurePostExistsAsync(connection, transaction, input.PostId, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                INSERT INTO votes(post_id, agent, value, created_at)
                VALUES ($postId, $agent, $value, $createdAt);
                """,
                cancellationToken,
                ("$postId", input.PostId),
                ("$agent", input.Agent),
                ("$value", input.Value),
                ("$createdAt", FormatTimestamp(now)))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Vote(input.PostId, input.Agent, input.Value, now);
    }

    public async Task<Verification> AddVerificationAsync(
        VerifyPostInput input,
        CancellationToken cancellationToken = default)
    {
        ForumValidation.Validate(input);
        var now = GetUtcNow();
        var timestamp = FormatTimestamp(now);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsurePostExistsAsync(connection, transaction, input.PostId, cancellationToken)
            .ConfigureAwait(false);

        var verificationId = await InsertAndReadIdAsync(
                connection,
                transaction,
                """
                INSERT INTO verifications(
                    post_id, outcome, note, branch, commit_hash, agent, created_at)
                VALUES (
                    $postId, $outcome, $note, $branch, $commit, $agent, $createdAt);
                """,
                cancellationToken,
                ("$postId", input.PostId),
                ("$outcome", (int)input.Outcome),
                ("$note", input.Note),
                ("$branch", input.Branch),
                ("$commit", input.Commit),
                ("$agent", input.Agent),
                ("$createdAt", timestamp))
            .ConfigureAwait(false);

        await UpdateLastActivityAsync(connection, transaction, input.PostId, timestamp, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Verification(
            verificationId,
            input.PostId,
            input.Outcome,
            input.Note,
            input.Branch,
            input.Commit,
            input.Agent,
            now);
    }

    public async Task<IReadOnlyList<long>> SearchLexicalPostIdsAsync(
        string? repo,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The lexical result limit must be positive.");
        }

        var normalizedRepo = repo is null ? null : RepositoryKey.Normalize(repo);
        var matchExpression = BuildFtsMatchExpression(query);
        if (matchExpression is null)
        {
            return Array.Empty<long>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var postIds = new List<long>(limit);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = normalizedRepo is null
                ? """
                    SELECT posts_fts.rowid
                    FROM posts_fts
                    INNER JOIN posts ON posts.id = posts_fts.rowid
                    WHERE posts_fts MATCH $match
                    ORDER BY bm25(posts_fts), posts_fts.rowid
                    LIMIT $limit;
                    """
                : """
                    SELECT posts_fts.rowid
                    FROM posts_fts
                    INNER JOIN posts ON posts.id = posts_fts.rowid
                    WHERE posts_fts MATCH $match AND posts.repo = $repo
                    ORDER BY bm25(posts_fts), posts_fts.rowid
                    LIMIT $limit;
                    """;
            command.Parameters.AddWithValue("$match", matchExpression);
            if (normalizedRepo is not null)
            {
                command.Parameters.AddWithValue("$repo", normalizedRepo);
            }

            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                postIds.Add(reader.GetInt64(0));
            }
        }

        if (postIds.Count == limit)
        {
            return postIds;
        }

        var seenPostIds = postIds.ToHashSet();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = normalizedRepo is null
                ? """
                    SELECT CAST(post_activity_fts.post_id AS INTEGER)
                    FROM post_activity_fts
                    INNER JOIN posts ON posts.id = CAST(post_activity_fts.post_id AS INTEGER)
                    WHERE post_activity_fts MATCH $match
                    ORDER BY bm25(post_activity_fts), post_activity_fts.rowid;
                    """
                : """
                    SELECT CAST(post_activity_fts.post_id AS INTEGER)
                    FROM post_activity_fts
                    INNER JOIN posts ON posts.id = CAST(post_activity_fts.post_id AS INTEGER)
                    WHERE post_activity_fts MATCH $match AND posts.repo = $repo
                    ORDER BY bm25(post_activity_fts), post_activity_fts.rowid;
                    """;
            command.Parameters.AddWithValue("$match", matchExpression);
            if (normalizedRepo is not null)
            {
                command.Parameters.AddWithValue("$repo", normalizedRepo);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (postIds.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var postId = reader.GetInt64(0);
                if (seenPostIds.Add(postId))
                {
                    postIds.Add(postId);
                }
            }
        }

        return postIds;
    }

    public async Task<IReadOnlyList<StoredPostEmbedding>> ReadStoredEmbeddingsAsync(
        string? repo,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        RequireText(modelId, nameof(modelId));
        var normalizedRepo = repo is null ? null : RepositoryKey.Normalize(repo);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = normalizedRepo is null
            ? """
                SELECT e.post_id, e.model_id, e.dimensions, e.vector_blob
                FROM post_embeddings AS e
                INNER JOIN posts AS p ON p.id = e.post_id
                WHERE e.model_id = $modelId
                ORDER BY e.post_id;
                """
            : """
                SELECT e.post_id, e.model_id, e.dimensions, e.vector_blob
                FROM post_embeddings AS e
                INNER JOIN posts AS p ON p.id = e.post_id
                WHERE p.repo = $repo AND e.model_id = $modelId
                ORDER BY e.post_id;
                """;
        if (normalizedRepo is not null)
        {
            command.Parameters.AddWithValue("$repo", normalizedRepo);
        }

        command.Parameters.AddWithValue("$modelId", modelId);

        var embeddings = new List<StoredPostEmbedding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimensions = reader.GetInt32(2);
            var blob = (byte[])reader.GetValue(3);
            embeddings.Add(new StoredPostEmbedding(
                reader.GetInt64(0),
                reader.GetString(1),
                dimensions,
                SqliteFloat32VectorCodec.Decode(blob, dimensions)));
        }

        return embeddings;
    }

    public async Task<IReadOnlyList<string>> ReadDistinctEmbeddingModelIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT model_id
            FROM post_embeddings
            ORDER BY model_id COLLATE BINARY;
            """;

        var modelIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            modelIds.Add(reader.GetString(0));
        }

        return modelIds;
    }

    public async Task<IReadOnlyList<PostSearchResult>> ReadRecentPostsAsync(
        string? repo,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The recent post limit must be positive.");
        }

        var normalizedRepo = repo is null ? null : RepositoryKey.Normalize(repo);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = normalizedRepo is null
            ? $"""
                {CompactPostProjectionSql}
                ORDER BY p.last_activity_at DESC, p.id DESC
                LIMIT $limit;
                """
            : $"""
                {CompactPostProjectionSql}
                WHERE p.repo = $repo
                ORDER BY p.last_activity_at DESC, p.id DESC
                LIMIT $limit;
                """;
        if (normalizedRepo is not null)
        {
            command.Parameters.AddWithValue("$repo", normalizedRepo);
        }

        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<PostSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSearchResult(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<PostSearchResult>> ReadSearchResultsAsync(
        string? repo,
        IReadOnlyCollection<long> postIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postIds);
        var normalizedRepo = repo is null ? null : RepositoryKey.Normalize(repo);

        if (postIds.Count == 0)
        {
            return Array.Empty<PostSearchResult>();
        }

        foreach (var postId in postIds)
        {
            ValidateId(postId, nameof(postIds));
        }

        var distinctIds = postIds.Distinct().ToArray();
        var resultsById = new Dictionary<long, PostSearchResult>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in distinctIds.Chunk(HydrationBatchSize))
        {
            await using var command = connection.CreateCommand();
            var parameterNames = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                var parameterName = $"$id{index}";
                parameterNames[index] = parameterName;
                command.Parameters.AddWithValue(parameterName, batch[index]);
            }

            command.CommandText = normalizedRepo is null
                ? $"""
                    {CompactPostProjectionSql}
                    WHERE p.id IN ({string.Join(", ", parameterNames)});
                    """
                : $"""
                    {CompactPostProjectionSql}
                    WHERE p.repo = $repo AND p.id IN ({string.Join(", ", parameterNames)});
                    """;
            if (normalizedRepo is not null)
            {
                command.Parameters.AddWithValue("$repo", normalizedRepo);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = ReadSearchResult(reader);
                resultsById.Add(result.PostId, result);
            }
        }

        // Preserve the fused-ranking order supplied by the caller. Repeated input
        // IDs are intentionally preserved; this layer does not silently merge them.
        return postIds
            .Where(resultsById.ContainsKey)
            .Select(postId => resultsById[postId])
            .ToArray();
    }

    internal static string? BuildFtsMatchExpression(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = FtsTokenRegex()
            .Matches(query)
            .Select(match => match.Value)
            .ToArray();

        return tokens.Length == 0
            ? null
            : string.Join(" AND ", tokens.Select(token => $"\"{token}\""));
    }

    private static async Task<SchemaState> ReadSchemaStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var objects = new HashSet<SchemaObject>();
        await using (var objectsCommand = connection.CreateCommand())
        {
            objectsCommand.CommandText = """
                SELECT type, name
                FROM sqlite_master
                WHERE name NOT LIKE 'sqlite_%';
                """;
            await using var reader = await objectsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objects.Add(new SchemaObject(reader.GetString(0), reader.GetString(1)));
            }
        }

        if (objects.Count == 0)
        {
            return new SchemaState(true, null, objects);
        }

        if (!objects.Contains(new SchemaObject("table", "schema_migrations")))
        {
            return new SchemaState(false, null, objects);
        }

        try
        {
            var versions = new List<int>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                versions.Add(reader.GetInt32(0));
            }

            return versions.Count == 1
                ? new SchemaState(false, versions[0], objects)
                : new SchemaState(false, null, objects);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw IncompatibleSchema("The database schema version cannot be read.", exception);
        }
    }

    private static InvalidOperationException IncompatibleSchema(string detail, Exception? innerException = null) =>
        new(
            $"{detail} This server requires database schema version {CurrentSchemaVersion}; " +
            "recreate the database instead of upgrading it in place.",
            innerException);

    private static async Task<IReadOnlyList<Verification>> ReadRecentVerificationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long postId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, post_id, outcome, note, branch, commit_hash, agent, created_at
            FROM verifications
            WHERE post_id = $postId
            ORDER BY created_at DESC, id DESC
            LIMIT 10;
            """;
        command.Parameters.AddWithValue("$postId", postId);

        var verifications = new List<Verification>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            verifications.Add(new Verification(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (VerificationOutcome)reader.GetInt32(2),
                GetNullableString(reader, 3),
                reader.GetString(4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                ParseTimestamp(reader.GetString(7))));
        }

        return verifications;
    }

    private static async Task<IReadOnlyList<Comment>> ReadRecentCommentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long postId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, post_id, content, branch, commit_hash, agent, created_at
            FROM comments
            WHERE post_id = $postId
            ORDER BY created_at DESC, id DESC
            LIMIT 3;
            """;
        command.Parameters.AddWithValue("$postId", postId);

        var comments = new List<Comment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            comments.Add(new Comment(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                GetNullableString(reader, 5),
                ParseTimestamp(reader.GetString(6))));
        }

        return comments;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task EnsurePostExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long postId,
        CancellationToken cancellationToken)
    {
        ValidateId(postId, nameof(postId));
        var exists = await ExecuteInt32ScalarAsync(
                connection,
                transaction,
                "SELECT EXISTS(SELECT 1 FROM posts WHERE id = $postId);",
                cancellationToken,
                ("$postId", postId))
            .ConfigureAwait(false);

        if (exists == 0)
        {
            throw MissingPost(postId);
        }
    }

    private static async Task UpdateLastActivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long postId,
        string timestamp,
        CancellationToken cancellationToken)
    {
        var affectedRows = await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE posts
                SET last_activity_at = CASE
                    WHEN last_activity_at < $timestamp THEN $timestamp
                    ELSE last_activity_at
                END
                WHERE id = $postId;
                """,
                cancellationToken,
                ("$timestamp", timestamp),
                ("$postId", postId))
            .ConfigureAwait(false);

        if (affectedRows != 1)
        {
            throw MissingPost(postId);
        }
    }

    private static async Task<long> InsertAndReadIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string insertSql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{insertSql}\nSELECT last_insert_rowid();";
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteInt32ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static Post ReadPost(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        GetNullableString(reader, 6),
        ParseTimestamp(reader.GetString(7)),
        ParseTimestamp(reader.GetString(8)));

    private static PostSearchResult ReadSearchResult(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        CreateSnippet(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        ParseTimestamp(reader.GetString(6)),
        ParseTimestamp(reader.GetString(7)),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetInt32(12),
        reader.GetInt32(13));

    private static string CreateSnippet(string content)
    {
        if (content.Length <= SnippetLength)
        {
            return content;
        }

        var end = SnippetLength;
        if (char.IsHighSurrogate(content[end - 1]) && char.IsLowSurrogate(content[end]))
        {
            end--;
        }

        return string.Concat(content.AsSpan(0, end).TrimEnd(), "…");
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.ParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }

    private static void ValidateId(long id, string parameterName)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, id, "SQLite entity IDs must be positive.");
        }
    }

    private static KeyNotFoundException MissingPost(long postId) =>
        new($"Forum post {postId} does not exist.");

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex FtsTokenRegex();

    private const string ReadPostSql = """
        SELECT
            p.id, p.repo, p.title, p.content, p.branch, p.commit_hash,
            p.agent, p.created_at, p.last_activity_at,
            COALESCE((SELECT SUM(CASE WHEN value = 1 THEN 1 ELSE 0 END) FROM votes WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN value = -1 THEN 1 ELSE 0 END) FROM votes WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN outcome = 0 THEN 1 ELSE 0 END) FROM verifications WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN outcome = 1 THEN 1 ELSE 0 END) FROM verifications WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN outcome = 2 THEN 1 ELSE 0 END) FROM verifications WHERE post_id = p.id), 0),
            (SELECT COUNT(*) FROM comments WHERE post_id = p.id),
            (SELECT COUNT(*) FROM verifications WHERE post_id = p.id)
        FROM posts AS p
        WHERE p.id = $postId;
        """;

    private const string CompactPostProjectionSql = """
        SELECT
            p.id, p.repo, p.title, p.content, p.branch, p.commit_hash,
            p.created_at, p.last_activity_at,
            COALESCE((SELECT SUM(CASE WHEN value = 1 THEN 1 ELSE 0 END) FROM votes WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN value = -1 THEN 1 ELSE 0 END) FROM votes WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN outcome = 0 THEN 1 ELSE 0 END) FROM verifications WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN outcome = 1 THEN 1 ELSE 0 END) FROM verifications WHERE post_id = p.id), 0),
            COALESCE((SELECT SUM(CASE WHEN outcome = 2 THEN 1 ELSE 0 END) FROM verifications WHERE post_id = p.id), 0),
            (SELECT COUNT(*) FROM comments WHERE post_id = p.id)
        FROM posts AS p
        """;

    private sealed record SchemaObject(string Type, string Name);

    private sealed record SchemaState(
        bool IsEmpty,
        int? Version,
        IReadOnlySet<SchemaObject> Objects);

    private static readonly SchemaObject[] RequiredSchemaObjects =
    [
        new("table", "schema_migrations"),
        new("table", "posts"),
        new("table", "comments"),
        new("table", "votes"),
        new("table", "verifications"),
        new("table", "post_embeddings"),
        new("table", "posts_fts"),
        new("table", "post_activity_fts"),
        new("trigger", "posts_fts_after_insert"),
        new("trigger", "posts_fts_after_delete"),
        new("trigger", "posts_fts_after_content_update"),
        new("trigger", "comments_activity_fts_after_insert"),
        new("trigger", "verifications_activity_fts_after_insert"),
        new("index", "ix_posts_repo"),
        new("index", "ix_posts_repo_last_activity"),
        new("index", "ix_comments_post_created"),
        new("index", "ix_votes_post"),
        new("index", "ix_verifications_post_created"),
        new("index", "ix_post_embeddings_model_post")
    ];

    private const string CurrentSchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS posts (
            id INTEGER PRIMARY KEY,
            repo TEXT NOT NULL CHECK (length(trim(repo)) > 0),
            title TEXT NOT NULL CHECK (length(trim(title)) > 0),
            content TEXT NOT NULL CHECK (length(trim(content)) > 0),
            branch TEXT NOT NULL CHECK (length(trim(branch)) > 0),
            commit_hash TEXT NOT NULL CHECK (length(trim(commit_hash)) > 0),
            agent TEXT NULL,
            created_at TEXT NOT NULL,
            last_activity_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS comments (
            id INTEGER PRIMARY KEY,
            post_id INTEGER NOT NULL REFERENCES posts(id),
            content TEXT NOT NULL CHECK (length(trim(content)) > 0),
            branch TEXT NOT NULL CHECK (length(trim(branch)) > 0),
            commit_hash TEXT NOT NULL CHECK (length(trim(commit_hash)) > 0),
            agent TEXT NULL,
            created_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS votes (
            post_id INTEGER NOT NULL REFERENCES posts(id),
            agent TEXT NULL,
            value INTEGER NOT NULL CHECK (value IN (-1, 1)),
            created_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS verifications (
            id INTEGER PRIMARY KEY,
            post_id INTEGER NOT NULL REFERENCES posts(id),
            outcome INTEGER NOT NULL CHECK (outcome IN (0, 1, 2)),
            note TEXT NULL,
            branch TEXT NOT NULL CHECK (length(trim(branch)) > 0),
            commit_hash TEXT NOT NULL CHECK (length(trim(commit_hash)) > 0),
            agent TEXT NULL,
            created_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS post_embeddings (
            post_id INTEGER PRIMARY KEY REFERENCES posts(id),
            model_id TEXT NOT NULL CHECK (length(trim(model_id)) > 0),
            dimensions INTEGER NOT NULL CHECK (dimensions > 0),
            vector_blob BLOB NOT NULL CHECK (length(vector_blob) = dimensions * 4)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_posts_repo ON posts(repo);
        CREATE INDEX IF NOT EXISTS ix_posts_repo_last_activity ON posts(repo, last_activity_at DESC, id);
        CREATE INDEX IF NOT EXISTS ix_comments_post_created ON comments(post_id, created_at, id);
        CREATE INDEX IF NOT EXISTS ix_votes_post ON votes(post_id);
        CREATE INDEX IF NOT EXISTS ix_verifications_post_created ON verifications(post_id, created_at, id);
        CREATE INDEX IF NOT EXISTS ix_post_embeddings_model_post ON post_embeddings(model_id, post_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS posts_fts USING fts5(
            title,
            content,
            content = 'posts',
            content_rowid = 'id',
            tokenize = "unicode61 tokenchars '_'"
        );

        CREATE TRIGGER IF NOT EXISTS posts_fts_after_insert
        AFTER INSERT ON posts
        BEGIN
            INSERT INTO posts_fts(rowid, title, content)
            VALUES (new.id, new.title, new.content);
        END;

        CREATE TRIGGER IF NOT EXISTS posts_fts_after_delete
        AFTER DELETE ON posts
        BEGIN
            INSERT INTO posts_fts(posts_fts, rowid, title, content)
            VALUES ('delete', old.id, old.title, old.content);
        END;

        CREATE TRIGGER IF NOT EXISTS posts_fts_after_content_update
        AFTER UPDATE OF title, content ON posts
        BEGIN
            INSERT INTO posts_fts(posts_fts, rowid, title, content)
            VALUES ('delete', old.id, old.title, old.content);
            INSERT INTO posts_fts(rowid, title, content)
            VALUES (new.id, new.title, new.content);
        END;

        CREATE VIRTUAL TABLE post_activity_fts USING fts5(
            post_id UNINDEXED,
            activity_type UNINDEXED,
            content,
            tokenize = "unicode61 tokenchars '_'"
        );

        CREATE TRIGGER comments_activity_fts_after_insert
        AFTER INSERT ON comments
        BEGIN
            INSERT INTO post_activity_fts(post_id, activity_type, content)
            VALUES (new.post_id, 'comment', new.content);
        END;

        CREATE TRIGGER verifications_activity_fts_after_insert
        AFTER INSERT ON verifications
        WHEN new.note IS NOT NULL AND length(trim(new.note)) > 0
        BEGIN
            INSERT INTO post_activity_fts(post_id, activity_type, content)
            VALUES (new.post_id, 'verification', new.note);
        END;
        """;
}
