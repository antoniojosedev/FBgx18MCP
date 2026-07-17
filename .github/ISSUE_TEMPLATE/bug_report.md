---
name: Bug report
about: Worker crash / disconnect / timeout, wrong result, or any defect
title: "[Bug] "
labels: ""
assignees: ""
---

<!--
Please DON'T paste source code, object contents, KB paths, or anything private.
The diagnostics collector below already redacts paths / user / host / KB names —
run it and paste its output; that's usually enough for us to debug a crash
without your KB.
-->

## What happened

<!-- One or two sentences. For a crash: "the MCP disconnected / worker died". -->

## The exact tool call

<!-- The genexus_* tool + args that triggered it. For edit/patch crashes this is
     the single most useful thing. IMPORTANT: did the call pass `type`
     (e.g. type: "Transaction")? A homonym Transaction/Table needs it. -->

```json
{ "tool": "genexus_edit", "args": { } }
```

## Expected vs actual

- **Expected:**
- **Actual:**

## Diagnostics bundle (redacted — please paste)

<!--
Run this from the installed package folder (or a clone) and paste the file's
contents. It collects versions + the worker crash ledger + crash/cold-start log
markers, with paths/user/host/KB redacted to placeholders. Skim it first.

  pwsh -File scripts/collect-diagnostics.ps1

If you don't have the repo, download scripts/collect-diagnostics.ps1 from the
release and run it the same way. If the worker_debug.log isn't found, pass it:

  pwsh -File scripts/collect-diagnostics.ps1 -WorkerLog "C:\path\to\worker_debug.log"
-->

<details><summary>genexus-mcp-diagnostics.txt</summary>

```
paste here
```

</details>

## Also helpful (optional)

- Output of `genexus_whoami` (the `worker.deaths` block — a recovered native
  crash shows as `byExitCode: 70` / `WorkerNativeCrashRecovered`).
- Whether a **smaller / simpler** version of the same edit works.
- Steps to reproduce, if you can narrow it down.
