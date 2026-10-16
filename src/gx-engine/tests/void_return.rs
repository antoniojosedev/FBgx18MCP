//! Bare Return admission and typed control flow. Native evidence is tracked in plan 129.
use gx_engine::{build, incremental::IncrementalCompiler, model::Project};
use serde_json::json;
use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn project(main: &str, helper: &str) -> Project {
    let number = json!({"kind":"numeric","precision":5,"scale":0});
    Project::from_json(
        &json!({"format_version":2,"entry":"Main","procedures":[
            {"name":"Main","params":[],"variables":{"X":number},"source":main},
            {"name":"Helper","params":["out:Value"],"variables":{"Value":number},"source":helper}
        ]})
        .to_string(),
    )
    .unwrap()
}

fn run(project: &Project) -> String {
    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let folder = Folder(std::env::temp_dir().join(format!(
        "gx-void-return-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )));
    fs::create_dir(&folder.0).unwrap();
    let artifact = build::build(project, &folder.0).unwrap();
    let output = Command::new(artifact.executable).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout)
        .unwrap()
        .replace("\r\n", "\n")
}

/// Value-level stdout lines: the native return-probe lane recorded
/// `.ToString().Trim()` results, so picture padding carries no evidence in
/// this fixture. The raw padded picture is covered by
/// tests/numeric_render_native.rs.
fn run_values(project: &Project) -> Vec<String> {
    run(project)
        .lines()
        .map(|line| line.trim().to_string())
        .collect()
}

#[test]
fn bare_return_exits_nested_control_flow_and_preserves_output_copyback() {
    let input = project(
        "Helper(&X)\nPrint(&X)\nPrint(2)",
        "&Value = 7\nDo While True\nIf True\nReturn\nEndIf\nEndDo\n&Value = 99",
    );
    assert_eq!(run_values(&input), ["7", "2"]);
    let defaults = project("&X = 42\nHelper(&X)\nPrint(&X)", "Return");
    assert_eq!(run_values(&defaults), ["0"]);
    assert_eq!(run_values(&project("Print(Helper())", "Return")), ["0"]);
}

#[test]
fn only_fallthrough_branches_determine_definite_assignment() {
    let mut input = project(
        "Helper(&X)\nPrint(&X)",
        "If False\nReturn\nElse\n&Local = 7\nEndIf\n&Value = &Local",
    );
    input.procedures[1].variables.insert(
        "LOCAL".into(),
        gx_engine::model::DataType::Numeric {
            precision: 5,
            scale: 0,
        },
    );
    assert_eq!(run_values(&input), ["7"]);
    input.procedures[1].source = "If True\n&Local = 8\nElse\nReturn\nEndIf\n&Value = &Local".into();
    assert_eq!(run_values(&input), ["8"]);
    input.procedures[1].source = "If True\n&Local = 8\nEndIf\n&Value = &Local".into();
    assert!(
        gx_engine::compile(&input)
            .unwrap_err()
            .to_string()
            .contains("read before assignment")
    );
}

#[test]
fn entry_can_return_without_a_value() {
    for source in ["Print(1)\nReturn\nPrint(99)", "Print(1); Return; Print(99)"] {
        assert_eq!(run_values(&project(source, "Return")), ["1"]);
    }
}

#[test]
fn bare_return_is_rejected_in_legacy_and_value_returning_procedures() {
    let mut legacy = project("Return", "Return");
    legacy.format_version = 1;
    legacy.procedures.pop();
    legacy.procedures[0].variables.clear();
    assert!(
        gx_engine::compile(&legacy)
            .unwrap_err()
            .to_string()
            .contains("format_version 2")
    );
    let mut typed = project("Print(Helper())", "Return");
    typed.procedures[1].params.clear();
    typed.procedures[1].return_type = Some(gx_engine::model::DataType::Numeric {
        precision: 5,
        scale: 0,
    });
    assert!(gx_engine::compile(&typed).is_err());
}

#[test]
fn incremental_early_return_change_invalidates_callers() {
    let mut input = project("Helper(&X)\nPrint(&X)", "&Value = 7");
    let mut compiler = IncrementalCompiler::new();
    compiler.update(&input).unwrap();
    input.procedures[1].source = "Return".into();
    let changed = compiler.update(&input).unwrap();
    assert!(changed.affected.iter().any(|name| name == "MAIN"));
    assert_eq!(run_values(&input), ["0"]);
}
