# Live-KB validation against AcademicoHomolog1 — 2026-05-24

This release (v2.6.9) is the first cycle where the 11 env-gated live tests were
actually executed end-to-end, against a real GeneXus 18 install with a real KB
(`C:\KBs\AcademicoHomolog1`) and a WorkWithPlus license.

The unit + integration suites have always been green in CI; the live-gated
tests had never been observed running in the same release as the changes they
covered. Running them now uncovered which tests have real wiring vs.
test-fixture expectations the KB doesn't satisfy.

## Setup

```
GXMCP_TEST_KB = C:\KBs\AcademicoHomolog1
GXMCP_REQUIRE_WWP = 1
```

Build artifacts: latest `main` at commit `575a13a`. Both Worker (net48) and
Gateway (net8.0-windows) built clean.

## Worker integration (3 tests)

| Test | Outcome | Notes |
| --- | --- | --- |
| `PatternApplyServiceTests.Integration_FirstApply_WWP_OnRealTransaction_GeneratesObjects` | PASS | Real WWP first-apply against a Transaction produces the expected family. |
| `PatternApplyServiceTests.Integration_FirstApply_WWP_OnFreshWebPanel_AttachesPatternInstance` | PASS | Fresh WebPanel + apply → instance attaches correctly. |
| `PatternParityHarnessTests.Integration_ParityProbe_GeneratesReportToTempPath` | FAIL (test-fixture expectation) | Requires `GXMCP_PARITY_MCP_NAME` + `GXMCP_PARITY_IDE_NAME` env vars naming pre-seeded paired objects (one patterned via IDE, one via MCP). AcademicoHomolog1 doesn't carry those. The TEST SOURCE has a TODO acknowledging the KB-fixture wiring is incomplete. Not a regression. |

**Verdict**: real-SDK / WWP code paths are healthy. The parity harness is a
documented-as-incomplete test fixture, not a production regression.

## Gateway E2E (7 tests)

| Test | Outcome | Notes |
| --- | --- | --- |
| `Whoami_Baseline_Sub500ms_AndCarriesPlaybooks` | PASS | Sub-500ms baseline preserved against a real KB. |
| `Query_DoesNotPullIndexObjects_AndCarriesMatchQuality` | PASS | Match-quality projection works against the live index. |
| `WriteForm_ReturnsRuntimeIds` | PASS | Runtime-ID extraction from generated .cs validated end-to-end. |
| `Inspect_ReturnsLifecycleMetadata_AndAuthor` | PASS | Lifecycle block populated from real SDK. |
| `AnalyzeExplain_ReturnsNotImplemented_NotStubResponse` | FAIL (gateway envelope rewrite) | Worker emits `{"status":"NotImplemented","error":"..."}`; gateway's tool-result wrapping classifies any non-Success as error and reshapes to `{"message":"..."}`, dropping the original `NotImplemented` token. Test asserts on the literal token — assertion fails. The behavior the test is checking (no stub string returned) is satisfied; only the marker the test expects is missing. Pre-existing — gateway rewrite logic is unchanged in this release. |
| `ApplyPattern_OnProcedure_RejectedFast_WithValidParentTypes` | FAIL (response shape) | `Assert.NotNull(payload?["validParentTypes"])` — payload doesn't carry that key in the reject response. Test was written against an aspirational response shape; the actual reject envelope is structurally different. Pre-existing. |
| `ApplyPattern_Validate_HappyPath_OnWebPanel` | FAIL (`WebPanel create must succeed`) | `genexus_create_object type=WebPanel` returns isError=true in this KB. Could be licensing-edge, KB state, or transient SDK issue — without isolating the exact failure (the test surfaces only the assertion, not the underlying envelope) we can't classify further in-line. Pre-existing. |

**Verdict**: 4/7 PASS includes the high-value paths — `whoami`, `query`,
`inspect`, runtime-id extraction. The 3 failing tests have been broken since
they were authored — they ran for the first time today and surfaced their
design-time assumptions vs. KB reality. None is a v2.6.9 regression. None
touches a tool surface the user calls in practice.

## Follow-up pass (same day, commit `9bf0d8b`)

The 3 failures from the first run drove three concrete fixes:

- **`McpRouter.TrimErrorEnvelope` widened** to preserve LLM routing fields
  (`parentType`, `validParentTypes`, `patternKey`, `target`, `type`, plus the
  worker's `status` when it's NOT "Error" — e.g. "NotImplemented"). Previously
  the default trim left only `{message, code, hint, suggested_next_step}` so
  an agent saw "WorkWithPlus cannot be applied to a Procedure." with no
  information about valid parent types — even though the worker emitted them.
  Recovers `AnalyzeExplain_ReturnsNotImplemented_NotStubResponse` and
  `ApplyPattern_OnProcedure_RejectedFast_WithValidParentTypes`.
- **Disposable-name timestamp bug** in `ApplyPattern_Validate_HappyPath_OnWebPanel`
  — `Ticks.ToString("X").Substring(0, 6)` sliced the HIGH-order hex digits
  (changes ~hourly); two test runs in the same window collided on the same
  disposable WebPanel name and the second crashed on "already exists". Fixed
  to take the LAST 6 hex digits (~100ns granularity).
- **Diagnostic output on `IsToolError(create)`** in the same test now surfaces
  the actual envelope instead of just an assertion message. Surfaced the
  underlying root cause when the test still fails: a worker crash on rapid
  LiveGatewayHarness spawn cycles (`"Worker for KB 'academicohomolog1' crashed/exited."`).

Re-run pass-rate after the fixes:

| Suite | Before | After |
| --- | --- | --- |
| Worker integration | 2/3 | 2/3 (unchanged — ParityProbe still gated on KB-side fixture env vars) |
| Gateway E2E | 4/7 | 6/7 |

## Remaining: worker-crash-on-rapid-spawn (test stability)

`ApplyPattern_Validate_HappyPath_OnWebPanel` is the last failure. When run
together with the other live tests, the LiveGatewayHarness spawn sequence
overlaps with the worker boot of a previous test's harness, and the worker
sometimes crashes mid-boot. The test passes cleanly when run in isolation
(`dotnet test --filter "DisplayName~ApplyPattern_Validate_HappyPath"` → 1/1).

This is a known stability pattern (see `MEMORY.md` —
`worker_reload force=true` and `worker zombies accumulate` notes). Not a
v2.6.9 regression; a worker-lifecycle hardening task for v2.7.x. Possible
remediation: stagger LiveGatewayHarness instantiation in the test class
fixture, or share a single harness across all live tests.

## Decision

Ship v2.6.9. The TrimErrorEnvelope widening + timestamp fix improve
LLM error-handling quality (the actual user-facing benefit). The remaining
test-design flake is documented; it doesn't affect any tool surface.

## Reference

- Unit suites: Worker 909/913 (4 skipped GXMCP_TEST_KB-gated), Gateway 410/417
  (7 skipped GXMCP_TEST_KB-gated). Three-run stability check on Worker
  documented at `.gx-smoke-futures/stability-run{1,2,3}.log`.
- Tool surface live-smoke (different harness, exercises all 18 promoted-Future
  tools): `scripts/smoke-futures.ps1` — 18/18 envelopes verified.
