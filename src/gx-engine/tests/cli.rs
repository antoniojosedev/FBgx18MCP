use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};
static NEXT: AtomicU64 = AtomicU64::new(0);
struct Scratch(std::path::PathBuf);
impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-engine-cli-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
    fn project(&self, source: &str) -> std::path::PathBuf {
        let path = self.0.join("portable project.json");
        fs::write(&path, serde_json::json!({"format_version":1,"entry":"Main","procedures":[{"name":"Main","params":[],"source":source}]}).to_string()).unwrap();
        path
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn cli() -> Command {
    Command::new(env!("CARGO_BIN_EXE_gx-engine"))
}

#[test]
fn help_and_bad_arguments_have_correct_exit_status() {
    let output = cli().arg("--help").output().unwrap();
    assert!(output.status.success());
    assert!(String::from_utf8_lossy(&output.stdout).contains("No native KB access"));
    for args in [vec!["unknown"], vec!["check"], vec!["emit", "a", "b"]] {
        assert!(!cli().args(args).output().unwrap().status.success());
    }
}

#[test]
fn version_flag_prints_the_package_version_for_host_probes() {
    for args in [
        vec!["--version"],
        vec!["-V"],
        vec!["--diagnostics=json", "--version"],
    ] {
        let output = cli().args(args).output().unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&output.stdout).trim(),
            format!("gx-engine {}", env!("CARGO_PKG_VERSION"))
        );
    }
}

#[test]
fn check_and_run_execute_the_same_admitted_project() {
    let scratch = Scratch::new();
    let project = scratch.project("Print(6 * 7)\nReturn 0");
    assert!(
        cli()
            .arg("check")
            .arg(&project)
            .output()
            .unwrap()
            .status
            .success()
    );
    assert!(!scratch.0.join(".gx-engine").exists());
    let output = cli().arg("run").arg(&project).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "42");
    assert!(scratch.0.join(".gx-engine").is_dir());
}

#[test]
fn lowered_case_depth_is_rejected_before_build_artifacts() {
    let scratch = Scratch::new();
    let path = scratch.0.join("case-depth.json");
    let source = format!("Do Case\n{}EndCase", "Case False\nPrint(1)\n".repeat(150));
    fs::write(
        &path,
        serde_json::json!({"format_version":2,"entry":"Main","procedures":[
            {"name":"Main","params":[],"source":source}
        ]})
        .to_string(),
    )
    .unwrap();
    for command in ["check", "build"] {
        let output = cli().arg(command).arg(&path).output().unwrap();
        assert!(
            !output.status.success(),
            "{command} admitted excessive lowered Case depth"
        );
        assert!(String::from_utf8_lossy(&output.stderr).contains("nesting"));
        assert!(!scratch.0.join(".gx-engine").exists());
    }
}

#[test]
fn unsupported_source_fails_before_any_build_output() {
    let scratch = Scratch::new();
    let project = scratch.project("For each Customer\nEndFor");
    let output = cli().arg("build").arg(&project).output().unwrap();
    assert!(!output.status.success());
    assert!(!output.stderr.is_empty());
    assert!(!scratch.0.join(".gx-engine").exists());
}

#[test]
fn compiler_failure_cleans_staging_and_preserves_existing_outputs() {
    let scratch = Scratch::new();
    let project = scratch.project("Print(1)");
    let fake_compiler = scratch
        .0
        .join(if cfg!(windows) { "rustc.exe" } else { "rustc" });
    // This executable rejects rustc's -vV argument: exercise a real failed subprocess,
    // without changing the parent environment or invoking a network/toolchain install.
    fs::copy(env!("CARGO_BIN_EXE_gx-engine"), fake_compiler).unwrap();
    let output_dir = scratch.0.join("output");
    fs::create_dir(&output_dir).unwrap();
    fs::write(output_dir.join("keep.txt"), "existing user output").unwrap();
    let output = cli()
        .arg("build")
        .arg(&project)
        .arg(&output_dir)
        .env("PATH", &scratch.0)
        .output()
        .unwrap();
    assert!(!output.status.success());
    assert!(
        String::from_utf8_lossy(&output.stderr).contains("rustc version probe failed"),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(fs::read_dir(&output_dir).unwrap().count(), 1);
    assert_eq!(
        fs::read_to_string(output_dir.join("keep.txt")).unwrap(),
        "existing user output"
    );
}

#[test]
fn generated_empty_loop_is_stopped_by_runtime_budget() {
    let scratch = Scratch::new();
    let project = scratch.project("Do While 1\nEndDo");
    let output = cli().arg("run").arg(&project).output().unwrap();
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(
        error.contains("gx-engine runtime error: execution step limit exceeded"),
        "expected controlled runtime budget failure, got: {error}"
    );
}

#[test]
fn explain_reports_semantic_invalidation_without_building() {
    let scratch = Scratch::new();
    let old = scratch.0.join("previous.json");
    fs::rename(scratch.project("Print(1)"), &old).unwrap();
    let next = scratch.project("Print(2)");
    let output = cli().arg("explain").arg(&old).arg(&next).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let summary: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(summary["reparsed"], serde_json::json!(["MAIN"]));
    assert_eq!(summary["affected"], serde_json::json!(["MAIN"]));
    assert_eq!(summary["artifact_cache"], false);
    assert!(!scratch.0.join(".gx-engine").exists());
    assert!(
        !cli()
            .arg("explain")
            .arg(&next)
            .output()
            .unwrap()
            .status
            .success()
    );
}

#[test]
fn oversized_input_is_rejected() {
    let scratch = Scratch::new();
    let path = scratch.0.join("too-large.json");
    fs::write(&path, vec![b' '; 1_048_577]).unwrap();
    let output = cli().arg("check").arg(&path).output().unwrap();
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("exceeds 1 MiB"));
}
