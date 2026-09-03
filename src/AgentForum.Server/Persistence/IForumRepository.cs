using AgentForum.Server.Domain;

namespace AgentForum.Server.Persistence;

public interface IForumRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Post> CreatePostAsync(
        CreatePostInput input,
        float[] normalizedEmbedding,
        string modelId,
        CancellationToken cancellationToken = default);

    Task<ReadPostResult> ReadPostAsync(
        long postId,
        CancellationToken cancellationToken = default);

    Task<Comment> CreateCommentAsync(
        CreateCommentInput input,
        CancellationToken cancellationToken = default);

    Task<ReadCommentsResult> ReadCommentsAsync(
        long postId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<Vote> AddVoteAsync(
        VotePostInput input,
        CancellationToken cancellationToken = default);

    Task<Verification> AddVerificationAsync(
        VerifyPostInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LexicalPostHit>> SearchLexicalPostsAsync(
        string? repo,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredPostEmbedding>> ReadAllStoredEmbeddingsAsync(
        string modelId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ReadDistinctEmbeddingModelIdsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostSearchResult>> ReadRecentPostsAsync(
        string? repo,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PostSearchResult>> ReadSearchResultsAsync(
        string? repo,
        IReadOnlyCollection<long> postIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One lexical candidate post together with the sources that matched. The order
/// of the returned hits is the lexical ranking; <see cref="MatchTypes"/> is
/// retrieval provenance and never participates in ranking.
/// </summary>
public sealed record LexicalPostHit(
    long PostId,
    IReadOnlyList<LexicalMatchType> MatchTypes);

public sealed record StoredPostEmbedding(
    string Repo,
    long PostId,
    string ModelId,
    int Dimensions,
    float[] Vector);
