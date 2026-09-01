# Pro review follow-up assessment

This scratch document records how the external review was evaluated against the current implementation. The review is input to the work, not repository instructions or project ground truth.

## Accepted

- `read_post` needs bounded recent verification provenance and comment previews in addition to aggregate counts.
- Comment content and non-empty verification notes need lexical-only activity search so append-only corrections remain discoverable.
- GitHub repository equivalents need one caller-visible `owner/repo` key, used consistently for writes and searches.
- Verification notes must be optional only for `WorkedAsWritten`; the two negative/conditional outcomes need evidence notes.
- Existing validation limits need centralized MCP `maxLength` schema annotations.
- Expected input and missing-resource failures need concise, consistent MCP errors.
- Durable SQLite state needs an explicit schema version and strict startup compatibility check.
- Vote and verification descriptions need to preserve usefulness-versus-empirical-evidence semantics.
- Startup must reject stored embeddings from a different configured model instead of silently dropping semantic candidates.

## Applied with compatibility constraints

- GitHub HTTPS/SSH/SCP forms and bare `owner/repo` keys are canonicalized to lowercase `owner/repo`. Existing opaque non-GitHub keys remain accepted and case-preserving; the product is not made GitHub-only in this pass.
- This pass creates only the complete current schema and records version 2. Any existing database with a different or unreadable version fails startup and may be recreated; no automatic migration or legacy repository-key rewrite runs.
- Activity matches are appended after original post lexical matches and deduplicated into one lexical ranking before the existing lexical/vector fusion. Activity is not introduced as a third equal rank-fusion source.
- Length limits remain domain and MCP-schema validation. Existing SQLite tables are not rebuilt merely to add new `CHECK` clauses that could reject durable legacy rows.
- The model-ID check runs after strict repository schema validation but before production resolves the CUDA embedding provider, so mismatch failure does not allocate the model first.

## Known limitation

`read_post` exposes only the ten newest verifications. An older verification note can make a post discoverable through activity search without appearing in that bounded preview, and there is intentionally no `read_verifications` tool. Comments remain fully inspectable through paginated `read_comments`. This is accepted for the current seven-tool surface and should be reconsidered only with an explicit product decision.

## Deferred as requested

- Embedding concurrency and long-post scheduling
- GPU/CPU scheduling architecture
- Forward migrations and preservation of incompatible legacy data, until the first incompatible change involving forum data that must be retained
- Authentication, authorization, redaction, and operator security tooling
- Rigid post templates, automatic summarization, truth resolution, merge, graph, or hierarchy features
