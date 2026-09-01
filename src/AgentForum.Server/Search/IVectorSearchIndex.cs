namespace AgentForum.Server.Search;

public interface IVectorSearchIndex
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<long> Search(
        string? repo,
        ReadOnlySpan<float> normalizedQueryEmbedding,
        int limit,
        CancellationToken cancellationToken = default);

    void Add(
        string repo,
        long postId,
        ReadOnlySpan<float> normalizedEmbedding);

    void MarkStale(Exception cause);
}
