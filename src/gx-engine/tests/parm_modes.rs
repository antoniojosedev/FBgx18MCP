//! GeneXus `Parm` modes in the typed path. Expected values come from a native
//! KBTeste probe (plan 129, `scratchpad/parm-modes-native-1792068810475057600`).
use gx_engine::{build, model::Project};
use serde_json::json;
use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn execute(input: &Project) -> std::process::Output {
    let dir = std::env::temp_dir().join(format!(
        "gx-parm-modes-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir(&dir).unwrap();
    let artifact = build::build(input, &dir).unwrap();
    let output = Command::new(artifact.executable).output().unwrap();
    let _ = fs::remove_dir_all(&dir);
    output
}

/// Value-level stdout lines: the native parm-mode probes recorded
/// `.ToString().Trim()` results, so picture padding carries no evidence in
/// this fixture. The raw padded picture is covered by
/// tests/numeric_render_native.rs.
fn run(input: &Project) -> Vec<String> {
    let output = execute(input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .map(|line| line.trim().to_string())
        .collect()
}

fn numeric() -> serde_json::Value {
    json!({"kind":"numeric","precision":5,"scale":2})
}

fn helper_project(
    main: &str,
    params: &[&str],
    helper: &str,
    returns: bool,
) -> Result<Project, String> {
    let mut helper_json = json!({"name":"Helper","params":params,
        "variables":{"A":numeric(),"B":numeric(),"Flag":{"kind":"boolean"}},"source":helper});
    if returns {
        helper_json["return_type"] = numeric();
    }
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":{"X":numeric(),"Y":numeric(),"Flag":{"kind":"boolean"}},"source":main},
        helper_json
    ]}).to_string())
}

fn compile_error(input: Result<Project, String>) -> String {
    match input {
        Err(error) => error,
        Ok(project) => gx_engine::compile(&project).unwrap_err().to_string(),
    }
}

#[test]
fn parameter_modes_match_native_observations() {
    let input = Project::from_json(include_str!("../fixtures/typed-parm-modes.json")).unwrap();
    // Native lines, split into values: in-callee "in_a in_b in_c", then caller "a b c".
    let expected = [
        "1.00", "0.00", "3.00", // statement call: callee sees in, empty out, inout
        "1.00", "2.00", "6.00", // caller: in unchanged, out and inout copied back
        "1.00", "0.00", "6.00", // Proc.Call(...) form behaves the same
        "1.00", "2.00", "12.00", "0.00",
        "0.00", // an unassigned out parameter resets the caller variable
        "2.00", "0.00", "2.00", "4.00", // one variable in all modes: last copy-back wins
    ];
    assert_eq!(run(&input), expected);
}

#[test]
fn nested_calls_run_before_direct_operand_snapshots_like_genexus() {
    // Native evidence: parm-order-native-19b0581ce13e4d3da76765b69d5cf0fe.
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":{"X":numeric(),"Result":numeric()},"source":
            "&X = 1\n&Result = Pair(&X, Bump(&X))\nPrint(&Result)\nPrint(&X)\n&X = 10\n&Result = &X + Bump(&X)\nPrint(&Result)\nPrint(&X)\n&X = 10\n&Result = Bump(&X) + &X\nPrint(&Result)\nPrint(&X)"},
        {"name":"Bump","params":["inout:V"],"variables":{"V":numeric()},"return_type":numeric(),
            "source":"&V = &V + 1\nReturn &V"},
        {"name":"Pair","params":["in:A","in:B"],"variables":{"A":numeric(),"B":numeric()},
            "return_type":numeric(),"source":"Return &A * 10 + &B"}
    ]}).to_string()).unwrap();
    assert_eq!(
        run(&input),
        ["22.00", "2.00", "22.00", "11.00", "22.00", "11.00"]
    );
}

#[test]
fn interleaved_modes_copy_back_in_formal_order_like_genexus() {
    let vars = json!({"A":numeric(),"B":numeric(),"C":numeric(),"D":numeric(),"E":numeric()});
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":vars,"source":
            "&A = 1\n&B = 2\n&C = 3\n&D = 4\n&E = 5\nMix(&A, &B, &C, &D, &E)\nPrint(&A)\nPrint(&B)\nPrint(&C)\nPrint(&D)\nPrint(&E)\n&A = 1\nMix(&A, &A, &A, &A, &A)\nPrint(&A)"},
        {"name":"Mix","params":["in:A","out:B","inout:C","out:D","inout:E"],"variables":vars,
            "source":"Print(&A)\nPrint(&B)\nPrint(&C)\nPrint(&D)\nPrint(&E)\n&B = &A + 10\n&C = &C + 20\n&D = &A + 30\n&E = &E + 40"}
    ]}).to_string()).unwrap();
    assert_eq!(
        run(&input),
        [
            "1.00", "0.00", "3.00", "0.00", "5.00", "1.00", "11.00", "23.00", "31.00", "45.00",
            "1.00", "0.00", "1.00", "0.00", "1.00", "41.00"
        ]
    );
}

#[test]
fn explicit_inout_numeric_rvalues_use_local_temporaries_like_genexus() {
    let input = helper_project(
        "&X = 10\nHelper.Call(7)\nHelper.Call(&X + 1)\nPrint(&X)",
        &["inout:A"],
        "Print(&A)\n&A = &A + 1\nPrint(&A)",
        false,
    )
    .unwrap();
    assert_eq!(run(&input), ["7.00", "8.00", "11.00", "12.00", "10.00"]);
}

#[test]
fn character_and_datetime_out_values_start_empty_and_copy_back() {
    // Native: parm-out-defaults-native-20261015111326252. Only empty length is
    // compared here; native Len(Q-space)=1 does not characterize .Length().
    let variables = json!({"Text":{"kind":"character","length":5},"Stamp":{"kind":"date_time"}});
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":variables,"source":
            "&Text = \"OLD\"\n&Stamp = YMDHMSToT(2001,1,2,3,4,5)\nReset(&Text, &Stamp)\nPrint(\"[\" + &Text + \"]\")\nPrint(&Stamp.Year())\nPrint(&Stamp.IsEmpty())"},
        {"name":"Reset","params":["out:Text","out:Stamp"],"variables":variables,
            "source":"Print(\"[\" + &Text + \"]\")\nPrint(&Text.Length())\nPrint(&Stamp.Year())\nPrint(&Stamp.IsEmpty())\n&Text = \"Q \"\n&Stamp = YMDHMSToT(2024,2,29,12,34,56)"}
    ]}).to_string()).unwrap();
    assert_eq!(
        run(&input),
        ["[]", "0", "0", "true", "[Q ]", "2024", "false"]
    );
}

#[test]
fn modes_canonicalize_and_round_trip() {
    let input = helper_project("Helper(&X, &Y)", &["in:a", "OUT:b"], "&B = &A", false).unwrap();
    assert_eq!(input.procedures[1].params, ["IN:A", "OUT:B"]);
    let again = Project::from_json(&input.to_json().unwrap()).unwrap();
    assert_eq!(again, input);
}

#[test]
fn unprefixed_parameters_are_inout_like_genexus() {
    // Native (`scratchpad/parm-default-native-1792069248962676800`): `Parm(&A)` writes back to a
    // variable argument; a literal or an expression is accepted and passed by value.
    let input = helper_project(
        "&X = 1
Helper(&X)
Print(&X)
Helper(5)
&X = 7
Helper(&X + 1)
Print(&X)",
        &["A"],
        "Print(&A)
&A = &A + 1",
        false,
    )
    .unwrap();
    assert_eq!(run(&input), ["1.00", "2.00", "5.00", "8.00", "7.00"]);
}

#[test]
fn out_argument_counts_as_assignment() {
    let input = helper_project(
        "Helper(7, &Y)\nPrint(&Y)",
        &["A", "out:B"],
        "&B = &A * 2",
        false,
    )
    .unwrap();
    assert_eq!(run(&input), ["14.00"]);
}

#[test]
fn last_out_parameter_is_a_function_result_like_genexus() {
    // Native `Parm(in:&Value, out:&Result)` functions from the typed-call oracle,
    // now represented directly rather than rewritten into a synthetic Return.
    let mut input = Project::from_json(include_str!("../fixtures/typed-calls.json")).unwrap();
    for helper in &mut input.procedures[1..] {
        let result_type = helper.return_type.take().unwrap();
        for parameter in &mut helper.params {
            *parameter = format!("IN:{parameter}");
        }
        helper.params.push("OUT:RESULT".into());
        helper.variables.insert("RESULT".into(), result_type);
        helper.source = helper.source.replace("Return ", "&Result = ");
    }
    assert_eq!(
        run(&input),
        [
            "1.24", "2.00", "3.24", "99.00", "1", "3.13", "4.00", "35.25", "1.00", "1.00", "1.00",
            "2.00",
        ]
    );
    // Native unassigned last-out result returns the empty value, not the caller's old value.
    let input = helper_project(
        "&X = 9\n&X = Helper()\nPrint(&X)",
        &["out:A"],
        "&B = 1",
        false,
    )
    .unwrap();
    assert_eq!(run(&input), ["0.00"]);
}

#[test]
fn out_and_inout_work_in_function_calls() {
    let input = helper_project(
        "&Y = 3\n&X = Helper(&Y) + &Y\nPrint(&X)\nPrint(&Y)",
        &["inout:A"],
        "&A = &A + 1\nReturn &A * 10",
        true,
    )
    .unwrap();
    // The call is hoisted, so `&Y` after it already holds the copied-back 4.
    assert_eq!(run(&input), ["44.00", "4.00"]);
}

#[test]
fn void_calls_share_depth_budget_and_check_copyback_storage() {
    for (input, expected) in [
        (
            helper_project("Helper()", &[], "Helper()", false).unwrap(),
            "call depth limit exceeded",
        ),
        (
            {
                let mut project =
                    helper_project("Helper(&X)", &["out:A"], "&A = 32768", false).unwrap();
                project.procedures[0].variables.insert(
                    "X".into(),
                    gx_engine::model::DataType::Numeric {
                        precision: 4,
                        scale: 0,
                    },
                );
                project
            },
            "storage overflow",
        ),
    ] {
        let output = execute(&input);
        assert_eq!(output.status.code(), Some(70));
        assert!(String::from_utf8_lossy(&output.stderr).contains(expected));
    }
}

#[test]
fn invalid_mode_usage_is_rejected() {
    for (input, expected) in [
        (
            helper_project("Helper(1, 2)", &["A", "out:B"], "&B = 1", false),
            "must be a variable",
        ),
        (
            helper_project("&X = Helper(1)", &["A"], "&B = 1", false),
            "no return_type",
        ),
        (
            helper_project("Helper(1)", &["A"], "Return 1", true),
            "returns a value",
        ),
        (
            helper_project("Helper(&Y)", &["inout:A"], "&B = 1", false),
            "read before assignment",
        ),
        (
            helper_project("Helper(&Flag)", &["out:A"], "&A = 1", false),
            "typed value mismatch",
        ),
        (
            helper_project("Helper(&X)", &["ref:A"], "&B = 1", false),
            "unknown parameter mode",
        ),
    ] {
        let error = compile_error(input);
        assert!(error.contains(expected), "{expected}: {error}");
    }
    let legacy = |params: serde_json::Value, main: &str| {
        Project::from_json(
            &json!({"format_version":1,"entry":"Main","procedures":[
                {"name":"Main","params":[],"source":main},
                {"name":"Helper","params":params,"source":"Return 1"}
            ]})
            .to_string(),
        )
    };
    assert!(compile_error(legacy(json!(["out:A"]), "Return 0")).contains("format_version 2"));
    assert!(
        compile_error(legacy(json!(["A"]), "Helper(1)\nReturn 0")).contains("format_version 2")
    );
}

#[test]
fn incremental_mode_change_invalidates_statement_callers_and_rolls_back_invalid_modes() {
    let mut input =
        helper_project("&X = 3\nHelper(&X)\nPrint(&X)", &["in:A"], "&A = 9", false).unwrap();
    let mut compiler = gx_engine::incremental::IncrementalCompiler::new();
    compiler.update(&input).unwrap();
    input.procedures[1].params = vec!["OUT:A".into()];
    let changed = compiler.update(&input).unwrap();
    assert_eq!(changed.reparsed, ["HELPER"]);
    assert_eq!(changed.reused, ["MAIN"]);
    assert_eq!(changed.affected, ["HELPER", "MAIN"]);
    assert_eq!(changed.source, gx_engine::compile(&input).unwrap());
    assert_eq!(run(&input), ["9.00"]);
    let mut invalid = input.clone();
    invalid.procedures[1].params = vec!["ref:A".into()];
    assert!(compiler.update(&invalid).is_err());
    let unchanged = compiler.update(&input).unwrap();
    assert!(unchanged.affected.is_empty());
    assert_eq!(unchanged.source, changed.source);
}

#[test]
fn an_out_target_does_not_initialize_an_input_before_the_call() {
    let input = helper_project("Helper(&Y, &Y)", &["in:A", "out:B"], "&B = &A", false);
    let error = compile_error(input);
    assert!(error.contains("read before assignment"), "{error}");
}

#[test]
fn non_numeric_out_parameters_start_and_return_empty() {
    // Native (`scratchpad/parm-outtypes-native-1792069401258459300`): an unassigned out
    // Boolean/VarChar/Date is false/""/empty (Year 0) on entry and after the call.
    let input = Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[
        {"name":"Main","params":[],"variables":{"F":{"kind":"boolean"},"T":{"kind":"varchar","length":20},"D":{"kind":"date"}},
         "source":"&F = True\n&T = \"kept\"\n&D = YMDtoD(2020, 1, 2)\nProbe(&F, &T, &D)\nPrint(&F)\nPrint(\"[\" + &T + \"]\")\nPrint(&D.Year())"},
        {"name":"Probe","params":["out:F","out:T","out:D"],"variables":{"F":{"kind":"boolean"},"T":{"kind":"varchar","length":20},"D":{"kind":"date"}},
         "source":"Print(&F)\nPrint(\"[\" + &T + \"]\")\nPrint(&D.Year())"}
    ]}).to_string()).unwrap();
    assert_eq!(run(&input), ["false", "[]", "0", "false", "[]", "0"]);
}
