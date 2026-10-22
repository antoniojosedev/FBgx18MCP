# AGENTS.md

Project-level instructions for AI assistants working on Genexus18MCP. Keep this
file short: detailed, task-specific guidance lives in the linked documents
below and should be read only when the task matches it.

## Project orientation

Genexus18MCP is a two-process MCP server exposing Knowledge Bases from the
officially supported native-SDK majors and catalogued legacy versions through
their selected driver. The default SDK route does not parse KB files or scrape
IDE state; native edits use the same SDK paths as the IDE, while legacy paths use
explicit reflection/COM adapters without changing the MCP contract. The default
experimental gx-engine route uses independent native UDM readers and compiler
paths for admitted blocking builds and field-limited type-only queries; unsupported
operations retain the SDK. See the gx-engine pointers below for admission rules.

```text
MCP clients (Claude/Cursor/…)
   │ independent stdio JSON-RPC processes
   ▼
GxMcp.Gateway (net10.0-windows, one logical MCP context per client)
   ├─ isolated: owns a direct Worker child
   └─ shared-host: attaches to a per-KB local WorkerHost broker
                         │ one compatible Worker child
                         ▼
                 GxMcp.Worker (net48 STA)
                         │ compatibility adapters + Artech.* SDK
                         ▼
                 Selected SDK/driver → Knowledge Base on disk
```

- Gateway: `src/GxMcp.Gateway/` (`net10.0-windows`); owns the worker pool and routes MCP tools. In `stdio-isolated` mode (`GatewayMode: "stdio-isolated"`), each client runs its own dedicated Gateway without an HTTP listener or shared Gateway lease. Legacy non-strict configs may still expose `Server.TransportMode`; strict v2 documents use only `GatewayMode` and reject `Server.TransportMode` at parse time. `Server.WorkerSharingMode: "shared-host"` optionally attaches compatible Gateways to one per-KB WorkerHost; `"isolated"` remains the default when separate Workers are required. In legacy HTTP mode, gateways share a master process on the HTTP port with proxies.
- Worker: `src/GxMcp.Worker/`; hosts the COM-flavoured SDK on an STA thread.
- Worker sharing: `src/GxMcp.Gateway/SharedWorker*` and `src/GxMcp.Worker/SharedWorkerHost*` implement the supported broker/attachment path. Sharing is keyed by physical KB, Worker executable, GeneXus installation, driver, and target major; it never merges Gateway sessions, authorization, caches, cancellation, progress, notifications, or artifacts.
- CLI: `cli/run.js`, `cli/index.js`, and `cli/lib/config.js`; configures MCP
  clients, forwards stdio, and ships the Windows launcher diagnostics.
- Version catalog: `config/gx-versions.json` is the explicit compatibility list;
  `supportedMajors` is the native-SDK contract and `legacyMajors` is the basic
  compatibility contract with its driver profile. `src/GxMcp.Gateway/GeneXusVersionCatalog.cs`
  is the runtime loader.
  `src/GxMcp.Worker/Compatibility/` contains reusable runtime adapters for SDK
  members that vary between GeneXus majors.
- Native SDK compatibility is a **GeneXus-major** contract, not an exact DLL build
  contract. Do not block a supported major because `ProductVersion`, patch or
  assembly hashes differ between installations; patch/build drift is expected
  and must be handled by the compatibility adapters plus focused/live smoke
  tests. A different major requires the Worker built for that major (or a
  verified adapter); never run a Worker against an unsupported major. Exact
  build fingerprints may be retained as diagnostics, but must not silently
  become a runtime compatibility gate again.
- Legacy compatibility is intentionally best-effort and driver-backed rather
  than SDK-backed: GeneXus 15 and Evolution 1–3 use `dotnet-reflection`, while
  GeneXus 8.0 and 9.0 use `com-gxpublic`/`GXPublic.GXPublic` COM automation.
- Design System compatibility: `DesignSystemSdkAdapter` uses the native helper
  when available and parses the `Tokens`/`Styles` source parts independently
  when an SDK helper member is absent.
- Package artifact: `publish/`; `GxMcp.Gateway.exe` is at its root and
  `worker/GxMcp.Worker.exe` is one level below. The npm package includes it.

<!-- BEGIN GENERATED: gx-compatibility -->
Supported SDK majors: **GeneXus 16, GeneXus 17, GeneXus 18** (native SDK).
Basic legacy compatibility: **GeneXus Evolution 3, GeneXus Evolution 2, GeneXus Evolution 1, GeneXus 15, GeneXus 9.0, GeneXus 8.0** via `com-gxpublic` and `dotnet-reflection` (not the native SDK build).
Primary SDK: **GeneXus 18**.
Source of truth: `config/gx-versions.json`.
<!-- END GENERATED: gx-compatibility -->

- **Basic legacy GeneXus support (not native SDK support):** In addition to the primary native SDK majors (GX 16, 17, 18), the server supports every legacy version declared in `legacyMajors` via driver-specific degradation:
  - GeneXus Evolution 1 (10.1), Evolution 2 (10.2), Evolution 3 (10.3), and GeneXus 15 via `dotnet-reflection` (using `DynamicSdkBridge` and `OptionalSdkInvoker` for module-less vs `QualifiedName` and API differences).
  - GeneXus 8.0 and 9.0 via `com-gxpublic` (`ComGxPublicDriver` connecting to classic Win32 `GXPublic.GXPublic` COM automation on an STA thread for `.gxi` KBs).
  - Modern tools unsupported in earlier versions return structured degradation envelopes (`UNSUPPORTED_IN_GENEXUS_VERSION`).

## gx-engine development

- For compiler/storage work, read [`src/gx-engine/README.md`](src/gx-engine/README.md);
  for MCP routing, read [`docs/engine_backend.md`](docs/engine_backend.md).
  The objective is independent full SDK replacement; current bounded coverage
  is not that outcome. `Engine.Backend` defaults to `gxengine`: admitted blocking
  builds and field-limited type-only queries use gx-engine. Reads and edits use
  SDK fallback until native identity, freshness and token parity is verified.
  `auto` routes only builds; explicit `sdk` opts out entirely. Preserve capability/argument
  gates, SDK fallback before build start, and reconciliation instead of replay
  after a started build fails or has an uncertain outcome.
- For performance work or speed claims, read
  [Plan 131](plans/131-sdk-performance-benchmark.md) first. Compare the same
  workload and observable behavior; distinguish warm/cold, selected-object/full-KB,
  and benchmarked/packaged binaries. Keep all raw samples outside Git.
- For generated-file parity, missing/extra artifacts or SDK output comparisons,
  read [gxe-artifact-parity](.agents/skills/gxe-artifact-parity/SKILL.md).
- Drive remaining full-KB failures down with SDK differential evidence and
  regression tests. Preserve refusals and malformed-input errors; account for
  skipped/not-generated objects explicitly. A green package build or unit suite
  does not establish a successful whole-KB build, runtime parity or SDK replacement.
- Live engine validation uses only disposable `C:\KBs\*teste*` KBs. Native UDM
  access can change KB state; record/restore temporary database settings and
  reconcile uncertain effects before replay. Keep Cloud off and use local
  SDK/MSBuild stages as the oracle; preserve licensing and existing SDK routing.
- Validate the affected reader/compiler/runtime path, including actual runnable
  output when behavior changes. Preserve the final `build.ps1` ordering below;
  identify the exact packaged binary in postchecks rather than borrowing timings
  from another target directory.

## KB and harness contracts

- KB resolution order is explicit `kb` → MCP-session selection (`genexus_kb action=select` / `set_session_default`) → strict/legacy resolution policy.
- In strict mode (`ResolutionPolicy: "strict"`, default), persisted `DefaultKb`/`ActiveKb` does NOT auto-seed sessions. When 1 KB is open, it resolves as `single-open` only if it does not conflict with a configured default (mismatches yield `KB_CONTEXT_REQUIRED` with `DefaultConflict`). When 0 KBs are open, declared catalog entries are never auto-opened (`KB_CONTEXT_REQUIRED` or `KB_AMBIGUOUS`).
- In legacy mode (`ResolutionPolicy: "legacy"`), the legacy fallback chain (`config-default` → `single-open` → `declared-first`) remains active.
- `genexus_kb action=open` starts/registers a Worker; `action=select` or `set_session_default` sets an in-memory session selection without mutating config files; `action=set_persistent_default` mutates the startup fallback on disk and returns `persistedTo`. `action=set_default` remains as a legacy persistent operation returning `persistedTo`.
- `genexus_whoami` and `genexus_kb action=list` expose alias auditability state:
  `sessionSelection`, `selectionSource` (`session-select`, `single-open`, `config-default`, `declared-first`, `explicit-arg`, `none`), `selectionState` (`valid`, `absent`, `invalid`, `conflicting`), `startupDefault`, `resolutionPolicy`, `config.resolvedFrom`, open, known, and declared KBs.
  KB-bound results carry `kbAlias` in-band and in MCP `_meta`.
- `genexus-mcp init` configures detected clients with neutral configurations by default (no hardcoded KB path). OpenCode must preserve both
  `mcp.<name>` and `mcp.servers.<name>` layouts and unrelated servers.
- Sessionless HTTP clients must use an explicit `kb` or persisted fallback; `select` on sessionless HTTP returns `KB_SESSION_UNAVAILABLE`. Do
  not introduce shared server-side selection between independent clients.
- `genexus_whoami` and `genexus_doctor` must preserve actionable Worker diagnostics: effective sharing mode, physical identity, pipe/host/Worker identity, generation, attachment/connection state, startup/failure detail, and the likely failing stage. Keep credentials, tokens, connection strings, and other sensitive values redacted.

## Source of truth and tool changes

- Tool schemas: `src/GxMcp.Gateway/tool_definitions.json`.
- Discovery golden fixture: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`; keep it alphabetically sorted. Regenerate automatically after intentional schema changes: `$env:GXMCP_UPDATE_GOLDEN='1'; dotnet test src\GxMcp.Gateway.Tests --filter McpDiscoveryContractTests; Remove-Item Env:\GXMCP_UPDATE_GOLDEN`.
- Tool dispatch path: gateway router → `src/GxMcp.Worker/Services/CommandDispatcher.cs` → service method. A tool change requires schema (`tool_definitions.json`), router, dispatcher, service, help catalog (`src/GxMcp.Gateway/ToolHelpCatalog.cs`), and fixture updates.
- Tool schema budget bumps require a `CHANGELOG.md` explanation.
- A published action change must also update `docs/mcp_capabilities_inventory.md` and the generated `docs/operation-contract-inventory.json`. Run `python scripts/validate-tool-contracts.py`, `python scripts/generate-operation-contract-inventory.py --check`, and the focused contract tests before pushing; the first gate checks the schema and capabilities table together.
- `genexus_query` and `genexus_list_objects` compact output must be added to
  `Program.GetDefaultCompactFields` when a new output field is introduced.
- For CLI launcher/config changes, update `cli/run.test.js`; use
  `docs/agent_playbook.md` for SDK authoring and tool-specific constraints.
- Release-facing version text is generated from `config/gx-versions.json` by
  `scripts/sync-release-metadata.py`; `release.ps1` runs it before the dirty-tree
  gate, and CI/release verification fails on drift.

## Build and test

Read [`CODING_STANDARDS.md` → Validation lanes](CODING_STANDARDS.md#validation-lanes)
for the complete check matrix and evidence rules. Start with the narrowest
behavior test; an authorized release uses the canonical preflight described in
[`docs/release_protocol.md`](docs/release_protocol.md), not repeated manual suites.
Run `.\build.ps1` **last** to preserve the published Worker/x86 fingerprint.

SDK compatibility changes require a Worker build, focused tests and live smoke
for each installed major, with `GX_PATH` selecting that major's installation.
Use [`scripts/test-live-matrix.ps1`](scripts/test-live-matrix.ps1) and
[`docs/live-kb-test-harness.md`](docs/live-kb-test-harness.md) for explicit KB/SDK
selection and passed/unavailable/failed evidence. A missing SDK or skipped live
test is not compatibility evidence. Catalog additions require a verified build
and live smoke, not a matching version string.

Backend/default changes additionally require the
[behavioral compatibility checklist](CODING_STANDARDS.md#backend-and-default-changes).
Known test limitations live in [`docs/agent_playbook.md`](docs/agent_playbook.md).

## Live Knowledge Bases for validation

Disposable test Knowledge Bases live under `C:\KBs`. Any directory whose name
contains `teste` is a scratch KB that may be written to, retargeted and left in
a modified state — it is not user data, and a lane that needs to reproduce a
write, a rollback or a persistence failure should use one rather than asking
for a KB.

| Path | GeneXus major | Notes |
| :--- | :--- | :--- |
| `C:\KBs\KBTeste` | 18 | Largest (~1,800 files); the default target for SDK-level read/write lanes |
| `C:\KBs\KBTeste17` | 17 | ~66 files; also carries `.gx` and `.gx_mirror` |
| `C:\KBs\KBTeste16` | 16 | Smallest (~10 files); fast for smoke-level checks |
| `C:\KBs\KBTesteProbe2` | 18 | ~122 files; used by probe/conversion work |

Two properties must not be conflated, because they decide which tools can be
validated where:

- **These are native Knowledge Bases.** `GXLOCK`, `DATA*` and `.gx` are
  present, so object sources live in the Knowledge Base database and are read
  through the SDK. `genexus_read`, `genexus_edit`, `genexus_analyze` and the
  `mode=ops` authoring paths work against them normally.
- **`genexus_wire` cannot analyse them, except `pack_task`.** GxWire indexes
  files on disk, so on a native KB it walks generated output and infers a graph
  from whatever text is there. Validating a gxwire change needs a *text tree
  export* of a KB (a directory of `.txt`/`.trn`/`.wp`/`.xml` object files), not a
  native KB directory. This is the defect filed as
  [#499](https://github.com/lennix1337/Genexus18MCP/issues/499), and it is why
  gxwire parser work cannot be confirmed on these paths alone. `pack_task` on
  the open native KB does not use gxwire at all: the Gateway falls through to
  the Worker (`WirePackTaskService`), which resolves `targets[]` and the
  identifiers named in `task` through the SDK-backed index, so it *can* be
  validated live on these KBs, read-only.

A live KB opens on demand:

```text
genexus_kb action=open path=<KB root>   then   action=select to bind the session
```

## Debugging and validation gates

- For a numbered issue, read the current issue with `gh issue view <number>` and
  inspect local divergence before tracing code: `pwsh -NoProfile -File
  scripts/check-upstream-drift.ps1 -BaseRef origin/main`. This command never
  fetches or changes refs; it reports whether `origin/main` contains a likely
  upstream fix that is absent locally.
- Run `npm run test:live-contract` after changing live harness/configuration
  code. It is part of `npm test` and CI; this catches duplicate KB declarations,
  stale aliases, process ownership and missing fail-closed guards without
  requiring a GeneXus installation.
- For Worker/lease/KB routing changes, a local green suite is insufficient:
  run the smallest live smoke available with `pwsh`, using an explicit KB and
  `GXMCP_LOG_DIR`. Record `workerPid`, selection state, error count and the
  isolated log path. For `shared-host`, use two independent Gateway clients and
  verify one broker/Worker identity, attachment isolation, same-object lock
  rejection, distinct-object progress, and cleanup. A live gate that cannot run
  is `unavailable`, not pass.
- Use `ripwire src --for="<specific behavior>"` before source searches, then
  search narrowed file types (`*.cs`, `*.ps1`, `*.js`) and exclude generated
  `bin`, `obj`, `publish`, `TestResults` and `.trx` artifacts. Broad numeric
  searches are diagnostic noise, not evidence.
- When a shared Worker smoke fails, inspect `worker.diagnostics` and
  `workerHealth` before changing lifecycle code. Distinguish configuration or
  identity mismatch, registry/mutex election, pipe/handshake, attachment,
  startup, respawn, TTL, malformed-frame, and child-exit failures; do not reduce
  them to a generic `no_worker` message.
- Prefer bounded output for routine checks: `dotnet test ...
  --logger "console;verbosity=minimal"`. Repeat with normal verbosity only
  when the focused check fails or no test-run banner is present.
- Use `pwsh` for PowerShell entry points, never Windows PowerShell 5.1.
  `scripts/run-gate.ps1` requires PowerShell 7.3+ so a failed native command
  cannot be masked by a later success; live entry points require 7+.
If a build/test fails with `MSB3027` or `MSB3021` naming the Gateway/Worker exe,
use the scoped permission below; do not kill unrelated processes.

- Codex Windows sandbox caveat: `npm test` may report `EPERM` when
  `applyLauncherConfigOrExit` creates `os.homedir()\.genexus-mcp`. The test
  uses a temporary working directory but does not isolate the user-home target.
  Reproduce this case outside the sandbox with a temporary `USERPROFILE` before
  classifying it as a product failure. Do not touch an existing user
  `.genexus-mcp` configuration during reproduction.
- When the same run also fails `quiet flag suppresses launcher stderr noise`
  with an `Unhandled CLI failure` envelope, treat it as a secondary symptom
  of the user-home setup failure until the isolated test passes outside the
  sandbox.

## Runtime iteration

The gateway serves Streamable HTTP at `http://127.0.0.1:5000/mcp` by default.
Use the handshake and scratch-KB procedure in `docs/agent_playbook.md` or
`docs/mcp_debugging_guide.md` when validating SDK behavior. After Worker edits,
hot-swap with:

```text
genexus_worker_reload mode=hard sourceDir=<repoRoot>\src\GxMcp.Worker\bin\Debug
```

- `GxMcp.Gateway.exe` is a long-running stdio JSON-RPC server and does not accept interactive CLI flags such as `--help`. Running it directly without redirected stdio or via an unsupported command hangs waiting on stdin. Use `genexus-mcp` CLI commands or test harness entry points (`LiveGatewayHarness`, `scripts/test-live.ps1`) to interact with the Gateway.
If the next call reports a stale pipe or crashed Worker, reconnect `/mcp` once.
For a version smoke, call `genexus_whoami` and verify
`geneXus.versionMatches=true`, `matchedMajor`, and `supportedMajors`; the
legacy `supportedMajor` field remains the catalog-primary compatibility alias.

## Required workflow

- **Architectural discovery (`ripwire`).** Reach for it when a text search would
  otherwise be the first move — a signature you must locate, a contract you are
  about to change, or a diff you did not write:
  - Locating a signature or orienting on unfamiliar code:
    `ripwire <dir> --for="<task in words>"`.
  - Before changing a contract: `ripwire <dir> --callers=SYM` and
    `--impact=SYM` for the transitive caller set.
  - Reviewing an incoming diff: `ripwire . --pr-context=<BaseRef>` (for this
    checkout, normally `origin/main`; this is also the form used by
    `scripts/pr-preflight.ps1 -PullRequest <N>`, which only **fails** the run
    when called with `-RequireRipwire`).
  - On Windows, `ripwire.cmd` delegates to WSL. `Wsl/Service/E_ACCESSDENIED` or
    `Wsl/EnumerateDistros/Service/E_ACCESSDENIED` means the WSL launcher was
    blocked by the execution context; it does not prove that `ripwire` is
    unavailable or failed. Re-run in an authorized context, capture the emitted
    bundle and exit code, and report the environment restriction separately.
  - The PR bundle is budgeted and may report `trimmed`/`truncated` nested lists;
    treat its counts and caps as evidence boundaries and use targeted
    `--callers`, `--impact`, `--edit-check`, or `--offset` follow-ups when deeper
    coverage is required.

  On a branch you own and are pushing directly, no gate invokes this for you;
  the trigger above is the whole obligation.
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
  ref. Canonical form from the repo root:
  `git fetch origin` then
  `pwsh -NoProfile -File scripts/Invoke-PrePushMechanicalChecks.ps1 -AsJson`.
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
  `details.aggregateFailure`, not as an empty phase list.   `-IncludeRustLane` adds
  an advisory, opt-in `gx-engine` fmt/clippy/test block that never changes
  `pushReadiness`; without it the lane appears in `notCovered`, so `readyLocal`
  is not a claim about the Rust crates. Detailed limits
  live in `FORK_CHANGELOG.md`; non-blocking gaps in this fork-owned routine are
  tracked in `FORK_BACKLOG.md`.
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
- Inspect the actual input/request/route/function/query/response path before
  fixing behavior. Add a regression test when technically viable.
- Edit tracked text with the file-editing tool, not by piping it through a shell:
  fixed line indices truncate long files, shell round-trips re-encode UTF-8, and
  index arithmetic with shifting bounds deletes code. Use the shell to read and
  to run commands. See [`CODING_STANDARDS.md`](CODING_STANDARDS.md) § Text edits.
- Inspect a diff with `git show <ref>:<path>` and `--stat`, never by dumping a
  whole file; cap output with `Select-Object -First N`. A single unfiltered diff
  of a large file can cost thousands of tokens.
- After a build failure, the next test run may report the previous binary. Force
  `-t:Rebuild` before believing a result that contradicts your change.
- Every new regression guard gets a mutation check: revert the fix and confirm
  the test goes red. A guard that cannot fail is not coverage.
- Make the smallest scoped change; preserve unrelated working-tree changes.
- Every verified bugfix, feature, performance improvement, or architectural
  change gets an immediate entry under `CHANGELOG.md` → `## Unreleased`, using
  `### Added`, `### Changed`, `### Fixed`, or `### Internal`. Release-facing
  style and PR-credit rules are in `docs/release_protocol.md`.
- For issue-driven fixes, read the current issue before editing and add its
  canonical `https://github.com/lennix1337/Genexus18MCP/issues/<N>` URL to the
  same `## Unreleased` entry. Grouped bullets must list every fixed issue
  explicitly; a PR number or `/pull/<N>` URL is not an issue reference. The
  release entrypoint verifies this ledger before publishing.
- Any new KB-mutating tool must be registered in `Program.IsMutatingTool` and
  its invalidation behavior must have a regression test. See the detailed
  cache rules in `docs/agent_playbook.md`.
- Do not claim completion without fresh validation. Review the final diff for
  scope, logic, edge cases, compatibility, security, tests, and docs.
- Do not commit, push, merge, release, deploy, or close an issue unless the
  user explicitly asks. Issue closure requires a released fix and release link.
- When creating issues, submit them to the upstream repository
  (`lennix1337/Genexus18MCP`), with a preference for Brazilian Portuguese
  (`pt-BR`).
- After pushing a merged fix that is not yet released, mark each fixed issue
  with `pwsh -NoProfile -File scripts/release-issues.ps1 -Action MarkFixedPendingRelease -Issue <N>`;
  the release entrypoint closes labeled issues on publish. Do not post issue
  comments unless the user asks.

## MCP update and harness synchronization

Before creating or proposing any new script for build, installation, upgrade, or agent registration, inspect existing tooling:
- `install.ps1` (local checkout orchestrator)
- `build.ps1` (compiler & artifact packager)
- `scripts/install.ps1` (fixed-path release installer)
- `cli/run.js` & `cli/lib/config.js` (client registry & discovery)
- `cli/lib/update-check.js` (update planning)
- `docs/llm_cli_mcp_playbook.md` (authoritative CLI playbook)

### Decision matrix

| Scenario | Recommended flow | Notes |
|---|---|---|
| Updated local checkout | `.\install.ps1` | Updates `config.json`, runs `build.ps1`, and registers every writable client against the checkout gateway: it sets `GENEXUS_MCP_GATEWAY_EXE` to `publish\GxMcp.Gateway.exe` and runs `clients add --all-clients` (not `init --write-clients`). Parameters: `-GeneXusPath`, `-SkipClientConfig` (there is no `-KBPath`); the neutral config it writes defines no `Environment.KBPath`. |
| Compile local checkout only | `.\build.ps1` | Regenerates `publish/` without modifying `config.json` or client registrations. |
| Fixed-path release install / upgrade | `powershell -File scripts/install.ps1` | Downloads release `publish.zip` into fixed location. Parameters: `-Kb`, `-Gx`. |
| npx / npm global upgrade | Run `genexus-mcp update` plan | Follow returned guidance and fully restart the AI client. |
| Local checkout: stale launcher | From the repo root: `$env:GENEXUS_MCP_GATEWAY_EXE='<repoRoot>\publish\GxMcp.Gateway.exe'; node cli\run.js clients add --clients <id>` (or re-run `.\install.ps1`) | Do not reach for `npx @latest clients add` here: it rewrites the client to the npm-cache launcher and silently moves the harness off the checkout gateway. |
| Local checkout: post-sync validation | From the repo root: `node cli\run.js clients --format json`<br>`node cli\run.js doctor --mcp-smoke --format json` (same env as `install.ps1`) | Validation only — neither command rewrites a launcher. A client pointing at a *different* existing gateway is reported as `launcherPathDrift` (informational, `commandStale: false`), so a checkout registration read back by an `npx` CLI is no longer a false stale; `doctor`'s `client_config_sync` still warns that the exe is not the packaged one. |
| Distributed package: stale Antigravity launcher | `npx genexus-mcp clients add --clients antigravity` | Re-points the launcher to current direct gateway or package cache. For a local checkout use the checkout branch above instead. |
| Distributed package: post-sync validation | `npx genexus-mcp clients --format json`<br>`npx genexus-mcp doctor --mcp-smoke --format json` | Validates registration, gateway HTTP loopback, and live MCP protocol smoke. |
| Release publication | `.\release.ps1` | Only upon explicit user request. See `docs/release_protocol.md`. |

### LOCAL POLICY — checkout operators (this fork)

> **Permanent preference** for sessions whose cwd is this checkout
> (`FBgx18MCP`). It does not replace the upstream matrix above; it sets the
> **agent default** when working from this tree. On `git pull` / sync,
> re-check that this block survived.

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

For local-checkout work in this fork, **prefer** the rows below over matrix
rows that recommend `npx … clients add` / `npx … clients` / `npx … doctor`:

| Scenario (checkout) | Use this instead of the npx matrix rows |
|---|---|
| **Default:** install / update / refresh MCP harnesses (after sync or otherwise) | `.\install.ps1` at repo root |
| Explicit no-rebuild re-point of **all** writable harnesses | From repo root: `$env:GENEXUS_MCP_GATEWAY_EXE='<repo>\publish\GxMcp.Gateway.exe'; node cli\run.js clients add --all-clients` |
| Stale / wrong launcher (one client) | Prefer `.\install.ps1`; or same env + `node cli\run.js clients add --clients <id>` only if the user forbade rebuild |
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

### Operational safety and side effects

- **Installer vs. build**: `install.ps1` mutates `config.json` and client configs; treat it as an installer, not a neutral build. `build.ps1` is the neutral compiler.
- **Client registration**: `clients add` / `init --write-clients` makes atomic backups and preserves unrelated servers and both OpenCode config formats (`mcp.<name>` and `mcp.servers.<name>`). OpenCode Desktop shares `opencode.jsonc` with OpenCode CLI and is registered automatically.
- **Session reloading is a separate gate**: A healthy CLI, green build, or rewritten client config does NOT mean the current agent session has reloaded MCP. AI clients cache tool schemas at connection start; a full client restart is mandatory before new tools/schemas take effect.
- **Process management**: Prefer the scoped process termination in `build.ps1` (terminating only processes mapped to the current checkout path). Do not use broad `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker` across the machine when other checkouts or instances may be active, unless hitting locked output errors covered by the Scoped Permission.

## Task-specific references

Read only the relevant reference; [`docs/README.md`](docs/README.md) indexes every
document under `docs/` by area:

- SDK tools, authoring order, placement, SDPanels, live validation, and Windows
  gotchas: [`docs/agent_playbook.md`](docs/agent_playbook.md).
- Release execution, merge discipline, npm verification, and changelog voice:
  [`docs/release_protocol.md`](docs/release_protocol.md).
- Release interruption or failed-publication recovery: read
  [`docs/RELEASE.md`](docs/RELEASE.md), then use `scripts/release-status.ps1`
  and `scripts/release-doctor.ps1`; prefer `release.ps1 -Detach` and follow the
  doctor's exact-input/fingerprint check before any `-SkipBuild` or `-SkipTests`.
- Runtime variables: [`docs/environment_variables.md`](docs/environment_variables.md).
- MCP/HTTP debugging: [`docs/mcp_debugging_guide.md`](docs/mcp_debugging_guide.md).
- LLM-facing CLI/MCP operation: [`docs/llm_cli_mcp_playbook.md`](docs/llm_cli_mcp_playbook.md).

## Scoped permission: locked Gateway/Worker outputs

- **Trigger:** `dotnet build` or `dotnet test` fails with `MSB3027`/`MSB3021`
  naming `GxMcp.Gateway.exe` or `GxMcp.Worker.exe`.
- **Action:** run `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` or
  `taskkill /IM GxMcp.Gateway.exe /F`.
- **Rationale:** these are the user's own development processes and can be
  restarted by reconnecting the MCP client or rerunning the harness.
- **Out of scope:** arbitrary process-name matches, GeneXus IDE, Visual Studio,
  other users, system services, remote machines, or any case without the
  specified MSB lock error.
- **Granted:** 2026-05-15 by the user; reviewed 2026-05-15.

## Self-update behavior

On the first `genexus_whoami` of a session, if its cached `update.updateAvailable`
is true, tell the user the current/latest versions and release URL and ask before
installing. Use the returned command only after approval, then require a full
AI-client restart. Respect `GENEXUS_MCP_NO_UPDATE_CHECK=1` and do not nag on
subsequent calls. Environment details are in `docs/environment_variables.md`.
