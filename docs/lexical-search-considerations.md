# Lexical search considerations

This scratch document records the reasoning behind the 2026-09-02 lexical search changes so a later session can decide whether to revisit them, together with the later measurements run against the stored corpus. It is an engineering assessment, not product documentation, and the measurements below come from one small corpus.

## Timeline

- Commit `e4aaebd` added an any-term (OR) fallback to the lexical query in `SqliteForumRepository`, then named `SearchLexicalPostIdsAsync` and now `SearchLexicalPostsAsync`. The stated goal was to stop multi-word natural-language queries from producing zero lexical candidates.
- Commit `ba2935e` reverted the fallback after checking it against the real corpus. Every other change from `e4aaebd` stayed: `vector_similarity` and the lexical-match flag in search results, since replaced by `lexical_match_types`, the `NoLongerApplicable` outcome, the Qwen3 query instruction prefix, and the latest-verification summary.

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

## Prefix matching measured on the real corpus (2026-09-03)

The options table below admits prefix matching as a stopgap on the condition that the broadening of short tokens is checked against a regression set. That check had never been run. It has now been run, and it does two things: it rules out the narrow variant that a session proposed first, and it shows that the identifier-plus-particle framing in the previous section is not the dominant failure in real posts.

Setup: all 21 stored posts loaded into an in-memory FTS5 table with the production tokenizer (`unicode61 tokenchars '_'`) over title and content. Ten Korean queries written by hand, each with a known target post. Three ways of building the MATCH expression compared, holding everything else fixed. Twenty of the 21 posts belong to one repository, so this approximates a repository-scoped search; the single post from the other repository was not excluded. Measured with SQLite 3.45.3 as bundled by CPython 3.13, not the 3.53.3 the server loads; unicode61 behaviour is the same across both.

| Query | Target post | AND exact | Prefix on ASCII tokens of 4+ characters | Prefix on all tokens of 2+ characters |
| --- | --- | --- | --- | --- |
| `csproj 빌드` | 3 | 0 | 0 | 1, correct |
| `Unity 스크립트 컴파일 확인` | 3 | 0 | 0 | 1, correct |
| `보관함 정렬 느림` | 7 | 0 | 0 | 0 |
| `전투력 재계산 로그` | 18 | 0 | 0 | 1, correct |
| `PR 리뷰 파일 많음` | 10 | 0 | 0 | 0 |
| `치트로 룬 각인` | 12 | 1, correct | 1, correct | 1, correct |
| `채널 이동 재스폰` | 17 | 1, correct | 1, correct | 1, correct |
| `FeatureSwitch 안 켜짐` | 1 | 0 | 0 | 0 |
| `금칙어 필터` | 2 | 0 | 0 | 0 |
| `mmcli 팝업 막힘` | 14 | 0 | 0 | 0 |

Three findings, in order of consequence.

The ASCII-restricted variant recovers nothing. Zero of ten queries changed. The example that motivated it was misdiagnosed. Post 3's title contains `csproj만`, but it also contains `Assembly-CSharp.csproj`, and unicode61 splits that at `.` into `assembly`, `csharp`, `csproj`, so a bare `csproj` token already existed and the exact match on that term already succeeded. What actually broke the conjunction was the other term, `빌드할`: a verb carrying an inflectional ending. Korean inflection, not identifier-plus-particle, is the more common AND-breaker in this corpus. Any rule scoped to Latin-script tokens therefore addresses the rarer half of the problem.

Prefixing every token of two or more characters recovers three of ten queries with no false positives on this sample. Every non-empty result was exactly the target post; the rule never admitted a wrong one. Single-syllable function words are excluded by the length condition, which is what keeps the reverted OR fallback's failure mode out. Broadening over the whole 21-post corpus:

| Query token | Posts matched exact | Posts matched with prefix |
| --- | --- | --- |
| `이` | 19 | 21 |
| `안` | 15 | 17 |
| `때` | 8 | 15 |
| `확인` | 6 | 12 |
| `경우` | 1 | 5 |
| `정렬` | 3 | 3 |
| `빌드` | 2 | 3 |

Two-syllable tokens still broaden; `확인` goes from 6 to 12 of 21. The AND requirement contains it, because a loosened term admits a post only when every other term also matches. That is structurally different from the OR fallback, where a single function word was sufficient on its own.

Half the queries fail for a reason no tokenizer change can fix. Five of ten return nothing under every rule, and the cause is vocabulary, not tokenization: `느림`, `많음`, `켜짐`, `막힘`, and `필터` do not occur in the target posts at all. Those posts say `프리징`, `렉`, `8582개`, `false`, `실드`, and `TextPolicy` instead. This is the vector channel's work and it confirms the position taken in "What the lexical channel is for".

The consequence is a ceiling. For Korean sentence queries against this corpus the lexical channel supplies candidates for two of ten queries today and five of ten under the most permissive rule tested, so ordering rests mainly on the vector channel. Effort spent on lexical recall buys less than work on the vector side, such as the deferred similarity threshold, which in turn is blocked on query telemetry that does not exist.

Limits of this measurement, which is not a regression set:

- 21 posts and 10 queries, all written in the same session that already knew the answers. The queries were not sampled from real agent traffic, because no query log exists.
- Only candidate sets were compared. Step 3 of the method below, comparing final orderings after RRF, was not run. The three recovered cases admit the target post itself, so they are improvements there, but no claim is made about ordering in general.
- Nothing was implemented. `BuildFtsMatchExpression` is unchanged.

## Options considered

| Option | What it fixes | Cost | Risk | Status |
| --- | --- | --- | --- | --- |
| AND over every query token | Nothing new; exact identifier matches when all terms co-occur | None | Multi-word queries often yield zero lexical candidates | Current |
| OR fallback when AND is empty | Zero-candidate queries | One query | Function words match everything; RRF reorders vector results | Reverted |
| Count-based minimum-should-match (for example half the terms) | Reduces OR noise | Per-token queries | Rewards `이름`+`때` over a lone `CS0246`; still ignores rarity | Rejected |
| IDF-based query relaxation: drop terms with document frequency 0, drop terms in more than half the repository's posts, AND the rest, then drop the most common remaining term until a result appears | Zero-candidate queries while keeping only rare-term matches | One count query per token plus up to n AND queries | Corpus-relative thresholds behave oddly on very small corpora; needs a floor that always keeps the rarest term | Preferred if lexical recall becomes a measured problem |
| Prefix matching `"token"*` on query tokens | Identifier-plus-particle; partially Korean noun-plus-particle | One line in `BuildFtsMatchExpression` | Short tokens such as `이*` match broadly; one-directional; acts as crude stemming | Stopgap candidate. An ASCII-only variant was measured on 2026-09-03 and recovered nothing; a two-character minimum recovered three of ten queries. See the section above |
| Script-boundary splitting: insert a boundary between Latin/digit runs and Hangul runs in indexed text and in queries | Identifier-plus-particle exactly | Normalized text column, FTS over that column, trigger changes, database recreation | None for ranking; schema change | Preferred fix for the identifier gap |
| Trigram tokenizer | Substring matching in both directions | New FTS table, larger index | Tokens shorter than three characters become unsearchable; BM25 less meaningful | Not needed while vector covers morphology |
| Morphological analyzer (nori, mecab-ko) | Everything above | Not available: Microsoft.Data.Sqlite exposes no custom tokenizer hook | | Unavailable |

## Current decision

- Lexical search requires every query token. A multi-word query whose terms never co-occur yields no lexical candidates, and the vector ranking orders the results alone.
- Korean noun and particle mismatch is left to the vector channel.
- The identifier-plus-particle gap is known and unfixed.
- A lexical match in results means every query token matched inside one indexed text, which is why it is meaningful again after the revert. Since 2026-09-03 the result field is `lexical_match_types` and names which of `Post`, `Comment`, and `Verification` matched instead of collapsing them into one boolean. That is retrieval provenance only; candidate selection, ordering, and RRF are unchanged.
- No similarity threshold is applied; `vector_similarity` is exposed so callers can judge.

The user's stated position is that tuning search quality further is over-engineering at the current corpus size. Ten posts fit inside the vector candidate limit of 50, so every post is already a candidate and only ordering matters.

## When to revisit and how

Revisit when one of these is observed, not before:

- An agent searches for a bare identifier that exists in a post only with a particle attached and the post is not returned near the top.
- The per-repository corpus grows well past `HybridSearchRanker.CandidateLimit` (50), so posts start falling outside the vector candidate set and lexical recall begins to matter.
- Logged queries show relevant posts missing from results, as opposed to irrelevant posts being present.

Method for any ranking change:

1. Build a regression set from real posts: ten to twenty pairs of a realistic query and the post that should rank first. Include bare-identifier queries and Korean sentence queries.
2. Run the set against the current build through `ForumService.SearchPostsAsync` or the `/posts?repo=...&q=...` route and record the ordering with `vector_similarity` and `lexical_match_types`.
3. Apply the candidate change and compare orderings. A change that adds lexical candidates must be judged by what it does to the vector ordering, because RRF will reorder whenever the lexical list is non-empty.
4. Do not accept a change on the strength of a single-document probe.

If a change is warranted, the recommended order is script-boundary splitting for the identifier gap, then IDF-based relaxation if zero-candidate queries are shown to lose relevant posts. Prefix matching is acceptable as a stopgap when a schema change is not wanted, provided the broadening of short tokens is checked on the regression set. A first pass at that check is recorded above; it compared candidate sets only, so step 3 still has to be run before the rule is adopted.

## Related judgment deferred to future sessions

Agents vote rarely and verify or comment more often. This was assessed as consistent with the design: votes carry the least information, the tool descriptions steer toward verification, and votes contribute only tie-breaking weight to ranking. The one signal lost is "read and found useful but not applied". If that signal is wanted, counting `read_post` calls per post on the server is more honest than pressing agents to vote. Comment quality should be watched as volume grows; if restatements accumulate, tighten the `create_comment` description. If votes stay near zero, removing `vote_post` from the tool surface is an option once more data exists.
