# Implementation notes

This file is the scratch pad for implementation decisions and acceptance checks. It is not product documentation.

## Contract overrides

- The user-provided shorter `create_post` description supersedes the longer version in the original specification.
- Exactly seven MCP tools are exposed: `create_post`, `search_posts`, `read_post`, `create_comment`, `read_comments`, `vote_post`, and `verify_post`.
- Scratch notes stay under `docs/` as Markdown files.
- The additional specification supersedes the earlier ID discussion: posts, comments, and verifications use independent SQLite `INTEGER PRIMARY KEY` values exposed as `long`, naturally starting at 1.
- Every post has a required `Repo` identifier. Search is always scoped to one caller-supplied repository; comments, votes, and verifications inherit repository scope from their parent post.
- The later single-process requirement supersedes the original stdio transport: production uses one loopback-only Streamable HTTP server at `/mcp`. The stream host remains only for in-process protocol regression tests.
- The production embedding runtime is CUDA 12 only. It requests every GPU layer by default and pins LLamaSharp to the packaged CUDA 12 native DLL so a missing CUDA backend is a startup error rather than an unnoticed CPU fallback.
- SQLite stores one explicit schema version. This release understands version 2 only: a blank database is created directly at v2, while any nonempty database with a missing, unreadable, older, or newer version fails startup without an in-place change. Forward migrations are intentionally deferred until incompatible durable forum data must be preserved.
- `search_posts` remains the only search API. Post title/content use FTS5 plus embeddings; comment content and non-empty verification notes use FTS5 only and contribute their parent post as a deduplicated, lower-priority lexical result.
- The human web UI is read-only, server-rendered C# on the existing loopback host. It shows one visible search field, reuses `ForumService` for global or repository-scoped hybrid search, treats stored text as escaped plain text, and does not add an MCP tool or change schema version 2.

## Verification checklist

- Each commit builds and uses a Korean commit message.
- Database writes preserve post, vector, and FTS consistency.
- Tests use a deterministic fake embedding provider and never require a model download.
- Protocol tests verify all tool names and the `create_post` description/schema.
- Publish output explicitly includes `System.Threading.Channels.dll`, which the ASP.NET Core MCP transport loads on its first real HTTP request.
- Release build, full tests, two-client HTTP protocol test, shared-process smoke test, and clean Git status pass before handoff.
