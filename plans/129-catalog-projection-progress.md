# Catalog projection progress

Scope: `src/gx-engine/src/kb.rs`, `src/gx-engine/tests/kb_pipeline.rs`, and this ledger. This is an incremental workstream toward portable catalog projection; it does not claim native Knowledge Base decoding or GeneXus parity.

## Nested projection and direct Calls contract — verified

`project_from_catalog` now follows directly declared `Uses` dependencies for SDTs. Record/Collection schema names in an SDT's `Fields`, a Procedure's `Variables`, and a typed return value must match an explicit `Uses` SDT by case-insensitive schema name. Unsupported dependency kinds fail closed. The resulting `Project` validation rejects unknown schema names and recursive record/collection graphs. Projection remains read-only, preserves raw field/declaration JSON for duplicate-key detection, and enforces 1,024 procedures, 128 schemas, and 1,152 total reachable catalog objects.

The end-to-end fixture projects `Probe → Child → Grandchild` plus a Row collection, saves the catalog, invokes `kb-run`, observes `7\n8\n9\n`, and confirms the serialized catalog is unchanged. Rejection coverage includes missing Uses at Procedure, transitive SDT, and typed-return sites, recursive schemas, and the 129th schema.

Validation:

- Focused pipeline: `cargo test --manifest-path src/gx-engine/Cargo.toml --test kb_pipeline -- --nocapture` — **13 passed, 0 failed**.
- Explicit-Uses guard mutation: the relevant pipeline tests went red (3 failures / 3 passes); restored guard, then focused pipeline was green.
- Cycle propagation mutation: replacing projected Fields with a scalar made the cycle regression fail because the invalid recursive graph became an accepted Project; restored Fields projection, then the focused pipeline was green.
- Direct Calls regression: Main→A→B made B transitively reachable, but Main source `B()` with only Main→A returned `Ok(Project)` before the fix. Requiring each parsed non-builtin direct callee to match its direct Calls references now rejects it; Main→A→B and A↔B recursion pass when those direct edges match. Bypassing the guard makes the regression fail (exit 101); restored guard, pipeline is green.
- `rustfmt --edition 2024 src/gx-engine/src/kb.rs src/gx-engine/tests/kb_pipeline.rs` — passed.

The first Cargo attempt was blocked before tests by an unrelated E0382 in `exchange.rs`; a separate change added an SDT-branch `continue` and resolved compilation. This worker did not modify `exchange.rs`.

## Documentation-ready capability/test bullets

- Catalog projection requires a direct `Calls` reference for every non-builtin callee in a Procedure's parsed source; a transitive path is insufficient, and projection never infers/repairs references. The shared frontend/dependencies traversal is reused, centralized typed builtins remain exempt, and recursive graphs pass when their direct edges match.
- The portable catalog projection follows `Uses`-declared SDTs transitively, supports nested Record/Collection fields and typed returns, and rejects undeclared dependencies and recursive record schemas. Projection is read-only and enforces 1,024 Procedures, 128 schemas, and 1,152 reachable objects.
- Focused `kb_pipeline` validation passed 13/13. Nested `kb-run` prints `7`, `8`, `9` without catalog mutation; direct-call rejection, recursive/direct Calls, builtins, Uses/cycle rejection, and Procedure/SDT bounds are covered. The direct-call guard has a red mutation check.

## Next step

Run focused exchange integration tests and the complete `gx-engine` suite for the `project_from_catalog` call-validation change; then review the final diff. The 1,024-Procedure and 128-SDT bounds are already covered.
