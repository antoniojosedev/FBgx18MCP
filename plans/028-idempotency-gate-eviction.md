# Plan 028: `IdempotencyCache` evicts per-key gates instead of leaking them forever

> **Executor instructions**: Follow step by step, verify each step, touch only
> in-scope files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`. This is a concurrency-sensitive change — read the "Correctness
> argument" section before writing code.
>
> **Drift check (run first)**: `git diff --stat 00573c3..HEAD -- src/GxMcp.Gateway/IdempotencyCache.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `00573c3`, 2026-07-21

## Why this matters

`_gates` is a `ConcurrentDictionary<(kbPath,tool,key), SemaphoreSlim>` that
serializes concurrent computes of the same idempotency key. It is populated via
`GetOrAdd` (line 52) and **never removed anywhere in the file**. `key` is a
client-supplied idempotency key (a natural client pattern is a fresh UUID per call).
A long-lived gateway serving many distinct write calls therefore accumulates one live
`SemaphoreSlim` per unique key **forever** — unbounded memory plus undisposed
`SemaphoreSlim` handles — even though the result-cache entries those gates guard have
long since TTL-expired and been evicted from the bounded `KbBucket`. Add an eviction
path.

## Current state

- `src/GxMcp.Gateway/IdempotencyCache.cs`. `_gates` declared lines 21–22. The only
  write is `GetOrAdd` at line 52 inside `GetOrCompute`:

```csharp
var gate = _gates.GetOrAdd((kbPath, tool, key), _ => new SemaphoreSlim(1, 1));
bool gateAcquired = await gate.WaitAsync(GateAcquisitionTimeout).ConfigureAwait(false);
if (!gateAcquired) { ... return await factory()...; }   // timeout path: no gate held

try
{
    if (TryGet(...)) return cached!;
    try { var result = await factory()...; Put(...); return result; }
    catch (ErrorNotCacheable ex) { return ex.Result; }
}
finally
{
    gate.Release();
}
```

The result cache (`KbBucket`/`Shard`) is already a bounded, TTL'd LRU — only `_gates`
grows without bound.

## Correctness argument (read before coding)

The gate only needs to serialize concurrent *in-flight* computes of the same key.
Once a key's result is cached, later callers hit `TryGet` and never touch the gate.
So a gate for a completed key is dead weight and safe to remove. The only observable
effect of removing a gate that a not-yet-arrived caller will re-`GetOrAdd` is that two
callers could, in a rare race, each hold a *different* `SemaphoreSlim` instance for
the same key and both run `factory()` — a duplicate compute, not corruption. The class
already tolerates exactly this in its timeout path (comment lines 56–58,
"best-effort idempotency beats a deadlock"). Therefore removal-when-uncontended is
safe. Use the value-matching `TryRemove(KeyValuePair)` overload so you only remove the
gate instance you actually hold (never a replacement someone else added).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~Idempotency"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Gateway/IdempotencyCache.cs` — `GetOrCompute`'s `finally`, plus a small
  helper if needed.
- `src/GxMcp.Gateway.Tests/` — a test.

**Out of scope:**
- The `KbBucket`/`Shard` LRU — already bounded; do not touch.
- The 30s gate-acquisition timeout / best-effort fallback — keep it.
- Changing the gate's purpose or the `(kbPath,tool,key)` key shape.

## Git workflow

- Branch: `advisor/028-idempotency-gate-eviction`
- One commit: `fix(gateway): evict idempotency gate entries to stop unbounded growth`
- Do NOT push.

## Steps

### Step 1: Remove the gate in `finally` when uncontended

In the `finally` block that calls `gate.Release()`, after releasing, attempt to remove
and dispose the gate iff nobody else is holding/waiting on it. Use the
value-matching overload:

```csharp
finally
{
    gate.Release();
    // If no other caller is holding or waiting on this gate, evict it so _gates
    // doesn't grow without bound. CurrentCount == 1 means fully released and idle.
    if (gate.CurrentCount == 1 &&
        _gates.TryRemove(new KeyValuePair<(string, string, string), SemaphoreSlim>((kbPath, tool, key), gate)))
    {
        gate.Dispose();
    }
}
```

Add `using System.Collections.Generic;` if not already present (it is used elsewhere;
confirm). The `TryRemove(KeyValuePair)` overload removes only if the stored value is
still `gate` — so a concurrent `GetOrAdd` that returned this same instance is
unaffected (it still holds the reference), and a replacement instance is never
clobbered.

Note the timeout path (`!gateAcquired`) does not hold the gate and must NOT try to
remove/dispose it — leave that branch as-is.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 2: Test

Create `src/GxMcp.Gateway.Tests/IdempotencyGateEvictionTests.cs`. Model on any
existing `IdempotencyCache` test (search for `new IdempotencyCache`). To assert
`_gates` doesn't grow, expose the count minimally: if there's no existing accessor,
add an `internal int GateCount => _gates.Count;` property (guarded by
`InternalsVisibleTo` if the test project already uses it — check the gateway csproj /
an existing internal-accessed test; if `InternalsVisibleTo` is NOT already set up, do
NOT add it — instead assert behavior indirectly and note the limitation). Cover:
- After N sequential `GetOrCompute` calls with N distinct keys that all complete,
  `GateCount` is 0 (or bounded), not N.
- Idempotency still holds: two calls with the same key + same payload run `factory`
  once and return the cached result on the second call.
- A `payloadHash` mismatch on a reused key still throws `IdempotencyConflictException`
  (regression — eviction must not change conflict detection, which lives in the bucket
  not the gate).

**Verify**: `dotnet test ...GxMcp.Gateway.Tests... --filter "FullyQualifiedName~Idempotency"` → all pass.

## Done criteria

- [ ] Gateway build exits 0
- [ ] `_gates` has a removal site in `GetOrCompute`'s `finally` using the value-matching `TryRemove` overload, and the removed semaphore is disposed
- [ ] Idempotency semantics preserved: same-key/same-payload computes once; conflicting-payload still throws
- [ ] New test shows gates don't accumulate for completed keys
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `GetOrCompute` already removes gates (drift) — report.
- The `TryRemove(KeyValuePair)` overload isn't available on this target framework
  (it is on net8.0 — but if the build disagrees, STOP; do not fall back to the plain
  `TryRemove(key)` overload, which could clobber a replacement gate).
- You cannot observe `_gates` size without adding `InternalsVisibleTo` that isn't
  already configured — STOP and report; do not add new assembly-visibility plumbing.

## Maintenance notes

- The worst case after this change is a rare duplicate `factory()` run under a tight
  race — acceptable and already tolerated by the timeout path. There is no corruption
  path.
- Reviewer: confirm the timeout branch (`!gateAcquired`) does not remove/dispose the
  gate, and that disposal only happens after a successful value-matching removal.
