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

    Task<IReadOnlyList<long>> SearchLexicalPostIdsAsync(
        string? repo,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredPostEmbedding>> ReadStoredEmbeddingsAsync(
        string? repo,
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

public sealed record StoredPostEmbedding(
    long PostId,
    string ModelId,
    int Dimensions,
    float[] Vector);
