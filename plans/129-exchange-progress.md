# Issue 129 — bounded SDK XML projection progress

## Scope and acceptance

Owned files for this lane: `src/gx-engine/src/exchange.rs`,
`src/gx-engine/tests/exchange.rs`, and this progress ledger. Project authored SDK
`ExportFile` XML only for the existing bounded Procedure/SDT subset. Nested SDT
Levels and collections must preserve schema identity and references, resolve only
authored type aliases, and fail closed on unsupported or ambiguous XML. Keep the
8 MiB, 50,000-node, depth-128, 128-schema, 1,152-object, and 1,024-field bounds;
never decode KB storage, extract XPZ, or imply a lossless import.

## Evidence and semantic boundaries

- `scratchpad/authored-record-export-1792077163910380000/authored.xml` is an SDK
  ExportFile with a flat SDT Level and scalar Numeric/VarChar/Character/Boolean/
  Date/DateTime Items; it does not establish nested Level XML behavior.
- Existing native export `scratchpad/nested-record-export-1792080479819133800/authored.xml`
  establishes the one-level XML shape: root SDT LevelInfo with direct `Child`
  (`AttCollection=False`) and `Rows` (`AttCollection=True`) Levels. The native
  `scratchpad/custom-item-export-1792080825587526300/authored.xml` export records
  `idCollectionItemName=Entry`; both shapes match the parser's LevelInfo handling.
- Native disposable-KB probes `scratchpad/record-nested-native-1792078896703774100`
  and `scratchpad/record-collection-native-1792079116541738600` establish the
  observed one-child and collection semantics; native Variables readback uses
  `RSN03944700.RowsItem`. The SDK API dump documents `SDTLevel.IsCollection`,
  `CollectionItemName`, and `AddLevel(name, bool)`.
- A read-only scan of existing XML artifacts found no deeper nested-Level export.
  Keep rejecting grandchildren: evidence supports direct nested/collection Levels,
  not arbitrary recursive export depth. Tests using `nested_xml` remain synthetic
  parser-contract tests, not native export round-trip tests.

## Progress

- [x] Read current XML parser/tests and relevant native evidence.
- [x] Fix SDT root control flow after adding root and synthetic schemas: `continue`
      prevents moving the root object into edits and then reusing it; no clone.
- [x] Add an end-to-end XML projection test for native-observed one-level child,
      collection, aliasing, Clone, and input immutability behavior.
- [x] Refactor `parse_sdt` identity/alias sets into `SdtProjectionContext`; no lint
      suppression, preserving the cross-SDT collision checks.
- [x] Focused exchange tests after refactor: 11 passed, 0 failed (actual exit 0).
- [x] `cargo clippy --all-targets -- -D warnings` passes (actual exit 0).
- [x] Re-run the CLI XML differential against the existing native export/report.
- [x] Review the parent-provided full-suite log; no duplicate full-suite run.
- [x] Run Ripwire edit-checks and test-gate; see limitations below.
- [ ] Parent/docs owner to decide how to document the custom-item entry limitation
      and nested-export end-to-end evidence.

## Results

- `cargo clippy --offline --locked --manifest-path src/gx-engine/Cargo.toml
  --all-targets -- -D warnings` passed (actual exit 0). Its first attempt found two
  test-only `useless_format` warnings; those static strings now use string literals.
- `cargo test --offline --locked --manifest-path src/gx-engine/Cargo.toml --test exchange`
  passed all 11 tests (actual exit 0), including the XML → catalog → compile →
  `xml-run` path. The test uses conditions rather than `Str`, avoiding the
  parent-owned `Str` API change.
- The parent-provided `scratchpad/engine-fanout-full-tests.log` contains successful
  summaries for every suite (42 passed/1 ignored in its first binary; all other
  reported suites have zero failures). I read the log but did not rerun the full
  suite, per the integration-gate ownership instruction.
- On the post-refactor CLI executable, `xml-check`, `xml-emit`, `xml-build`, and
  `xml-run` all exited 0 for native export
  `scratchpad/nested-record-export-1792080479819133800/authored.xml`, entry
  `RTM45184200`; the generated executable existed. `xml-run` matched the native
  `scratchpad/record-collection-native-1792079116541738600/report.json` output
  after CRLF-to-LF normalization, and the XML input SHA-256 was unchanged.
- The custom-item export has only SDT `RCE06829400`, not a Procedure entry. All
  four CLI actions exited 1 with `catalog compiler entry must be a Procedure`;
  this is an entry-selection limitation, not a projection refusal, and there is no
  matching native runtime report. Its input remained unchanged.
- `rustfmt --check --edition 2024` passed for the two owned Rust files. A prior
  package-wide `cargo fmt -- --check` returned 1 on other parent-owned engine
  files/tests; those unrelated files were not formatted here.
- Ripwire edit-checks for `parse_sdt` and `catalog_from_xml` reported no
  incompatible callers. The static test-gate exited 4 with `run_unknown=1` and
  `execute`/`main` listed as untested; the direct `xml-*` CLI E2E above exercised
  that route, but the heuristic gate remains ungreen. Quality-delta is unavailable:
  Ripwire found neither a quality baseline nor a Git HEAD to compare, so no
  post-change baseline was created.
- A standalone `cargo build` call had an uncertain tool outcome; a read-only stat
  showed the CLI binary newer than `exchange.rs`, and the CLI E2E plus focused
  tests passed. No standalone build exit code is claimed.
- No new native probes, SDK actions, KB writes, or out-of-scope file edits were made in this refactor/verification pass.

## Deep hierarchy and depth-2 item alias (2026-10-15)

This section supersedes the earlier "Keep rejecting grandchildren" note in this
ledger; nested Levels are now projected recursively and the alias policy below is
the current contract.

- Projection: `project_level` recurses through nested `<Level>` elements carrying
  `depth` (root = 0) and the level's dotted `path`; each Level becomes a synthetic
  `GXL_<guid>` schema with `Uses` edges to its direct child Levels, scalar Items
  only, and nested collections admitted from `AttCollection=True`.
- Alias policy (evidence-gated): depth-1 children of the root register `Root.Name`
  (composite) or `Root.Item` (collection item); depth-2 collections register the
  full-path item alias `Root.Child.Item`; deeper collections and non-root composite
  aliases are not registered and referencing them fails closed with
  `unresolved XML SDT declaration`.
- Native evidence for the policy: round 1 built and ran a deep-SDT Procedure with
  `&V.NestedValue = "i"` (exit 0); round 2 read back the Variable type text and
  found only `NativeDeepSdt85299100.Child.NestedCollectionItem` preserved by the
  SDK registry -- every other dotted candidate collapses to the root binding.
  Scripts: `scratchpad/probe-deep-aliases.py`,
  `scratchpad/probe-deep-binding-registry.py`; evidence directories under
  `C:\Users\swluc\AppData\Local\Temp\opencode\deep-alias-probe-20261015195027` and
  `...\deep-binding-registry-20261015195329`.
- Tests: `exchange` 16/16, including
  `deep_nested_sdt_depth_two_collection_item_alias_resolves_and_wires_uses` and
  `deep_nested_sdt_depth_two_collection_item_alias_runs_native_observed_operations`;
  the rejection test pins `sdt:Deep.Child.NestedCollection` and
  `sdt:Deep.Grandchild` as unregistered. Mutation check (alias gate `depth < 1`)
  turned both new tests red; restored.
- Real-export e2e: `scratchpad/run-deep-projection-check.py` (companion Procedure
  injected into `exportfile-sanitized.xml`) exits 0 with stdout
  `deep_default=0/deep_write=42/deep_count=0/deep_text=child/deep_item=i` and the
  captured input unchanged.
- Gates: full suite 241 passed / 0 failed / 2 ignored across 27 binaries
  (`scratchpad/engine-gate-6.log`); Clippy `-D warnings` and `cargo fmt --check`
  exit 0. README updated with the new totals and the deep-export evidence.
