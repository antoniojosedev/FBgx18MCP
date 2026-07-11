# Plan 006: Factor a shared filter-predicate builder for SearchService & ListService

> **Executor instructions**: Behavior-preserving refactor — but FIRST prove the two
> current filter chains are equivalent (or document where they intentionally differ).
> Honor STOP conditions. Update `plans/README.md`.
>
> **Drift check**: `git diff --stat b326cd4..HEAD -- src/GxMcp.Worker/Services/SearchService.cs src/GxMcp.Worker/Services/ListService.cs`

## Status

- **Priority**: P2
- **Effort**: S-M
- **Risk**: LOW (if equivalence is verified first)
- **Depends on**: 002 (do the indexes first, then commonize)
- **Category**: tech-debt
- **Planned at**: commit `b326cd4`, 2026-07-10

## Why this matters

`SearchService.cs:169-199` and `ListService.cs:193-470` independently re-implement the
same filter predicates (type, domain, description substring, parent/parentPath, date
range). The type filter notably differs: Search uses an `IsTypeMatch` helper
(alias/synonym-aware) while List uses inline `filterTypes.Contains`. That means filter
semantics can silently drift between `search` and `list`, and every new filter must be
added twice. One shared builder removes the drift risk.

## Current state

- `SearchService.cs:169-199` — filter chain, type via `IsTypeMatch`.
- `ListService.cs:193-470` — filter chain, type via inline `Contains`.
- Both operate over `IEnumerable<SearchIndex.IndexEntry>`.

## Steps

### Step 1 (MANDATORY FIRST): characterize the current behavior

Write tests that pin the CURRENT behavior of BOTH services for: type filter with an
alias/synonym, description substring casing, parent vs parentPath, date-range
boundaries, empty-filter. Run them at HEAD — they document reality, including any
Search-vs-List divergence. **Record the divergences in this plan file.**

### Step 2: extract the shared builder

Create an `IndexEntryFilterBuilder` (or equivalent) taking the common criteria and
producing a predicate/`IEnumerable` filter over `IndexEntry`. Decide per divergence
found in Step 1 whether to (a) unify on the richer behavior (`IsTypeMatch`) or
(b) keep a documented option flag. Prefer unifying on `IsTypeMatch` unless a Step-1
test shows a caller depends on the raw-Contains behavior.

### Step 3: route both services through it

Keep Search's ranking/scoring separate — only the filtering is shared. Consult the
Plan 002 type/domain indexes inside the builder where present.

**Verify**: Step 1 characterization tests still pass (or, where you intentionally
unified, update the specific test with a comment explaining the reconciliation).

## Commands

| Purpose | Command | Expected |
|---|---|---|
| Set SDK ref | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | 0 errors |
| Test | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | all pass |

## Scope

**In scope:** `SearchService.cs`, `ListService.cs`, a new filter-builder file, tests.
**Out of scope:** ranking/scoring; the index structures (Plan 002 owns those).

## Done criteria

- [x] Build 0 errors; full Worker suite green (4 known skips)
- [x] Step-1 characterization tests exist and pass; divergences documented in this file
- [x] Both services call the shared builder (no duplicated predicate chains remain)

## Executor findings (2026-07-10)

Step 1 characterization tests: `src/GxMcp.Worker.Tests/FilterBuilderCharacterizationTests.cs`.

Confirmed divergences/behaviors, decision per each:

- **Type match — DIVERGENT, deliberately preserved.** SearchService's `IsTypeMatch`
  is alias/synonym-aware with a substring-containment fallback (`type.Contains(query)`),
  so `typeFilter="WebForm"` also matches `WebFormAttribute`. ListService's
  `filterTypes.Contains(e.Type)` is an exact case-insensitive set match — no
  over-matching, and no alias table (`typeFilter="prc"` matches nothing, since no
  type is literally named "prc"). Unifying on `IsTypeMatch` would change List's
  observable results (over-matching), which existing List tests implicitly rely
  on not happening. **Decision: keep both.** `IsTypeMatchAliasAware` was moved into
  the new `IndexEntryFilterBuilder` as the single source of truth for Search's four
  call sites (was previously copy-pasted logic sitting in `SearchService` only);
  List's exact-set match stayed untouched inline in `ListService.ListObjects`.
- **Domain filter — not actually duplicated.** Grepped `ListService.cs` for
  `BusinessDomain`/`domainFilter`/`DomainFilter` — zero matches. ListService has no
  domain-filtering concept at all (`ListCriteria` has no domain field). Nothing to
  commonize; left inline in SearchService only.
- **Parent/parentPath equality-as-safety-net — not actually duplicated.**
  SearchService applies a redundant `.Where` equality check after a
  `ChildrenByParent` index hit (gated on `sourceSet == index.Objects.Values` by
  reference), for defense-in-depth. ListService's index path only does the
  `ChildrenByParent` dictionary lookup and never re-applies an equality `.Where`
  afterward. Since List has no matching duplicate, left inline in SearchService.
- **Description substring match — TRUE duplicate.** Identical
  `(e.Description ?? "").IndexOf(x, OrdinalIgnoreCase) >= 0` logic in both. Factored
  into `IndexEntryFilterBuilder.DescriptionContains(filter)`; both services now call it
  (List also uses it for the description half of its legacy name-or-description filter).
- **Date range (Since inclusive / ModifiedBefore exclusive) — TRUE duplicate.**
  Byte-for-byte identical in both, including the "`DateTime.MinValue` never satisfies
  ModifiedBefore" edge case. Factored into `IndexEntryFilterBuilder.SinceInclusive` /
  `ModifiedBeforeExclusive`; both services now call them.
- **Runtime-SDK fallback path (ListService, no index available) — out of scope.**
  Operates over `RuntimeListEntry`/`KBObject`, not `SearchIndex.IndexEntry`; the plan's
  "Current state" section scopes this to the `IEnumerable<IndexEntry>` path only.

Ranking/scoring in SearchService was untouched — only the pre-ranking filter
predicates route through the shared builder.

## STOP conditions

- Step 1 reveals a divergence that a caller clearly depends on and you can't tell
  which behavior is correct → STOP and ask the maintainer which is canonical.

## Maintenance notes

- Reviewer: confirm Search's scoring wasn't accidentally folded into the shared filter.
