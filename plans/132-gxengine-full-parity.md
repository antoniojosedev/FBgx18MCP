# Plan 132 — close the remaining gx-engine SDK gaps

## Goal and baseline

Continue independent SDK replacement from published 4.0.0, commit
`62272a9c499ff02698c5b3300d64e294f888f4fa`. Full replacement is **not verified**.
The 346-object/328-file KBTeste result is one bounded oracle, not a global
coverage percentage. Existing performance measurements retain their documented
workload and cache qualifications.

Use Rust for the independent engine and preserve the existing Gateway contract.
Keep `gxengine` as default, SDK fallback for unsupported operations, and
`Fallback=error` fail-closed behavior. Promoting a capability requires evidence
for its complete admitted space; changing a registry label is not implementation.

This round authorizes local implementation and validation, not another release,
commit, push or merge. Preserve other edits. Only verified disposable
`C:/KBs/*teste*` may be used for live tests. Serialize native probes through the
parent; no Cloud, license bypass or proprietary source in Git. Run `build.ps1`
last after integration and other applicable gates.

## Ownership and first investigation wave

This is multi-seam work. Read-only discovery shares the clean baseline;
implementation writers must use exclusive worktrees **inside this repository**.
No concurrent writers or live probes may share a KB/output directory.

| Lane | Boundary | First deliverable | Next gate |
| --- | --- | --- | --- |
| Native read | Rust UDM read output versus SDK identity, freshness, token and normalization | Exact missing inputs/algorithm, test-first patch scope | Differential full envelopes and stale/missing-part behavior |
| Generator | Developer Menu selection, generated artifacts and unsupported forms | Oracle-backed selection/order or next evidenced generator gap | Fixture regression, exact output comparison, executable output where affected |
| Surface inventory | Actual commands, capabilities, language, storage and navigation coverage | Prioritized finite acceptance inventory with citations | No category silently omitted or marked complete from unit tests alone |
| Parent | Planning, oracle scheduling, isolated writer setup, integration and review | Durable lane board and acceptance records | Integrated behavior tests, project gates, final package postchecks |

Runtime job IDs, reports, patches and intermediate evidence belong in ignored
`scratchpad/gxe-completion.json` and subagent artifacts, not this plan.

## Implementation sequence

- [x] Reconcile the three discovery handoffs against code and existing evidence.
- [x] Choose the smallest independently verifiable increment per seam; record
      exact files, isolation paths and acceptance before writer launch.
- [x] Add the increment's regressions and observe failures for the intended reasons.
- [x] Implement database emission refusal, token primitives, evidence classification
      and artifact comparison without widening native routing.
- [x] Run focused positive, malformed-input and boundary tests. Writers disabled
      guards, observed failures, restored them and reran the affected tests.
- [x] Obtain bounded SDK differential evidence using a verified disposable fixture;
      record exact SDK major, object/part identity, request and complete result.
      This identifies gaps; full native-read admission remains unverified.
- [x] Have fresh read-only reviewers inspect the four candidates; correct findings
      and inspect the follow-up diffs in the parent.
- [x] Integrate reviewed patches; focused combined checks pass (Gateway 269,
      database refusal 12, token 9, comparator 34, documentation 7).
- [x] Update capability guidance, README and this increment's checklist from evidence.
- [x] Run applicable lint/fmt/type/build/test and Ripwire quality/change gates;
      classify failures and skipped/unavailable lanes explicitly (Ripwire remains non-green).
- [x] Run `build.ps1` last and identify the actual packaged engine in postchecks.

## Reconciled discovery and first implementation wave

The checked operation inventory contains 60 tools and 309 actions. Only narrow
query and blocking build/rebuild shapes are engine-served; this is not a product
coverage percentage. Native read tokens need SDK-equivalent content, parent and
UTC ticks; the stored timestamp mapping and snapshot consistency remain unknown.
Native writes/transactions are in-memory only; live persistence remains refused.

The SDK-form Procedure emitter previously substituted comments/inlined bodies for
`For Each`, `New` and `Delete`; the integrated guard now refuses them. Twelve
parsed-source/rebuild regressions pass, including transitive caller refusal.
WebPanel rebuild uses a separate planner that already refuses all event code;
a new mutation-tested rebuild regression verifies eight event/rule/condition
cases write no panel/compiler input. The library-only `WebPanelClass` emitter
is not on that path and remains an unvalidated, lossy library surface.
Capability evidence labels have been corrected; deferrals are not native parity.

Developer Menu selection is not ready for production wiring: existing offline
oracles disagree, and some files predate their generation manifests. Do not turn
a parameterless-page hypothesis into an admitted generator rule.

| Independent writer | Exclusive contract | Acceptance and integration boundary |
| --- | --- | --- |
| Database emission safety | SDK-form unsupported database statements and focused compiler/pipeline tests | Parsed-source regression, named refusal, no emitted success, valid transaction emission retained |
| Native token groundwork | Pure Rust formatting/hash primitives and independent vectors | Exact golden bytes, missing authoritative inputs refused; no SQL timestamp inference or routing admission |
| Capability evidence | Registry classification, focused Gateway tests and backend documentation | Evidence-backed status; existing admitted query/build and fallback behavior unchanged |
| Artifact inventory | Read-only deterministic tree comparator, tests and README pointer | Byte-level classification, finite denominator, no normalization hiding differences or writes to input trees |

Every writer is isolated; exact paths and runtime ownership are in the ignored
lane board. The four local patches are integrated. All 15 sequential local lanes
passed at fingerprint `88C57B749D96B715DBA570F0EB6C801C2471143E8F51550270E66A0AE34C0ABD`:
Rust 1,322; Gateway 3,106; Worker 6,097; CLI 178; Nexus 118, plus process smoke,
contracts, lint and coverage (Gateway 73.44%, Worker 54.44%). Subsequent changes
only correct capability/doc wording and add the WebPanel boundary regression;
Gateway 269, WebPanel 5, docs 7, fmt and Clippy were revalidated. Ripwire remains
non-green: quality exit 2 (31 gating findings), test-gate exit 4; review found
no P0/P1 integration defects. Final `build.ps1` passed; packaged engine SHA-256 is
`efd13ab0b582e74a7f895e7ae4c7a90ba28378e7190bee03f46cbfb275a4600b`.
Packaged executable identities/manifest hashes passed, as did portable fixture
check and generated-program execution (stdout `42` twice). The initial postcheck
incorrectly compared a ReadyToRun DLL to plain IL; PE metadata confirmed the
matching RID module identity, and remaining postchecks passed without a build
replay. Evidence is under `C:/tmp/gxe-completion-final-build/` outside Git.
Fresh-agent comparator discovery and `--help` invocation passed in this working
tree; new files remain uncommitted, so clean-clone availability is not claimed.
These are safety and evidence increments,
not implementation of database execution or native MCP reads. Token primitives
remain unwired until timestamp, parent and source parity is measured; a read-only
schema probe alone does not establish that mapping. Historical 4.0.0 query and
artifact evidence is retained but has not been rerun on this changed generator.

## Bounded native-read follow-up

Read-only SDK/native sampling on the verified disposable `C:/KBs/KBTeste`
found matching sampled Procedure, Transaction and DataProvider query rows and
totals; WebPanel differed (SDK 12 versus native 15). Native reads remain deferred.

SDK pagination projects LF text to Windows CRLF and removes the final newline;
version tokens hash the complete unpaginated text instead. Two sampled source
fingerprints already match. A typed Transaction read resolves the name collision
safely; bare ambiguous names remain errors. For an external Procedure with no
stored Source, SDK part discovery returns Rules under the requested Source name;
native missing-part reporting remains storage-truthful rather than copying this
order-dependent alias.

The StyleClass correction projects only the evidenced design-class wrapper at
the native read-payload boundary, preserving raw reader/codegen inputs. An
independent review caught escaped-quote corruption; a failing Rules regression
reproduced it, the scanner was corrected, and re-review found no remaining issues.
Integrated Rust checks pass (1,328 tests, 3 ignored; fmt/Clippy clean). New-source
`build.ps1` and package postchecks passed; engine SHA-256 is
`03343744969c5e11fc3cc21338d7a3323b35d080912f9d5dc3a68d25229a2700`.
The packaged `WebPanel:Gx0010` Events read matches both the SDK full-source token
fingerprint and its paginated display. This verifies the captured mismatch,
not all token families or full native-read parity. Raw/codegen isolation remains
covered by the regression; no full-KB generator rerun is implied.

`ObjectSaved` output timestamps (type 301, version 0) and parent GUIDs match
10/10 sampled SDK tokens, including the two objects whose entity/model timestamps
disagreed. Missing/duplicate rows, other versions, concurrent changes and general
SDK timestamp semantics remain uncertified. No native token/routing promotion or
SDK mutation is implied.

A complete WebPanel inventory narrows the extra rows to `MasterPrompt`,
`MasterUnanimoSidebar`, and `Trn353General`. Unfiltered SDK queries classify the
first two as MasterPage and the third as WebComponent. The integrated standalone
query fix uses active-version `WEB_COMP` properties, not names, and classifies
before counting/paging. Missing or unsupported metadata fails closed. Review
caught first-text-child XML parsing that could hide classification flags behind
comments; a red-first regression now protects complete scalar boundaries.
Re-review passed; integrated Rust checks pass (1,335 tests, 3 ignored), with
7 classification and 16 `kb_ops` tests. Final `build.ps1` and package checks
passed; packaged engine SHA-256 is
`697473d41e3eef47b1ed9db68ccf352a0e5ac5f85befc51e9553b9123a69fa8b`.
Eight read-only queries on the revalidated disposable KB match the settled SDK
oracle: all 12 WebPanels, first 10 with correct totals/truncation, three subtype
descriptors and their three WebPanel exclusions, with zero errors and unchanged
artifact hashes. Portable execution still prints `42` twice. WebPanel MCP routing
remains SDK-deferred. Current-tree Ripwire quality/test gates still fail (quality
32 gating findings, test-gate exit 4); no pass is inferred.

Timestamp investigation stops before an unused metadata helper or token wiring.
Readable GX18 storage code establishes `UdmModel`, `ModelType.Design=0` and
`ObjectSaved=301`, but protected getter bodies leave active-KBVersion/design-model
binding and exact/latest/version-independent output selection unresolved. A
confined SELECT finds ModelId 1 (`Design`) and 2 (`.NET`) both have ModelType 0,
with parent links 0 and 1. Thus a type-category match cannot resolve the active
design model. SQL `datetime` scale 3 and zero fractional samples do not establish
UTC conversion or intentional second truncation. Neither ten token matches nor
schema uniqueness proves these SDK rules. No speculative resolver, fallback,
native metadata helper, or routing promotion is accepted from this evidence.
No new release or publishing is authorized.

### Controlled timestamp perturbation (isolated copy)

With user approval, `C:\KBs\KBTeste` was copied through a SQL `COPY_ONLY`
backup (checksum, verify-only, fresh restore targets) to the disposable
`C:\KBs\KBTesteTimestamp_6e3640210e9b` / `GX_KB_KBTesteTimestamp_6e3640210e9b`;
the original descriptors were hash-checked unchanged. Each case changed one
candidate column for Procedure `AddDeviceGroups` (EntityTypeId 38, EntityId 1),
read the token from a fresh packaged SDK Gateway process, then restored the
baseline (receipts in `C:/tmp/gxe-timestamp-controlled-6e3640210e9b`). All
eight cases verified; the post-reset token equals the baseline:

| Perturbation | SDK token timestamp |
| :--- | :--- |
| `ObjectSaved` output set to `.123` fraction | follows output, fraction kept |
| Newer decoy at `OutputTypeVersion=1`, `OutputEntityVersionId=0`, or ModelId 2 | ignored (exact model 1 / version 0 / entity version 1 key) |
| `EntityVersionTimestamp` changed | ignored |
| Output row removed | falls back to model 1 `ModelEntityVersionTimestamp` |
| Output removed + `EntityVersionTimestamp` changed | still model 1 `ModelEntityVersionTimestamp` |
| Output removed + model 1 `ModelEntityVersionTimestamp` set to `.123` | follows it, fraction kept; ModelId 2 ignored |

This is one Procedure on one installed GX18 profile; other majors are unverified.
The token ticks equal the raw stored `datetime` ticks (2011, 2014 and the
2016 DST date alike) on a UTC−3 host (−2 under 2016 DST): the SDK applies no
local-to-UTC conversion. `KBVersion.LastUpdate` (reported as UTC) is a separate value.

**Branch selection (follow-up on the same copy).** The first clone `branch`
failed with a cause our Worker hid behind `TargetInvocationException`; the
Worker now reports the root (`KbVersionService.VersionFailure`) and the retry
showed `InvalidOperationException` "A versão de desenvolvimento não pode ter um
pai de desenvolvimento" from `KBVersion.Save <- KBVersionHelper.BranchModel`:
a branch needs a frozen parent, and the default parent (the active trunk) is a
development version. `branch` now refuses that with `KbVersionParentNotFrozen`
before the SDK (verified live). `action=list` omits the trunk itself.

Then `freeze TimestampAuthorityFreeze` and `branch TimestampAuthorityBranch`
(parent = the freeze) created Design models 3 and 4, each with a copied
`ObjectSaved` row (same 2016-12-02T20:30:13 timestamp). With models 1/3/4 given
distinct `ObjectSaved` timestamps (2011/2013/2014), one packaged SDK process
read `AddDeviceGroups`:

| Active version (in-process `set_active`) | Token timestamp |
| :--- | :--- |
| `KBTeste` (trunk) | model 1 (2011) |
| `TimestampAuthorityBranch` | model 4 (2014) |
| `KBTeste` restored | model 1 (2011) |

The frozen model 3 is never used. **The token key is the active version's own
Design model, not ModelId 1.** The SQL link: each Design model's
`EntityVersionProperties` carries `KBVersion` (model 1 = `0` trunk, model 3 =
`1`, model 4 = `2`), matching the `Udm.Types.KnowledgeBaseVersions`
(EntityTypeId 10) entity ids, whose properties carry `ParentId`/`IsFrozen`/
`Name`. A fresh process reopened the copy with active `KBTeste` although the
previous process had set the branch active, so the active version is session
state a separate native process cannot read from the database. Native-token
rule implied: resolve the Design model by `KBVersion` = the caller's active
version id, and fail closed when versions exist and the active version is not
supplied. Outputs were reset afterwards (receipts `branch-output-*.json`,
`version-map-probe*.json`); the freeze/branch rows remain in the disposable
copy. Native token wiring and routing are unchanged.

**Resolved (uncommitted, option C): native model selection.** The Worker
publishes the active version and Design model (`kbversion ActiveModel`), the
Gateway passes `--design-model/--kb-version`, and `udm::version_scope` selects
and verifies the model (reads: any version; builds: sole Design model, trunk
model 1 only; no named version: single-Design-model KBs only; else exit 4 before
any object read). Supporting build facts still select latest-global versions;
versioned KB builds are therefore refused, even with trunk active, until every
fact is pinned. This is conservative SDK fallback, not branch build parity. Live on the
copy: trunk reported 0/1, branch 2/4, queries served, branch build refused in
50 ms with no SDK build; trunk restored, artifact hashes unchanged. Not observed
live: a content difference between the models (copy contents are identical).
Frozen-only KBs now need the named version too (no longer assumed model 1).

Original hazard: `gx-engine` reads and writes
`DEFAULT_DESIGN_MODEL_ID = 1` (`src/udm/reader.rs:45`, also `writer.rs`) and has
no version check: `kb-query` on the copy (now with a branch) exits 0 against
model 1. When a branch is active in the SDK session, the SDK reads model 4, so
native query/read/build results can silently come from the trunk. Fix direction:
the Gateway passes the SDK's active version id; the engine resolves the Design
model whose `KBVersion` property equals it and refuses when versions exist but
no id is supplied. Whether frozen-only KBs may stay on model 1 is unproven.

### Compatibility hardening wave (local, uncommitted)

- Structural model-properties XML parsing replaces adjacent-tag SQL extraction.
  Formatted/reordered/escaped KBVersion values are preserved; duplicate or
  non-scalar values, malformed XML and missing property rows fail closed.
- Both legacy Catalog::load native inputs (directory and connection file) use
  the sole Design model's actual id; version ambiguity refuses before catalog
  reads. Their existing input diagnostic is preserved, not relabeled exit 4.
- Gateway build scope is sampled inside the build semaphore and rechecked after
  execution; observed scope loss/change requires reconciliation, never replay.
  Start/end checks do not detect ABA or eliminate the pre-execution race.
- Successful Worker scope refreshes are atomic; failed/null refreshes clear.
- Regression red/green evidence: XML 8/10 -> 10/10; catalog native tests 0/2 ->
  2/2; versioned-build guard red (previously Ok(1)) -> green. Rust fmt, strict
  Clippy and all 1,349 tests passed (3 ignored). C# lane-reported gates and
  mutations passed; integrated gates and updated packaged postchecks pending.
- Transport snapshot lookup's possible Worker re-acquisition/cold-start is being
  fixed in an isolated lane. No SDK routing expansion or release is authorized.

## Replacement acceptance inventory

No row is currently certified complete.

| Category | Evidence required before full-replacement acceptance |
| --- | --- |
| Native reads | Complete SDK-compatible identities, parts, encoding/text normalization, timestamps, parent, tokens, missing/ambiguous objects, refresh and stale-state behavior |
| Native persistence | Real create/edit/delete/reload through both independent and SDK readers, optimistic concurrency, rollback, references and lock ownership; no in-memory substitute |
| Query/index | Admitted search/filter/order/page semantics, type classification, complete envelopes, freshness and invalid input; native execution demonstrated |
| Object/language model | Enumerated supported object/part families, nested structures, Rules/Events, runtime semantics and explicit unsupported cases |
| Specification/navigation | SDK comparison of base/extended tables, joins, formulas, diagnostics and generated SQL, plus execution against synthetic data |
| Generation/artifacts | Same object closure, output inventory and deterministic generated bytes where promised; compilation and real runtime behavior, not syntax alone |
| Lifecycle | Target/full-KB builds, changed-input invalidation, cancellation, timeouts, job reconciliation and owned-process cleanup without duplicate effects |
| Compatibility | Explicit SDK-major/generator/database/encoding matrix; unavailable lanes are blocked, not passed |
| SDK independence | Supported end-to-end workflows run without Worker/COM/SDK fallback; generated applications' runtime-library dependencies reported separately |

Do not remove the Worker or SDK fallback until the complete intended workflow
inventory is verified. A finished increment closes only its own acceptance rows.
