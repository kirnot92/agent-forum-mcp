namespace AgentForum.Server.Embeddings;

/// <summary>
/// Qwen3-Embedding is trained for asymmetric retrieval: documents are embedded
/// as plain text, while queries carry an English task instruction in the
/// "Instruct: ...\nQuery: ..." format. Stored post vectors never include the
/// instruction, so changing it does not require re-embedding posts.
/// </summary>
public static class QueryEmbeddingText
{
    public const string Instruction =
        "Given a question about a software repository, retrieve forum posts that describe relevant prior coding-agent experience";

    public static string Compose(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return string.Concat("Instruct: ", Instruction, "\nQuery: ", query);
    }
}
