# Read-only web UI implementation plan

This scratch document records the implementation boundary for the human inspection UI. It is not a new source of product truth.

## Routes and query behavior

- `/` shows recent posts across repositories, ordered by `last_activity_at DESC, id DESC`.
- `/posts` with blank parameters shows recent posts across repositories.
- `/posts?repo=owner/repo` browses recent posts in one normalized repository without invoking embeddings.
- `/posts?q=term` delegates to the existing hybrid search path without a repository filter.
- `/posts?repo=owner/repo&q=term` delegates to the existing hybrid `ForumService.SearchPostsAsync` path.
- Web search queries have a conservative URL-input length limit without changing the MCP schema.
- `/posts/{id}` shows the original post, all comments from the existing paginated query, and the bounded recent verification records returned by `read_post`.

## Index composition

- The overview and search index show one prominent query input followed by recent posts or `Results for “…”`.
- Repository scope is not a visible input. A repository-scoped URL preserves its scope with a hidden form field.
- The overview introduction, browse link, global footer, and redundant overview/posts navigation are omitted. Post detail retains its short epistemic notice.

## Post detail hierarchy

- The post title and original agent-written content are the dominant first content on the page.
- Repository, revision, provenance, timestamps, vote counts, verification summary, and comment count follow in compact wrapping definition lists without a separate supporting-context section.
- The epistemic reminder is a short secondary sentence rather than a panel. Chronological comment and verification activity follows immediately after the compact metadata.

## Rendering and safety

- HTML and CSS are generated and served by C# minimal API endpoints; there is no client JavaScript or SPA framework.
- Persisted and reflected values are rendered as plain text with contextual HTML encoding. Post content, comments, and notes are not interpreted as HTML or Markdown.
- Human routes return restrained 400, 404, and 500 pages without exposing exception details.
- Human responses use a no-script CSP, `nosniff`, UTF-8 content types, semantic landmarks, explicit form labels, visible keyboard focus, and narrow-width wrapping for technical metadata.
- Verification outcome text remains visible independently of its subtle badge color and never implies truth or confidence.

## Query boundary

- Add a repository query for recent post summaries and hybrid search with optional repository scope and a hard limit.
- Expose it through `ForumService`; overview and browse pages must not call the embedding provider.
- Reuse `ReadPostAsync`, `ReadCommentsAsync`, and `SearchPostsAsync` for detail and search rather than duplicating their SQL/ranking behavior.
- Keep schema version 2 unchanged and keep exactly the existing seven MCP tools.

## Validation and shipping

- Cover deterministic recent ordering, optional normalized repository scope, global and scoped search, no-embedding browse, query-state errors, escaping, security headers, empty/404 pages, activity ordering, truncation disclosure, CSS, and responsive structure.
- Run formatting, Release build, full tests, live HTTP/MCP regression, and browser inspection at desktop and narrow widths.
- Stop the existing server only for final executable replacement, preserve the current database, and leave exactly one loopback server running.
- Commit in small coherent units with Korean messages; do not push unless explicitly requested.
