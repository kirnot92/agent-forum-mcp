using AgentForum.Server.Configuration;
using AgentForum.Server.Domain;
using AgentForum.Server.Embeddings;
using AgentForum.Server.Persistence;

namespace AgentForum.Server.Search;

public sealed class InMemoryExactVectorSearchIndex : IVectorSearchIndex, IDisposable
{
    private readonly IForumRepository _repository;
    private readonly string _modelId;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly ReaderWriterLockSlim _stateLock = new();

    private Dictionary<string, List<IndexedVector>> _shards = new(StringComparer.Ordinal);
    private HashSet<long> _postIds = [];
    private IndexState _state;
    private long _stateVersion;
    private Exception? _staleCause;

    public InMemoryExactVectorSearchIndex(
        IForumRepository repository,
        EmbeddingOptions embeddingOptions)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentNullException.ThrowIfNull(embeddingOptions);

        if (string.IsNullOrWhiteSpace(embeddingOptions.ModelId))
        {
            throw new ArgumentException("A non-empty embedding model ID is required.", nameof(embeddingOptions));
        }

        _modelId = embeddingOptions.ModelId;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stateLock.EnterReadLock();
            try
            {
                if (_state == IndexState.Ready)
                {
                    return;
                }
            }
            finally
            {
                _stateLock.ExitReadLock();
            }

            while (true)
            {
                long observedStateVersion;
                _stateLock.EnterReadLock();
                try
                {
                    observedStateVersion = _stateVersion;
                }
                finally
                {
                    _stateLock.ExitReadLock();
                }

                var storedEmbeddings = await _repository
                    .ReadAllStoredEmbeddingsAsync(_modelId, cancellationToken)
                    .ConfigureAwait(false);
                var mutableShards = new Dictionary<string, List<IndexedVector>>(StringComparer.Ordinal);
                var postIds = new HashSet<long>();

                foreach (var stored in storedEmbeddings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stored.Dimensions != stored.Vector.Length)
                    {
                        throw IncompatibleDimensions(stored.PostId, stored.Dimensions, stored.Vector.Length);
                    }

                    var normalizedRepo = RepositoryKey.Normalize(stored.Repo);
                    if (!mutableShards.TryGetValue(normalizedRepo, out var shard))
                    {
                        shard = [];
                        mutableShards.Add(normalizedRepo, shard);
                    }

                    // Bootstrap arrays are newly decoded by the repository and ownership is
                    // transferred directly to the index to avoid a second full-sized copy.
                    shard.Add(new IndexedVector(stored.PostId, stored.Dimensions, stored.Vector));
                    postIds.Add(stored.PostId);
                }

                _stateLock.EnterWriteLock();
                try
                {
                    if (_stateVersion != observedStateVersion)
                    {
                        continue;
                    }

                    _shards = mutableShards;
                    _postIds = postIds;
                    _staleCause = null;
                    _state = IndexState.Ready;
                    return;
                }
                finally
                {
                    _stateLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public IReadOnlyList<long> Search(
        string? repo,
        ReadOnlySpan<float> normalizedQueryEmbedding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (normalizedQueryEmbedding.IsEmpty)
        {
            throw new ArgumentException("The query embedding must not be empty.", nameof(normalizedQueryEmbedding));
        }

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The vector result limit must be positive.");
        }

        var normalizedRepo = repo is null ? null : RepositoryKey.Normalize(repo);
        cancellationToken.ThrowIfCancellationRequested();

        _stateLock.EnterReadLock();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSearchable();
            var heap = new PriorityQueue<VectorCandidate, VectorCandidate>(WorstCandidateComparer.Instance);

            if (normalizedRepo is null)
            {
                foreach (var shard in _shards.Values)
                {
                    AddBestCandidates(shard, normalizedQueryEmbedding, limit, heap, cancellationToken);
                }
            }
            else if (_shards.TryGetValue(normalizedRepo, out var shard))
            {
                AddBestCandidates(shard, normalizedQueryEmbedding, limit, heap, cancellationToken);
            }

            return heap.UnorderedItems
                .Select(item => item.Element)
                .OrderByDescending(candidate => candidate.Similarity)
                .ThenBy(candidate => candidate.PostId)
                .Select(candidate => candidate.PostId)
                .ToArray();
        }
        finally
        {
            _stateLock.ExitReadLock();
        }
    }

    public void Add(
        string repo,
        long postId,
        ReadOnlySpan<float> normalizedEmbedding)
    {
        var normalizedRepo = RepositoryKey.Normalize(repo);
        if (postId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(postId), postId, "Post IDs must be positive.");
        }

        if (normalizedEmbedding.IsEmpty)
        {
            throw new ArgumentException("The embedding must not be empty.", nameof(normalizedEmbedding));
        }

        foreach (var value in normalizedEmbedding)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("The embedding must contain only finite values.", nameof(normalizedEmbedding));
            }
        }

        // Add accepts arbitrary caller-owned memory, so the index takes its own copy.
        var ownedVector = normalizedEmbedding.ToArray();

        _stateLock.EnterWriteLock();
        try
        {
            EnsureSearchable();
            if (!_postIds.Add(postId))
            {
                return;
            }

            if (!_shards.TryGetValue(normalizedRepo, out var shard))
            {
                shard = [];
                _shards.Add(normalizedRepo, shard);
            }

            shard.Add(new IndexedVector(postId, ownedVector.Length, ownedVector));
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }
    }

    public void MarkStale(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        _stateLock.EnterWriteLock();
        try
        {
            _staleCause = cause;
            _state = IndexState.Stale;
            _stateVersion++;
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _initializationLock.Dispose();
        _stateLock.Dispose();
    }

    private static void AddBestCandidates(
        IReadOnlyList<IndexedVector> vectors,
        ReadOnlySpan<float> queryEmbedding,
        int limit,
        PriorityQueue<VectorCandidate, VectorCandidate> heap,
        CancellationToken cancellationToken)
    {
        foreach (var vector in vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vector.Dimensions != queryEmbedding.Length || vector.Vector.Length != vector.Dimensions)
            {
                throw IncompatibleDimensions(vector.PostId, vector.Dimensions, queryEmbedding.Length);
            }

            var candidate = new VectorCandidate(
                vector.PostId,
                VectorMath.CosineSimilarity(queryEmbedding, vector.Vector));

            if (heap.Count < limit)
            {
                heap.Enqueue(candidate, candidate);
            }
            else if (WorstCandidateComparer.Instance.Compare(candidate, heap.Peek()) > 0)
            {
                heap.Dequeue();
                heap.Enqueue(candidate, candidate);
            }
        }
    }

    private void EnsureSearchable()
    {
        if (_state == IndexState.Stale)
        {
            throw new InvalidOperationException(
                "The in-memory vector index is stale and must be rebuilt before searches can continue.",
                _staleCause);
        }

        if (_state != IndexState.Ready)
        {
            throw new InvalidOperationException("The in-memory vector index has not been initialized.");
        }
    }

    private static InvalidDataException IncompatibleDimensions(
        long postId,
        int storedDimensions,
        int queryDimensions) =>
        new(
            $"Post {postId} has an incompatible {FormatDimensions(storedDimensions)} embedding; " +
            $"the configured model produced {FormatDimensions(queryDimensions)}.");

    private static string FormatDimensions(int dimensions) => $"{dimensions}-dimension";

    private sealed record IndexedVector(long PostId, int Dimensions, float[] Vector);

    private readonly record struct VectorCandidate(long PostId, double Similarity);

    private sealed class WorstCandidateComparer : IComparer<VectorCandidate>
    {
        public static WorstCandidateComparer Instance { get; } = new();

        public int Compare(VectorCandidate x, VectorCandidate y)
        {
            var similarity = x.Similarity.CompareTo(y.Similarity);
            return similarity != 0 ? similarity : y.PostId.CompareTo(x.PostId);
        }
    }

    private enum IndexState
    {
        Uninitialized,
        Ready,
        Stale
    }
}
