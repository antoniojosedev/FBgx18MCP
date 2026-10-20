//! Procedure call sites are written in the form the SDK output shows, or refused by name.
//!
//! The real-object tests compare the emitted call statements of KBTeste Procedures with the lines the
//! SDK generated for them (fixtures in `tests/fixtures/calls/`, read from the KB by `kb-read`, not typed
//! by hand). Names and variable numbering differ between the two (`AV8A` in the SDK, `AV_A` here), so the
//! comparison normalises them; everything else, including order and keywords, must match.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::{
        ProcedureClass,
        calls::{CallParam, CallSignatures, CallTarget},
    },
};
use std::sync::Arc;

const POC_SOURCE: &str = include_str!("fixtures/calls/POC97435900.SOURCE.txt");
const POC_VARIABLES: &str = include_str!("fixtures/calls/POC97435900.VARIABLES.json");
const POC_SDK: &str = include_str!("fixtures/calls/POC97435900.sdk-lines.txt");
const POI_RULES: &str = include_str!("fixtures/calls/POI97435900.RULES.txt");
const POI_VARIABLES: &str = include_str!("fixtures/calls/POI97435900.VARIABLES.json");
const POL_SOURCE: &str = include_str!("fixtures/calls/POL97435900.SOURCE.txt");
const POL_VARIABLES: &str = include_str!("fixtures/calls/POL97435900.VARIABLES.json");
const POL_SDK: &str = include_str!("fixtures/calls/POL97435900.sdk-lines.txt");
const POD_RULES: &str = include_str!("fixtures/calls/POD97435900.RULES.txt");
const POD_VARIABLES: &str = include_str!("fixtures/calls/POD97435900.VARIABLES.json");
const FRI_SOURCE: &str = include_str!("fixtures/calls/FRI00138700.SOURCE.txt");
const FRI_VARIABLES: &str = include_str!("fixtures/calls/FRI00138700.VARIABLES.json");
const FRI_SDK: &str = include_str!("fixtures/calls/FRI00138700.sdk-lines.txt");
const FRH_RULES: &str = include_str!("fixtures/calls/FRH00138700.RULES.txt");
const FRH_VARIABLES: &str = include_str!("fixtures/calls/FRH00138700.VARIABLES.json");
const POZ_SOURCE: &str = include_str!("fixtures/calls/POZ97435900.SOURCE.txt");
const POZ_VARIABLES: &str = include_str!("fixtures/calls/POZ97435900.VARIABLES.json");
const POZ_SDK: &str = include_str!("fixtures/calls/POZ97435900.sdk-lines.txt");
const POB_RULES: &str = include_str!("fixtures/calls/POB97435900.RULES.txt");
const POB_VARIABLES: &str = include_str!("fixtures/calls/POB97435900.VARIABLES.json");
const POA_SOURCE: &str = include_str!("fixtures/calls/POA97435900.SOURCE.txt");
const POA_VARIABLES: &str = include_str!("fixtures/calls/POA97435900.VARIABLES.json");
const POA_SDK: &str = include_str!("fixtures/calls/POA97435900.sdk-lines.txt");
const POP_RULES: &str = include_str!("fixtures/calls/POP97435900.RULES.txt");
const POP_VARIABLES: &str = include_str!("fixtures/calls/POP97435900.VARIABLES.json");
const RTC_SOURCE: &str = include_str!("fixtures/calls/RTC97435900.SOURCE.txt");
const RTC_VARIABLES: &str = include_str!("fixtures/calls/RTC97435900.VARIABLES.json");
const RTC_SDK: &str = include_str!("fixtures/calls/RTC97435900.sdk-lines.txt");
const RCE_RULES: &str = include_str!("fixtures/calls/RustCallEchoProbe.RULES.txt");
const RCE_VARIABLES: &str = include_str!("fixtures/calls/RustCallEchoProbe.VARIABLES.json");
const RCP_RULES: &str = include_str!("fixtures/calls/RustCallPositiveProbe.RULES.txt");
const RCP_VARIABLES: &str = include_str!("fixtures/calls/RustCallPositiveProbe.VARIABLES.json");
const RPP_RULES: &str = include_str!("fixtures/calls/RustCallPairProbe.RULES.txt");
const RPP_VARIABLES: &str = include_str!("fixtures/calls/RustCallPairProbe.VARIABLES.json");

/// A Procedure's parameters, read from its KB-form RULES text (`Parm(in:&A, out:&B)`), as a caller sees them.
fn callee(name: &str, rules: &str, variables: &str) -> Vec<CallParam> {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: variables.into(),
    });
    object.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: rules.into(),
    });
    ProcedureClass::from_kb_object(&object)
        .expect("the callee parses")
        .call_target()
        .params
}

/// A caller Procedure, emitted against the callees given as (name, RULES, VARIABLES).
fn emit(source: &str, variables: &str, callees: &[(&str, &str, &str)]) -> Result<String, String> {
    let mut table = CallSignatures::new();
    for (name, rules, vars) in callees {
        table
            .insert(CallTarget {
                class_name: name.to_string(),
                namespace: String::new(),
                params: callee(name, rules, vars),
            })
            .expect("unique");
    }
    let mut object = KbObject::new(
        ObjectId::new("caller_proc").unwrap(),
        "CallerProc",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: variables.into(),
    });
    ProcedureClass::from_kb_object(&object)
        .expect("the caller parses")
        .with_calls(Arc::new(table))
        .emit_checked()
}

fn refusal(source: &str, variables: &str, callees: &[(&str, &str, &str)]) -> String {
    emit(source, variables, callees).expect_err("the call has no SDK form, so it is refused")
}

/// Normalises a line of C# for comparison: upper case, no whitespace, SDK variable numbers removed
/// (`AV8A` and `AV_A` both become `AV_A`).
fn normal(line: &str) -> String {
    let mut out = String::new();
    let chars: Vec<char> = line.chars().filter(|c| !c.is_whitespace()).collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == 'A' && chars.get(i + 1) == Some(&'V') {
            let mut j = i + 2;
            while j < chars.len() && chars[j].is_ascii_digit() {
                j += 1;
            }
            if j > i + 2 {
                out.push_str("AV_");
                i = j;
                continue;
            }
        }
        out.push(chars[i].to_ascii_uppercase());
        i += 1;
    }
    out
}

fn normal_lines(text: &str) -> Vec<String> {
    text.lines().map(normal).filter(|l| !l.is_empty()).collect()
}

fn lines_with(text: &str, needle: &str) -> Vec<String> {
    text.lines()
        .filter(|l| l.contains(needle))
        .map(normal)
        .collect()
}

// ---- the SDK's form, by parameter mode -----------------------------------------------------------

const POI_CALLEE_VARS: &str = r#"{
    "A": {"kind": "numeric", "precision": 5, "scale": 2},
    "B": {"kind": "numeric", "precision": 5, "scale": 2},
    "C": {"kind": "numeric", "precision": 5, "scale": 2},
    "D": {"kind": "numeric", "precision": 5, "scale": 2},
    "E": {"kind": "numeric", "precision": 5, "scale": 2}
}"#;

const INTEGER_CALLEE_VARS: &str = r#"{
    "Calls": {"kind": "numeric", "precision": 5, "scale": 0}
}"#;

const CALLER_VARS: &str = r#"{
    "A": {"kind": "numeric", "precision": 5, "scale": 2},
    "B": {"kind": "numeric", "precision": 5, "scale": 2},
    "C": {"kind": "numeric", "precision": 5, "scale": 2},
    "D": {"kind": "numeric", "precision": 5, "scale": 2},
    "E": {"kind": "numeric", "precision": 5, "scale": 2},
    "X": {"kind": "numeric", "precision": 5, "scale": 2}
}"#;

#[test]
fn a_statement_call_takes_each_argument_in_its_parameter_mode() {
    let code = emit(
        "POI97435900(&A, &B, &C, &D, &E)\n",
        CALLER_VARS,
        &[(
            "POI97435900",
            "Parm(in:&A, out:&B, inout:&C, out:&D, inout:&E);",
            POI_CALLEE_VARS,
        )],
    )
    .expect("every argument has an SDK form");
    assert!(
        code.contains(
            "new POI97435900(context).execute(AV_A, out AV_B, ref AV_C, out AV_D, ref AV_E);"
        ),
        "{code}"
    );
}

#[test]
fn a_literal_for_an_in_out_decimal_goes_through_the_decimal_temporary() {
    let code = emit(
        "POD97435900(7)\n",
        CALLER_VARS,
        &[("POD97435900", "Parm(inout:&V);", POD_VARIABLES)],
    )
    .expect("the literal has the SDK temporary form");
    assert!(code.contains("GXt_decimal1 = (decimal)(7);"), "{code}");
    assert!(
        code.contains("new POD97435900(context).execute(ref GXt_decimal1);"),
        "{code}"
    );
    assert!(code.contains("private decimal GXt_decimal1 ;"), "{code}");
}

#[test]
fn an_arithmetic_expression_for_an_in_out_decimal_goes_through_the_temporary() {
    let code = emit(
        "POD97435900(&X + 1)\n",
        CALLER_VARS,
        &[("POD97435900", "Parm(inout:&V);", POD_VARIABLES)],
    )
    .expect("the expression has the SDK temporary form");
    assert!(
        code.contains("GXt_decimal1 = (decimal)((AV_X + 1));"),
        "{code}"
    );
}

#[test]
fn a_literal_for_an_in_out_integer_has_no_evidenced_temporary_and_is_refused() {
    // One in-out integer: the literal has no temporary, because the SDK's temporaries are decimal here.
    let error = refusal(
        "FRH00138700(7)\n",
        CALLER_VARS,
        &[("FRH00138700", "Parm(inout:&Calls);", INTEGER_CALLEE_VARS)],
    );
    assert!(
        error.contains("call to 'FRH00138700'")
            && error.contains("no temporary the SDK output shows"),
        "{error}"
    );
}

#[test]
fn an_output_argument_that_is_not_a_variable_is_refused_by_name() {
    let error = refusal(
        "POI97435900(1, 2, &C, &D, &E)\n",
        CALLER_VARS,
        &[(
            "POI97435900",
            "Parm(in:&A, out:&B, inout:&C, out:&D, inout:&E);",
            POI_CALLEE_VARS,
        )],
    );
    assert!(
        error.contains("call to 'POI97435900': an output argument must be a variable"),
        "{error}"
    );
}

#[test]
fn two_in_out_literals_in_one_statement_are_refused_because_their_temporaries_are_not_evidenced() {
    // Both in-out parameters are non-variables; the second must be refused, not given a second temporary.
    let error = refusal(
        "POX97435900(&A, 7, 8)\n",
        CALLER_VARS,
        &[(
            "POX97435900",
            "Parm(in:&A, inout:&C, inout:&E);",
            POI_CALLEE_VARS,
        )],
    );
    assert!(
        error.contains("call to 'POX97435900': two temporaries in one statement"),
        "{error}"
    );
}

#[test]
fn an_assignment_whose_target_type_differs_from_the_call_result_is_refused() {
    // FRH's output is `out:&Result`, an integer in FRH_VARIABLES; the target here is decimal.
    let error = refusal(
        "&X = FRH00138700(&Calls)\n",
        CALLER_VARS,
        &[(
            "FRH00138700",
            "Parm(inout:&Calls, out:&Result);",
            FRH_VARIABLES,
        )],
    );
    assert!(
        error.contains(
            "call to 'FRH00138700' in an assignment to 'AV_X': its result is int, not decimal"
        ),
        "{error}"
    );
}

#[test]
fn an_in_value_that_is_an_arithmetic_expression_has_no_evidenced_form_and_is_refused() {
    let error = refusal(
        "POI97435900(&A + 1, &B, &C, &D, &E)\n",
        CALLER_VARS,
        &[(
            "POI97435900",
            "Parm(in:&A, out:&B, inout:&C, out:&D, inout:&E);",
            POI_CALLEE_VARS,
        )],
    );
    assert!(
        error.contains("call to 'POI97435900': an input argument of this form has no SDK form"),
        "{error}"
    );
}

#[test]
fn a_call_to_a_procedure_this_build_does_not_emit_is_refused_by_name() {
    let error = refusal("NOPE97435900(&X)\n", CALLER_VARS, &[]);
    assert!(
        error.contains("needs Procedure 'NOPE97435900': this build does not generate it"),
        "{error}"
    );
}

#[test]
fn a_statement_call_with_the_wrong_argument_count_is_refused_by_name() {
    let error = refusal(
        "POD97435900(&X, &X)\n",
        CALLER_VARS,
        &[("POD97435900", "Parm(inout:&V);", POD_VARIABLES)],
    );
    assert!(
        error.contains("call to 'POD97435900': 2 argument(s) for 1 parameter(s)"),
        "{error}"
    );
}

// ---- assignments that take a call's result -------------------------------------------------------

#[test]
fn an_assignment_from_a_call_copies_the_target_in_calls_and_copies_the_result_back() {
    let code = emit(
        "&I = FRH00138700(&Calls)\n",
        FRI_VARIABLES,
        &[(
            "FRH00138700",
            "Parm(inout:&Calls, out:&Result);",
            FRH_VARIABLES,
        )],
    )
    .expect("the assignment has the SDK form");
    let lines = normal_lines(&code);
    let start = lines
        .iter()
        .position(|l| l == "GXT_INT1=AV_I;")
        .expect("the copy-in is written");
    assert_eq!(
        &lines[start..start + 3],
        &[
            "GXT_INT1=AV_I;".to_string(),
            "NEWFRH00138700(CONTEXT).EXECUTE(REFAV_CALLS,OUTGXT_INT1);".to_string(),
            "AV_I=GXT_INT1;".to_string(),
        ],
        "{code}"
    );
    assert!(code.contains("private int GXt_int1 ;"), "{code}");
}

#[test]
fn a_call_nested_in_a_value_reads_the_temporary_in_its_place() {
    let code = emit(
        "&R = &X + POB97435900(&X)\n",
        POZ_VARIABLES,
        &[("POB97435900", POB_RULES, POB_VARIABLES)],
    )
    .expect("the nested call has the SDK form");
    assert!(code.contains("GXt_decimal1 = AV_R;"), "{code}");
    assert!(
        code.contains("new POB97435900(context).execute(ref AV_X, out GXt_decimal1);"),
        "{code}"
    );
    assert!(
        code.contains("AV_R = (decimal)((AV_X + GXt_decimal1));"),
        "{code}"
    );
}

#[test]
fn two_calls_in_one_assignment_take_the_sdk_temporaries_in_pre_order() {
    // SDK shape of RustTypedCallProbe (`&Amount = Echo(1.235) + Echo(2.001)`): one decimal temporary per call,
    // in the order the calls are written, both released after the statement.
    let code = emit(
        "&R = POB97435900(&X) + POB97435900(&X)\n",
        POZ_VARIABLES,
        &[("POB97435900", POB_RULES, POB_VARIABLES)],
    )
    .expect("two calls in one assignment have the SDK form");
    assert!(code.contains("GXt_decimal1 = AV_R;"), "{code}");
    assert!(code.contains("GXt_decimal2 = AV_R;"), "{code}");
    assert!(
        code.contains("new POB97435900(context).execute(ref AV_X, out GXt_decimal1);"),
        "{code}"
    );
    assert!(
        code.contains("new POB97435900(context).execute(ref AV_X, out GXt_decimal2);"),
        "{code}"
    );
    assert!(
        code.contains("AV_R = (decimal)((GXt_decimal1 + GXt_decimal2));"),
        "{code}"
    );
}

#[test]
fn the_procedure_with_two_assignments_from_calls_matches_the_sdk_lines_of_poa97435900() {
    let code = emit(
        POA_SOURCE,
        POA_VARIABLES,
        &[
            ("POB97435900", POB_RULES, POB_VARIABLES),
            ("POP97435900", POP_RULES, POP_VARIABLES),
        ],
    )
    .expect("POA97435900 has the SDK form");
    let ours = normal_lines(&code);
    let sdk = normal_lines(POA_SDK);
    let start = ours
        .iter()
        .position(|l| l == "GXT_DECIMAL1=AV_PAIRRESULT;")
        .expect("the copy-in is written");
    assert_eq!(&ours[start..start + 4], &sdk[..4], "{code}");
    assert_eq!(ours[start + 4], sdk[4], "{code}");
}

#[test]
fn the_assignments_from_calls_in_rust_typed_call_probe_match_the_sdk_lines() {
    let code = emit(
        RTC_SOURCE,
        RTC_VARIABLES,
        &[
            ("RustCallEchoProbe", RCE_RULES, RCE_VARIABLES),
            ("RustCallPositiveProbe", RCP_RULES, RCP_VARIABLES),
            ("RustCallPairProbe", RPP_RULES, RPP_VARIABLES),
        ],
    )
    .expect("RustTypedCallProbe has the SDK form");
    let ours = normal_lines(&code);
    let sdk = normal_lines(RTC_SDK);
    // The first sum: two copy-ins, two calls and the sum, in the SDK's order and with the SDK's temporaries.
    // The sum differs only in the parentheses around the arithmetic, as in POZ97435900 above.
    let loose = |l: &str| l.replace(['(', ')'], "");
    let start = ours
        .iter()
        .position(|l| l == "GXT_DECIMAL1=AV_AMOUNT;")
        .expect("the first copy-in is written");
    assert_eq!(&ours[start..start + 4], &sdk[..4], "{code}");
    assert_eq!(loose(&ours[start + 4]), loose(&sdk[4]), "{code}");
}

#[test]
fn a_for_loop_started_by_a_call_assigns_the_call_before_the_loop() {
    let code = emit(
        "For &I = FRH00138700(&Calls) To 3 Step 1\n  msg(&I.ToString().Trim(), status)\nEndFor\n",
        FRI_VARIABLES,
        &[(
            "FRH00138700",
            "Parm(inout:&Calls, out:&Result);",
            FRH_VARIABLES,
        )],
    )
    .expect("the loop start has the SDK form");
    let copy_in = code
        .find("GXt_int1 = AV_I;")
        .expect("copy-in before the loop");
    let call = code
        .find("new FRH00138700(context).execute(ref AV_Calls, out GXt_int1);")
        .expect("call before the loop");
    let copy_out = code
        .find("AV_I = GXt_int1;")
        .expect("copy-out before the loop");
    let header = code
        .find("for ( ; AV_I <= 3; AV_I++ )")
        .expect("loop without a start");
    assert!(
        copy_in < call && call < copy_out && copy_out < header,
        "{code}"
    );
}

#[test]
fn a_call_in_a_member_assignment_is_refused_because_it_has_no_evidenced_form() {
    let error = refusal(
        "&First.Amount = POB97435900(&X)\n",
        POZ_VARIABLES,
        &[("POB97435900", POB_RULES, POB_VARIABLES)],
    );
    assert!(
        error.contains("a Procedure call in a member, attribute, collection or JSON statement"),
        "{error}"
    );
}

// ---- calls as values -----------------------------------------------------------------------------

#[test]
fn a_call_as_a_value_uses_execute_udp_with_the_keyword_of_each_input() {
    let code = emit(
        "If POB97435900(&X) = 1\n  msg(\"one\", status)\nEndIf\n",
        POZ_VARIABLES,
        &[("POB97435900", POB_RULES, POB_VARIABLES)],
    )
    .expect("the call as a value has the SDK form");
    assert!(
        code.contains("new POB97435900(context).executeUdp(ref AV_X)"),
        "{code}"
    );
}

#[test]
fn the_date_constructors_are_called_on_the_context_utility_as_the_sdk_calls_them() {
    // The SDK writes `AV8First.gxTpr_Day = context.localUtil.YMDToD( 2024, 2, 29);` (artm06759200.cs:82).
    // A static `DateTimeUtil.YMDToD` does not exist (CS0120), so the receiver is part of the contract.
    let date_vars = r#"{
        "D": {"kind": "date"},
        "T": {"kind": "date_time"}
    }"#;
    let code = emit(
        "&D = YMDToD(2024, 2, 29)\n&T = YMDHMSToT(2023, 12, 31, 23, 59, 58)\n",
        date_vars,
        &[],
    )
    .expect("the date constructors have a C# form");
    assert!(
        code.contains("AV_D = context.localUtil.YMDToD((int)(2024), (int)(2), (int)(29));"),
        "{code}"
    );
    assert!(
        code.contains(
            "AV_T = context.localUtil.YMDHMSToT((int)(2023), (int)(12), (int)(31), (int)(23), (int)(59), (int)(58));"
        ),
        "{code}"
    );
    assert!(!code.contains("DateTimeUtil.YMD"), "{code}");
}

#[test]
fn a_call_nested_in_an_in_argument_of_a_call_as_a_value_is_written_as_execute_udp() {
    // An `in` parameter takes a nested value call: `executeUdp(new X(context).executeUdp(...))`. The
    // outer callee has one output (`out:&R`) and an `in` input, so it is callable as a value.
    let code = emit(
        "If POB97435900(POL97435900(&X)) = 1\n  msg(\"one\", status)\nEndIf\n",
        POZ_VARIABLES,
        &[
            ("POB97435900", "Parm(in:&V, out:&R);", POB_VARIABLES),
            ("POL97435900", "Parm(in:&V, out:&Result);", POD_VARIABLES),
        ],
    )
    .expect("the nested value call has the SDK form");
    assert!(
        code.contains(
            "new POB97435900(context).executeUdp(new POL97435900(context).executeUdp(AV_X))"
        ),
        "{code}"
    );
}

#[test]
fn a_procedure_with_two_outputs_cannot_be_called_as_a_value() {
    let error = refusal(
        "If POP97435900(&X) = 1\n  msg(\"one\", status)\nEndIf\n",
        POZ_VARIABLES,
        &[(
            "POP97435900",
            "Parm(inout:&X, out:&Y, out:&Z);",
            POB_VARIABLES,
        )],
    );
    assert!(
        error.contains("call to 'POP97435900' in an expression: it must return exactly one value"),
        "{error}"
    );
}

#[test]
fn a_call_as_a_value_with_an_in_out_argument_that_is_not_a_variable_is_refused() {
    let error = refusal(
        "If POB97435900(7) = 1\n  msg(\"one\", status)\nEndIf\n",
        POZ_VARIABLES,
        &[("POB97435900", POB_RULES, POB_VARIABLES)],
    );
    assert!(
        error.contains(
            "call to 'POB97435900' in an expression: an in-out argument that is not a variable"
        ),
        "{error}"
    );
}

// ---- declarations and the path without a table ---------------------------------------------------

#[test]
fn each_temporary_type_is_declared_once_however_many_statements_use_it() {
    let code = emit(
        "POD97435900(7)\nPOD97435900(8)\n",
        CALLER_VARS,
        &[("POD97435900", "Parm(inout:&V);", POD_VARIABLES)],
    )
    .expect("both literals have the SDK form");
    assert_eq!(
        code.matches("private decimal GXt_decimal1 ;").count(),
        1,
        "{code}"
    );
    assert_eq!(
        code.matches("GXt_decimal1 = (decimal)(").count(),
        2,
        "{code}"
    );
}

#[test]
fn without_a_signature_table_a_call_is_written_as_it_was() {
    let mut object = KbObject::new(
        ObjectId::new("caller_proc").unwrap(),
        "CallerProc",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&R = POB97435900(&X)\n".into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: POZ_VARIABLES.into(),
    });
    let code = ProcedureClass::from_kb_object(&object)
        .expect("parses")
        .emit();
    assert!(code.contains("executeUdp(AV_X)"), "{code}");
}

// ---- real objects: the SDK's own lines -----------------------------------------------------------

#[test]
fn the_call_statements_of_poc97435900_match_the_sdk_by_mode_and_order() {
    let code = emit(
        POC_SOURCE,
        POC_VARIABLES,
        &[("POI97435900", POI_RULES, POI_VARIABLES)],
    )
    .expect("POC97435900 has the SDK form for every call");
    let ours = lines_with(&code, "POI97435900(context).execute(");
    let sdk = lines_with(POC_SDK, "poi97435900(context ).execute(");
    assert_eq!(ours.len(), 2, "{code}");
    assert_eq!(ours, sdk);
}

#[test]
fn the_literal_temporary_of_pol97435900_matches_the_sdk_lines() {
    let code = emit(
        POL_SOURCE,
        POL_VARIABLES,
        &[("POD97435900", POD_RULES, POD_VARIABLES)],
    )
    .expect("POL97435900 has the SDK form");
    // The temporary's assignment and the call; the declaration is checked separately.
    let is_call_line = |l: &String| {
        l.starts_with("GXT_DECIMAL1=") || l.starts_with("NEW") && l.contains("EXECUTE(")
    };
    let ours: Vec<String> = normal_lines(&code)
        .into_iter()
        .filter(is_call_line)
        .collect();
    let sdk: Vec<String> = normal_lines(POL_SDK)
        .into_iter()
        .filter(is_call_line)
        .collect();
    assert_eq!(ours.len(), 2, "ours={ours:?}\n{code}");
    assert_eq!(ours, sdk, "{code}");
}

#[test]
fn the_assignment_from_a_call_in_fri00138700_matches_the_sdk_lines() {
    let code = emit(
        FRI_SOURCE,
        FRI_VARIABLES,
        &[("FRH00138700", FRH_RULES, FRH_VARIABLES)],
    )
    .expect("FRI00138700 has the SDK form");
    let ours = normal_lines(&code);
    let sdk = normal_lines(FRI_SDK);
    let start = ours
        .iter()
        .position(|l| l == "GXT_INT1=AV_I;")
        .expect("the copy-in is written");
    assert_eq!(&ours[start..start + 3], &sdk[..3], "{code}");
}

#[test]
fn the_nested_call_in_poz97435900_matches_the_sdk_lines() {
    let code = emit(
        POZ_SOURCE,
        POZ_VARIABLES,
        &[("POB97435900", POB_RULES, POB_VARIABLES)],
    )
    .expect("POZ97435900 has the SDK form");
    let ours = normal_lines(&code);
    let sdk = normal_lines(POZ_SDK);
    // The copy-in and the call match exactly. The assignment differs only in the parentheses around the
    // arithmetic, which the SDK omits and this emitter writes for every binary expression.
    let loose = |l: &str| l.replace(['(', ')'], "");
    let start = ours
        .iter()
        .position(|l| l == "GXT_DECIMAL1=AV_R;")
        .expect("the copy-in is written");
    assert_eq!(ours[start], sdk[1]);
    assert_eq!(ours[start + 1], sdk[2]);
    assert_eq!(loose(&ours[start + 2]), loose(&sdk[3]), "{code}");
}

// --- Module namespaces ---------------------------------------------------------------------------

/// A Procedure held by a module is declared in that module's namespace, as the SDK writes it
/// (`general/ui/listprograms.cs`: `namespace GeneXus.Programs.general.ui`), and a call to it is
/// namespace-qualified (`new GeneXus.Programs.general.ui.sidebaritemsdp(context )` in
/// `masterunanimosidebar.cs`), so a caller in any namespace binds to it.
#[test]
fn a_procedure_held_by_a_module_is_declared_and_called_in_its_namespace() {
    let mut held = KbObject::new(
        ObjectId::new("poi97435900").unwrap(),
        "POI97435900",
        ObjectKind::Procedure,
    );
    held.module_path = vec!["General".into(), "UI".into()];
    held.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: POI_CALLEE_VARS.into(),
    });
    held.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: "Parm(in:&A, out:&B, inout:&C, out:&D, inout:&E);".into(),
    });
    let class = ProcedureClass::from_kb_object(&held).expect("the callee parses");
    assert_eq!(class.namespace, "GeneXus.Programs.general.ui");
    assert!(
        class
            .emit_checked()
            .expect("the callee emits")
            .contains("namespace GeneXus.Programs.general.ui {")
    );
    let target = class.call_target();
    assert_eq!(target.namespace, "GeneXus.Programs.general.ui");

    let mut table = CallSignatures::new();
    table.insert(target).expect("unique");
    let mut caller = KbObject::new(
        ObjectId::new("caller_proc").unwrap(),
        "CallerProc",
        ObjectKind::Procedure,
    );
    caller.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "POI97435900(&A, &B, &C, &D, &E)\n".into(),
    });
    caller.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: CALLER_VARS.into(),
    });
    let code = ProcedureClass::from_kb_object(&caller)
        .expect("the caller parses")
        .with_usings(vec!["GeneXus.Programs.genexusunanimo".into()])
        .with_calls(Arc::new(table))
        .emit_checked()
        .expect("the call has its SDK form");
    assert!(code.contains("namespace GeneXus.Programs {"), "{code}");
    assert!(
        code.contains("new GeneXus.Programs.general.ui.poi97435900(context).execute(AV_A, out AV_B, ref AV_C, out AV_D, ref AV_E);"),
        "{code}"
    );
    assert!(
        code.contains("using GeneXus.Programs.genexusunanimo;\nnamespace GeneXus.Programs {"),
        "{code}"
    );
}

#[test]
fn a_procedure_at_the_root_module_is_called_by_its_bare_class_name() {
    let target = CallTarget {
        class_name: "pob".into(),
        namespace: "GeneXus.Programs".into(),
        params: Vec::new(),
    };
    assert_eq!(target.qualified_class(), "pob");
}
