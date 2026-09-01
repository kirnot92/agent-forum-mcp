using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

public sealed class EmbeddingProviderTests
{
    [Fact]
    public async Task DeterministicFake_ReturnsSameNormalizedVectorForSameText()
    {
        IEmbeddingProvider provider = new DeterministicFakeEmbeddingProvider(dimensions: 12);

        var first = await provider.EmbedAsync("same text");
        var second = await provider.EmbedAsync("same text");

        Assert.Equal(first, second);
        Assert.Equal(12, first.Length);
        Assert.Equal(1d, Math.Sqrt(first.Sum(value => value * value)), precision: 6);
    }

    [Fact]
    public async Task DeterministicFake_DistinguishesDifferentText()
    {
        var provider = new DeterministicFakeEmbeddingProvider();

        var first = await provider.EmbedAsync("first");
        var second = await provider.EmbedAsync("second");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Consumer_CanUseReplacementProviderThroughNarrowInterface()
    {
        IEmbeddingProvider provider = new ConstantEmbeddingProvider([0.25f, 0.75f]);

        var result = await EmbedForConsumerAsync(provider, "ignored by replacement");

        Assert.Equal([0.25f, 0.75f], result);
    }

    [Fact]
    public async Task DeterministicFake_HonorsCancellation()
    {
        var provider = new DeterministicFakeEmbeddingProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.EmbedAsync("text", cancellation.Token));
    }

    private static Task<float[]> EmbedForConsumerAsync(
        IEmbeddingProvider provider,
        string text,
        CancellationToken cancellationToken = default) =>
        provider.EmbedAsync(text, cancellationToken);

    private sealed class ConstantEmbeddingProvider(float[] embedding) : IEmbeddingProvider
    {
        public Task<float[]> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((float[])embedding.Clone());
    }
}
