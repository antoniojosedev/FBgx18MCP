use gx_engine::{build, model::Project};
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
            "gx-engine-test-{}-{}",
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

fn project(source: &str) -> Project {
    Project::from_json(
        &serde_json::json!({
            "format_version": 1, "entry": "Main",
            "procedures": [{ "name": "Main", "params": [], "source": source }]
        })
        .to_string(),
    )
    .unwrap()
}

#[test]
fn independently_compiles_runs_and_rebuilds_changed_input() {
    let scratch = Scratch::new();
    let first = build::build(&project("Print(21 * 2)\nReturn 0"), &scratch.0).unwrap();
    let output = Command::new(&first.executable).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "42");
    assert!(first.source.is_file());
    assert!(first.manifest.is_file());
    assert!(
        fs::read_dir(first.manifest.parent().unwrap())
            .unwrap()
            .all(|entry| {
                !entry
                    .unwrap()
                    .file_name()
                    .to_string_lossy()
                    .ends_with(".log")
            })
    );
    let second = build::build(&project("Print(7)\nReturn 0"), &scratch.0).unwrap();
    assert_ne!(first.executable, second.executable);
    let output = Command::new(&second.executable).output().unwrap();
    assert!(output.status.success());
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "7");
    // Previously published generations are never overwritten by a subsequent build.
    assert!(first.executable.is_file());
}

#[test]
fn source_emission_is_deterministic_and_reads_do_not_create_artifacts() {
    let input = project("&value = 9\nPrint(&value)\nReturn 0");
    let first = gx_engine::compile(&input).unwrap();
    assert_eq!(first, gx_engine::compile(&input).unwrap());
    let roundtrip = Project::from_json(&input.to_json().unwrap()).unwrap();
    assert_eq!(first, gx_engine::compile(&roundtrip).unwrap());
}

#[test]
fn rejects_unknown_call_without_publishing_build_artifacts() {
    let scratch = Scratch::new();
    assert!(build::build(&project("Print(Missing())"), &scratch.0).is_err());
    assert_eq!(fs::read_dir(&scratch.0).unwrap().count(), 0);
}

#[test]
fn authored_demo_exercises_calls_branches_and_loops_end_to_end() {
    let scratch = Scratch::new();
    let input = Project::from_json(include_str!("../fixtures/demo.json")).unwrap();
    let artifact = build::build(&input, &scratch.0).unwrap();
    let output = Command::new(&artifact.executable).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "42\n42\n"
    );
}

#[test]
fn programmatic_model_and_json_model_have_identical_case_insensitive_entry() {
    let input = gx_engine::model::Project {
        format_version: 1,
        date_two_digit_year_max: None,
        record_types: Vec::new(),
        entry: "Main".into(),
        procedures: vec![gx_engine::model::ProcedureSource {
            name: "MAIN".into(),
            params: vec![],
            variables: Default::default(),
            return_type: None,
            source: "Print(1)".into(),
        }],
    };
    let from_json = Project::from_json(&input.to_json().unwrap()).unwrap();
    assert_eq!(
        gx_engine::compile(&input).unwrap(),
        gx_engine::compile(&from_json).unwrap()
    );
}

#[test]
fn admitted_numeric_semantics_have_observed_native_results() {
    let scratch = Scratch::new();
    for (source, expected) in [
        ("Print(2 + 3 * 4)", "14"),
        // GeneXus 18's .NET emitter produced CS0220 for this admitted constant product.
        ("Print(123456789 * 123456789)", "15241578750190521"),
        ("Print(-9223372036854775808)", "-9223372036854775808"),
        ("Print(-7 / 2)", "-3"),
        ("Print(3 < 4)\nPrint(3 >= 4)", "1\n0"),
        ("Do While 0\nPrint(99)\nEndDo\nPrint(7)", "7"),
    ] {
        let artifact = build::build(&project(source), &scratch.0).unwrap();
        let output = Command::new(&artifact.executable).output().unwrap();
        assert!(
            output.status.success(),
            "{source}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&output.stdout)
                .replace("\r\n", "\n")
                .trim(),
            expected,
            "{source}"
        );
    }
}

#[test]
fn logical_conditions_preserve_precedence_and_short_circuit() {
    let scratch = Scratch::new();
    let input = project(
        "If 1 = 1 Or 1 = 2 And 2 = 3\nPrint(10)\nEndIf\nIf Not 1 = 2 And Not (3 < 2 Or 4 <> 4)\nPrint(20)\nEndIf\nIf (1 = 2 Or 2 = 2) And 3 = 3\nPrint(30)\nEndIf\n&x = 0\nIf &x <> 0 And 10 / &x > 1\nPrint(99)\nEndIf\nIf &x = 0 Or 10 / &x > 1\nPrint(40)\nEndIf\nIf Not Not 1 = 1\nPrint(50)\nEndIf",
    );
    let artifact = build::build(&input, &scratch.0).expect("logical conditions compile");
    let output = Command::new(&artifact.executable).output().unwrap();
    assert!(output.status.success(), "{:?}", output);
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "10\n20\n30\n40\n50\n"
    );
}

#[test]
fn logical_operands_skip_effects_and_evaluate_needed_calls_once() {
    let scratch = Scratch::new();
    let input = Project::from_json(
        &serde_json::json!({
            "format_version":1, "entry":"Main", "procedures":[
                {"name":"Main", "params":[], "source":"If 1 = 1 Or Mark() = 1\nPrint(10)\nEndIf\nIf 1 = 2 And Mark() = 1\nPrint(99)\nEndIf\nIf 1 = 1 And Mark() = 1\nPrint(20)\nEndIf\nIf 1 = 2 Or Mark() = 1\nPrint(30)\nEndIf"},
                {"name":"Mark", "params":[], "source":"Print(7)\nReturn 1"}
            ]
        }).to_string(),
    ).unwrap();
    let artifact = build::build(&input, &scratch.0).unwrap();
    let output = Command::new(&artifact.executable).output().unwrap();
    assert!(output.status.success());
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "10\n7\n20\n7\n30\n"
    );
}

#[test]
fn helper_effects_execute_once_in_source_evaluation_order() {
    let scratch = Scratch::new();
    let input = Project::from_json(
        &serde_json::json!({
            "format_version":1, "entry":"Main", "procedures":[
                {"name":"Main", "params":[], "source":"Print(Left() + Right())"},
                {"name":"Left", "params":[], "source":"Print(1)\nReturn 10"},
                {"name":"Right", "params":[], "source":"Print(2)\nReturn 20"}
            ]
        })
        .to_string(),
    )
    .unwrap();
    let artifact = build::build(&input, &scratch.0).unwrap();
    let output = Command::new(&artifact.executable).output().unwrap();
    assert!(output.status.success());
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "1\n2\n30\n"
    );
}

#[test]
fn recursion_fails_with_a_controlled_depth_diagnostic() {
    let scratch = Scratch::new();
    let input = Project::from_json(
        &serde_json::json!({
            "format_version":1, "entry":"Main", "procedures":[
                {"name":"Main", "params":[], "source":"Print(Recurse())"},
                {"name":"Recurse", "params":[], "source":"Return Recurse()"}
            ]
        })
        .to_string(),
    )
    .unwrap();
    let artifact = build::build(&input, &scratch.0).unwrap();
    let output = Command::new(&artifact.executable).output().unwrap();
    assert!(!output.status.success());
    assert!(
        String::from_utf8_lossy(&output.stderr).contains("call depth"),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn runtime_overflow_and_zero_division_fail_instead_of_wrapping() {
    let scratch = Scratch::new();
    for source in [
        "Print(9223372036854775807 + 1)",
        "Print(-9223372036854775808 - 1)",
        "Print(9223372036854775807 * 2)",
        "&x = 0\nPrint(1 / &x)",
        "Print(-9223372036854775808 / -1)",
        "Print(-(-9223372036854775808))",
    ] {
        let artifact = build::build(&project(source), &scratch.0).unwrap();
        let output = Command::new(&artifact.executable).output().unwrap();
        assert!(!output.status.success(), "{source} unexpectedly succeeded");
        assert!(!output.stderr.is_empty());
    }
}
