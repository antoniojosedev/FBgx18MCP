//! Independent acceptance checks for the bounded portable compiler path.
//!
//! These tests exercise the CLI without a GeneXus SDK and intentionally do not
//! claim native-KB interoperability or full SDK parity.
use serde_json::Value;
use std::{
    fs,
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-sdk-replacement-acceptance-{}-{}",
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

fn cli() -> Command {
    Command::new(env!("CARGO_BIN_EXE_gx-engine"))
}

fn assert_success(output: &std::process::Output, action: &str) {
    assert!(
        output.status.success(),
        "{action} failed ({}): {}",
        output.status,
        String::from_utf8_lossy(&output.stderr)
    );
}

fn normalized_stdout(stdout: &[u8]) -> String {
    std::str::from_utf8(stdout)
        .expect("program stdout must be UTF-8")
        .replace("\r\n", "\n")
}

#[test]
fn stdout_normalization_preserves_spaces_and_blank_lines() {
    let stdout = b"line with spaces  \r\n\r\nlast\t \r\n";
    assert_eq!(normalized_stdout(stdout), "line with spaces  \n\nlast\t \n");
}

#[test]
fn str_overflow_fills_width_for_large_values_with_high_scale() {
    let scratch = Scratch::new();
    let project = scratch.0.join("large-str-overflow.json");
    fs::write(
        &project,
        r#"{"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"source":"Print(Str(79228162514264337593543950335.0,18,18))"}]}"#,
    )
    .unwrap();

    let output = cli()
        .arg("run")
        .arg(&project)
        .arg(scratch.0.join("run output"))
        .output()
        .unwrap();
    assert_success(&output, "run");
    assert_eq!(normalized_stdout(&output.stdout), "******************\n");
}

fn built_manifest(project: &Path, output_dir: &Path) -> Value {
    let output = cli()
        .arg("build")
        .arg(project)
        .arg(output_dir)
        .output()
        .unwrap();
    assert_success(&output, "build");
    let executable = PathBuf::from(String::from_utf8_lossy(&output.stdout).trim());
    assert!(executable.is_file(), "build did not publish its executable");
    let manifest = executable
        .parent()
        .expect("generation directory")
        .join("manifest.json");
    serde_json::from_slice(&fs::read(manifest).unwrap()).unwrap()
}

#[test]
fn cli_check_emit_build_run_executes_the_nested_record_collection_fixture() {
    let scratch = Scratch::new();
    let project = scratch.0.join("nested record collection.json");
    fs::write(
        &project,
        include_str!("../fixtures/typed-record-collection.json"),
    )
    .unwrap();

    let checked = cli().arg("check").arg(&project).output().unwrap();
    assert_success(&checked, "check");
    assert!(String::from_utf8_lossy(&checked.stdout).contains("SDK parity unverified"));
    assert!(!scratch.0.join(".gx-engine").exists());

    let emitted = cli().arg("emit").arg(&project).output().unwrap();
    assert_success(&emitted, "emit");
    let emitted_source = String::from_utf8(emitted.stdout).unwrap();
    assert!(emitted_source.contains("#![forbid(unsafe_code)]"));
    assert!(!scratch.0.join(".gx-engine").exists());

    let build_dir = scratch.0.join("build output");
    let built = cli()
        .arg("build")
        .arg(&project)
        .arg(&build_dir)
        .output()
        .unwrap();
    assert_success(&built, "build");
    let executable = PathBuf::from(String::from_utf8_lossy(&built.stdout).trim());
    assert!(executable.is_file(), "build did not publish its executable");
    let generation = executable.parent().expect("generation directory");
    assert_eq!(
        fs::read_to_string(generation.join("program.rs")).unwrap(),
        emitted_source
    );
    let manifest: Value =
        serde_json::from_slice(&fs::read(generation.join("manifest.json")).unwrap()).unwrap();
    assert_eq!(manifest["cache_hit"], false);
    assert_eq!(manifest["entry"], "MAIN");
    assert_eq!(manifest["backend"], "independent-rust-typed-subset");

    let expected = concat!(
        "default_count=0\n",
        "first_count=1\n",
        "first_item=7\n",
        "root_clone_first_item=9\n",
        "root_clone_first_count=2\n",
        "root_clone_second_count=2\n",
        "collection_clone_first_item=13\n",
        "collection_clone_second_item=13\n",
        "collection_clone_first_count=2\n",
        "collection_clone_second_count=3\n"
    );
    let executed = Command::new(&executable).output().unwrap();
    assert_success(&executed, "built executable");
    assert_eq!(normalized_stdout(&executed.stdout), expected);

    let run_dir = scratch.0.join("run output");
    let ran = cli()
        .arg("run")
        .arg(&project)
        .arg(&run_dir)
        .output()
        .unwrap();
    assert_success(&ran, "run");
    assert_eq!(normalized_stdout(&ran.stdout), expected);
}

#[test]
fn build_manifest_distinguishes_the_legacy_i64_and_typed_backends() {
    let scratch = Scratch::new();
    let legacy = scratch.0.join("legacy.json");
    fs::write(
        &legacy,
        r#"{"format_version":1,"entry":"Main","procedures":[{"name":"Main","params":[],"source":"Print(42)\nReturn 0"}]}"#,
    )
    .unwrap();
    let legacy_manifest = built_manifest(&legacy, &scratch.0.join("legacy output"));
    assert_eq!(legacy_manifest["backend"], "independent-rust-i64-subset");

    let typed = scratch.0.join("typed.json");
    fs::write(
        &typed,
        include_str!("../fixtures/typed-record-collection.json"),
    )
    .unwrap();
    let typed_manifest = built_manifest(&typed, &scratch.0.join("typed output"));
    assert_eq!(typed_manifest["backend"], "independent-rust-typed-subset");
}
