# Open issues plan — 2026-10-13

Execution order for the 16 open issues on `main` at `fdc1b8dd`. Created while
resolving the incremental-reindex audit; see `CHANGELOG.md` `## Unreleased`.

## Coordination decisions (taken with the repo owner)

| Question | Decision |
|---|---|
| PR #507 is adjacent to #495/#496 but cites no issue number | Assume #507 covers them. Do not duplicate. |
| PR #506 also edits `.github/workflows/ci.yml` | Keep the #482 packaging gate as-is; resolve the trivial conflict at merge time. |
| #440 (PR in another owner's repo) and #499 (product decision) | Deferred. Not started. |

## Already in flight — out of scope here

| Issue | Covered by | Note |
|---|---|---|
| #493 | PR #505 | PR body states `Fixes #493`. Stacked on #504. |
| #495, #496 | PR #507 | `ObjectSaveIncomplete` false positive on Events under U16; covers the same area. |
| — | PR #506 | CI deduplication and dependency caching; touches the same workflow file as the #482 gate. |

## Done

| Issue | Branch | Validation |
|---|---|---|
| #497 | `fix/497-search-source-dedup` | 7 new cases + full Worker suite 5034 passed; mutation check confirmed |
| #482 | `fix/482-vscode-types-vs-engine` | compile, 112 tests, `vsce package`; mutation check confirmed |
| #477 | `fix/477-default-normalization-honesty` | 4 new disclosure cases, WorkWith suite 32, full Worker 5031/5; mutation check confirmed |
| #498 | `fix/498-gxwire-process-lifecycle` | 14 runner cases (real child processes), full Gateway 2477/35; two mutation checks confirmed |
| #500 | `fix/500-wire-lexer-precision` | 8 call-form guards, each mutation-confirmed; verified with the release binary |
| #501 | `fix/501-attribute-formula-references` | 7 parse + 4 graph guards; mutation-confirmed; **stacks on #500** |
| (perf, no issue) | `feat/gxwire-incremental-reindex` | 31 cargo tests, no warnings; end-to-end CLI measured |

## Queue

Ordered by risk reduction per unit of effort. Each item lands on its own branch
from `origin/main`, in an isolated worktree so in-progress local work is never
touched.

### 1. #500 — gxwire lexer precision
Loses function-form calls and `.Popup(`, classifies `&x = Proc(...)` as
`reads_att`, and infers object kind from the first letter of the name so
Transactions named `Pessoa*` report as `Proc`. False negatives presented as
definitive `empty: true`.
Shares `parser/lexer.rs` with #501.

### 4. #501 — attribute formulas as references
**gxwire side delivered** (PR #514, stacked on #513). A Procedure called from
`[Formula: ...]` now produces a `DefinesFormula` edge from the attribute, and
`impact` lists it under `formulas`. Verified with the release binary on the pair
named in the issue.

**Residue, Worker side, not started:** `genexus_analyze mode=callers` still mixes
attribute-formula attributes with direct callers in one `sdkCallers` list without
distinguishing them, and `genexus_search_source` still has no searchable formula
field (`fields` accepts `source`, `caption`, `description`, `parmNames`,
`webForm`). Both live in `SourceSearchService` / `AnalyzeService` and need a live
KB to confirm.

## Live Knowledge Bases

Scratch KBs are under `C:\KBs`; any directory containing `teste` is disposable
and may be left modified. `KBTeste` (GX18, largest, ~1,800 files), `KBTeste17`,
`KBTeste16`, `KBTesteProbe2`. Recorded in `AGENTS.md` so a later lane does not
rediscover them.

Two properties decide which tool can be validated where, and they are easy to
conflate:

- **They are native Knowledge Bases.** `GXLOCK`, `DATA*` and `.gx` are present,
  so object sources live in the database and are read through the SDK.
  `genexus_read`, `genexus_edit`, `genexus_analyze` and `mode=ops` work normally.
- **`genexus_wire` cannot analyse them.** GxWire indexes files on disk, so on a
  native KB it walks generated output and infers a graph from whatever text is
  there. Validating a gxwire change needs a *text tree export* - a directory of
  `.txt`/`.trn`/`.wp`/`.xml` object files - which is precisely the defect in #499.

## Needs a live Knowledge Base to close honestly

These three are not closed-scope. The code work is tractable, but the claim that
they are *fixed* needs a real GeneXus 18 KB, so they sit at the end on purpose.

### 5. #492 — Transaction Structure DSL breaks multiline formulas
`mode=ops` serializes the whole Structure and reinterprets it line by line.
Multiline formulas fail three ways; one leaves an **orphaned global attribute**
in the Knowledge Base, because `SyncTransactionNodes` runs `KBObject.Create` +
`newAttr.Save()` *before* the Transaction save and nothing undoes it when the
save is rejected. Same family as #217. Four parts: parse the formula to a
balanced `]` instead of end-of-line, insert after the complete attribute entry
rather than the physical line, make the global attribute transactional, and make
`dryRun` validate formulas instead of emitting a generic caveat.

### 6. #494 — false `WriteNotPersisted` on Transaction `add_attribute`
**Partially delivered** — merged as PR #516.
Two of the three contributing differences are closed: line endings are
canonicalized for every part under `exact`, and the description GeneXus derives
from an attribute's own name no longer fails the comparison.

**Reproduced live** on `C:\KBs\KBTeste` (GeneXus 18.0.10, rebuilt Worker hot-swapped):
`add_attribute` with `after` persists the attribute and the receipt still reports
`WriteNotPersisted` with `partialPersistenceDetected: true`. The remaining cause
is **not** textual:

- The receipt diff names line 1, but line 1 is byte-identical on both sides. It is
  named because the two texts diverge in *length*, not because that line differs.
- The SDK does **not** honour the requested insertion point. Requested
  `McpTst492Fixo` immediately after `McpTst492Valor`; persisted at the end of the
  Structure. `after` is accepted by the request and ignored on apply.

So the verdict compares an ordered rendering against a differently ordered one.
Two possible resolutions, neither trivial: order-insensitive comparison of
attribute *entries* (which weakens the check for genuinely reordered output), or
fixing the apply path to honour `after` (the real defect, and it changes what
persists, not just what is reported). The second is correct but is a behaviour
change to Structure authoring and needs its own validation.

Containment was rejected as the fix and must stay rejected: "persisted ⊇ requested"
proves an addition but not a removal, so a patch that failed to delete a line would
read as persisted. That is a false positive in a write verdict.

**Next: honour `after` in the Structure apply path.** Not started.

---

### 7. #491 — `mode=callers` reported 0 call sites for every target
**Delivered** — merged as PR #517. **Verified live.**

`#287` fixed the call syntax; the scan never reached it. Two layers sat in front of
the text, both silent:

1. `ReadObjectSource` answers with a JSON envelope, not the part text.
2. The scan reads with the default `client: "ide"`, and `ProcessTextResponse`
   returns plain text only for `client == "mcp"` — every other client gets a
   **base64** body.

Both are now stripped before the scan. Live on `C:\KBs\KBTeste` (GeneXus 18.0.10,
rebuilt Worker hot-swapped): `callers of PCalculaDesconto` went from
`callSiteCount: 0` / `CallerSitesUnconfirmed` to `callSiteCount: 2` with
`provenance: sdk-reference-cross-check`, site `Test_PCalculaDesconto` line 14.

Two mutations were caught on this one and both pointed at a wrong hypothesis first:
the envelope-only fix left `callSiteCount` at 0 because the base64 layer was still
there, and an early attempt decoded unconditionally, which would have scanned
garbage for a plain-text body. The production shape is only reachable by reading
`ProcessTextResponse`, so the guard uses it.

**Residue (recorded for #501):** when the caller is an Attribute whose referencing
expressions are attribute *formulas*, `callSiteCount` stays 0 — those live in the
Structure part, which this scan does not read, and `sdkCallers` mixes them with
direct callers in one list.

### 7. #491 — `mode=callers` scans the read envelope instead of the source
Returns `CallerSitesUnconfirmed` / `callSiteCount: 0` for every target while
`sdkCallers` is populated, and `genexus_impact direction=up` returns
`affected: []` with `fieldTrust: "complete"`. #287 fixed the syntax; the text
reaching the parser is still not the source. The data path has to be traced
first, and the fix confirmed against a real KB.

## Deferred by decision

- **#499** — a native GeneXus 18 Knowledge Base keeps object sources in the
  database, not in files. The wire walks the physical folder, indexes whatever
  text happens to be there, and writes `.gxwire_cache.bin` inside a directory the
  published contract declares read-only. Requires a product decision before code.
  Note: the incremental index enlarges that file (roughly doubles it), which makes
  this decision more pressing, not less.
- **#440** — PR into `hashgraph-online/awesome-ai-plugins`, another owner's
  repository. Needs explicit authorization.
