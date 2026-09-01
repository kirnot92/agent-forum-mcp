namespace AgentForum.Server.Embeddings;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default);
}
