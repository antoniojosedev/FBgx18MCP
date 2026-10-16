# Plan 129 — diagnostic structure progress

## Scope and evidence

The engine's existing model, parser, catalog, XML projection, compiler, build, and CLI paths currently return `String` errors. Inspection confirmed that only the source parser carries a stable location prefix (`line N, column N: ...`). Message-text heuristics cannot reliably recover error provenance or safely invent native GeneXus diagnostic codes.

Added an isolated `diagnostics` module with phase-level categories (`usage`, `input`, `syntax`, `compilation`, `build`, `execution`), an unchanged message field, and optional one-based line/column coordinates. The parser constructor recognizes only its current exact prefix and rejects malformed/zero coordinates. Categories are caller-assigned; this is a gx-engine transport shape, not a GeneXus SDK code mapping or compatibility claim. No general message redaction is applied: the module preserves error text verbatim and does not persist or log it.

Files owned by this increment:

- `src/gx-engine/src/diagnostics.rs`
- `src/gx-engine/tests/diagnostics.rs`
- `plans/129-diagnostics-progress.md`

No SDK probes or native error-code mappings were added. The parent exported the module from `lib.rs` and integrated optional `--diagnostics=json` output in the CLI. The owned integration test now imports `gx_engine::diagnostics`, so it exercises the shipped public API rather than compiling a duplicate module.

## C4 rejection-fixture inventory (proposed synthetic inputs; not native observations)

Use a freshly authored disposable Procedure named `RustC4RejectionProbe`, with no parameters, `Value` declared `Numeric(8,0)`, and `Flag` declared Boolean. Submit exactly one case body at a time to the official SDK and the portable engine. These are candidate inputs for future paired evidence, not claims that GeneXus has rejected them or assigned any particular category/code.

| Fixture intent (not an SDK category) | Exact Procedure source body | Portable-side acceptance criterion | Native-side evidence required before counting it for C4 |
| --- | --- | --- | --- |
| Malformed expression | `&Value = 1 +` | `gx-engine check` exits nonzero and emits one JSON diagnostic with category `compilation`; preserve its message, do not require text equality. The direct parser error is located; the current CLI wrapper may report `location: null`. | A fresh supported syntax/specification request rejects this exact body. Record the actual operation stage and structured category only if the SDK exposes one; do not infer it from the message or numeric code. |
| Type-incompatible assignment | `&Value = True` | The portable compiler rejects before rustc; CLI JSON category is the call-site phase `compilation`, with no native code. | Confirm the same source/declarations reach the intended native semantic/specification stage and are rejected. Record observable structured category/stage, not message text. If accepted, replace the candidate rather than treating it as a negative. |
| Unresolved callee | `MissingC4Procedure()` | Portable compile rejects the unresolved call before build; CLI JSON category is `compilation`. | Confirm native syntax accepts the call form and the supported resolution/specification operation rejects the missing target. If the source is rejected earlier as invalid syntax, it does not count as an unresolved-callee pair. |

Fixture completion criteria: feed identical procedure metadata and source to both paths; record outcome, operation stage, and category only where returned structurally or established by the called phase. Assert rejection/acceptance and the phase/category contract, never message equality, substring matching, or an invented SDK code. Leave unavailable native categories explicitly unclassified. Preserve no generated SDK source or raw proprietary diagnostic transcript in tracked fixtures. `VALIDATION.md` currently records only that malformed arithmetic and an unresolved call were rejected by earlier in-memory syntax checks; exact source and structured diagnostics were not retained, so those historical summaries do not complete any row above. The undeclared-variable syntax-check acceptance documented there is not a semantic acceptance result and must not be repurposed as a negative fixture.

## Independent CLI diagnostics review

`main.rs` assigns `usage`, `input`, `compilation`, `build`, and `execution` at their operation boundaries; the build label correctly covers the whole build call, including compilation/code generation. There is no text-based category inference. CLI tests exercise usage/input, parser compilation failure, build failure after valid project loading, and execution-time division-by-zero. The run tests build and execute generated programs with local `rustc`. The JSON failure test parses all stderr as exactly one JSON diagnostic, verifies child stderr is included in its message, checks exact `42\n` stdout, and checks the generation-local capture file is removed after failure. A successful JSON run checks exact stdout, blank stderr, and capture-file cleanup; human mode keeps child stderr and stdout inherited. `Syntax` remains an API category but is not assigned by the CLI, whose `compile` boundary remains `compilation`. Parser text retains coordinates, while the CLI's generic wrapper leaves structured `location` null; exposing that location end-to-end would require typed error propagation, not a message heuristic. No CLI implementation code was changed.

## Validation status

- `cargo test --manifest-path src/gx-engine/Cargo.toml --test diagnostics -- --test-threads=1`: **passed**, 5/5. This now compiles through the public `gx_engine::diagnostics` module.
- `cargo test --manifest-path src/gx-engine/Cargo.toml --test cli_diagnostics -- --test-threads=1`: **passed**, 5/5.
- `cargo test --manifest-path src/gx-engine/Cargo.toml --test cli_diagnostics` (default parallelism): **passed**, 5/5. The execution case invokes the real CLI and local `rustc`; it is not a mocked process result.
- `rustfmt --check src/gx-engine/src/diagnostics.rs src/gx-engine/tests/diagnostics.rs`: **passed**.
- Parent-reported Str tests: **passed**, 2/2.
- The previous full `cargo fmt --check` exited 1 on parent/concurrent files; it was not rerun here. Parent owns the remaining full-format pass.
- No native SDK probe was run; the proposed C4 pairs remain unverified until exact native outcomes/categories are captured through an authorized supported path.
