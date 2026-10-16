# Plan 129 — Native GeneXus `For` semantics progress

Scope: native SDK observations only, using supported Gateway/Worker authoring and fresh GeneXus 18 MSBuild/run against the authorized disposable `C:\KBs\KBTeste`. No direct SQL, other KB, Cloud, Git, or Rust implementation changes in this lane. Probe harness: `scratchpad/run-for-native.py`, derived from the successful `scratchpad/run-parm-order-native.py` author path and `scratchpad/rust-timing.proj` build path. The runner verifies source/Rules/Variables readback, fresh executable timestamp, build exit, runtime exit, and captures each scenario’s stdout/stderr/report.

## Verified native observations

### Inclusive ascending, post-loop iterator

Fresh procedure `FRA02317900`, authored/read back in `scratchpad/for-native-probe-1792100818161492200/`:

```gx
&I = 99
For &I = 1 To 3
  msg("ascending_i=" + &I.ToString().Trim(), status)
EndFor
msg("ascending_after_i=" + &I.ToString().Trim(), status)
```

Build exit 0; fresh binary; runtime exit 0. Output: `ascending_i=1`, `ascending_i=2`, `ascending_i=3`, `ascending_after_i=4`. The end is inclusive; after the loop the iterator is one past the last visited value.

Evidence: `scratchpad/for-native-probe-1792100818161492200/ascending-report.json`, `ascending-stdout.txt`, `FRA02317900-Source.json` and `author-report.json`.

### Descending with negative Step, post-loop iterator

Fresh procedure `FRD02317900` in the same evidence directory:

```gx
&I = 99
For &I = 3 To 1 Step -1
  msg("descending_i=" + &I.ToString().Trim(), status)
EndFor
msg("descending_after_i=" + &I.ToString().Trim(), status)
```

Build/runtime exit 0. Output: `descending_i=3`, `descending_i=2`, `descending_i=1`, `descending_after_i=0`. Negative literal Step is accepted; endpoint is inclusive and the iterator advances once past it.

Evidence: `scratchpad/for-native-probe-1792100818161492200/descending-report.json`, `descending-stdout.txt`, `FRD02317900-Source.json` and `author-report.json`.

### Positive literal Step

Fresh procedure `FRP03636400` in `scratchpad/for-native-probe-1792100950749692600/`:

```gx
&I = 99
For &I = 1 To 5 Step 2
  msg("positive_step_i=" + &I.ToString().Trim(), status)
EndFor
msg("positive_step_after_i=" + &I.ToString().Trim(), status)
```

Build/runtime exit 0. Output: indices `1`, `3`, `5`; after-loop iterator `7`.

Evidence: `scratchpad/for-native-probe-1792100950749692600/positive_step-report.json`, `positive_step-stdout.txt`, `FRP03636400-Source.json` and `author-report.json`.

### Start expression is evaluated once; `To` expression each condition

A side-effecting procedure-call start expression was accepted. Fresh procedure `FRI00138700` in `scratchpad/for-native-probe-1792101094954546300/` used start helper `FRH00138700(&Calls)`, which increments `Calls` and returns 2:

```gx
&Calls = 0
For &I = FRH00138700(&Calls) To 3 Step 1
  msg("dynamic_start_i=" + &I.ToString().Trim() + ";calls=" + &Calls.ToString().Trim(), status)
EndFor
msg("dynamic_start_after_i=" + &I.ToString().Trim() + ";calls=" + &Calls.ToString().Trim(), status)
```

Build/runtime exit 0. Output: `start_call=1`, body `i=2;calls=1`, body `i=3;calls=1`, after `i=4;calls=1`. The start expression is evaluated exactly once.

Fresh procedure `FRL03636400` in `scratchpad/for-native-probe-1792100950749692600/` used an upper-bound helper returning 3 on its first call and 2 thereafter:

```gx
&Calls = 0
For &I = 1 To FRB03636400(&Calls)
  msg("dynamic_bound_i=" + &I.ToString().Trim() + ";calls=" + &Calls.ToString().Trim(), status)
EndFor
msg("dynamic_bound_after_i=" + &I.ToString().Trim() + ";calls=" + &Calls.ToString().Trim(), status)
```

Helper body increments `&Calls`, sets `&Result = 3` when `&Calls = 1`, otherwise `&Result = 2`, and emits its call/result. Build/runtime exit 0. Exact output order:

```text
bound_call=1;result=3
dynamic_bound_i=1;calls=1
bound_call=2;result=2
dynamic_bound_i=2;calls=2
bound_call=3;result=2
dynamic_bound_after_i=3;calls=3
```

Therefore the `To` expression is evaluated on every loop condition, including the final false condition; do not hoist it once. First bound call occurs before the first body execution.

Evidence: `scratchpad/for-native-probe-1792101094954546300/dynamic_start-report.json`, `dynamic_start-stdout.txt`, `FRI00138700-Source.json`; and `scratchpad/for-native-probe-1792100950749692600/dynamic_bound-report.json`, `dynamic_bound-stdout.txt`, `FRL03636400-Source.json`, `FRB03636400-Source.json`, `author-report.json`.

### Step syntax and zero-step safety observation

`Step 0` was accepted by the SDK. A bounded safety fixture immediately returned from its body:

```gx
&I = 99
For &I = 1 To 3 Step 0
  msg("zero_step_entered", status)
  Return
EndFor
msg("zero_step_after_i=" + &I.ToString().Trim(), status)
```

Fresh build/runtime exit 0 and output only `zero_step_entered`. This establishes that the body is entered and bare `Return` exits safely; it intentionally does **not** observe a second iteration or claim full zero-step runtime behavior. The native runner applies a 5-second timeout to this case.

A helper call and a numeric variable in the Step position were both rejected at SDK authoring/validation, before build, with `src0100: 'Step' deve ser seguido por um número.` The variable fixture attempted to change `&StepValue` from 2 to 3 in the body to distinguish per-iteration evaluation from a cached value, but native syntax rejects `Step &StepValue`, so no Step timing exists to characterize for dynamic variables.

Evidence: helper-call rejection `scratchpad/for-native-probe-1792101273500620700/FRX50048100-create.json` and variable rejection `scratchpad/for-native-probe-1792101273500620700/FRV50048100-create.json`; a later clean author run confirming both expected rejections is `scratchpad/for-native-probe-1792101561192920400/author-report.json`.

### Direction mismatch and invalid iterator types

Three bounded fresh procedures in `scratchpad/for-native-probe-1792101561192920400/` confirmed that GeneXus does not infer a descending Step from the endpoints, and the Step sign must agree with the range:

- `For &I = 3 To 1` with a body that immediately returns produced only `default_descending_after_i=3` (no body execution).
- `For &I = 1 To 3 Step -1` with immediate Return produced only `negative_mismatch_after_i=1`.
- `For &I = 3 To 1 Step 1` with immediate Return produced only `positive_mismatch_after_i=3`.

All three built from fresh binaries and ran with exit 0. The immediate Return guards make the direction probes bounded even if the body unexpectedly executes.

Evidence: `default_descending-report.json`, `negative_mismatch-report.json`, and `positive_mismatch-report.json` in the directory above.

Character and Date iterator declarations were accepted and source-read back by the SDK, but the native MSBuild path returned exit 1 for both at `TIMING specify sample=0 ... success=False`, with no fresh executable. A control Procedure in the same KB declaring both `Character(10)` and `Date(8)` variables without a loop built and ran successfully (exit 0). Thus the observed failure is specific to the attempted nonnumeric `For` procedures versus a declaration-only control, but the captured build log contains no detailed compiler diagnostic; report this as native build rejection, not as a specific error code.

Evidence for these probes: `scratchpad/for-native-probe-1792101412478542200/iterator_control-report.json`, `character_iterator-report.json`, and `date_iterator-report.json`, with the latter two build logs. The corresponding read-back metadata in `author-report.json` confirms the iterator variable types were Character and Date.

## Harness incident and correction

Initial author command used a relative evidence-root path. The Gateway runs with `publish/` as its working directory and resolved `GX_CONFIG_PATH` relative to that directory, then exited `3762504530` before any KB object creation. The isolated error was `FileNotFoundException` for `publish/scratchpad/.../http-config.json`. Updated `run-for-native.py` to resolve the evidence-root to an absolute path before calling the existing author harness; subsequent authoring and builds succeeded. Initial failed launch artifacts are isolated in `scratchpad/for-native-probe-1792100786459218200/`.

## Remaining native checks

The requested native `For` cases are covered: inclusive ascending/descending, positive and negative literal Steps, one-time start evaluation, per-condition `To` evaluation (including final false check), dynamic-Step syntax rejection, safe zero-Step body entry/Return, default direction, sign mismatch, and Character/Date iterator build rejection against a successful declaration-only control. The zero-Step probe intentionally does not observe a repeat because that would be unbounded. The build logs for nonnumeric iterators do not contain a detailed native diagnostic.

Native results are evidence for, not a substitute for, the parent implementation's focused regression tests. See exact artifacts above.
