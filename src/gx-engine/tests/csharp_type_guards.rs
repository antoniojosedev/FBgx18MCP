//! A C# loop or assignment whose types have no conversion this build evidences is refused by name. It is
//! never written, because csc would reject it (CS0019, CS0023, CS0029).
//!
//! The real-object tests read KBTeste's `FRC12224800` (a loop over a Character), `FRQ12224800` (a loop over
//! a Date) and `RustDisplaySyntaxProbe` (a Numeric assigned to a Varchar). Their parts are fixtures in
//! `tests/fixtures/guards/`, read from the KB by `kb-read`, not typed by hand.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::ProcedureClass,
};

const FRC_SOURCE: &str = include_str!("fixtures/guards/FRC12224800.SOURCE.txt");
const FRC_VARIABLES: &str = include_str!("fixtures/guards/FRC12224800.VARIABLES.txt");
const FRQ_SOURCE: &str = include_str!("fixtures/guards/FRQ12224800.SOURCE.txt");
const FRQ_VARIABLES: &str = include_str!("fixtures/guards/FRQ12224800.VARIABLES.txt");
const RDS_SOURCE: &str = include_str!("fixtures/guards/RustDisplaySyntaxProbe.SOURCE.txt");
const RDS_VARIABLES: &str = include_str!("fixtures/guards/RustDisplaySyntaxProbe.VARIABLES.txt");

const VARIABLES: &str = r#"{
    "N": {"kind": "numeric", "precision": 5, "scale": 0},
    "M": {"kind": "numeric", "precision": 5, "scale": 2},
    "C": {"kind": "character", "length": 10},
    "V": {"kind": "varchar", "length": 40},
    "D": {"kind": "date"},
    "E": {"kind": "date"},
    "T": {"kind": "date_time"},
    "B": {"kind": "boolean"}
}"#;

fn procedure(source: &str, variables: &str) -> ProcedureClass {
    let mut object = KbObject::new(
        ObjectId::new("guard_proc").unwrap(),
        "GuardProc",
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
    ProcedureClass::from_kb_object(&object).expect("the procedure parses")
}

/// The emitted C# of a procedure, or its named refusals.
fn emit(source: &str, variables: &str) -> Result<String, String> {
    procedure(source, variables).emit_checked()
}

fn refusal(source: &str, variables: &str) -> String {
    emit(source, variables).expect_err("the statement has no conversion this build evidences")
}

// ---- real objects ---------------------------------------------------------------------------------

#[test]
fn a_loop_over_a_character_iterator_is_refused_by_name_not_written() {
    let error = refusal(FRC_SOURCE, FRC_VARIABLES);
    assert!(
        error.contains("For 'I': the iterator is a character variable"),
        "{error}"
    );
}

#[test]
fn a_loop_over_a_date_iterator_is_refused_by_name_not_written() {
    let error = refusal(FRQ_SOURCE, FRQ_VARIABLES);
    assert!(
        error.contains("For 'I': the iterator is a date variable"),
        "{error}"
    );
}

#[test]
fn a_numeric_assigned_to_a_varchar_is_refused_by_name_not_written() {
    let error = refusal(RDS_SOURCE, RDS_VARIABLES);
    assert!(
        error.contains(
            "assignment to 'T40': a numeric value cannot be assigned to a varchar variable"
        ),
        "{error}"
    );
}

// ---- the loop form that stays -------------------------------------------------------------------

#[test]
fn a_loop_over_a_numeric_iterator_is_still_written_as_a_c_for_loop() {
    let code = emit("For &N = 1 To 3\n  msg(\"x\", status)\nEndFor\n", VARIABLES)
        .expect("a numeric iterator has the C# form");
    assert!(
        code.contains("for ( AV_N = 1; AV_N <= 3; AV_N++ )"),
        "{code}"
    );
}

// ---- assignments across families are refused; same-family assignments are kept -----------------

#[test]
fn a_text_literal_assigned_to_a_numeric_is_refused() {
    let error = refusal("&N = \"abc\"\n", VARIABLES);
    assert!(
        error.contains(
            "assignment to 'N': a text literal value cannot be assigned to a numeric variable"
        ),
        "{error}"
    );
}

#[test]
fn a_numeric_literal_assigned_to_a_character_is_refused() {
    let error = refusal("&C = 42\n", VARIABLES);
    assert!(
        error.contains(
            "assignment to 'C': a numeric literal value cannot be assigned to a character variable"
        ),
        "{error}"
    );
}

#[test]
fn a_numeric_variable_assigned_to_a_date_is_refused() {
    let error = refusal("&D = &N\n", VARIABLES);
    assert!(
        error.contains("assignment to 'D': a numeric value cannot be assigned to a date variable"),
        "{error}"
    );
}

#[test]
fn a_date_variable_assigned_to_a_boolean_is_refused() {
    let error = refusal("&B = &D\n", VARIABLES);
    assert!(
        error.contains("assignment to 'B': a date value cannot be assigned to a boolean variable"),
        "{error}"
    );
}

#[test]
fn a_date_assigned_to_a_date_time_is_one_family_and_is_written() {
    // Both are `DateTime` in C#; this assignment compiles and must keep its form.
    let code = emit("&T = &D\n", VARIABLES).expect("date and date-time are one family");
    assert!(code.contains("AV_T = AV_D;"), "{code}");
}

#[test]
fn a_character_assigned_to_a_varchar_and_a_numeric_to_a_numeric_are_written() {
    let code = emit("&V = &C\n&N = &N\n", VARIABLES).expect("same family");
    assert!(code.contains("AV_V = AV_C;"), "{code}");
    // N is Numeric(5,0), which maps to `int` (short is for precision 4 and under).
    assert!(code.contains("AV_N = (int)(AV_N);"), "{code}");
}

#[test]
fn an_expression_of_unproven_type_is_not_refused_here() {
    // A call or an arithmetic expression has no proven type in this check, so it is left to the
    // emitter as before (its own refusals still apply). Here the target is numeric and the value is a sum.
    let code = emit("&N = &N + 1\n", VARIABLES).expect("arithmetic is not checked by this guard");
    assert!(code.contains("AV_N = (int)((AV_N + 1));"), "{code}");
}
