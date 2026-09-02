# Lexical search considerations

This scratch document records the reasoning behind the 2026-09-02 lexical search changes so a later session can decide whether to revisit them. It is an engineering assessment, not product documentation, and the measurements below come from one small corpus.

## Timeline

- Commit `e4aaebd` added an any-term (OR) fallback to `SqliteForumRepository.SearchLexicalPostIdsAsync`. The stated goal was to stop multi-word natural-language queries from producing zero lexical candidates.
- Commit `ba2935e` reverted the fallback after checking it against the real corpus. Every other change from `e4aaebd` stayed: `vector_similarity` and `lexical_match` in search results, the `NoLongerApplicable` outcome, the Qwen3 query instruction prefix, and the latest-verification summary.

## What was observed

Corpus: one private repository with ten Korean posts, browsed through the web UI at `/posts?repo=...&q=...`. The examples below are paraphrased; the real post texts and identifiers are not reproduced here.

A six-token Korean sentence query of the shape `<domain noun> <noun> <noun+particle> 안 <verb> 때` with the fallback active:

- All ten posts were reported as `lexical match`.
- The correct post was first, but only because the vector and lexical rankings happened to agree on it.
- Below it the order no longer followed similarity: a post at 0.461 ranked above posts at 0.432 and 0.420 because its BM25 rank on a one-syllable match was higher.

Without the fallback the same query yields zero lexical candidates and the vector ranking stands alone. That ordering was clean.

## Why the fallback failed

Two properties of the current stack combined.

1. `posts_fts` uses `unicode61 tokenchars '_'`. unicode61 classifies characters by Unicode category, so Hangul syllables are letters and every run of them is one token. Korean function words such as `안`, `때`, `이`, and `가` become tokens, and they occur in almost every Korean post. An OR query over the user's tokens therefore matched nearly the whole corpus through these words alone.
2. `HybridSearchRanker` fuses by Reciprocal Rank Fusion. RRF discards score magnitude and keeps only rank. A post that matched `때` with a very low BM25 score still received rank n in the lexical list and the same 1/(60+n) credit as a vector rank n. That credit was enough to reorder the vector results.

The unit-level probe that motivated the fallback used one document and showed only that OR returned a row where AND did not. It could not show the ranking effect, which only appears with a corpus. Ranking changes need to be evaluated on real posts.

## What the lexical channel is for

The vector channel absorbs Korean morphology. Embedding tokenizers split `검증이` and `검증은` into the same stem subwords, and particles carry little meaning, so particle mismatch barely moves cosine similarity. Trigram indexing for Korean particles is therefore not needed while the vector channel exists.

The lexical channel matters where embeddings blur: exact identifiers, error codes, ticket ids, file names, and branch names such as `CS0246`, `ISSUE1234`, `OrderRepositoryCache`, or `release/2.3`. A bare identifier query against a long post produces a low cosine, and only an exact token match can lift the right post. Lexical evidence should therefore be weighted by how rare the matched term is, not by how many terms matched. The OR fallback and any count-based "at least k of n terms" rule both ignore rarity.

## Remaining lexical gap

unicode61 does not split at script boundaries. In Korean prose an identifier is often followed directly by a particle, and the pair becomes one token. Measured with SQLite 3.53.3 (the version bundled by SQLitePCLRaw 2.1.12):

| Stored text | Indexed token | `"csproj"` exact | `"csproj"*` prefix |
| --- | --- | --- | --- |
| `csproj만` | `csproj만` | 0 | 1 |
| `CS0246이` | `cs0246이` | 0 | 1 |
| `ISSUE1234에서` | `issue1234에서` | 0 | 1 |
| `BindingExpression이` | `bindingexpression이` | 0 | 1 |

Real posts already contain identifiers followed directly by a particle, for example a method name followed by `이` or a tool name followed by `를`. This is the one case where both channels are weak: the vector channel because the query is a short identifier, the lexical channel because the token is glued to a particle. It is accepted for now.

## Options considered

| Option | What it fixes | Cost | Risk | Status |
| --- | --- | --- | --- | --- |
| AND over every query token | Nothing new; exact identifier matches when all terms co-occur | None | Multi-word queries often yield zero lexical candidates | Current |
| OR fallback when AND is empty | Zero-candidate queries | One query | Function words match everything; RRF reorders vector results | Reverted |
| Count-based minimum-should-match (for example half the terms) | Reduces OR noise | Per-token queries | Rewards `이름`+`때` over a lone `CS0246`; still ignores rarity | Rejected |
| IDF-based query relaxation: drop terms with document frequency 0, drop terms in more than half the repository's posts, AND the rest, then drop the most common remaining term until a result appears | Zero-candidate queries while keeping only rare-term matches | One count query per token plus up to n AND queries | Corpus-relative thresholds behave oddly on very small corpora; needs a floor that always keeps the rarest term | Preferred if lexical recall becomes a measured problem |
| Prefix matching `"token"*` on query tokens | Identifier-plus-particle; partially Korean noun-plus-particle | One line in `BuildFtsMatchExpression` | Short tokens such as `이*` match broadly; one-directional; acts as crude stemming | Stopgap candidate |
| Script-boundary splitting: insert a boundary between Latin/digit runs and Hangul runs in indexed text and in queries | Identifier-plus-particle exactly | Normalized text column, FTS over that column, trigger changes, database recreation | None for ranking; schema change | Preferred fix for the identifier gap |
| Trigram tokenizer | Substring matching in both directions | New FTS table, larger index | Tokens shorter than three characters become unsearchable; BM25 less meaningful | Not needed while vector covers morphology |
| Morphological analyzer (nori, mecab-ko) | Everything above | Not available: Microsoft.Data.Sqlite exposes no custom tokenizer hook | | Unavailable |

## Current decision

- Lexical search requires every query token. A multi-word query whose terms never co-occur yields no lexical candidates, and the vector ranking orders the results alone.
- Korean noun and particle mismatch is left to the vector channel.
- The identifier-plus-particle gap is known and unfixed.
- `lexical_match` in results means every query token matched, which is why it is meaningful again after the revert.
- No similarity threshold is applied; `vector_similarity` is exposed so callers can judge.

The user's stated position is that tuning search quality further is over-engineering at the current corpus size. Ten posts fit inside the vector candidate limit of 50, so every post is already a candidate and only ordering matters.

## When to revisit and how

Revisit when one of these is observed, not before:

- An agent searches for a bare identifier that exists in a post only with a particle attached and the post is not returned near the top.
- The per-repository corpus grows well past `HybridSearchRanker.CandidateLimit` (50), so posts start falling outside the vector candidate set and lexical recall begins to matter.
- Logged queries show relevant posts missing from results, as opposed to irrelevant posts being present.

Method for any ranking change:

1. Build a regression set from real posts: ten to twenty pairs of a realistic query and the post that should rank first. Include bare-identifier queries and Korean sentence queries.
2. Run the set against the current build through `ForumService.SearchPostsAsync` or the `/posts?repo=...&q=...` route and record the ordering with `vector_similarity` and `lexical_match`.
3. Apply the candidate change and compare orderings. A change that adds lexical candidates must be judged by what it does to the vector ordering, because RRF will reorder whenever the lexical list is non-empty.
4. Do not accept a change on the strength of a single-document probe.

If a change is warranted, the recommended order is script-boundary splitting for the identifier gap, then IDF-based relaxation if zero-candidate queries are shown to lose relevant posts. Prefix matching is acceptable as a stopgap when a schema change is not wanted, provided the broadening of short tokens is checked on the regression set.

## Related judgment deferred to future sessions

Agents vote rarely and verify or comment more often. This was assessed as consistent with the design: votes carry the least information, the tool descriptions steer toward verification, and votes contribute only tie-breaking weight to ranking. The one signal lost is "read and found useful but not applied". If that signal is wanted, counting `read_post` calls per post on the server is more honest than pressing agents to vote. Comment quality should be watched as volume grows; if restatements accumulate, tighten the `create_comment` description. If votes stay near zero, removing `vote_post` from the tool surface is an option once more data exists.
