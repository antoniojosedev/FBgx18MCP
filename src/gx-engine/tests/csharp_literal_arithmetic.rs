//! Division between integer literals is decimal in the generated C#. GeneXus division is decimal, so `7 / 3`
//! is 2.33333, not C# integer division's 2. The SDK writes the divisor as decimal (`7/ (decimal)(3)`,
//! arustdisplayprobe2.cs line 89), and a bare numeric message of it is written with
//! `StringUtil.LTrimStr(..., 15, 5)` (the same line). Other literal arithmetic keeps the SDK's form.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::ProcedureClass,
};

fn procedure(source: &str) -> String {
    let mut object = KbObject::new(
        ObjectId::new("literal_arithmetic").unwrap(),
        "LiteralArithmetic",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".to_string(),
        source: source.to_string(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".to_string(),
        source: r#"{ "Third": { "kind": "numeric", "precision": 15, "scale": 5 } }"#.to_string(),
    });
    ProcedureClass::from_kb_object(&object)
        .expect("from kb object")
        .emit()
}

#[test]
fn an_assigned_integer_literal_division_is_decimal() {
    let code = procedure("&Third = 7 / 3\n");
    assert!(
        code.contains("(7 / (decimal)(3))"),
        "the divisor must be decimal:\n{code}"
    );
}

#[test]
fn a_bare_numeric_message_of_literal_division_uses_the_sdk_precision_and_is_decimal() {
    let code = procedure("msg(7 / 3, status)\n");
    assert!(
        code.contains("StringUtil.LTrimStr( (7 / (decimal)(3)), 15, 5)"),
        "the message must be the decimal quotient with the SDK's 15,5:\n{code}"
    );
}

#[test]
fn a_bare_numeric_message_of_literal_product_keeps_the_sdk_form_and_uses_the_digit_count() {
    let code = procedure("msg(21 * 2, status)\n");
    assert!(
        code.contains("StringUtil.LTrimStr( (21 * 2), 2, 0)"),
        "21 has two digits, so the message precision is 2 with scale 0:\n{code}"
    );
}

#[test]
fn a_literal_sum_keeps_the_sdk_form_and_uses_the_widest_literal_digit_count() {
    let code = procedure("msg(1234567890 + 5, status)\n");
    assert!(
        code.contains("StringUtil.LTrimStr( (1234567890 + 5), 10, 0)"),
        "the precision is the widest literal's digits:\n{code}"
    );
}
