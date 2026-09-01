# Vector search performance notes

This engineering note records the performance characteristics and operational limits of the in-memory exact vector index introduced in commit `1db1d50`. It is an implementation assessment, not product documentation or product ground truth.

## Motivation

Previously, every vector search queried SQLite for every matching `post_embeddings` row, read each BLOB, decoded it into a `float[]`, calculated cosine similarity in `ForumService`, and sorted the candidates. The vector path therefore repeated database I/O, decoding, and large transient allocations on every request, in addition to the unavoidable exact-search arithmetic.

## Current architecture

- SQLite remains the source of truth for posts and embeddings.
- Before HTTP starts, `ReadAllStoredEmbeddingsAsync()` loads and decodes every embedding for the configured model into a singleton `InMemoryExactVectorSearchIndex`.
- The index groups vectors into repository shards. A repository-scoped search scans one shard; a global search scans every shard.
- Search calculates exact cosine similarity for every vector in scope and keeps only the best `K` candidates in a bounded `PriorityQueue`. Results are ordered by similarity descending, then post ID ascending.
- `create_post` commits the post and embedding to SQLite first, then calls `Add`. `Add` copies the new vector into the index before the request reports success.

The per-search time complexity remains `O(N * D + N log K)`, where `N` is the number of vectors in scope, `D` is the embedding dimension, and `K` is the candidate limit. Ranking-state allocation is `O(D + K)`, while the resident index is `O(N * D)`. Global search uses the same algorithm over all shards, so its `N` is the full indexed corpus.

## Improvement status

The implementation structurally removes per-query SQLite vector reads, BLOB decoding, full candidate-list materialization, and full-result sorting. The decoded vectors are shared across searches, and only bounded top-K ranking state is created per request. This should reduce latency, allocation, and GC pressure, especially for repeated or concurrent searches over larger repositories.

No representative performance benchmark or production measurement has been recorded yet. The change establishes a better cost structure, but no specific speedup, throughput, or supported corpus size should be claimed until it is measured.

## Memory and startup cost

At 1,024 dimensions, one raw float32 vector occupies 4,096 bytes:

| Vectors | Raw vector memory |
| ---: | ---: |
| 1,000 | 3.9 MiB |
| 10,000 | 39.1 MiB |
| 50,000 | 195.3 MiB |
| 100,000 | 390.6 MiB |
| 1,000,000 | 3.8 GiB |

These values exclude arrays and object headers, dictionaries, lists, records, heap state, query vectors, and temporary startup overhead.

Startup is a full database scan and decode. `ReadAllStoredEmbeddingsAsync()` materializes the complete `StoredPostEmbedding` list before index construction. Bootstrap does not copy each decoded vector a second time: the index takes ownership of the existing `float[]`. However, the repository result wrappers and the index wrappers coexist until initialization completes, increasing peak startup memory. There is no streaming bootstrap, persisted sidecar index, configurable memory cap, or automatic startup-size guard.

The codec rejects invalid dimensions, incorrectly sized BLOBs, and non-finite values, and the index checks that each declared dimension matches its decoded array length. Startup does not establish that every stored vector has one uniform dimension or unit normalization. A dimension mismatch against a query can therefore be detected during search, while cosine currently remains correct for finite, non-zero vectors whether or not they are normalized.

## Remaining limits

### Search time and layout

- Exact search still touches all `N * D` values in scope. Repository sharding does not help global search.
- `VectorMath.CosineSimilarity()` recomputes both norms and finite-value checks for every comparison, even though normal application writes normalize vectors before storage.
- Vectors are separate managed arrays behind list and record wrappers. There is no contiguous packed storage or explicit SIMD dot-product path.
- There is no minimum similarity threshold. The vector ranker returns the nearest `K` entries even when their absolute similarity is weak; hybrid ranking behavior and search quality still need corpus-level evaluation.

### Concurrency

The read lock is held for the full synchronous scan. Multiple searches may hold read locks concurrently, but they still compete for CPU cores and memory bandwidth. `Add` requires the write lock, so a `create_post` can wait after its SQLite transaction has already committed when long-running searches are active. Cancellation is checked once per vector, but it cannot remove the shared hardware contention caused by simultaneous exact scans.

### Consistency and recovery

SQLite commit and in-memory `Add` are not atomic. The intended order prevents an index entry for a failed database write, but leaves a failure window after the durable commit:

- If `Add` fails, the durable post remains even though `create_post` reports an error.
- The index is marked stale and vector search fails fast; this is detection, not automatic repair.
- Recovery requires calling `InitializeAsync()` successfully or restarting the process so the index is rebuilt from SQLite.
- A process failure between commit and `Add` is recovered on restart, but not within the terminated process.
- Direct or out-of-band writes to SQLite are invisible to the running index.

There is no background rebuild, health integration beyond thrown search errors, persisted index generation, memory-pressure policy, or vector-specific metrics. Retrying a reported `create_post` failure also requires care because the original row may already be durable.

## Prioritized next steps

1. Add phase-level metrics and representative benchmarks for startup, vector search p50/p95, allocation, resident memory, and concurrent search/create workloads.
2. Add a configurable memory/startup guard with an estimated footprint and a clear startup failure before unsafe corpus sizes are loaded.
3. Strengthen bootstrap invariants for uniform dimensions and normalization, then evaluate contiguous storage and a SIMD dot-product implementation against exact ranking compatibility.
4. Replace the full-scan read lock with an immutable or versioned snapshot design so searches do not delay post publication after the database commit.
5. Consider ANN/HNSW only after measurements show that the exact index misses an explicit latency or throughput SLO; validate recall and hybrid-ranking effects before adoption.
