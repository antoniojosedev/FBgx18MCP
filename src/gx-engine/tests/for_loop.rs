use gx_engine::{build, model::Project};
use serde_json::json;
use std::{
    path::PathBuf,
    process::{Command, Output},
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn project(source: &str) -> Project {
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"variables":{"I":{"kind":"numeric","precision":5,"scale":0},"J":{"kind":"numeric","precision":5,"scale":0}},"source":source}]}).to_string()).unwrap()
}

fn execute(input: &Project) -> Output {
    struct Folder(PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let root = Folder(std::env::temp_dir().join(format!(
        "gx-for-loop-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )));
    let artifact = build::build(input, &root.0).unwrap();
    Command::new(&artifact.executable).output().unwrap()
}

/// Value-level stdout lines: the native for-loop lane recorded
/// `.ToString().Trim()` results, so picture padding carries no evidence in
/// this fixture. The raw padded picture is covered by
/// tests/numeric_render_native.rs.
fn printed_values(output: &Output) -> Vec<String> {
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .map(|line| line.trim().to_string())
        .collect()
}

#[test]
fn ascending_for_is_inclusive_and_preserves_iterator_after_loop() {
    // Native evidence: for-native-probe-1792100818161492200/ascending-report.json.
    let output = execute(&project("For &I = 1 To 3\nPrint(&I)\nEndFor\nPrint(&I)"));
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(printed_values(&output), ["1", "2", "3", "4"]);
}

#[test]
fn for_requires_a_matching_terminator_and_numeric_iterator() {
    for source in [
        "For &I = 1 To 3\nPrint(&I)",
        "EndFor",
        "For &I = 1 To 3\nEndDo",
        "For &I = 1 To 3 Step 0\nEndFor",
        "For &I = 1 To 3 Step &J\nEndFor",
        "For Each\nEndFor",
        "For &I = 3 To 1\n&J = 7\nEndFor\nPrint(&J)",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "admitted {source}"
        );
    }
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"variables":{"I":{"kind":"boolean"}},"source":"For &I = 1 To 3\nEndFor"}]}).to_string()).unwrap();
    assert!(gx_engine::compile(&input).is_err());
}

#[test]
fn nested_for_loops_keep_separate_iterators_and_statement_boundaries() {
    let output = execute(&project(
        "&J = 0; For &I = 1 To 2; For &J = 1 To 2; Print(&I * 10 + &J); EndFor; EndFor; Print(&I); Print(&J)",
    ));
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(printed_values(&output), ["11", "12", "21", "22", "3", "3"]);
}

#[test]
fn for_loop_consumes_shared_execution_budget_even_with_empty_body() {
    let output = execute(&project("For &I = 1 To 2147483647\nEndFor"));
    assert_eq!(output.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&output.stderr).contains("execution step limit exceeded"));
}

#[test]
fn for_nesting_and_legacy_format_fail_closed() {
    let source = format!(
        "{}{}",
        "For &I = 1 To 2\n".repeat(130),
        "EndFor\n".repeat(130)
    );
    assert!(
        gx_engine::compile(&project(&source))
            .unwrap_err()
            .contains("nesting")
    );
    let mut input = project("For &I = 1 To 2\nEndFor");
    input.format_version = 1;
    input.procedures[0].variables.clear();
    assert!(gx_engine::compile(&input).is_err());
}

#[test]
fn for_evaluates_start_once_limit_each_check_and_tracks_helper_dependencies() {
    // Native probes reported start once and To reevaluated for every condition.
    let mut input = project(
        "&J = 0
For &I = Bump(&J) To 3
Print(&I)
EndFor
Print(&I)
Print(&J)",
    );
    input.procedures.push(
        serde_json::from_value(json!({
            "name":"Bump","params":["inout:Count"],
            "variables":{"Count":{"kind":"numeric","precision":5,"scale":0}},
            "return_type":{"kind":"numeric","precision":5,"scale":0},
            "source":"&Count = &Count + 1
        Return 2"
        }))
        .unwrap(),
    );
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(printed_values(&output), ["2", "3", "4", "1"]);
    input.procedures[0].source = "&J = 0
For &I = 1 To Bump(&J)
Print(&I)
EndFor
Print(&I)
Print(&J)"
        .into();
    input.procedures[1].source = "&Count = &Count + 1
Return 3"
        .into();
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(printed_values(&output), ["1", "2", "3", "4", "4"]);
    let mut compiler = gx_engine::incremental::IncrementalCompiler::new();
    compiler.update(&input).unwrap();
    input.procedures[1].source = "&Count = &Count + 1
Return 4"
        .into();
    assert!(
        compiler
            .update(&input)
            .unwrap()
            .affected
            .iter()
            .any(|name| name == "MAIN")
    );
}

#[test]
fn literal_steps_and_direction_mismatch_match_native_observations() {
    for (source, expected) in [
        (
            "For &I = 3 To 1 Step -1
Print(&I)
EndFor
Print(&I)",
            vec!["3", "2", "1", "0"],
        ),
        (
            "For &I = 1 To 5 Step 2
Print(&I)
EndFor
Print(&I)",
            vec!["1", "3", "5", "7"],
        ),
        (
            "For &I = 3 To 1
Print(99)
EndFor
Print(&I)",
            vec!["3"],
        ),
        (
            "For &I = 1 To 3 Step -1
Print(99)
EndFor
Print(&I)",
            vec!["1"],
        ),
        (
            "For &I = 3 To 1 Step 1
Print(99)
EndFor
Print(&I)",
            vec!["3"],
        ),
    ] {
        let output = execute(&project(source));
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(printed_values(&output), expected);
    }
}

#[test]
fn changing_upper_bound_is_recomputed_including_the_final_false_check() {
    // FRL03636400: initial bound3, then bound2; body1,2; iterator3/calls3.
    let mut input = project(
        "&J = 0
For &I = 1 To Limit(&J)
Print(&I)
EndFor
Print(&I)
Print(&J)",
    );
    input.procedures.push(
        serde_json::from_value(json!({
            "name":"Limit","params":["inout:Count"],
            "variables":{"Count":{"kind":"numeric","precision":5,"scale":0}},
            "return_type":{"kind":"numeric","precision":5,"scale":0},
            "source":"&Count = &Count + 1
If &Count = 1
Return 3
Else
Return 2
EndIf"
        }))
        .unwrap(),
    );
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(printed_values(&output), ["1", "2", "3", "3"]);
}
