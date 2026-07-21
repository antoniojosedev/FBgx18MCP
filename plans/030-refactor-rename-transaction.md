# Plan 030: `RefactorService` rename is atomic (transaction around patch-callers + rename)

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`. This changes SDK save semantics — read STOP conditions carefully.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Worker/Services/RefactorService.cs`
> Mismatch vs the excerpts = STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`RenameAttribute` and `RenameObject` patch every caller's source (regex replace
`oldName`→`newName`, `EnsureSave()` per caller) and only *then* rename the target
itself (`obj.Name = newName; obj.EnsureSave();`) — with no transaction and no
rollback. If the final target rename throws after one or more callers were already
saved, the KB is left inconsistent: callers now reference `newName`, but the target
still has `oldName`, so every patched caller has a dangling reference to a name that
doesn't exist. The sibling `StructureService` already wraps its multi-step SDK
mutations in `KB.BeginTransaction()` / `Commit()` / `Rollback()`; do the same here so
a failure at any step rolls the whole rename back.

## Current state

- `src/GxMcp.Worker/Services/RefactorService.cs`.
  - `RenameAttribute` (~lines 420–481): patches callers in a `foreach` (each
    `caller.EnsureSave()` at ~line 445), then `attrObj.Name = newName;
    attrObj.EnsureSave();` (~lines 454–455), unguarded.
  - `RenameObject` (~lines 483–560+): same shape — patch callers (~line 548), then
    `obj.Name = newName; obj.EnsureSave();` (~lines 557–558), unguarded.
  - Both run inside `Refactor(...)`'s outer `try/catch` that returns
    `RefactorFailed` (lines 147–157).

Per-caller patch loop (RenameObject, representative):

```csharp
foreach (var objName in affectedObjects.Distinct()) {
    var caller = _objectService.FindObject(objName);
    if (caller == null) { failed.Add(...); continue; }
    bool changed = false;
    try {
        foreach (var part in caller.Parts.Cast<KBObjectPart>()) {
            if (part is ISource sourcePart) {
                string original = sourcePart.Source;
                if (!string.IsNullOrEmpty(original)) {
                    string updated = Regex.Replace(original, pattern, newName);
                    if (updated != original) { sourcePart.Source = updated; changed = true; }
                }
            }
        }
        if (changed) { caller.EnsureSave(); _indexCacheService.UpdateEntry(caller); patched.Add(objName); }
    } catch (Exception patchEx) { failed.Add(new JObject { ["name"]=objName, ["reason"]=patchEx.Message }); }
}
// Now rename the object itself.
obj.Name = newName; obj.EnsureSave(); _indexCacheService.UpdateEntry(obj);
```

Reference — the transaction pattern already used in this repo,
`StructureService.cs:46-66`:

```csharp
using (var sdkTrans = trn.Model.KB.BeginTransaction()) {
    try {
        ... mutate + trn.EnsureSave();
        sdkTrans.Commit();
        _objectService.GetKbService().GetIndexCache().UpdateEntry(trn);
        return Models.McpResponse.Ok(...);
    } catch (Exception ex) {
        sdkTrans.Rollback();
        return Models.McpResponse.Err(...);
    }
}
```

`obj.Model.KB` / `attrObj.Model.KB` exposes `BeginTransaction()`.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Refactor"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/RefactorService.cs` — `RenameAttribute` and
  `RenameObject` only.
- `src/GxMcp.Worker.Tests/` — a test if one is achievable (see STOP conditions).

**Out of scope:**
- `RenameVariable`, `ExtractProcedure`, `WWPSetCondition` — unchanged.
- The regex/patch logic itself and the `failed`/`patched` reporting shape — keep them;
  only wrap them in a transaction.
- `_indexCacheService.UpdateEntry` calls — keep, but move them to after `Commit()`
  (mirror StructureService: update the index only once the transaction committed).

## Git workflow

- Branch: `advisor/030-refactor-rename-transaction`
- One commit: `fix(refactor): wrap rename (patch-callers + rename target) in a KB transaction`
- Do NOT push.

## Steps

### Step 1: Wrap `RenameObject`'s mutation in a transaction

Wrap the caller-patch `foreach` + the target rename in
`using (var sdkTrans = obj.Model.KB.BeginTransaction()) { try { ... obj.Name=newName;
obj.EnsureSave(); sdkTrans.Commit(); } catch { sdkTrans.Rollback(); throw; } }`.
Guidance:
- The target rename (`obj.Name = newName; obj.EnsureSave();`) goes INSIDE the
  transaction, after the caller loop.
- On any exception (including from the final `EnsureSave`), `Rollback()` then rethrow
  so the outer `Refactor` catch still returns `RefactorFailed` — but now the callers'
  saves are rolled back too, so the KB stays consistent.
- Move `_indexCacheService.UpdateEntry(...)` calls to AFTER `Commit()` (both the
  per-caller ones and the target one). Collect the patched objects during the loop and
  re-index them post-commit, matching StructureService's "commit, then UpdateEntry"
  order. If per-caller inline `UpdateEntry` is simpler to keep and the index is
  tolerant of a rolled-back entry, that is acceptable ONLY if you can confirm
  `UpdateEntry` doesn't persist to disk mid-transaction — otherwise move it out.
- Keep the existing per-caller inner `try/catch` that records `failed` — a caller that
  can't be patched should still be recorded; decide (and document in the commit) whether
  a `failed` caller aborts the transaction or is tolerated. **Preserve current
  behavior**: today a failed caller is tolerated (added to `failed`, loop continues,
  target still renamed). Keep that — the transaction's job is to roll back on a *hard*
  throw from the target rename, not to change the partial-success contract.

### Step 2: Apply the same wrapping to `RenameAttribute`

Identical treatment with `attrObj.Model.KB.BeginTransaction()` and `attrObj.Name =
newName; attrObj.EnsureSave();` inside the transaction.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 3: Test (best-effort — see STOP)

If the existing `Refactor` tests can drive a rename against a test KB and simulate a
failing final rename, add a test asserting that after a failed rename no caller retains
`newName` (rollback worked). If renames can only be exercised against a live SDK KB,
STOP after build + running the existing `--filter "FullyQualifiedName~Refactor"` suite
green, and report that a failure-injection test needs a live KB.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~Refactor"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] Both `RenameAttribute` and `RenameObject` wrap patch-callers + target rename in a
      single `BeginTransaction()` with `Commit()` on success and `Rollback()` on throw
- [ ] Existing `Refactor` tests still pass (no partial-success-contract regression)
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live rename methods are already transactional (drift) — report.
- `obj.Model.KB.BeginTransaction()` doesn't compile / the SDK type differs from
  `StructureService`'s usage — STOP and report (do NOT hand-roll a compensating undo).
- Wrapping the loop in a transaction changes the existing tests' partial-success
  results (some callers previously saved independently) — STOP; that's a semantic
  change needing a maintainer decision, not an improvised fix.
- You cannot verify whether `_indexCacheService.UpdateEntry` writes to disk
  mid-transaction — move the calls after `Commit()` to be safe; if that's not
  structurally possible without larger refactoring, STOP and report.

## Maintenance notes

- This is the highest-risk plan in the batch: it changes save ordering. The reviewer
  must confirm (a) the partial-success contract for un-patchable callers is unchanged,
  and (b) index updates happen only after commit.
- Follow-up (out of scope): `RenameVariable` may warrant the same treatment if it also
  multi-saves — check separately.
