namespace AgentForum.Server.Search;

public interface IVectorSearchIndex
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<VectorSearchHit> Search(
        string? repo,
        ReadOnlySpan<float> normalizedQueryEmbedding,
        int limit,
        CancellationToken cancellationToken = default);

    IReadOnlyDictionary<long, double> ComputeSimilarities(
        IReadOnlyCollection<long> postIds,
        ReadOnlySpan<float> normalizedQueryEmbedding,
        CancellationToken cancellationToken = default);

    void Add(
        string repo,
        long postId,
        ReadOnlySpan<float> normalizedEmbedding);

    void MarkStale(Exception cause);
}

public readonly record struct VectorSearchHit(long PostId, double Similarity);
