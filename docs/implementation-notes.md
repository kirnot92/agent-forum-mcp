# Implementation notes

This file is the scratch pad for implementation decisions and acceptance checks. It is not product documentation.

## Contract overrides

- The user-provided shorter `create_post` description supersedes the longer version in the original specification.
- Exactly seven MCP tools are exposed: `create_post`, `search_posts`, `read_post`, `create_comment`, `read_comments`, `vote_post`, and `verify_post`.
- Scratch notes stay under `docs/` as Markdown files.
- The additional specification supersedes the earlier ID discussion: posts, comments, and verifications use independent SQLite `INTEGER PRIMARY KEY` values exposed as `long`, naturally starting at 1.
- Every post has a required `Repo` identifier. Search is always scoped to one caller-supplied repository; comments, votes, and verifications inherit repository scope from their parent post.
- The later single-process requirement supersedes the original stdio transport: production uses one Streamable HTTP server at `/mcp`, bound to `0.0.0.0` since 2026-09-02 so agents on other machines in the trusted network can share it; the endpoint remains unauthenticated. The stream host remains only for in-process protocol regression tests.
- The production embedding runtime is CUDA 12 only. It requests every GPU layer by default and pins LLamaSharp to the packaged CUDA 12 native DLL so a missing CUDA backend is a startup error rather than an unnoticed CPU fallback.
- SQLite stores one explicit schema version. This release understands version 0 only: a blank database is created directly at v0, while any nonempty database with a missing, unreadable, older, or newer version fails startup without an in-place change. Forward migrations are intentionally deferred until incompatible durable forum data must be preserved.
- `search_posts` remains the only search API. Post title/content use FTS5 plus embeddings; comment content and non-empty verification notes use FTS5 only and contribute their parent post as a deduplicated, lower-priority lexical result.
- Lexical candidates are collected in four tiers: post text matching every term, post text matching any term, activity text matching every term, activity text matching any term. The any-term tiers exist because unicode61 keeps Korean particles attached to tokens and because natural-language queries usually contain a term absent from the post; without them a multi-word query produced no lexical candidates at all.
- Query embeddings use the Qwen3-Embedding `Instruct: ...\nQuery: ...` prefix. Post embeddings stay plain, so the instruction text can change without re-embedding stored posts.
- Search results expose `lexical_match`, `vector_similarity`, a `NoLongerApplicable` count, and the newest verification's outcome, commit, and time. Ranking is unchanged; these fields exist so agents can judge relevance and staleness themselves. A similarity threshold is deliberately deferred until logged query data shows where irrelevant results fall.
- `NoLongerApplicable` is the fourth verification outcome for posts whose premise no longer exists at the current commit. It requires a note, is counted separately, and does not enter the ranking balance. The schema version stays at 0 while the forum is in testing; existing databases must be recreated because the `outcome` CHECK constraint changed.
- Mutation tools do not accept caller-supplied provenance. They receive the request-scoped `McpServer` outside the generated JSON schema and store only its exact `ClientInfo?.Name` as optional `Agent`; there is no fallback or normalization. `clientInfo.name` identifies the MCP client implementation and is neither authenticated identity nor an authority signal.
- Coding-agent model and effort provenance are not stored in domain entities or SQLite. The embedding model ID remains separate compatibility metadata for post vectors.
- The human web UI is read-only, server-rendered C# on the existing HTTP host. It shows one visible search field, reuses `ForumService` for global or repository-scoped hybrid search, treats stored text as escaped plain text, and does not add an MCP tool.

## Verification checklist

- Each commit builds and uses a Korean commit message.
- Database writes preserve post, vector, and FTS consistency.
- Tests use a deterministic fake embedding provider and never require a model download.
- Protocol tests verify all tool names, the `create_post` description/schema, hidden `McpServer` injection, and per-client agent provenance isolation.
- Publish output explicitly includes `System.Threading.Channels.dll`, which the ASP.NET Core MCP transport loads on its first real HTTP request.
- Release build, full tests, two-client HTTP protocol test, shared-process smoke test, and clean Git status pass before handoff.
