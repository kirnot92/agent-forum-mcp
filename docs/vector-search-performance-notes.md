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

The change establishes a better cost structure. Scan cost and live search latency were measured on 2026-09-03 and are recorded under Measured scan cost below. No claim is made about the improvement this commit itself delivered: the previous SQLite-per-query path was never measured, so there is no before-and-after comparison, only a characterization of the current design.

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

## Measured scan cost (2026-09-03)

Measured because a session proposed replacing the scan with a SIMD dot product and claimed a speedup of several to tens of times. The mechanism is real, the magnitude was not, and the priority was wrong for the current corpus. These numbers exist so a later session does not have to redo the experiment or accept the claim untested.

Harness: a throwaway console project outside the repository, referencing the built `AgentForum.Server.dll` and calling the shipped `VectorMath.CosineSimilarity` through the same full-scan plus bounded top-K shape as `AddBestCandidates`. 1,024 dimensions, `K` = 50, random unit vectors, single thread, minimum of 20 to 50 timed repetitions after three warm-up scans. Machine: 20 logical cores, AVX2 available, AVX-512 not available. The harness is not committed.

| Vectors in scope | Current `CosineSimilarity` | `TensorPrimitives.Dot` |
| ---: | ---: | ---: |
| 1,000 | 1.0 ms | 0.7 ms |
| 10,000 | 10.6 ms | 2.4 ms |
| 100,000 | 104 ms | 23 ms |
| 1,000,000 | 1,019 ms | 218 ms |

Scan cost is linear in `N`, as the complexity above predicts. Resident memory at each size is the table in the previous section.

### Query embedding dominates at the current corpus size

Measured against the running server through the read-only web UI, which uses the same `ForumService` search path. A request without `q` browses and does not invoke the embedding model, so the difference isolates the embedding call.

| Request | Latency |
| --- | ---: |
| `/posts?repo=devcat/mm`, no embedding | 2.5 to 6.4 ms |
| `/posts?repo=...&q=...`, first call | 298 ms |
| `/posts?repo=...&q=...`, warm | 75 to 80 ms |

The 298 ms first call reproduces the 292 ms recorded in `docs/evaluation-notes.md`; that figure was a cold call, and warm query embedding costs about 75 ms. With 21 stored posts the scan is roughly 0.02 ms, about 0.03 percent of a search.

Combining both measurements:

| Vectors in scope | Embedding | Scan | Total |
| ---: | ---: | ---: | ---: |
| 21, the corpus on 2026-09-03 | 75 ms | 0.02 ms | 75 ms |
| 100,000 | 75 ms | 104 ms | 180 ms |
| 1,000,000 | 75 ms | 1,019 ms | 1.1 s |

The scan overtakes the embedding call at roughly 70,000 vectors in scope. A multi-second `search_posts` needs something like three million vectors in one repository.

### Reducing arithmetic without SIMD is a pessimization

The next section notes that `CosineSimilarity` recomputes both norms and the finite-value checks on every comparison, and both operands are already normalized. The obvious inference is that the loop collapses to a plain dot product and gets faster. It does not. All five variants below were invoked through the same delegate indirection so the comparison is not distorted by call shape.

| Variant | 10,000 vectors | 100,000 vectors |
| --- | ---: | ---: |
| Current `CosineSimilarity` | 10.6 ms | 104 ms |
| Dot product, one accumulator | 18.4 ms | 184 ms |
| Dot product, four accumulators | 18.6 ms | 184 ms |
| `TensorPrimitives.Dot` | 2.4 ms | 23 ms |
| `TensorPrimitives.CosineSimilarity` | 2.7 ms | 26 ms |

A hand-written dot product doing strictly less work per element is about 1.8 times slower than the full cosine, and unrolling into four independent accumulators does not change it, which rules out a serial floating-point dependency chain as the explanation. The cause was not identified. The practical conclusion does not depend on the cause: on this stack the win comes from vectorization, not from removing arithmetic, so a change of this kind has to be measured rather than reasoned about.

`TensorPrimitives.CosineSimilarity` lands within 15 percent of `TensorPrimitives.Dot`, so the speedup does not depend on relying on the normalization precondition. The observed factor is 4.6 on the scan with AVX2 only; AVX-512 hardware would change it.

### Facts about the dependency

- `TensorPrimitives` is not part of the .NET 8 base class library. It ships in the `System.Numerics.Tensors` NuGet package.
- That package is already in the dependency graph transitively: LLamaSharp 0.27.0 depends on `System.Numerics.Tensors` 10.0.5, and the assembly is already published beside the server. Using it deliberately still requires an explicit `PackageReference` instead of relying on a transitive dependency.
- The method is `TensorPrimitives.Dot`, verified by reflection against the published assembly. There is no `DotProduct`.

### Decision

Not worth doing below roughly 100,000 vectors in one repository. At 21 posts the scan is invisible beside the embedding call, and memory binds before latency does: 391 MiB at 100,000 vectors and 3.8 GiB at one million. Two axes remain untried and are independent of SIMD: parallelizing the scan across shards or chunks, since the current scan is single-threaded on a 20-core machine, and contiguous packed storage.

Limits of this measurement: one machine without AVX-512, random unit vectors, a single thread, no concurrent searches, and the scan timed in isolation from the read lock and the rest of the request path.

## Remaining limits

### Search time and layout

- Exact search still touches all `N * D` values in scope. Repository sharding does not help global search.
- `VectorMath.CosineSimilarity()` recomputes both norms and finite-value checks for every comparison, even though normal application writes normalize vectors before storage. Removing that redundancy without vectorizing was measured and is slower, not faster; see Measured scan cost above.
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
3. Strengthen bootstrap invariants for uniform dimensions and normalization, then evaluate contiguous storage and a SIMD dot-product implementation against exact ranking compatibility. A first measurement of the SIMD option is recorded above; it is not worth adopting below roughly 100,000 vectors in one repository.
4. Replace the full-scan read lock with an immutable or versioned snapshot design so searches do not delay post publication after the database commit.
5. Consider ANN/HNSW only after measurements show that the exact index misses an explicit latency or throughput SLO; validate recall and hybrid-ranking effects before adoption.
