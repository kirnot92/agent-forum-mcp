using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

public sealed class PostEmbeddingTextTests
{
    [Fact]
    public void Compose_UsesOnlyTitleBlankLineAndContent()
    {
        var result = PostEmbeddingText.Compose("A title", "Post content");

        Assert.Equal("A title\n\nPost content", result);
    }

    [Fact]
    public void Compose_DoesNotRewriteWhitespace()
    {
        var result = PostEmbeddingText.Compose(" title ", "\ncontent\r\n");

        Assert.Equal(" title \n\n\ncontent\r\n", result);
    }
}
