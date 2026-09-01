using AgentForum.Server.Configuration;
using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

public sealed class LlamaSharpQwenEmbeddingProviderTests
{
    [Fact]
    public void Constructor_RejectsBlankModelPathBeforeLoadingNativeModel()
    {
        var options = new EmbeddingOptions { ModelPath = "   " };

        var exception = Assert.Throws<InvalidOperationException>(
            () => new LlamaSharpQwenEmbeddingProvider(options));

        Assert.Contains("Embedding:ModelPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GGUF", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_RejectsMissingModelFileBeforeLoadingNativeModel()
    {
        var modelPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-qwen3-embedding-{Guid.NewGuid():N}.gguf");
        var options = new EmbeddingOptions { ModelPath = modelPath };

        var exception = Assert.Throws<FileNotFoundException>(
            () => new LlamaSharpQwenEmbeddingProvider(options));

        Assert.Equal(Path.GetFullPath(modelPath), exception.FileName);
        Assert.Contains("Embedding:ModelPath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateModelPath_ReturnsAbsolutePathWithoutLoadingModel()
    {
        var modelPath = Path.GetTempFileName();
        try
        {
            var result = LlamaSharpQwenEmbeddingProvider.ValidateModelPath(modelPath);

            Assert.Equal(Path.GetFullPath(modelPath), result);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ValidateGpuLayerCount_RejectsCpuOnlyOrInvalidValues(int gpuLayerCount)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => LlamaSharpQwenEmbeddingProvider.ValidateGpuLayerCount(gpuLayerCount));

        Assert.Contains("GpuLayerCount", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CPU-only", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(28)]
    public void ValidateGpuLayerCount_AcceptsFullOrPartialGpuOffload(int gpuLayerCount)
    {
        Assert.Equal(
            gpuLayerCount,
            LlamaSharpQwenEmbeddingProvider.ValidateGpuLayerCount(gpuLayerCount));
    }

    [Fact]
    public async Task EmbedAsync_ReturnsNormalizedCopyOfSinglePooledVector()
    {
        var source = new[] { 3f, 4f };
        using var session = new FakeEmbeddingSession(_ => [source]);
        using var provider = new LlamaSharpQwenEmbeddingProvider(session);

        var result = await provider.EmbedAsync("post text");

        Assert.Equal([0.6f, 0.8f], result);
        Assert.NotSame(source, result);
        Assert.Equal([3f, 4f], source);
    }

    [Fact]
    public async Task EmbedAsync_RejectsMoreThanOneEmbeddingVector()
    {
        using var session = new FakeEmbeddingSession(_ => [[1f], [2f]]);
        using var provider = new LlamaSharpQwenEmbeddingProvider(session);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync("post text"));

        Assert.Contains("exactly one pooled embedding vector", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<float[]> MalformedVectors => new()
    {
        Array.Empty<float>(),
        new[] { 0f, 0f },
        new[] { float.NaN },
        new[] { float.PositiveInfinity },
    };

    [Theory]
    [MemberData(nameof(MalformedVectors))]
    public async Task EmbedAsync_RejectsMalformedVectorThroughVectorMath(float[] vector)
    {
        using var session = new FakeEmbeddingSession(_ => [vector]);
        using var provider = new LlamaSharpQwenEmbeddingProvider(session);

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.EmbedAsync("post text"));
    }

    [Fact]
    public async Task EmbedAsync_SerializesSessionAccess()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new CoordinatedEmbeddingSession(firstEntered, releaseFirst);
        using var provider = new LlamaSharpQwenEmbeddingProvider(session);

        var first = provider.EmbedAsync("first");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = provider.EmbedAsync("second");
        await Task.Delay(50);

        Assert.Equal(1, session.CallCount);
        Assert.Equal(1, session.MaximumConcurrency);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, session.CallCount);
        Assert.Equal(1, session.MaximumConcurrency);
    }

    [Fact]
    public async Task EmbedAsync_PassesCancellationTokenToSession()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observedToken = default;
        using var session = new FakeEmbeddingSession(
            token =>
            {
                observedToken = token;
                return [[1f]];
            });
        using var provider = new LlamaSharpQwenEmbeddingProvider(session);

        await provider.EmbedAsync("post text", cancellation.Token);

        Assert.Equal(cancellation.Token, observedToken);
    }

    [Fact]
    public async Task Dispose_DisposesSessionAndRejectsFurtherCalls()
    {
        var session = new FakeEmbeddingSession(_ => [[1f]]);
        var provider = new LlamaSharpQwenEmbeddingProvider(session);

        provider.Dispose();
        provider.Dispose();

        Assert.Equal(1, session.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.EmbedAsync("post text"));
    }

    private sealed class FakeEmbeddingSession : ILlamaEmbeddingSession
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<float[]>>> _embed;

        public FakeEmbeddingSession(Func<CancellationToken, IReadOnlyList<float[]>> embed)
            : this(token => Task.FromResult(embed(token)))
        {
        }

        public FakeEmbeddingSession(
            Func<CancellationToken, Task<IReadOnlyList<float[]>>> embed)
        {
            _embed = embed;
        }

        public int DisposeCount { get; private set; }

        public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(
            string text,
            CancellationToken cancellationToken) =>
            _embed(cancellationToken);

        public void Dispose() => DisposeCount++;
    }

    private sealed class CoordinatedEmbeddingSession(
        TaskCompletionSource firstEntered,
        TaskCompletionSource releaseFirst) : ILlamaEmbeddingSession
    {
        private int _concurrency;

        public int CallCount { get; private set; }

        public int MaximumConcurrency { get; private set; }

        public async Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(
            string text,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);

            try
            {
                if (CallCount == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return [[1f]];
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public void Dispose()
        {
        }
    }
}
