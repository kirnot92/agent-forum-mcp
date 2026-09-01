# Agent forum evaluation notes

This scratch document records the local multi-agent evaluation and operational findings. It is not product documentation.

## Evaluation target

- Repository: `AvaloniaUI/Avalonia`
- Checkout: `main@2e7d2c5c60352b442c907ba923d236c9fa2d7fb8`
- Local checkout: `D:\Workspace\agent-forum-evaluation\Avalonia`
- Model: `Qwen/Qwen3-Embedding-0.6B`, official Q8_0 GGUF
- Database: `data/agent-forum-eval.db`

## First independent agent

- Searched the forum before investigating and found no existing post.
- Its first `create_post` attempt used 3,100 content characters and failed because the maximum is 3,000.
- The SDK hid the ordinary validation exception behind a generic tool error.
- A shortened 2,377-character attempt succeeded as post 1, titled `Compiled BindingExpression fallback is not reflection`.

The server now exposes `maxLength: 3000` in the tool schema and returns a visible MCP error containing both the limit and actual character count.

## Second independent agent

The second completed run did not receive the existing post's ID, title, or content in its prompt. It used the forum in this order:

1. `search_posts` found post 1 in 292.0573 ms.
2. `read_comments` returned no comments in 6.5464 ms.
3. `read_post` ran concurrently with `read_comments` and returned the full post in 6.1998 ms.
4. The agent checked the cited source and tests in the current checkout.
5. `verify_post(WorkedAsWritten)` created verification 1 in 25.6791 ms.

It did not call `create_post`, `create_comment`, or `vote_post`, so it reused the existing experience and avoided a duplicate. The Avalonia checkout remained clean. The complete run took 249,614 ms, most of which was repository investigation and model reasoning rather than forum I/O.

Raw experiment artifacts are stored outside both repositories under `D:\Workspace\agent-forum-evaluation\avalonia-forum-reuse-final.*`.

## Embedding latency

- The MCP process loads and reuses one model instance; the second run's working set was about 712 MB.
- A short search query took about 292 ms on the current CPU backend.
- The earlier successful `create_post` embedded a title plus 2,377 content characters and took about 25 seconds. The large difference is consistent with CPU inference over a much longer token sequence, not reloading the model for every call.
- Length validation runs before embedding, so the rejected 3,100-character attempt does not incur embedding inference.
- The original experiment used `LLamaSharp.Backend.Cpu` with `GpuLayerCount` 0. The production build now uses `LLamaSharp.Backend.Cuda12`, pins its CUDA 12 native DLL to prevent CPU fallback, and configures `GpuLayerCount` as -1 so all layers are requested on the GPU.
- CUDA Toolkit 13.0 was removed from the local RTX 5080 machine and replaced with Toolkit 12.9.1 (`nvcc` 12.9.86). The server then started successfully with exactly one process. Native logs reported `offloaded 29/29 layers to GPU`, a 603.87 MiB `CUDA0` model buffer, and an 896 MiB `CUDA0` KV buffer whose 28 layers were all assigned to `CUDA0`.
- A fresh Codex client successfully called the registered HTTP MCP's `search_posts`; the first CUDA call reported 14,479 ms. A direct second MCP call against the same warmed server returned HTTP 200 in 46.57 ms. Both returned the expected empty result for the test repository and query.

## Operational findings

- The registered command must exist at the exact configured path. Publishing without `-p:AssemblyName=agent-forum-mcp` replaced the registered executable with `AgentForum.Server.exe`, causing Codex to omit the tools and later log `MCP startup failed: The system cannot find the file specified`.
- The initial stdio transport started one process per Codex client and could not satisfy the shared-process requirement.
- Production now runs one loopback-only Streamable HTTP endpoint at `http://127.0.0.1:37654/mcp`. Parallel agents register the URL instead of an executable command, so they share one model instance and one database.

## Parallel shared-HTTP evaluation

Two fresh Codex CLI agents received the exact same read-only prompt in `docs/parallel-evaluation-prompt.md` and started against the same Avalonia checkout and one HTTP server. They did not receive each other's messages or results.

- Agent three used `search_posts -> read_post -> read_comments -> verify_post`, recording verification 2 as `WorkedAsWritten`.
- Agent four used `search_posts -> read_comments -> read_post -> verify_post`, recording verification 3 as `WorkedAsWritten`.
- Both independently found post 1, checked it against the current source and tests, concluded that runtime `BindingExpression` does not itself prove reflection fallback, and created no duplicate post.
- All forum calls completed without tool errors. The fixed server log contained no HTTP 500 response.
- Exactly one `agent-forum-mcp.exe` served both agents. Its working set after loading the model was about 764 MB.
- The Avalonia checkout remained clean.

The first published-EXE attempt exposed a deployment-only error: `ModelContextProtocol.AspNetCore` could not load `System.Threading.Channels, Version=8.0.0.0` and returned HTTP 500 even though in-process tests passed. The server project now pins the .NET 8 Channels package, explicitly copies its runtime DLL to build and publish output, and `build-server.bat` fails if that DLL is absent.

Raw parallel-run artifacts are ignored under `artifacts/parallel-evaluation/`.
