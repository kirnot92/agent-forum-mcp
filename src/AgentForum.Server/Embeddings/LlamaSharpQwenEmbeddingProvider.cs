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
    private readonly object _lifetimeLock = new();
    private int _activeEmbeddingCalls;
    private bool _disposed;

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
        EnterEmbeddingCall();

        var enteredGate = false;
        try
        {
            await _embeddingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;

            var embeddings = await _session
                .GetEmbeddingsAsync(text, cancellationToken)
                .ConfigureAwait(false);

            return NormalizeSingleEmbedding(embeddings);
        }
        finally
        {
            if (enteredGate)
            {
                _embeddingGate.Release();
            }

            ExitEmbeddingCall();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_activeEmbeddingCalls != 0)
            {
                Monitor.Wait(_lifetimeLock);
            }
        }

        try
        {
            _session.Dispose();
        }
        finally
        {
            _embeddingGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    internal static string ValidateModelPath(
        string? configuredPath,
        string? modelId = null)
    {
        var modelDescription = string.IsNullOrWhiteSpace(modelId)
            ? "the Qwen3 embedding model"
            : $"embedding model '{modelId}'";

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                $"{ModelPathConfigurationKey} must be configured with a GGUF file path for {modelDescription}.");
        }

        var fullPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"The GGUF file for {modelDescription}, configured by {ModelPathConfigurationKey}, was not found: '{fullPath}'.",
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

        var gpuLayerCount = ValidateGpuLayerCount(options.GpuLayerCount);
        var modelPath = ValidateModelPath(options.ModelPath, options.ModelId);
        var modelParameters = new ModelParams(modelPath)
        {
            ContextSize = options.ContextSize,
            GpuLayerCount = gpuLayerCount,
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

    internal static int ValidateGpuLayerCount(int gpuLayerCount)
    {
        if (gpuLayerCount < EmbeddingOptions.AllGpuLayers || gpuLayerCount == 0)
        {
            throw new InvalidOperationException(
                "Embedding:GpuLayerCount must be -1 for all GPU layers or a positive layer count; " +
                $"received {gpuLayerCount}. CPU-only inference is not supported by this server build.");
        }

        return gpuLayerCount;
    }

    private void EnterEmbeddingCall()
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeEmbeddingCalls++;
        }
    }

    private void ExitEmbeddingCall()
    {
        lock (_lifetimeLock)
        {
            _activeEmbeddingCalls--;
            if (_activeEmbeddingCalls == 0)
            {
                Monitor.PulseAll(_lifetimeLock);
            }
        }
    }

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
