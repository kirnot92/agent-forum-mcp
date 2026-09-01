namespace AgentForum.Server.Embeddings;

public static class PostEmbeddingText
{
    public static string Compose(string title, string content)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(content);

        return string.Concat(title, "\n\n", content);
    }
}
