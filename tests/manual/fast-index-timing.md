# Manual: fast-index timing verification

## Goal
Confirm that on a 38k-object KB, `genexus_list_objects` is usable in ≤45s after a cold start
with `Indexing.UseLitePass=true`.

## Steps

1. Pick a large KB (≥30k objects). Note the object count via the previous
   `[BULK-INDEX]` log line from the legacy path (if available).
2. Clear the on-disk cache: delete `%LOCALAPPDATA%/GxMcp/Cache/index_*.json.gz` for that KB.
3. Start the worker fresh. Time how long the `[BULK-INDEX-LITE]` log line takes to appear.
4. Issue `genexus_list_objects --limit 5` immediately afterwards — it must return real
   objects, not "Reindexing".
5. Issue `genexus_analyze mode=impact target=<RealProc>` — it must return within a few
   seconds, even while `[BULK-INDEX-FULL]` is still pending.
6. Note the wall-clock time on `[BULK-INDEX-FULL]` for comparison with the legacy `[BULK-INDEX]`
   line from the prior baseline run.

## Acceptance
- `[BULK-INDEX-LITE] elapsedMs` ≤ 45000 (45s)
- `genexus_list_objects` returns within 1s after lite completes
- `genexus_analyze mode=impact` returns within 5s for a target with <100 callers

Save the full stdout/stderr capture as `tests/manual/fast-index-timing.log`.
