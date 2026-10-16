use gx_engine::{build, model::Project};
use serde_json::json;
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
            "gx-typed-pipeline-{}-{}",
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
        &json!({
            "format_version":2, "entry":"Main",
            "procedures":[{"name":"Main", "params":[], "variables":{
                "Amount":{"kind":"numeric", "precision":5, "scale":2},
                "Other":{"kind":"numeric", "precision":5, "scale":2},
                "Whole":{"kind":"numeric", "precision":3, "scale":0},
                "Flag":{"kind":"boolean"}
            }, "source":source}]
        })
        .to_string(),
    )
    .unwrap()
}

#[test]
fn typed_source_compiles_and_runs_without_sdk() {
    let input = project(
        "&Amount = 1.235\nPrint(&Amount)\n&Other = 0.001\n&Flag = &Amount + &Other = 1.236\nIf &Flag And Not False\n&Whole = 1.55\nPrint(&Whole)\nEndIf\nPrint(1 / 8)\nReturn 0",
    );
    let folder = Scratch::new();
    {
        let artifact = build::build(&input, &folder.0).unwrap();
        let output = Command::new(&artifact.executable).output().unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
            " 1.24\n  1\n0.125\n"
        );
        assert_eq!(
            gx_engine::compile(&input).unwrap(),
            gx_engine::compile(&Project::from_json(&input.to_json().unwrap()).unwrap()).unwrap()
        );
    }
}

fn run(source: &str) -> std::process::Output {
    let scratch = Scratch::new();
    let artifact = build::build(&project(source), &scratch.0).unwrap();
    Command::new(artifact.executable).output().unwrap()
}

#[test]
fn typed_short_circuit_preserves_boolean_type_and_skips_errors() {
    let result = run(
        "&Flag = False And 1 / 0 = 1\nPrint(&Flag)\n&Flag = True Or 1 / 0 = 1\nPrint(&Flag)\n&Whole = 0\nDo While &Whole < 3\n&Whole = &Whole + 1\nEndDo\nPrint(&Whole)",
    );
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&result.stdout).replace("\r\n", "\n"),
        "false\ntrue\n  3\n"
    );
}

#[test]
fn typed_runtime_errors_are_controlled_and_do_not_wrap_or_hang() {
    for (source, expected) in [
        ("&Whole = 32760\n&Whole = &Whole + 10", "storage overflow"),
        ("&Amount = 1 / 0", "division by zero"),
        ("Do While True\nEndDo", "step limit"),
    ] {
        let result = run(source);
        assert_eq!(result.status.code(), Some(70), "{source}");
        assert!(
            String::from_utf8_lossy(&result.stderr).contains(expected),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
    }
}

#[test]
fn repeating_division_uses_dotnet_decimal_precision() {
    let result = run("Print(1 / 3)");
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&result.stdout).trim(),
        "0.3333333333333333333333333333"
    );
}

#[test]
fn typed_declarations_round_trip_and_fail_closed() {
    let input = project("&Whole = 1");
    assert!(input.procedures[0].variables.contains_key("WHOLE"));
    assert_eq!(
        input,
        Project::from_json(&input.to_json().unwrap()).unwrap()
    );
    let valid: serde_json::Value = serde_json::from_str(&input.to_json().unwrap()).unwrap();
    for invalid in [
        json!({"kind":"numeric", "precision":0,"scale":0}),
        json!({"kind":"numeric", "precision":3,"scale":4}),
        json!({"kind":"boolean", "scale":1}),
        json!({"kind":"string"}),
    ] {
        let mut candidate = valid.clone();
        candidate["procedures"][0]["variables"]["WHOLE"] = invalid;
        assert!(
            Project::from_json(&candidate.to_string()).is_err(),
            "accepted {candidate}"
        );
    }
    let exact_duplicate = r#"{"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"source":"","variables":{"X":{"kind":"boolean"},"X":{"kind":"boolean"}}}]}"#;
    assert!(Project::from_json(exact_duplicate).is_err());
    let mut duplicate = valid.clone();
    duplicate["procedures"][0]["variables"]["whole"] = json!({"kind":"boolean"});
    assert!(Project::from_json(&duplicate.to_string()).is_err());
    let mut legacy = valid;
    legacy["format_version"] = json!(1);
    assert!(Project::from_json(&legacy.to_string()).is_err());
    for literal in ["Print(1.5)", "Print(True)"] {
        let legacy = Project::from_json(&json!({"format_version":1,"entry":"Main","procedures":[{"name":"Main","params":[],"source":literal}]}).to_string()).unwrap();
        assert!(gx_engine::compile(&legacy).is_err());
    }
}

#[test]
fn typed_source_rejects_mixed_types_and_missing_declarations() {
    for source in [
        "&Flag = 1",
        "&Amount = True",
        "If 1\nEndIf",
        "&Missing = 1",
        "Print(&Amount)",
        "&Flag = True + False",
        "&Whole = 1 And 2",
        "&Flag = Not 1",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "accepted {source}"
        );
    }
}

fn callable_project(main: &str, helper: &str) -> Project {
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":{"Amount":{"kind":"numeric","precision":5,"scale":2},"Flag":{"kind":"boolean"}},"source":main},
        {"name":"Echo","params":["Value"],"variables":{"Value":{"kind":"numeric","precision":5,"scale":2}},"return_type":{"kind":"numeric","precision":5,"scale":2},"source":helper},
        {"name":"Positive","params":["Value"],"variables":{"Value":{"kind":"numeric","precision":5,"scale":2}},"return_type":{"kind":"boolean"},"source":"Return &Value > 0"}
    ]}).to_string()).unwrap()
}

fn execute_project(input: &Project) -> std::process::Output {
    let scratch = Scratch::new();
    let artifact = build::build(input, &scratch.0).unwrap();
    Command::new(artifact.executable).output().unwrap()
}

/// Value-level stdout lines for tests whose expected values come from
/// `.ToString().Trim()` native probes; picture padding carries no evidence
/// there. The raw padded picture is covered by tests/numeric_render_native.rs.
fn value_level_lines(output: &std::process::Output) -> Vec<String> {
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .map(|line| line.trim().to_string())
        .collect()
}

#[test]
fn typed_helpers_preserve_values_argument_order_and_native_call_hoisting() {
    let input = callable_project(
        "&Amount = Echo(1.235) + Echo(2.001)
Print(&Amount)
&Flag = Positive(&Amount) Or Positive(Echo(99))
Print(&Flag)",
        "Print(&Value)
Return &Value",
    );
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        value_level_lines(&output),
        ["1.24", "2.00", "3.24", "99.00", "true"]
    );
    assert_eq!(
        gx_engine::compile(&input).unwrap(),
        gx_engine::compile(&Project::from_json(&input.to_json().unwrap()).unwrap()).unwrap()
    );
}

#[test]
fn typed_calls_reject_wrong_signatures_and_missing_returns() {
    for main in [
        "&Amount = Echo(True)",
        "&Amount = Echo()",
        "&Amount = Missing(1)",
        "&Flag = Echo(1)",
    ] {
        assert!(
            gx_engine::compile(&callable_project(main, "Return &Value")).is_err(),
            "{main}"
        );
    }
    for helper in [
        "Return True",
        "Print(&Value)",
        "If &Value > 0
Return &Value
EndIf",
    ] {
        assert!(
            gx_engine::compile(&callable_project("&Amount = Echo(1)", helper)).is_err(),
            "{helper}"
        );
    }
}

#[test]
fn typed_recursive_calls_share_depth_and_step_guards() {
    let output = execute_project(&callable_project(
        "&Amount = Echo(1)",
        "Return Echo(&Value)",
    ));
    assert_eq!(output.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&output.stderr).contains("call depth limit"));
}

#[test]
fn typed_call_boundaries_truncate_and_check_numeric_storage() {
    let base = callable_project("&Amount = Echo(1.9)\nPrint(&Amount)", "Return &Value + 0.9");
    let mut doc: serde_json::Value = serde_json::from_str(&base.to_json().unwrap()).unwrap();
    doc["procedures"][1]["variables"]["VALUE"] = json!({"kind":"numeric","precision":4,"scale":0});
    doc["procedures"][1]["return_type"] = json!({"kind":"numeric","precision":4,"scale":0});
    let input = Project::from_json(&doc.to_string()).unwrap();
    let output = execute_project(&input);
    assert!(output.status.success());
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "1.00");
    for (main, helper) in [
        ("&Amount = Echo(32768)", "Return &Value"),
        ("&Amount = Echo(1)", "Return 32768"),
    ] {
        doc["procedures"][0]["source"] = json!(main);
        doc["procedures"][1]["source"] = json!(helper);
        let output = execute_project(&Project::from_json(&doc.to_string()).unwrap());
        assert_eq!(output.status.code(), Some(70));
        assert!(String::from_utf8_lossy(&output.stderr).contains("storage overflow"));
    }
}

#[test]
fn typed_multiargument_calls_evaluate_each_operand_once_in_source_order() {
    let base = callable_project(
        "&Amount = Pair(Echo(1.235), Echo(2.001))\nPrint(&Amount)",
        "Print(&Value)\nReturn &Value",
    );
    let mut doc: serde_json::Value = serde_json::from_str(&base.to_json().unwrap()).unwrap();
    doc["procedures"].as_array_mut().unwrap().push(json!({"name":"Pair","params":["Left","Right"],"variables":{
        "Left":{"kind":"numeric","precision":5,"scale":2},"Right":{"kind":"numeric","precision":5,"scale":2}
    },"return_type":{"kind":"numeric","precision":5,"scale":2},"source":"Return &Left * 10 + &Right"}));
    let output = execute_project(&Project::from_json(&doc.to_string()).unwrap());
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(value_level_lines(&output), ["1.24", "2.00", "14.35"]);
}

#[test]
fn typed_boolean_parameters_and_recursive_base_cases_work() {
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"source":"Print(Flip(True))\nPrint(Sum(10))"},
        {"name":"Flip","params":["Flag"],"variables":{"Flag":{"kind":"boolean"}},"return_type":{"kind":"boolean"},"source":"Return Not &Flag"},
        {"name":"Sum","params":["N"],"variables":{"N":{"kind":"numeric","precision":9,"scale":0}},"return_type":{"kind":"numeric","precision":9,"scale":0},"source":"If &N = 0\nReturn 0\nElse\nReturn &N + Sum(&N - 1)\nEndIf"}
    ]}).to_string()).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        ["false", "55"]
    );
}

#[test]
fn typed_signature_validation_fails_before_native_compilation() {
    let base = callable_project("&Amount = Echo(1)", "Return &Value");
    let doc: serde_json::Value = serde_json::from_str(&base.to_json().unwrap()).unwrap();
    for (index, field, value) in [
        (0, "params", json!(["Unknown"])),
        (1, "params", json!(["Unknown"])),
        (1, "return_type", json!(null)),
        (0, "return_type", json!({"kind":"boolean"})),
    ] {
        let mut candidate = doc.clone();
        candidate["procedures"][index][field] = value;
        let input = Project::from_json(&candidate.to_string()).unwrap();
        assert!(gx_engine::compile(&input).is_err());
    }
    for invalid in [
        json!({"kind":"numeric","precision":0,"scale":0}),
        json!({"kind":"boolean","scale":1}),
    ] {
        let mut candidate = doc.clone();
        candidate["procedures"][1]["return_type"] = invalid;
        assert!(Project::from_json(&candidate.to_string()).is_err());
    }
}

#[test]
fn typed_incremental_reuses_bodies_invalidates_signatures_and_rolls_back_errors() {
    let input = callable_project("&Amount = Echo(1.235)\nPrint(&Amount)", "Return &Value");
    let mut session = gx_engine::incremental::IncrementalCompiler::new();
    let first = session.update(&input).unwrap();
    assert_eq!(first.source, gx_engine::compile(&input).unwrap());
    assert_eq!(first.reparsed, ["ECHO", "MAIN", "POSITIVE"]);
    let unchanged = session.update(&input).unwrap();
    assert!(unchanged.reparsed.is_empty());
    assert!(unchanged.affected.is_empty());
    assert_eq!(unchanged.reused, ["ECHO", "MAIN", "POSITIVE"]);
    let mut modified = input.clone();
    modified.procedures[1].return_type = Some(gx_engine::model::DataType::Numeric {
        precision: 4,
        scale: 0,
    });
    let changed = session.update(&modified).unwrap();
    assert_eq!(changed.source, gx_engine::compile(&modified).unwrap());
    assert!(
        changed.reparsed.is_empty(),
        "signature edits do not change parsed syntax"
    );
    assert_eq!(changed.affected, ["ECHO", "MAIN"]);
    let mut invalid = modified.clone();
    invalid.procedures[1].source = "Return True".into();
    assert!(session.update(&invalid).is_err());
    let restored = session.update(&modified).unwrap();
    assert!(restored.reparsed.is_empty());
    assert!(restored.affected.is_empty());
    assert_eq!(restored.source, changed.source);
    let mut declaration_edit = modified.clone();
    declaration_edit.procedures[1]
        .variables
        .insert("VALUE".into(), gx_engine::model::DataType::Boolean {});
    assert!(
        session.update(&declaration_edit).is_err(),
        "cached AST must not bypass type revalidation"
    );
}

#[test]
fn typed_call_fixture_matches_native_observed_side_effects_and_loop_guards() {
    let input = Project::from_json(include_str!("../fixtures/typed-calls.json")).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        value_level_lines(&output),
        [
            "1.24", "2.00", "3.24", "99.00", "1", "3.13", "4.00", "35.25", "1.00", "1.00", "1.00",
            "2.00"
        ]
    );
}

#[test]
fn numeric_record_assignment_aliases_and_clone_separates_like_genexus() {
    // SDK-created RustFreshSdt091817 and SDM87563700, local Nemo rebuild oracle.
    let input = Project::from_json(include_str!("../fixtures/typed-records.json")).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        ["0", "9", "9", "9", "11"]
    );
}

fn record_project(source: &str) -> Project {
    let mut input = Project::from_json(include_str!("../fixtures/typed-records.json")).unwrap();
    input.procedures[0].source = source.into();
    input
}

#[test]
fn record_schemas_reject_duplicates_bad_references_and_programmatic_size_overflow() {
    let mut input = record_project("&First = new()");
    input.record_types[0].fields = (0..1025)
        .map(|n| {
            (
                format!("FIELD{n}"),
                gx_engine::model::DataType::Numeric {
                    precision: 4,
                    scale: 0,
                },
            )
        })
        .collect();
    assert!(gx_engine::compile(&input).is_err());
    for fields in [
        r#"{"Amount":{"kind":"numeric","precision":4,"scale":0},"Amount":{"kind":"numeric","precision":4,"scale":0}}"#,
        r#"{"Amount":{"kind":"numeric","precision":4,"scale":0},"amount":{"kind":"numeric","precision":4,"scale":0}}"#,
        r#"{"Amount":{"kind":"record","schema":"Probe"}}"#,
    ] {
        let json = format!(
            "{{\"format_version\":2,\"entry\":\"Main\",\"procedures\":[{{\"name\":\"Main\",\"params\":[],\"source\":\"\"}}],\"record_types\":[{{\"name\":\"Probe\",\"fields\":{fields}}}]}}"
        );
        assert!(Project::from_json(&json).is_err(), "admitted {fields}");
    }
    let mut input = record_project("&First = new()");
    input.record_types[0].name = "Unknown".into();
    assert!(gx_engine::compile(&input).is_err());
    let mut input = record_project("&First = new()");
    input.record_types.push(input.record_types[0].clone());
    assert!(gx_engine::compile(&input).is_err());
}

fn record_parameter_project() -> Project {
    let mut input = record_project("");
    for mode in ["in", "out", "inout"] {
        input.procedures[0].source.push_str(&format!("&First = new()\n&First.Amount = 7\n&Second = &First\nP{mode}.Call(&First)\nPrint(&First.Amount)\nPrint(&Second.Amount)\n"));
        input.procedures.push(gx_engine::model::ProcedureSource {
            name: format!("P{mode}"),
            params: vec![format!("{mode}:Value")],
            variables: [(
                "Value".into(),
                gx_engine::model::DataType::Record {
                    schema: "Probe".into(),
                },
            )]
            .into(),
            return_type: None,
            source: "Print(&Value.Amount)\n&Value.Amount = 9".into(),
        });
    }
    input
}

#[test]
fn record_parameter_modes_match_native_entry_copyback_and_retained_aliases() {
    // record-params-native-1792076189364263600: four exact SDK readbacks,
    // local Nemo rebuild and a fresh executable, all exit zero.
    let output = execute_project(&record_parameter_project());
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        ["7", "9", "9", "0", "9", "7", "7", "9", "9"]
    );
}

#[test]
fn record_parameter_rebinding_matches_native_without_retargeting_old_aliases() {
    // Fresh native record-rebind-native-1792076401968894100 distinguishes
    // member mutation from rebinding the formal to a new record.
    let mut input = record_parameter_project();
    for helper in &mut input.procedures[1..] {
        helper.source = "Print(&Value.Amount)\n&Value = new()\n&Value.Amount = 9".into();
    }
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        ["7", "7", "7", "0", "9", "7", "7", "9", "7"]
    );
}

#[test]
fn text_record_fields_match_native_defaults_aliasing_clone_and_length() {
    // SDK SDT RST54161700, RTM27934700; fresh native build/run exit0:
    // scratchpad/record-text-native-1792076741653949900/report.json.
    let input = Project::from_json(include_str!("../fixtures/typed-record-text.json")).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        [
            "default_label=[]",
            "default_fixed=[]",
            "alias_first=[alias ]",
            "alias_second=[alias ]",
            "clone_first=[alias ]",
            "clone_second=[clone ]",
            "fixed_first=[beta ]",
            "fixed_second=[other ]",
            "5"
        ]
    );
}

#[test]
fn boolean_and_calendar_record_fields_match_native_defaults_aliases_and_clone() {
    // SDK RSC79223900/RTM06759200, record-calendar-native-1792076977979030500.
    let input = Project::from_json(include_str!("../fixtures/typed-record-calendar.json")).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        [
            "false", "0", "0", "false", "2000", "2001", "false", "true", "2000", "1999", "2001",
            "1998"
        ]
    );
}

#[test]
fn text_record_updates_release_borrows_and_reject_wrong_field_value_types() {
    let mut input = Project::from_json(include_str!("../fixtures/typed-record-text.json")).unwrap();
    input.procedures[0].source = "&First = new()\n&First.Label = \"a \"\n&First.Label = &First.Label + \"b\"\n&First = &First.Clone()\nPrint(&First.Label)".into();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "a b");
    for source in [
        "&First = new()\n&First.Label = 7",
        "&First = new()\n&First.Amount = \"text\"",
        "&First = new()\nPrint(&First.Label + 7)",
    ] {
        input.procedures[0].source = source.into();
        assert!(gx_engine::compile(&input).is_err(), "admitted {source}");
    }
}

#[test]
fn record_parameter_boundaries_reject_wrong_schemas_rvalues_and_expression_results() {
    for mode in ["in", "out", "inout"] {
        let mut input = record_parameter_project();
        input.procedures[0].source = format!("&First = new()\nP{mode}.Call(&First)");
        let mut other = input.record_types[0].clone();
        other.name = "Other".into();
        input.record_types.push(other);
        input.procedures[0].variables.insert(
            "FIRST".into(),
            gx_engine::model::DataType::Record {
                schema: "Other".into(),
            },
        );
        assert!(
            gx_engine::compile(&input)
                .unwrap_err()
                .contains("schema mismatch")
        );
        input.procedures[0].source = format!("P{mode}.Call(new())");
        assert!(gx_engine::compile(&input).is_err());
    }
    let mut input = record_parameter_project();
    input.procedures[0].source = "Print(Pout())".into();
    assert!(
        gx_engine::compile(&input)
            .unwrap_err()
            .contains("record expression")
    );
    let mut input = record_parameter_project();
    input.procedures[1].return_type = Some(gx_engine::model::DataType::Numeric {
        precision: 4,
        scale: 0,
    });
    input.procedures[1].source = "Return 7".into();
    assert!(
        gx_engine::compile(&input)
            .unwrap_err()
            .contains("void procedure")
    );
}

#[test]
fn record_out_copyback_initializes_unassigned_target_after_early_return() {
    let mut input = record_parameter_project();
    input.procedures[0].source = "Pout.Call(&First)\nPrint(&First.Amount)".into();
    input.procedures[2].source = "Return;".into();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "0");
}

#[test]
fn record_self_updates_and_self_clone_release_borrows_before_writes() {
    let input = record_project(
        "&First = new()\n&First.Amount = 7\n&First.Amount = &First.Amount + 1\n&First = &First.Clone()\n&First.Amount = &First.Amount + 1\nPrint(&First.Amount)",
    );
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "9");
}

#[test]
fn records_fail_closed_on_uninitialized_invalid_members_and_unsupported_operations() {
    for source in [
        "Print(&First.Amount)",
        "&First.Amount = 1",
        "&Second = &First",
        "&Second = &First.Clone()",
        "&First = new(1)",
        "&First = 1",
        "&First = new()\n&First.Missing = 1",
        "&First = new()\nPrint(&First.Missing)",
        "&First = new()\n&First.Amount = True",
        "&First = new()\nMsg(&First, status)",
        "&First = new()\nPrint(&First)",
        "&First = new()\nPrint(&First.Clone())",
        "&First = new()\nPrint(&First.Amount.Missing)",
    ] {
        assert!(
            gx_engine::compile(&record_project(source)).is_err(),
            "admitted {source}"
        );
    }
    let mut input = record_project("&First = new()\n&Second = &First");
    let mut schema = input.record_types[0].clone();
    schema.name = "OTHER".into();
    input.record_types.push(schema);
    input.procedures[0].variables.insert(
        "SECOND".into(),
        gx_engine::model::DataType::Record {
            schema: "OTHER".into(),
        },
    );
    assert!(
        gx_engine::compile(&input)
            .unwrap_err()
            .to_string()
            .contains("schema mismatch")
    );
}

#[test]
fn record_schema_changes_invalidate_incremental_sessions_and_failed_updates_roll_back() {
    let mut input = record_project("&First = new()\n&First.Amount = 7\nPrint(&First.Amount)");
    let mut session = gx_engine::incremental::IncrementalCompiler::new();
    session.update(&input).unwrap();
    input.record_types[0].fields.insert(
        "AMOUNT".into(),
        gx_engine::model::DataType::Numeric {
            precision: 5,
            scale: 0,
        },
    );
    let changed = session.update(&input).unwrap();
    assert!(changed.affected.iter().any(|name| name == "MAIN"));
    let mut invalid = input.clone();
    invalid.record_types[0].fields.clear();
    assert!(session.update(&invalid).is_err());
    assert!(session.update(&input).unwrap().affected.is_empty());
}

#[test]
fn record_field_assignments_check_storage_and_track_helper_dependencies() {
    let mut input = record_project("&First = new()\n&First.Amount = 32768");
    let output = execute_project(&input);
    assert_eq!(output.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&output.stderr).contains("overflow"));
    input.procedures[0].source =
        "&First = new()\n&First.Amount = Value()\nPrint(&First.Amount)".into();
    input.procedures.push(gx_engine::model::ProcedureSource {
        name: "VALUE".into(),
        params: Vec::new(),
        variables: Default::default(),
        return_type: Some(gx_engine::model::DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        source: "Return 7".into(),
    });
    let mut session = gx_engine::incremental::IncrementalCompiler::new();
    session.update(&input).unwrap();
    input.procedures[1].source = "Return 8".into();
    assert!(
        session
            .update(&input)
            .unwrap()
            .affected
            .iter()
            .any(|name| name == "MAIN")
    );
    let output = execute_project(&input);
    assert!(output.status.success());
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "8");
}

#[test]
fn default_str_and_ascii_space_trim_match_native_observations() {
    // str-trim-native-1792078418337983400: Trim preserves tabs and NBSP.
    let source = "Print(\"[\" + Str(42) + \"]\")\nPrint(\"[\" + Str(-7) + \"]\")\nPrint(\"[\" + Str(1.25) + \"]\")\nPrint(\"[\" + Str(1.5) + \"]\")\nPrint(\"[\" + Str(-1.5) + \"]\")\nPrint(\"[\" + Str(9999999999) + \"]\")\nPrint(\"[\" + Str(10000000000) + \"]\")\nPrint(Trim(\"  a b  \"))\n&Value = \" a b \"\nPrint(&Value.Trim())\nPrint(\"[\" + Trim(\" \ta\t \") + \"]\")\nPrint(\"[\" + Trim(\"\u{a0}a\u{a0}\") + \"]\")";
    let project = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"variables":{"Value":{"kind":"varchar","length":20}},"source":source}]}).to_string()).unwrap();
    let output = execute_project(&project);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        [
            "[        42]",
            "[        -7]",
            "[         1]",
            "[         2]",
            "[        -2]",
            "[9999999999]",
            "[**********]",
            "a b",
            "a b",
            "[\ta\t]",
            "[\u{a0}a\u{a0}]"
        ]
    );
}

#[test]
fn str_and_trim_reject_unimplemented_signatures_and_keep_incremental_dependencies() {
    for source in [
        "Print(Str())",
        "Print(Str(1,True,2))",
        "Print(Str(True))",
        "Print(Trim())",
        "Print(Trim(1))",
        "Print(\" a \".Trim())",
    ] {
        let project = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"source":source}]}).to_string()).unwrap();
        assert!(gx_engine::compile(&project).is_err(), "admitted {source}");
    }
    let mut input = callable_project("Print(Trim(Str(Echo(1))))", "Return &Value");
    let mut compiler = gx_engine::incremental::IncrementalCompiler::new();
    compiler.update(&input).unwrap();
    input.procedures[1].source = "Return &Value + 1".into();
    assert!(
        compiler
            .update(&input)
            .unwrap()
            .affected
            .iter()
            .any(|name| name == "MAIN")
    );
    let output = execute_project(&input);
    assert!(output.status.success());
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "2");
}

#[test]
fn nested_record_clone_preserves_child_aliases_like_native_genexus() {
    let project = Project::from_json(include_str!("../fixtures/typed-record-nested.json")).unwrap();
    let output = execute_project(&project);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        [
            "default_child=0",
            "alias_first=9",
            "alias_second=9",
            "clone_first=11",
            "clone_second=11"
        ]
    );
}

#[test]
fn collection_clone_copies_list_but_shares_items_like_native_genexus() {
    let project =
        Project::from_json(include_str!("../fixtures/typed-record-collection.json")).unwrap();
    let output = execute_project(&project);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        [
            "default_count=0",
            "first_count=1",
            "first_item=7",
            "root_clone_first_item=9",
            "root_clone_first_count=2",
            "root_clone_second_count=2",
            "collection_clone_first_item=13",
            "collection_clone_second_item=13",
            "collection_clone_first_count=2",
            "collection_clone_second_count=3"
        ]
    );
}

#[test]
fn nested_schemas_reject_missing_references_cycles_and_unsupported_collection_operations() {
    let original =
        Project::from_json(include_str!("../fixtures/typed-record-collection.json")).unwrap();
    let mut invalid = original.clone();
    invalid.record_types[1].fields.insert(
        "BACK".into(),
        gx_engine::model::DataType::Record {
            schema: "Root".into(),
        },
    );
    assert!(
        gx_engine::compile(&invalid)
            .unwrap_err()
            .contains("recursive")
    );
    let mut invalid = original.clone();
    invalid.record_types[1].fields.insert(
        "BACK".into(),
        gx_engine::model::DataType::Collection {
            schema: "Root".into(),
        },
    );
    assert!(
        gx_engine::compile(&invalid)
            .unwrap_err()
            .contains("recursive")
    );
    let mut invalid = original.clone();
    invalid.record_types[1].fields.insert(
        "BACK".into(),
        gx_engine::model::DataType::Record {
            schema: "Missing".into(),
        },
    );
    assert!(
        gx_engine::compile(&invalid)
            .unwrap_err()
            .contains("unknown record schema")
    );
    for source in [
        "&First = new()\n&First.Rows.Add(new())",
        "&First = new()\n&Row = &First.Rows.Item(0)",
        "&First = new()\n&Row = &First.Rows.Item(True)",
        "&First = new()\n&First.Rows.Count = 1",
        "&First = new()\n&First.Child = &Row",
        "&First = new()\n&First.Rows = &First.Child",
        "&First = new()\n&Second = new()\nPrint(&First = &Second)",
        "&First = new()\nPrint(&First.Rows = &First.Rows)",
    ] {
        let mut input = original.clone();
        input.procedures[0].source = source.into();
        assert!(gx_engine::compile(&input).is_err(), "admitted {source}");
    }
}

#[test]
fn nested_default_construction_uses_shared_execution_budget_without_source_explosion() {
    use gx_engine::model::{DataType, RecordType};
    let mut input = record_project("&First = new()");
    input.procedures[0].variables.clear();
    input.procedures[0].variables.insert(
        "First".into(),
        DataType::Record {
            schema: "N0".into(),
        },
    );
    input.record_types = (0..20)
        .map(|level| RecordType {
            name: format!("N{level}"),
            fields: if level == 19 {
                [(
                    "Value".into(),
                    DataType::Numeric {
                        precision: 4,
                        scale: 0,
                    },
                )]
                .into()
            } else {
                ["Left", "Right"]
                    .map(|field| {
                        (
                            field.into(),
                            DataType::Record {
                                schema: format!("N{}", level + 1),
                            },
                        )
                    })
                    .into()
            },
        })
        .collect();
    let source = gx_engine::compile(&input).unwrap();
    assert!(
        source.len() < 200_000,
        "constructor code expanded recursively"
    );
    let output = execute_project(&input);
    assert_eq!(output.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&output.stderr).contains("execution step limit"));
}

#[test]
fn collection_clone_charges_copied_entries_to_the_shared_budget() {
    let mut input =
        Project::from_json(include_str!("../fixtures/typed-record-collection.json")).unwrap();
    input.procedures[0].variables.insert(
        "Counter".into(),
        gx_engine::model::DataType::Numeric {
            precision: 5,
            scale: 0,
        },
    );
    input.procedures[0].source = "&First = new()\n&Second = new()\n&Counter = 0\nDo While &Counter < 50\n&Row = new()\n&First.Rows.Add(&Row)\n&Counter = &Counter + 1\nEndDo\n&Counter = 0\nDo While &Counter < 10000\n&Second.Rows = &First.Rows.Clone()\n&Counter = &Counter + 1\nEndDo".into();
    let output = execute_project(&input);
    assert_eq!(output.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&output.stderr).contains("execution step limit"));
}

#[test]
fn record_clone_charges_copied_fields_to_the_shared_budget() {
    let mut input = record_project(
        "&First = new()\n&Counter = 0\nDo While &Counter < 10000\n&Second = &First.Clone()\n&Counter = &Counter + 1\nEndDo",
    );
    input.record_types[0].fields = (0..64)
        .map(|n| {
            (
                format!("F{n}"),
                gx_engine::model::DataType::Numeric {
                    precision: 4,
                    scale: 0,
                },
            )
        })
        .collect();
    input.procedures[0].variables.insert(
        "Counter".into(),
        gx_engine::model::DataType::Numeric {
            precision: 5,
            scale: 0,
        },
    );
    let output = execute_project(&input);
    assert_eq!(output.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&output.stderr).contains("execution step limit"));
}

#[test]
fn native_msg_status_writes_text_to_the_cli_console() {
    let input = callable_project(
        "Msg(\"first\", status)\nMsg(\"next\" + \" line\", STATUS)",
        "Return &Value",
    );
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "first\nnext line\n"
    );
}

#[test]
fn msg_calls_keep_incremental_dependencies_and_returned_text() {
    let mut input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"source":"Msg(Echo(), status)"},
        {"name":"Echo","params":[],"return_type":{"kind":"varchar","length":20},"source":"Return \"first\""}
    ]}).to_string()).unwrap();
    let mut compiler = gx_engine::incremental::IncrementalCompiler::new();
    compiler.update(&input).unwrap();
    input.procedures[1].source = "Return \"next\"".into();
    let changed = compiler.update(&input).unwrap();
    assert!(changed.affected.iter().any(|name| name == "MAIN"));
    let output = execute_project(&input);
    assert!(output.status.success());
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "next");
}

#[test]
fn msg_rejects_unimplemented_modes_and_non_text_messages() {
    for source in [
        "Msg(\"text\")",
        "Msg(\"text\", nowait)",
        "Msg(\"text\", status, 1)",
        "Msg(1, status)",
        "Msg(True, status)",
    ] {
        let input = callable_project(source, "Return &Value");
        assert!(gx_engine::compile(&input).is_err(), "admitted {source}");
    }
    let legacy = Project::from_json(
        &json!({"format_version":1,"entry":"Main","procedures":[
            {"name":"Main","params":[],"source":"Msg(\"text\", status)"}
        ]})
        .to_string(),
    )
    .unwrap();
    assert!(
        gx_engine::compile(&legacy)
            .unwrap_err()
            .to_string()
            .contains("format_version 2")
    );
}

#[test]
fn do_case_fixture_matches_native_observed_control_flow() {
    // Native evidence: scratchpad/do-case-native-20261015104124607.
    // Msg literals are admitted directly; only numeric trace presentation is projected.
    let input = Project::from_json(include_str!("../fixtures/typed-do-case.json")).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        value_level_lines(&output),
        [
            "first_true_first_case",
            "first_true_after",
            "no_match_fell_through",
            "nested_outer_first",
            "nested_inner_two",
            "nested_after",
            "later_helper_first_case",
            "0.00"
        ]
    );
}

#[test]
fn typed_explain_cli_reports_metadata_changes_without_building() {
    let input = callable_project("&Amount = Echo(1)", "Return &Value");
    let mut changed = input.clone();
    changed.procedures[1].return_type = Some(gx_engine::model::DataType::Numeric {
        precision: 4,
        scale: 0,
    });
    let scratch = Scratch::new();
    let before = scratch.0.join("before.json");
    let after = scratch.0.join("after.json");
    fs::write(&before, input.to_json().unwrap()).unwrap();
    fs::write(&after, changed.to_json().unwrap()).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("explain")
        .arg(&before)
        .arg(&after)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let result: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(result["affected"], json!(["ECHO", "MAIN"]));
    assert_eq!(result["reparsed"], json!([]));
    assert!(!scratch.0.join(".gx-engine").exists());
}

#[test]
fn typed_strings_match_native_character_and_varchar_storage_observations() {
    let input = Project::from_json(include_str!("../fixtures/typed-strings.json")).unwrap();
    let output = execute_project(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout)
            .lines()
            .collect::<Vec<_>>(),
        [
            "fixed_short=[ab]",
            "2",
            "variable_short=[ab]",
            "2",
            "fixed_long=[abcdefg]",
            "variable_long=[abcdefg]",
            "fixed_spaces=[ ab ]",
            "variable_spaces=[ ab ]",
            "fixed_equal=1",
            "variable_equal=1",
            "case_equal=0",
            "concat=[ ab  ab ]",
            "fixed_empty=[]",
            "variable_empty=[]",
            "fixed_unicode=[a\u{e7}\u{e3}o]",
            "4",
            "astral=[a\u{1f600}b]",
            "4"
        ]
    );
}

fn text_project(source: &str) -> Project {
    let mut document: serde_json::Value =
        serde_json::from_str(include_str!("../fixtures/typed-strings.json")).unwrap();
    document["procedures"][0]["source"] = json!(source);
    Project::from_json(&document.to_string()).unwrap()
}

#[test]
fn text_compiler_rejects_invalid_operations_and_literals() {
    for source in [
        "&FixedText = 1",
        "&FixedText = True",
        "&FixedText = \"ab\" - \"a\"",
        "If \"a\" < \"b\"\nEndIf",
        "Print(&FixedText)",
        "Print(\"unterminated)",
        "Print(\"a\nb\")",
        "Print(\"a\" + 1)",
        "Print(Not \"a\")",
    ] {
        assert!(
            gx_engine::compile(&text_project(source)).is_err(),
            "{source}"
        );
    }
    for length in [0, 65536] {
        let mut document: serde_json::Value =
            serde_json::from_str(include_str!("../fixtures/typed-strings.json")).unwrap();
        document["procedures"][0]["variables"]["FixedText"]["length"] = json!(length);
        assert!(Project::from_json(&document.to_string()).is_err());
    }
    let input = Project::from_json(&json!({"format_version":1,"entry":"Main","procedures":[{"name":"Main","params":[],"source":"Print(\"ab\")"}]}).to_string()).unwrap();
    assert!(gx_engine::compile(&input).is_err());
}

#[test]
fn text_helpers_preserve_ownership_and_check_runtime_growth() {
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":{"Text":{"kind":"character","length":2}},"source":"&Text = \"abcdefg\"\nPrint(Join(&Text, &Text))\nPrint(&Text)"},
        {"name":"Join","params":["Left","Right"],"variables":{"Left":{"kind":"character","length":2},"Right":{"kind":"varchar","length":2}},"return_type":{"kind":"character","length":2},"source":"Return &Left + &Right"}
    ]}).to_string()).unwrap();
    let result = execute_project(&input);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&result.stdout)
            .lines()
            .collect::<Vec<_>>(),
        ["abcdefgabcdefg", "abcdefg"]
    );
    let result = execute_project(&text_project(
        "&FixedText = \"ab\"\nDo While True\n&FixedText = &FixedText + &FixedText\nEndDo",
    ));
    assert_eq!(result.status.code(), Some(70));
    assert!(String::from_utf8_lossy(&result.stderr).contains("text value exceeds"));
}

#[test]
fn text_length_excludes_trailing_spaces_without_modifying_storage() {
    // Native Character and VarChar probe: resume-native-1792075013410821800.
    let input = callable_project(
        "Print(\"Q \".Length())\nPrint(\" Q \".Length())\nPrint(\"  \".Length())\nMsg(\"[\" + \"Q \" + \"]\", status)",
        "Return &Value",
    );
    let output = execute_project(&input);
    assert!(output.status.success());
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "1\n2\n0\n[Q ]\n"
    );
}

#[test]
fn text_literals_preserve_native_windows_path_backslashes() {
    let input = callable_project(r#"Msg("C:\new\test", status)"#, "Return &Value");
    let output = execute_project(&input);
    assert!(output.status.success());
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).trim(),
        r"C:\new\test"
    );
}

#[test]
fn text_length_counts_utf16_units_and_rejects_nontext_receivers() {
    let result = execute_project(&text_project(
        "&FixedText = \"a\u{1f600}b\"\nPrint(&FixedText.Length())\nPrint(\"\".Length())\nPrint((\"ab\" + \"c\").Length())",
    ));
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&result.stdout)
            .lines()
            .collect::<Vec<_>>(),
        ["4", "0", "3"]
    );
    for source in [
        "Print(True.Length())",
        "Print(\"abc\".Length(1))",
        "Print(\"abc\".Missing())",
        "Print(\"abc\".Length().Length())",
    ] {
        assert!(
            gx_engine::compile(&text_project(source)).is_err(),
            "{source}"
        );
    }
}
