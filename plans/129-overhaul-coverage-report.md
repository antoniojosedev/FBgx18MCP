# 129 - Overhaul coverage report: SDK-dependent repo tests versus gx-engine

Snapshot 2026-10-15, repository working tree (uncommitted `src/gx-engine`, `plans/`).
Companion to [`129-sdk-capability-matrix.md`](129-sdk-capability-matrix.md) and
[`129-implementation-progress.md`](129-implementation-progress.md): the matrix tracks
capability rows, this report maps the repository's GeneXus-SDK-dependent test corpus
onto those rows and answers "how much does gx-engine already cover?" as of this
snapshot. It is an inventory and measurement document. It ran no KB operation and no
SDK probe; the engine gate was re-run and is cited with its log.

## 1. What "SDK-dependent tests" means in this repository

Four layers, with counts measured from the working tree:

| Layer | What it is | Counts (snapshot) |
| --- | --- | --- |
| L1 build-time | `src/GxMcp.Worker.Tests` compiles only with a resolved SDK (`GX_PATH` or the default install): the Worker csproj has Artech `HintPath` references plus a `validate-gx-sdk.ps1` build step. `scripts/coverage/collect.ps1` skips the project when no SDK resolves (`worker.skipped.txt`); the CI `dotnet` lane on `windows-latest` is therefore Gateway-only, and the SDK lane is `sdk-validation` on the self-hosted `gx-sdk-18` runner (`if: vars.GXMCP_SDK_CI_ENABLED == 'true'`). | Worker.Tests: 502 `.cs` files, 3,571 `[Fact]`, 317 `[Theory]`, 4 `[LiveKbFact]`. Gateway.Tests: 248 `.cs` files, 1,588 `[Fact]`, 152 `[Theory]`, 35 `[LiveKbFact]`. |
| L2 runtime live-KB | `[LiveKbFact]` methods, gated by `GXMCP_TEST_KB` (plus `GXMCP_REQUIRE_WWP`, `GXMCP_PARITY_MCP_NAME`/`_IDE_NAME` when requested). Skipped by default in every lane; the protected lane runs `scripts/test-live.ps1`, whose default filter is `Category=LiveE2E&FullyQualifiedName!~TeamDevelopment` plus one Worker `InProcessBuildRunner` check. | 39 attributes in 12 files (Gateway 35 in 9 files, Worker 4 in 3 files). |
| L3 other env-gated | `Skip=` sites for absent fixtures/licenses: GX_PATH cryptography OPC fixtures 3.16.13, `GXMCP_DSO_NAME`, `GXMCP_TEAMDEV_PENDING_NAME`, `GXMCP_REQUIRE_WWP` (x2), parity fixture names, `GXMCP_TEST_KB` missing navigation report, "no WWP license". | ~10 skip sites (plus opt-in env gates such as `GX_KB_PATH`, `GXMCP_LIVE_GATEWAY_EXE`, `GXMCP_ALLOW_CONCURRENT_BUILDS`). |
| L4 operator lanes | `scripts/ci/sdk-validation.ps1` -> `validate-gx-sdk.ps1` + `test-live.ps1` with an explicit KB and fixture manifest; `scripts/test-live-matrix.ps1`; `scripts/mcp_llm_contract_smoke.ps1`. `npm run test:live-contract` tests the harness scripts themselves and needs no SDK. | 3 lanes/scripts. |

`ProcessSmoke` (17 trait sites) is process-smoke contract, not an SDK requirement:
e.g. `McpSmokeScriptContractTests` runs in the SDK-less Gateway lane.

Live-corpus split by category: LiveE2E 25 (13 `E2ELiveSmokeTests` + 4
`LiveMutationIntegrityTests` + 8 `OpenIssueLiveTests`), LiveIssue 5 (Issue238 1,
Issue241 1, WorkerHardReload 3), LiveRefreshRead 3, LiveEvents 1,
LiveDesignSystemRead 1, Worker pattern integration 3 (WWP/parity gated),
`InProcessBuildRunner` 1.

## 2. Verdict: can this corpus be pointed at gx-engine today?

Not as test execution. Three hard blockers:

1. **No tool contract.** The live tests drive MCP tools (`genexus_read`,
   `genexus_edit`, `genexus_lifecycle`, ...) over a spawned Gateway. The engine is a
   standalone `check`/`emit`/`build`/`run` compiler over its own JSON project format;
   nothing calls it and it exposes no MCP surface.
2. **No native KB I/O.** Every live test opens a real KB (`GXMCP_TEST_KB`). The
   engine has no KB handle (matrix A1/A2) and no write path (I2).
3. **What the live tests assert is service contract** - envelopes, leases, version
   fences, worker identity - not language semantics. Even a perfect engine would not
   satisfy them; they are the MCP layer's acceptance suite.

Directly runnable live tests on gx-engine today: **0 of 39**. Only two of the 39 have
adjacent engine semantics, and neither is a tool-level match (Analyze/Explain -> C1/C4
parser diagnostics, partial; SdtAdd -> B3 schema subset, no authoring).

What is reusable is the *behavior* behind those tests, in the modes already in use:

- **Mode A, working:** native differentials. The SDK (or its generated output) is the
  oracle; the engine implements the semantics; outputs are compared. Every hard
  parity number in section 4 comes from this mode.
- **Mode B, partial:** artifact replay. SDK ExportFile XML of SDT-shaped content is
  consumed by the engine's `exchange`/`xml-run` path (nested Record and depth-2
  projection work; deeper or custom-item aliases fail closed).
- **Mode C, blocked:** service-level re-pointing (engine behind Gateway/Worker).
  Blocked on A2/I2 and the integration decision; not measurable as test re-runs yet.

## 3. Live corpus mapped to capability rows

| Live group | Tests | Engine rows | Engine status |
| --- | ---: | --- | --- |
| KB read/query/refresh | 7 | A2, A5 | none |
| Authoring/writes (incl. Events, SDT add, property batch, variables import, blob) | 12 | I2, B3, B7 | none (B3 schema subset only, no write path) |
| Patterns/WWP | 6 | B9, J1 | none |
| Team development/transfer | 4 | J3 | none |
| Platform (whoami, doctor, worker hard reload) | 5 | J5 | none |
| Navigation/refactor | 2 | D2, D4 | none (D4 call graph is portable subset, not the tool) |
| Design System | 1 | - (DesignSystem adapter) | none |
| Analyze/Explain | 1 | C1, C4 | partial: parser diagnostics exist, no tool surface |
| Build infra (task resolution) | 1 | F1, F2 | none |

Observation: the live corpus concentrates on tool plumbing - precisely the rows the
matrix still marks `none` - while the language/runtime core the engine targets (C/G)
is exercised by the unit corpora and by native probes, not by these live tests.
Therefore this corpus is not the engine's progress yardstick; the matrix is, and
this report is its test-side appendix.

## 4. Engine coverage rollup (the progress numbers)

Matrix tally (`scratchpad/tally_matrix.py`, 56 rows; case-insensitive status match):

- Rows with a bounded engine implementation: **20 / 56** (36%).
- Rows with no engine route: 36, including P0 row A2.
- Explicit `native-verified subset` rows: 6 (G1-G6). Additional native evidence
  without that phrase: B3/G8 (nested and collection Clone, dynamic indices, SDT
  exports), C2 (`For` observations), C3 (parm modes), F3 (native output match).
- By priority: **P0 5/6** (only A2 open; C2 bounded), **P1 10/20**, **P2 5/17**,
  **P3 0/13**.

Engine proof inventory (fresh gate, `scratchpad/engine-gate-9.log`): 27 harnesses,
**244 passed, 0 failed, 2 ignored**; `cargo fmt --check` / `clippy -D warnings`
clean on the last increment. 246 `#[test]` statically (203 in 24 integration files,
43 unit). Hard differential highlights:

- `Str` picture rule: 30,158 black-box native cases, zero mismatches; 524 discriminating
  rows committed (`tests/fixtures/str_matrix.tsv`).
- Decimal `.NET` oracle: 288 cases; Date 55 and DateTime 42 native observations.
- Record/Collection Clone, dynamic collection index (fractional truncation,
  fail-closed invalid index), catalog bounds (1,024 Procedures / 128 schemas),
  XML exchange 16/16.

Caveat: row counting weights areas equally and "portable subset" is not parity.
The 36 `none` rows include every UI/deployment/J area, where the engine makes no
current claim.

## 5. Distance to full replacement (estimate)

The question "how much is left until gx-engine replaces the native SDK" needs an
explicit rubric; here is the one computed by `scratchpad/tally_matrix.py`. Per matrix
row: score 0 (`none`), 1/3 (`portable subset`), 2/3 (`native-verified subset`), 1.0
(full replacement, no row yet). Row weight: P0=5, P1=3, P2=2, P3=1.

| Lens | Result |
| --- | --- |
| Weighted progress (linear thirds) | **19.7%** done (P0 27.8%, P1 23.3%, P2 13.7%, P3 0.0%) |
| Weighted progress (conservative: 0.25 / 0.5) | **14.8%** done |
| Rows touched, unweighted | 20 / 56 = 36% |
| Native-verified parity depth | 6 / 56 = 11% |
| Tool contract usable | 0 / 60 tools, 0 / 39 live tests |
| Excluding the P3 backlog (UI/deploy/teamdev/platform) | 21.8% (weighted) |

Reading: roughly **15-20% of the weighted capability surface**, concentrated in the
language/runtime core (G1-G6 native parity, C1-C4, B1/B3 bounded). Nothing is claimed
end-to-end: the engine cannot read a real KB (A2, the only P0 row at 0%), no product
operation is served by it, and the integration seam that now exists (Gateway
engine-backend switch, capability gate and `genexus_doctor` reporting — see
`docs/engine_backend.md`) routes nothing yet because the only parity capability
(`str_format`) has no consumer. The estimate is
deliberately conservative on B3/G8 (native evidence exists but the row keeps the
`portable subset` score) and does not include difficulty: the remaining work is not
linear (real KB decoding, full grammar and integration are the heaviest parts), so
"~80% left" is a floor on effort, not a schedule.

## 6. Recommendations

1. Keep the matrix as the progress tracker; refresh this report when the row tally
   or the live corpus changes.
2. Highest-payoff engine increments remain what the matrix already lists: A2 native
   KB read (the only P0 row without any implementation), the G4 remainder (dynamic
   `Str` width/scale, Substring, Upper/Lower, Replace, Val, Format), then C2 breadth
   and D1/D2 when a consumer needs them.
3. The live corpus becomes re-pointable only after the engine serves at least one
   product operation: the backend switch and capability gate landed, but A2/I2 and
   the first parity capability with a consumer are still missing; until then, grow
   coverage through Mode A/B differentials.
4. If a repeatable progress number is wanted, extend `scratchpad/tally_matrix.py`
   into a checked-in report generator; the commands below are the current manual path.

Regeneration:

```powershell
# Live gates (attributes only; comment mentions excluded)
Get-ChildItem -Recurse -Path src\GxMcp.Gateway.Tests,src\GxMcp.Worker.Tests -Include *.cs -File |
  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
    $t = Get-Content $_.FullName -Raw
    $n = ([regex]::Matches($t, '(?m)^\s*\[LiveKbFact')).Count
    if ($n) { "{0,3}  {1}" -f $n, $_.Name } }

# Matrix rollup
python scratchpad\tally_matrix.py plans\129-sdk-capability-matrix.md

# Engine gate (in src/gx-engine)
cargo test
```
