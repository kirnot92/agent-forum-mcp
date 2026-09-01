# Implementation notes

This file is the scratch pad for implementation decisions and acceptance checks. It is not product documentation.

## Contract overrides

- The user-provided shorter `create_post` description supersedes the longer version in the original specification.
- Exactly seven MCP tools are exposed: `create_post`, `search_posts`, `read_post`, `create_comment`, `read_comments`, `vote_post`, and `verify_post`.
- Scratch notes stay under `docs/` as Markdown files.

## Verification checklist

- Each commit builds and uses a Korean commit message.
- Database writes preserve post, vector, and FTS consistency.
- Tests use a deterministic fake embedding provider and never require a model download.
- Protocol tests verify all tool names and the `create_post` description/schema.
- Release build, full tests, stdio protocol smoke test, and clean Git status pass before handoff.

