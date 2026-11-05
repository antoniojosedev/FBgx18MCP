# Fork-local policy (FBgx18MCP)

This file holds the fork-local operator rules that used to live in `AGENTS.md`.
`AGENTS.md` keeps upstream content plus a short pointer here so it stays under
the agent truncation limit (agy reads 24 KB). Re-check this file on every
upstream sync.

## Peer review, pre-push and fork rules

- **Shared peer-review assets:** `C:\Dev\Knowledge\GeneXus-XPZ-Skills` is the canonical root for the shared `xpz-llm-delegate` skill, its `15-revisao-por-pares.md` methodology, and its `scripts\` directory. Do not copy those scripts into this fork. If that root is inaccessible, report the limitation and do not invent an alternative path or label an informal consultation as peer review.
- **Peer review and reinforced pre-push:** When the user requests a peer review or a reinforced pre-push review, load and follow the `xpz-llm-delegate` skill. Do not label informal subagent feedback as a peer review.
  - Load `C:\Dev\Knowledge\GeneXus-XPZ-Skills\xpz-llm-delegate\SKILL.md` and `C:\Dev\Knowledge\GeneXus-XPZ-Skills\15-revisao-por-pares.md` before responding.
  - Resolve the preferred reviewers with `C:\Dev\Knowledge\GeneXus-XPZ-Skills\scripts\Resolve-LlmDelegatePreferredReviewers.ps1 -Orchestrator codex`; a resolved list is a human preference, not authorization.
  - Classify the payload and run `C:\Dev\Knowledge\GeneXus-XPZ-Skills\scripts\Resolve-LlmDelegateAuthorization.ps1` for every destination. Announce each destination before dispatch; `kb-sensitive` material requires the gate outcome and any required human authorization.
  - Before dispatch, evaluate the full preferred list with `C:\Dev\Knowledge\GeneXus-XPZ-Skills\scripts\Resolve-LlmDelegatePanelDiversity.ps1`. A peer-review panel needs at least two *effectively consulted* known model-creator families; an unavailable, errored, empty, or off-task response does not count.
  - Use one identical manuscript and explicit reviewer-only prompt. Record every preferred reviewer's final state, including technical failures; do not silently reduce the panel to the diversity minimum.
  - Dispatch CLI reviewers only through `C:\Dev\Knowledge\GeneXus-XPZ-Skills\scripts\Invoke-LlmDelegatePanelDispatch.ps1`; adapters and support scripts remain in the same shared root.
  - Before reporting the round as complete, run `C:\Dev\Knowledge\GeneXus-XPZ-Skills\scripts\Resolve-LlmDelegatePeerReviewCloseout.ps1` with the initial preference snapshot, selected reviewers, actual reviewer states, diversity state, and vNext state. If drafting a revised vNext from the feedback, it is pending resubmission until the panel reviews it again or the human explicitly declines that resubmission.
- **Mechanical pre-push (this fork):** When the user asks for a pre-push routine
  without peer review, run the local checker after refreshing the comparison
  ref. Canonical forms from the repo root:
  `git fetch origin` then
  `pwsh -NoProfile -File scripts/Invoke-PrePushMechanicalChecks.ps1 -AsJson`
  (default), or, to force both Rust lanes,
  `pwsh -NoProfile -File scripts/Invoke-PrePushMechanicalChecks.ps1 -AsJson -IncludeRustLane`.
  Outside the repo root, use the absolute path quoted with backslashes:
  `pwsh -NoProfile -File "C:\Dev\Fork\FBgx18MCP\scripts\Invoke-PrePushMechanicalChecks.ps1" -AsJson`
  or the same form followed by ` -IncludeRustLane`. These are the literal forms
  the operator's global strict-invocation rule defers to; any other variant can
  trigger a permission prompt. Without `-IncludeRustLane`, a changed range
  touching `src/gx-engine/` or `src/gxwire/` auto-enables only the matching
  lane. `-CargoExecutable` is a test seam, not an operator form.
  This script is **fork-local** (not upstream); it never runs `git push`.
  Every phase that produced output writes its output, with recognized secrets
  redacted (the set is defined once in `scripts/release-contract.ps1`,
  `Protect-GxMcpReleaseText`; the checker delegates to it), to a per-run log
  directory under `%TEMP%` (`gxmcp-pre-push-<guid>`),
  exposed as `outputPath` per phase and printed by the text summary when run
  without `-AsJson`; a failing release-preflight summary is retained
  as `details.summaryPath` and each failed phase's `reason`/`exitCode`/`command`
  travels in `details.failedPhaseDetails`. So a red run leaves evidence on disk
  even when the console tail (last 60 lines per phase) drops the cause. Artifacts
  older than 7 days are pruned on the next run (`-ArtifactRetentionDays`).
  `pushReadiness=readyLocal` means only the mechanical gates and checkout state
  are ready — semantic review, real-KB/IDE behavior, remote CI, and publication
  remain separate. Follow with the semantic coherence search required by the
  global pre-push rules; do not treat mechanical `exit 0` as authorization to
  push.
  The checker validates `scripts/release-contract.ps1` before dot-sourcing it
  (top-level statements, blocks, `using`, and function-name collisions), so a
  refused contract never runs; the guard is structural, not a sandbox. A
  preflight that exits non-zero without any failed phase is reported as
  `details.aggregateFailure`, not as an empty phase list. `-IncludeRustLane`
  forces the advisory `gx-engine` and `gxwire` fmt/clippy/test lanes. Without
  it, a changed range touching `src/gx-engine/` or `src/gxwire/` auto-enables
  only the matching lane and records `auto-enabled-by-changed-paths`; neither
  lane changes `pushReadiness`, and a lane that did not run appears in
  `notCovered`, so `readyLocal` is not a claim about the Rust crates. Detailed
  limits live in `FORK_CHANGELOG.md`; non-blocking gaps in this fork-owned
  routine are tracked in `FORK_BACKLOG.md`.
- **Fork policy — report, do not patch (this fork):** A pre-push finding,
  mechanical or semantic, that points at upstream-authored code is reported and
  filed as an issue in `lennix1337/Genexus18MCP` (prefer `pt-BR`, per the issue
  rules above); the local tree is left untouched and the routine ends at
  diagnosis, reproduction evidence and a proposed change. Writing anything is a
  separate, explicitly approved step. The class this fork does fix locally is
  what the fork itself introduced or owns by construction:
  `scripts/Invoke-PrePushMechanicalChecks.ps1` and its `scripts/tests/`
  coverage, the `LOCAL POLICY` block below, and fork-local regressions.
  Attribute before acting. `git log upstream/main..HEAD -- <file>` counts the
  fork's own commits on a path; `git show upstream/main:<file>` shows whether
  the offending bytes already exist upstream. A `git diff --check
  origin/main..HEAD` report mixes both cases, because content that arrived
  through a merge is measured against this fork's older `origin/main` and reads
  as local. Fixing an upstream defect here is not a resolution — it only hides
  it from the maintainer. `pushReadiness=blocked` caused by an upstream defect
  is then a correct terminal state, not a backlog item.
- **Hygiene advisories (this fork):** Whitespace-hygiene findings
  (`trailing whitespace`, `new blank line at EOF`) are surfaced as **advisories**
  and create **no block of their own**: they do not set `pushReadiness=blocked` by
  themselves and are printed in text, JSON and log. All other blocking conditions
  (missing base, branch behind, dirty working tree, mechanical phase failure)
  remain fully in force. The advisories are the visible record, not a resolution:
  a hygiene finding in upstream-authored bytes is still reported under the fork
  policy above, never patched locally.

- Pull requests are always allowed upstream: create a branch, commit to it,
  push it and open or update the PR without asking. This fork works directly on
  `main`, so the stricter rule applies here: do not commit, push, merge,
  release, deploy, or close an issue unless the user explicitly asks. Issue
  closure requires a released fix and release link.
- When creating issues, submit them to the upstream repository
  (`lennix1337/Genexus18MCP`), with a preference for Brazilian Portuguese
  (`pt-BR`).

## Checkout operator policy (LOCAL POLICY)

### LOCAL POLICY — checkout operators (this fork)

> **Permanent preference** for sessions whose cwd is this checkout
> (`FBgx18MCP`). It does not replace the upstream matrix in
> [`docs/agent-operations.md`](docs/agent-operations.md#mcp-update-and-harness-synchronization);
> it sets the **agent default** when working from this tree. On `git pull` /
> sync, re-check that this file survived.

This checkout keeps harnesses on `publish\GxMcp.Gateway.exe` (not the npm
cache). The Antigravity-style target applies to **all writable clients**:
`command` = `<repo>\publish\GxMcp.Gateway.exe`, empty `args`, server key
`genexus18mcp` (not the official `genexus` HTTP entry).

**Default (complete path):** when the user asks to install, update, refresh, or
re-register MCP on this machine's harnesses/clients — including after an
upstream sync, and including phrasing such as “atualize o mcp nos harnesses” /
“atualize os clientes” / “instale o mcp” — run `.\install.ps1` at the repo
root. That path updates `config.json`, runs `build.ps1`, sets
`GENEXUS_MCP_GATEWAY_EXE` to `publish\GxMcp.Gateway.exe`, and runs
`clients add --all-clients`. Do **not** interpret those requests as a cue for
the no-rebuild re-point row.

**No-rebuild re-point only when explicit:** use
`$env:GENEXUS_MCP_GATEWAY_EXE=…; node cli\run.js clients add …` **only** if the
user clearly asks to re-point without compiling (e.g. “só reapontar”, “sem
rebuild”, “sem build”, “não recompile”). Ambiguous or incomplete requests
default to `.\install.ps1`.

**Engine backend override (upstream #649, verified on this checkout
2026-10-25):** this checkout deliberately runs with `Engine.Backend=sdk` in
`publish\config.json`, keeping the experimental `gx-engine` out of the way.
Upstream now carries the section across the rebuild instead of discarding it:
`build.ps1 -PreserveEngineConfig` keeps `Engine.Backend`, `Engine.Fallback` and
`Engine.TimeoutSeconds` (never `Engine.Executable`, which is machine-specific),
and `install.ps1` passes the switch, so `.\install.ps1` preserves the choice on
its own. A plain `build.ps1` still drops the section, but it now warns and names
the switch instead of doing it silently; `release.ps1` calls `build.ps1` without
the switch, so a release build returns to `gxengine` with that warning.
Environment variables are not an escape hatch: strict documents reject
`GXMCP_ENGINE_*` at load. Evidence: the upstream #649 guard
(`scripts/tests/test-build-publish-config.ps1`) passes, and the build's real
clean/config steps executed against this checkout's `publish\config.json` kept
`Engine.Backend=sdk` with the switch and dropped it with the warning without it.

- `.\install.ps1` no longer needs a wrapper. Run
  `build.ps1 -PreserveEngineConfig` when invoking the build directly;
  `pwsh -NoProfile -File scripts\Invoke-GxMcpLocalBuild.ps1` (`-Install`,
  `-EngineBackend`, `-InstallerArgs`) remains valid and re-applies the backend
  after the entry point.
- After a direct `build.ps1` without the switch, or after `release.ps1`, confirm
  `Engine.Backend` in `publish\config.json` and run
  `pwsh -NoProfile -File scripts\Set-GxMcpLocalEngineBackend.ps1` if it fell back
  to `gxengine`.
- Do not “fix” this with `GXMCP_ENGINE_BACKEND`; adjust the file only. Per fork
  policy neither `build.ps1` nor `install.ps1` is patched here.

For local-checkout work in this fork, **prefer** the rows below over matrix
rows that recommend `npx … clients add` / `npx … clients` / `npx … doctor`:

| Scenario (checkout) | Use this instead of the npx matrix rows |
|---|---|
| **Default:** install / update / refresh MCP harnesses (after sync or otherwise) | `.\install.ps1` at repo root (it passes `-PreserveEngineConfig`); `pwsh -NoProfile -File scripts\Invoke-GxMcpLocalBuild.ps1 -Install` is the equivalent wrapper |
| Explicit no-rebuild re-point of **all** writable harnesses | From repo root: `$env:GENEXUS_MCP_GATEWAY_EXE='<repo>\publish\GxMcp.Gateway.exe'; node cli\run.js clients add --all-clients` |
| Stale / wrong launcher (one client) | Prefer `.\install.ps1`; or the same env + `node cli\run.js clients add --clients <id>` only if the user forbade rebuild |
| Post-sync validation | From repo root: `node cli\run.js clients --format json` and `node cli\run.js doctor --mcp-smoke --format json` (**same** CLI/env as install) |

Acceptance for a harness refresh: every registered writable client lists that
`publish\GxMcp.Gateway.exe` path with no `commandStale`, and `doctor` reports
`client_config_sync` pass on this same CLI/env (not via `npx`).

**Do not** (for this checkout workflow):

- Choose the no-rebuild `clients add` path from surface wording about
  “harnesses” / “clientes” alone — that is still the `.\install.ps1` default
- `npx genexus-mcp@latest clients add …` — rewrites harnesses away from `publish\`
- `npx genexus-mcp clients` / `doctor` as “truth” for a checkout-registered
  launcher — can report false stale / `client_config_sync` against the npm
  package Gateway

Keep the npx matrix rows for operators who **do not** run from a local
`publish\` checkout (npm-only installs).

**Fork changelog:** record fork-local changes in `FORK_CHANGELOG.md`, never in
`CHANGELOG.md`. `CHANGELOG.md` stays a faithful mirror of upstream — adding a
fork-local entry there re-creates the recurring merge conflict this policy
exists to remove. This overrides the general "entry under `CHANGELOG.md` →
`## Unreleased`" rule for the fork-local change class (the set this block owns);
upstream-authored code is still reported, not patched. Caveat: the upstream-owned
`scripts/integration-preflight.ps1` gate still asks for a `CHANGELOG.md` touch
when a change lands under a production path (`src/`, `cli/`, `scripts/`,
`install.ps1`); that gate runs only for pull requests, and this fork pushes
directly to `main`, so it does not fire in the normal flow — do not resolve it by
moving the entry back.
