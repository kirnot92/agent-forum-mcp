using System.Runtime.CompilerServices;
using AgentForum.Server.Configuration;
using LLama;
using LLama.Common;
using LLama.Native;

[assembly: InternalsVisibleTo("AgentForum.Server.Tests")]

namespace AgentForum.Server.Embeddings;

public sealed class LlamaSharpQwenEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const string ModelPathConfigurationKey = "Embedding:ModelPath";

    private readonly ILlamaEmbeddingSession _session;
    private readonly SemaphoreSlim _embeddingGate = new(1, 1);
    private int _disposeRequested;

    public LlamaSharpQwenEmbeddingProvider(EmbeddingOptions options)
        : this(CreateSession(options))
    {
    }

    internal LlamaSharpQwenEmbeddingProvider(ILlamaEmbeddingSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();

        await _embeddingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var embeddings = await _session
                .GetEmbeddingsAsync(text, cancellationToken)
                .ConfigureAwait(false);

            return NormalizeSingleEmbedding(embeddings);
        }
        finally
        {
            _embeddingGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        // Wait for the native context currently using the shared weights. Calls
        // already queued on the semaphore will observe the disposed state before
        // entering the session.
        _embeddingGate.Wait();
        try
        {
            _session.Dispose();
        }
        finally
        {
            _embeddingGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    internal static string ValidateModelPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                $"{ModelPathConfigurationKey} must be configured with a Qwen3 embedding GGUF file path.");
        }

        var fullPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"The Qwen3 embedding model configured by {ModelPathConfigurationKey} was not found: '{fullPath}'.",
                fullPath);
        }

        return fullPath;
    }

    internal static float[] NormalizeSingleEmbedding(IReadOnlyList<float[]>? embeddings)
    {
        if (embeddings is null)
        {
            throw new InvalidOperationException("LLamaSharp returned no embedding result collection.");
        }

        if (embeddings.Count != 1)
        {
            throw new InvalidOperationException(
                $"LLamaSharp must return exactly one pooled embedding vector, but returned {embeddings.Count}.");
        }

        var embedding = embeddings[0]
            ?? throw new InvalidOperationException("LLamaSharp returned a null embedding vector.");

        return VectorMath.Normalize(embedding);
    }

    private static ILlamaEmbeddingSession CreateSession(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelPath = ValidateModelPath(options.ModelPath);
        var modelParameters = new ModelParams(modelPath)
        {
            ContextSize = options.ContextSize,
            GpuLayerCount = options.GpuLayerCount,
            Embeddings = true,

            // Qwen3-Embedding is trained to use the final token representation.
            // Official GGUFs may omit pooling metadata, so select it explicitly.
            PoolingType = LLamaPoolingType.Last,
        };

        var weights = LLamaWeights.LoadFromFile(modelParameters);
        try
        {
            var embedder = new LLamaEmbedder(weights, modelParameters);
            return new LlamaEmbeddingSession(weights, embedder);
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);

    private sealed class LlamaEmbeddingSession(
        LLamaWeights weights,
        LLamaEmbedder embedder) : ILlamaEmbeddingSession
    {
        private int _disposed;

        public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(
            string text,
            CancellationToken cancellationToken) =>
            embedder.GetEmbeddings(text, cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                embedder.Dispose();
            }
            finally
            {
                weights.Dispose();
            }
        }
    }
}

internal interface ILlamaEmbeddingSession : IDisposable
{
    Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(
        string text,
        CancellationToken cancellationToken);
}
