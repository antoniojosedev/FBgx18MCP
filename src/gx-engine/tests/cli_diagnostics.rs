use serde_json::{Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
    process::{Command, Output},
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-cli-diagnostics-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn format1_project(source: &str) -> Value {
    json!({
        "format_version": 1,
        "entry": "Main",
        "procedures": [{"name": "Main", "params": [], "source": source}]
    })
}

fn typed_project(source: &str) -> Value {
    json!({
        "format_version": 2,
        "entry": "Main",
        "procedures": [{
            "name": "Main",
            "params": [],
            "variables": {"Amount": {"kind": "numeric", "precision": 5, "scale": 2}},
            "source": source
        }]
    })
}

fn write_project(scratch: &Scratch, name: &str, project: Value) -> PathBuf {
    let path = scratch.0.join(name);
    fs::write(&path, project.to_string()).unwrap();
    path
}

fn assert_capture_logs_removed(output_directory: &Path) {
    let build_directories = fs::read_dir(output_directory)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| path.is_dir())
        .collect::<Vec<_>>();
    assert!(!build_directories.is_empty());
    for directory in build_directories {
        let capture = directory.join(".run-stderr.log");
        assert!(
            !capture.exists(),
            "capture file leaked: {}",
            capture.display()
        );
    }
}

fn invoke_json(args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("--diagnostics=json")
        .args(args)
        .output()
        .unwrap()
}

fn invoke_human(args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .args(args)
        .output()
        .unwrap()
}

fn assert_failed_without_stdout(output: &Output) {
    assert!(!output.status.success());
    assert!(output.stdout.is_empty());
}

fn diagnostic_from_output(output: &Output) -> Value {
    serde_json::from_slice(&output.stderr).expect("stderr is one JSON diagnostic")
}

fn failure(args: &[&str]) -> Value {
    let output = invoke_json(args);
    assert_failed_without_stdout(&output);
    diagnostic_from_output(&output)
}

fn assert_human_failure(args: &[&str]) -> String {
    let output = invoke_human(args);
    assert_failed_without_stdout(&output);
    let stderr = String::from_utf8(output.stderr).unwrap();
    assert!(stderr.contains("gx-engine: "), "{stderr}");
    stderr
}

#[test]
fn cli_json_diagnostics_preserve_usage_and_input_phases() {
    let usage = failure(&["not-a-command"]);
    assert_eq!(usage["category"], "usage");
    assert!(
        usage["message"]
            .as_str()
            .unwrap()
            .contains("unknown command")
    );
    assert_eq!(usage["location"], Value::Null);
    let input = failure(&["check", "missing-gx-engine-diagnostic-input.json"]);
    assert_eq!(input["category"], "input");
    assert!(
        input["message"]
            .as_str()
            .unwrap()
            .contains("cannot open project")
    );
    assert_eq!(input["location"], Value::Null);
}

#[test]
fn cli_default_errors_remain_human_readable() {
    let output = invoke_human(&["not-a-command"]);
    assert_failed_without_stdout(&output);
    assert!(
        String::from_utf8(output.stderr)
            .unwrap()
            .starts_with("gx-engine: unknown command")
    );
}

#[test]
fn cli_json_diagnostics_cover_compilation_and_build_failures() {
    let scratch = Scratch::new();
    let invalid_source = write_project(
        &scratch,
        "invalid-source.json",
        format1_project("Print(1 + )"),
    );
    let invalid_source_path = invalid_source.to_str().unwrap();

    let compilation = failure(&["check", invalid_source_path]);
    assert_eq!(compilation["category"], "compilation");
    assert!(!compilation["message"].as_str().unwrap().is_empty());
    assert_eq!(compilation["location"], Value::Null);
    assert_human_failure(&["check", invalid_source_path]);

    let admitted_project = write_project(
        &scratch,
        "admitted-project.json",
        format1_project("Print(1)"),
    );
    let admitted_path = admitted_project.to_str().unwrap();
    let output_file = scratch.0.join("not-a-directory");
    fs::write(&output_file, "synthetic output blocker").unwrap();
    let output_path = output_file.to_str().unwrap();

    let build = failure(&["build", admitted_path, output_path]);
    assert_eq!(build["category"], "build");
    assert!(!build["message"].as_str().unwrap().is_empty());
    assert_eq!(build["location"], Value::Null);
    assert_human_failure(&["build", admitted_path, output_path]);
}

#[test]
fn cli_json_execution_failure_preserves_child_and_default_console_output() {
    let scratch = Scratch::new();
    let project = write_project(
        &scratch,
        "division-by-zero.json",
        typed_project("Print(42)\n&Amount = 1 / 0"),
    );
    let project_path = project.to_str().unwrap();
    let json_output = scratch.0.join("json-build");
    let json_output_path = json_output.to_str().unwrap();

    // Exercise default inherited child streams before checking the JSON contract.
    let human_output = scratch.0.join("human-build");
    let human_output_path = human_output.to_str().unwrap();
    let human_output = invoke_human(&["run", project_path, human_output_path]);
    assert!(!human_output.status.success());
    assert_eq!(human_output.stdout.as_slice(), b"42\n");
    let human_stderr = String::from_utf8_lossy(&human_output.stderr);
    assert!(human_stderr.contains("division by zero"), "{human_stderr}");
    assert!(human_stderr.contains("gx-engine: "), "{human_stderr}");

    let output = invoke_json(&["run", project_path, json_output_path]);
    assert!(!output.status.success());
    assert_eq!(output.stdout.as_slice(), b"42\n");
    let diagnostic = diagnostic_from_output(&output);
    assert_eq!(diagnostic["category"], "execution");
    assert!(
        diagnostic["message"]
            .as_str()
            .unwrap()
            .contains("division by zero"),
        "{diagnostic}"
    );
    assert_eq!(diagnostic["location"], Value::Null);
    assert_capture_logs_removed(&json_output);

    let success_project =
        write_project(&scratch, "successful-run.json", typed_project("Print(42)"));
    let success_project_path = success_project.to_str().unwrap();
    let success_output_directory = scratch.0.join("successful-json-build");
    let success_output_path = success_output_directory.to_str().unwrap();
    let success = invoke_json(&["run", success_project_path, success_output_path]);
    assert!(
        success.status.success(),
        "{}",
        String::from_utf8_lossy(&success.stderr)
    );
    assert_eq!(success.stdout.as_slice(), b"42\n");
    assert!(success.stderr.is_empty());
    assert_capture_logs_removed(&success_output_directory);
}

#[test]
fn diagnostic_json_has_only_the_public_phase_message_location_contract() {
    let result = failure(&["check"]);
    assert_eq!(
        result,
        json!({"category":"usage","message":"invalid arguments; use --help","location":null})
    );
}
