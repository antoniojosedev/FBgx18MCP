# Plan 016: Extract the duplicated "resolve design model or NoKbOpen" boilerplate

> **Executor instructions**: Follow step by step; run every verification command;
> honor STOP conditions. Update this plan's row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 9fe6817..HEAD -- src/GxMcp.Worker/Services/`
> Compare the "Current state" excerpt against live code; on a mismatch in any target
> file, treat as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/015 (its guard tests are the safety net for this refactor — land 015 first, keep it green)
- **Category**: tech-debt
- **Planned at**: commit `9fe6817`, 2026-07-20

## Why this matters

The same 6–8 line block — resolve the KB's design model, and on null return a
`NoKbOpen` error — is copy-pasted verbatim across nine services added in the SDK
expansion. Divergence has already started (some copies dropped the `try`/`catch`
around the message). Any future change to model resolution or the no-KB hint means
editing nine files in lockstep. Extracting one helper removes the duplication and
gives a single place to evolve the behavior. This is a pure, behavior-preserving
refactor; plan 015's guard tests prove the `NoKbOpen` behavior is unchanged.

## Current state

The duplicated block (exact text varies slightly per copy). Canonical form
(`DeployService.cs:37-45`):

```csharp
KBModel model;
try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; }
catch { model = null; }
if (model == null)
    return McpResponse.Err(
        code: "NoKbOpen",
        message: "No open KB / design model available.",
        hint: "Open a KB first (genexus_kb action=open).");
```

Confirmed copies (grep `try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; }`):
- `DeployService.cs:38`
- `CurlProcService.cs:40`
- `CiPipelineService.cs:38`
- `KbStatsService.cs:37`
- `ReorgImpactService.cs:37`
- `SecurityScanService.cs:37`
- `UserControlsListService.cs:35`
- `TableRelationsService.cs:45`
- `TransferService.cs:49` — **variant**: also captures `kb` (`kb = _kb?.GetKB() as KnowledgeBase; model = kb?.DesignModel;`). Handle separately (see Step 3).
- `DesignSystemService.cs:36` — inspect; may be a variant too.

All hold a `private readonly KbService _kb;` field. `_kb.GetKB()` returns `dynamic`;
`KnowledgeBase` and `KBModel` come from `Artech.Architecture.Common.Objects` (see the
`using` lines already in these files).

Convention: `McpResponse.Err(code, message, hint)` is the canonical error factory
used everywhere here.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` (GX_PATH set) | Build succeeded |
| Test | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~NewServicesGuard"` | all pass (the 015 net) |
| Grep残 | `grep -rn "GetKB() as KnowledgeBase)?.DesignModel" src/GxMcp.Worker/Services` | only the helper + TransferService variant remain |

## Scope

**In scope** (the nine services + one new helper file):
- `src/GxMcp.Worker/Services/{Deploy,CurlProc,CiPipeline,KbStats,ReorgImpact,SecurityScan,UserControlsList,TableRelations,DesignSystem}Service.cs`
- `src/GxMcp.Worker/Services/TransferService.cs` (variant — Step 3)
- New helper: add to an existing shared helper location if one fits, else create
  `src/GxMcp.Worker/Helpers/KbModelGuard.cs`.

**Out of scope**:
- Services NOT in the confirmed-copies list (e.g. `GamService`, `ModuleService`,
  `GxServerSyncService`, `PatternApplyService`, `DatabaseInfoService`,
  `Structure/*`) — they use a *different* resolution shape (`kb.DesignModel`
  directly, no `_kb?.GetKB() as KnowledgeBase`). Do NOT fold them in.
- Any behavior change (message text, code, hint) — must stay byte-identical output.
- The confirm-gate ordering from plan 015 — leave it as 015 left it.

## Steps

### Step 1: Add the helper

Create `src/GxMcp.Worker/Helpers/KbModelGuard.cs`:

```csharp
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    internal static class KbModelGuard
    {
        /// <summary>
        /// Resolves the open KB's design model. Returns true with <paramref name="model"/>
        /// set on success; false with <paramref name="errJson"/> set to the canonical
        /// NoKbOpen envelope when no KB/model is available.
        /// </summary>
        public static bool TryGetDesignModel(KbService kb, out KBModel model, out string errJson)
        {
            try { model = (kb?.GetKB() as KnowledgeBase)?.DesignModel; }
            catch { model = null; }
            if (model == null)
            {
                errJson = McpResponse.Err(
                    code: "NoKbOpen",
                    message: "No open KB / design model available.",
                    hint: "Open a KB first (genexus_kb action=open).");
                return false;
            }
            errJson = null;
            return true;
        }
    }
}
```

Confirm `KbService`'s namespace matches the `using` you add (it's
`GxMcp.Worker.Services` — add `using GxMcp.Worker.Services;` to the helper, or
reference it fully). Build to confirm the types resolve.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → Build succeeded.

### Step 2: Replace the eight straightforward copies

In each of the eight non-variant services, replace the boilerplate block with:

```csharp
if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
    return kbErr;
```

Add `using GxMcp.Worker.Helpers;` if not already present. Preserve the exact place
in the method where `model` was resolved (in `DeployService`/`CiPipelineService`
that is *after* the plan-015 confirm pre-check — do not move it above that). Ensure
the rest of each method still refers to the same `model` local.

Build after each file (or after all eight) and keep the 015 test filter green.

### Step 3: Handle the `TransferService` variant

`TransferService` also needs the `kb` (`KnowledgeBase`) object, not just `model`.
Either:
- (a) leave `TransferService` as-is (it's a single divergent copy — acceptable), OR
- (b) use the helper for the `model` and derive `kb` separately.

Prefer (a) unless (b) is clean. If you leave it, that's why the "grep残" done-criterion
allows the `TransferService` line to remain. Do NOT contort the helper to return
both just for one caller.

### Step 4: Confirm no behavior drift

**Verify**:
- `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~NewServicesGuard"` → all pass (015's `NoKbOpen` asserts across these services still hold).
- Full suite: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → green (allow documented flakies).

## Test plan

No new tests — plan 015's `NoKbOpen` guard tests already cover the extracted
behavior for all these services. If 015 is not yet landed, STOP (see Depends on).

## Done criteria

- [ ] Build exits 0
- [ ] `grep -rn "GetKB() as KnowledgeBase)?.DesignModel" src/GxMcp.Worker/Services` returns at most one line (the `TransferService` variant, if left per Step 3) — the eight copies are gone
- [ ] `KbModelGuard.TryGetDesignModel` exists and is used by the eight services
- [ ] 015 guard tests still green; full worker suite green
- [ ] No output message/code changed (the 015 asserts on `NoKbOpen` prove this)
- [ ] `plans/README.md` row updated

## STOP conditions

- Plan 015 is not landed / its tests aren't green — do 015 first.
- Any target file doesn't match the "Current state" excerpt (drift).
- After extraction, a 015 guard test flips from `NoKbOpen` to something else — you
  changed behavior; revert and report.
- A service you're editing turns out to use `model` in a way that the helper's
  scoping (`out var model`) breaks (e.g. `model` declared earlier for another use) —
  STOP for that file, leave it inline, note it.

## Maintenance notes

- New services that need the open KB's design model should call
  `KbModelGuard.TryGetDesignModel(_kb, out var model, out var err)` rather than
  re-pasting the block.
- Reviewer: confirm the helper's `NoKbOpen` message/hint is byte-identical to the
  original copies so log/telemetry consumers see no change.
