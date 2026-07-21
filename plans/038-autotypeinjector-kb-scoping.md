# Plan 038: `AutoTypeInjector` caches must be scoped per KB

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`. MED-risk (touches the request hot path) — verify with the full
> gateway suite.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Gateway/AutoTypeInjector.cs src/GxMcp.Gateway/Program.RequestLoop.cs src/GxMcp.Gateway/Program.Whoami.cs`
> Mismatch vs the described state = STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`AutoTypeInjector` auto-fills a missing `type` argument on tool calls (e.g.
`genexus_read name=Customer` → `type=Transaction`) from a name→type cache. That cache
(`_nameLookup`, `_toolHasTypeCache`) is `private static` and **not keyed by KB**, and
`TryInject`/`RefreshFromRecentlyChanged` take no KB parameter. The refresh is fed from
*any* open KB's index updates. With two KBs open in one gateway (supported — `WorkerPool`
keys workers by alias) where an object name maps to different types (`Customer` =
Transaction in KB-A, Business Component in KB-B), a call against KB-B can get the type
cached from KB-A injected — a silent wrong-target read/edit. The adjacent `_databaseInfoByKb`
in the same area IS keyed by KB, showing the un-keyed injector cache is an oversight.

## Current state

- `src/GxMcp.Gateway/AutoTypeInjector.cs`:
  - `internal static class AutoTypeInjector` (line 17).
  - `private static readonly ConcurrentDictionary<string, string?> _nameLookup` (43).
  - `private static readonly ConcurrentDictionary<string, bool> _toolHasTypeCache` (47).
  - `public static bool TryInject(string toolName, JObject? arguments, out string injectedType)` (56) — reads `_nameLookup` (80).
  - `public static void RefreshFromRecentlyChanged(JArray? recentlyChanged)` (99) — writes `_nameLookup` (111).
  - `internal static void PrimeIndex(...)` (146), `ClearAll()` (154), `CompleteName(...)` (128).
  - `_toolHasTypeCache` is a per-*tool* schema cache (does the tool accept a `type` arg) —
    that is NOT KB-specific and should stay global. Only `_nameLookup` (name→type) needs
    KB scoping.
- Feeder: `src/GxMcp.Gateway/Program.Whoami.cs` `UpdateLastKnownIndexState(...)` calls
  `RefreshFromRecentlyChanged(recentlyChanged)` — read it to find the KB alias available
  there.
- Call site: `src/GxMcp.Gateway/Program.RequestLoop.cs` (~line 285) calls
  `AutoTypeInjector.TryInject(toolName, args, out var _ait)` — the resolved KB alias /
  `KbHandle` is available earlier in that method (used for worker dispatch). Read it.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Full gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:quiet` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Gateway/AutoTypeInjector.cs` — key `_nameLookup` (and `PrimeIndex`/
  `ClearAll`/`CompleteName`/`RefreshFromRecentlyChanged`/`TryInject`) by KB alias.
- `src/GxMcp.Gateway/Program.RequestLoop.cs` — pass the resolved KB alias into `TryInject`.
- `src/GxMcp.Gateway/Program.Whoami.cs` — pass the KB alias into `RefreshFromRecentlyChanged`.
- Any other caller of these methods (grep first — see Step 1).
- `src/GxMcp.Gateway.Tests/` — a cross-KB test.

**Out of scope:** `_toolHasTypeCache` / `ToolAcceptsTypeArg` / `SchemaDeclaresTypeProperty`
/ `_toolsWithTypeArg` — these are schema-shape caches, not KB-specific; leave global.

## Git workflow

- Branch: `advisor/038-autotypeinjector-kb-scoping`
- One commit: `fix(gateway): scope AutoTypeInjector name→type cache per KB`
- Do NOT push.

## Steps

### Step 1: Find every caller

`grep -rn "AutoTypeInjector\." src/GxMcp.Gateway` and list every call to `TryInject`,
`RefreshFromRecentlyChanged`, `PrimeIndex`, `ClearAll`, `CompleteName`. Each must be able
to supply a KB alias. If any call site has NO KB context available and can't get one
without a large refactor, STOP and report which one.

### Step 2: Key `_nameLookup` by KB alias

Change `_nameLookup` to `ConcurrentDictionary<string, ConcurrentDictionary<string, string?>>`
keyed by normalized KB alias (outer) → name→type (inner). Add a `string kbAlias`
parameter to `TryInject`, `RefreshFromRecentlyChanged`, `PrimeIndex`, `ClearAll`,
`CompleteName`. Normalize the alias the same way `WorkerPool`/`_databaseInfoByKb` do
(e.g. `ToLowerInvariant` — match the existing convention; grep `NormalizedAlias`).
`GetOrAdd` the inner dictionary per alias. `ClearAll(alias)` clears only that KB's inner
map; add KB-drop on worker exit / KB close if there's an obvious hook (optional — a
stale inner map is bounded and harmless; do not add plumbing if none exists).

### Step 3: Thread the alias through the two call sites

- `Program.RequestLoop.cs` `TryInject` call: pass the alias already resolved for worker
  dispatch in that method.
- `Program.Whoami.cs` `UpdateLastKnownIndexState`: pass the KB alias it's updating state
  for.

Preserve single-KB behavior exactly (the common case): one alias → one inner map →
identical results to today.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 4: Cross-KB test

Add `src/GxMcp.Gateway.Tests/AutoTypeInjectorKbScopingTests.cs` (model on the existing
`AutoTypeInjector` tests — search for `AutoTypeInjector` / `PrimeIndex` in the test
project). Cover:
- Prime KB-A with `Customer→Transaction`, KB-B with `Customer→BusinessComponent`;
  `TryInject` for a call scoped to KB-B injects `BusinessComponent`, and KB-A injects
  `Transaction` — no cross-contamination.
- Single-KB behavior unchanged (regression): prime one KB, `TryInject` returns its type.
- `ClearAll(aliasA)` doesn't affect KB-B's map.

Serialize these tests if the existing `AutoTypeInjector` tests were made to run serially
(the repo notes prior parallel-race issues with `AutoTypeInjector` state) — follow the
existing test file's collection/attribute setup.

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:quiet` → all pass.

## Done criteria

- [ ] Gateway build exits 0
- [ ] `_nameLookup` is keyed by KB alias; `TryInject`/`RefreshFromRecentlyChanged`/`PrimeIndex`/`ClearAll`/`CompleteName` take a KB alias
- [ ] `_toolHasTypeCache` (schema cache) left global
- [ ] Cross-KB test proves no contamination; single-KB regression test passes
- [ ] Full gateway suite passes
- [ ] `git status` shows only in-scope files

## STOP conditions

- A caller of these methods has no reachable KB alias and getting one needs a broad
  refactor (Step 1) — STOP and report which.
- Existing `AutoTypeInjector` tests can't be adapted to the new signatures without
  changing their intent — STOP.
- The request-loop call site's alias isn't the same identity used to resolve the worker
  (would inject from the wrong KB) — STOP and report.

## Maintenance notes

- Reviewer: confirm the alias passed at `TryInject` is the SAME normalized alias used to
  route the call to its worker, and that single-KB behavior is byte-identical to before.
- Dropping a KB's inner map on close is a nice-to-have, not required (bounded growth: one
  small map per KB ever opened in the process).
