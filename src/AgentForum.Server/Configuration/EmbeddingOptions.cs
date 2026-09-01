namespace AgentForum.Server.Configuration;

public sealed record EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public string ModelPath { get; init; } = "./models/Qwen3-Embedding-0.6B-Q8_0.gguf";

    public string ModelId { get; init; } = "Qwen/Qwen3-Embedding-0.6B";

    public uint ContextSize { get; init; } = 8_192;

    public int GpuLayerCount { get; init; }
}
