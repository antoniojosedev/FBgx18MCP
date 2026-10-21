//! Shapes the C# emitter used to refuse, now written as the SDK writes them. Each test quotes the
//! SDK line it reproduces (NetModel under `C:\KBs\KBTeste\NetModel\Web`). The sources are SYNTHETIC: they
//! use the same constructs as the stored objects named in each comment, but they are not copies of
//! stored rows. Each shape has one SDK vector, so this is evidence for that vector, not for the family.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::ProcedureClass,
};

fn procedure(source: &str, variables: &str) -> String {
    let mut object = KbObject::new(
        ObjectId::new("probe_proc").unwrap(),
        "ProbeProc",
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
        .expect("the procedure parses")
        .emit_checked()
        .expect("the procedure is emitted")
}

#[test]
fn a_for_loop_with_step_zero_is_written_with_the_sdk_condition_and_increment() {
    // SDK `afrz03636400.cs` (FRZ03636400, lines 72 and 77): `while ( AV8I <= 3 )` with
    // `AV8I = (int)(AV8I+0);`. The emitter writes the same test and increment as a for loop.
    let code = procedure(
        "For &I = 1 To 3 Step 0\n  msg(\"x\", status)\nEndFor\n",
        r#"{ "I": { "kind": "numeric", "precision": 9, "scale": 0 } }"#,
    );
    assert!(code.contains("<= 3;"), "the SDK test is `<= 3`:\n{code}");
    assert!(
        code.contains("+= 0 )"),
        "the SDK increment is `+0`:\n{code}"
    );
}

#[test]
fn a_trimmed_text_length_as_a_string_is_written_as_the_sdk_writes_it() {
    // SDK `arusttextfuncsemantics.cs` line 71: `StringUtil.Trim( StringUtil.Str( (decimal)(StringUtil.Len(
    // AV8C4)), 10, 0))`. Literal spacing differs from the SDK; the call nest and the arguments must match.
    let code = procedure(
        "&C4 = \"a b\"\nmsg(\"n=\" + &C4.Length().ToString().Trim(), status)\n",
        r#"{ "C4": { "kind": "character", "length": 4 } }"#,
    );
    assert!(
        code.contains("StringUtil.Trim(StringUtil.Str((decimal)(StringUtil.Len(AV_C4)), 10, 0))"),
        "the SDK nest is Trim(Str(decimal(Len(x)), 10, 0)):\n{code}"
    );
}

#[test]
fn year_as_a_function_is_written_as_the_datetimeutil_call() {
    // SDK `aodm42412500.cs` line 72: `StringUtil.Trim( StringUtil.Str( (decimal)(DateTimeUtil.Year( AV8Stamp)),
    // 10, 0))` is the SDK form of `Trim(Str(Year(&Stamp)))`.
    let code = procedure(
        "&Stamp = YMDHMStoT(2001, 1, 2, 3, 4, 5)\nmsg(\"y=\" + Trim(Str(Year(&Stamp))), status)\n",
        r#"{ "Stamp": { "kind": "date_time" } }"#,
    );
    assert!(
        code.contains("DateTimeUtil.Year(AV_Stamp)"),
        "Year(x) is DateTimeUtil.Year:\n{code}"
    );
}

#[test]
fn a_year_method_as_a_trimmed_string_is_written_as_the_sdk_writes_it() {
    // SDK `arustparmmainprobe.cs` line 83: `"after_d_year="+StringUtil.Trim( StringUtil.Str( (decimal)(
    // DateTimeUtil.Year( AV14D)), 10, 0))` is the SDK form of `"after_d_year=" + &D.Year().ToString().Trim()`.
    let code = procedure(
        "&D = YMDtoD(2020, 1, 2)\nmsg(\"after_d_year=\" + &D.Year().ToString().Trim(), status)\n",
        r#"{ "D": { "kind": "date" } }"#,
    );
    assert!(
        code.contains("StringUtil.Trim(StringUtil.Str((decimal)(DateTimeUtil.Year(AV_D)), 10, 0))"),
        "Year() as a ToString operand is the SDK's Str of decimal(Year):\n{code}"
    );
}

#[test]
fn a_date_to_string_is_written_with_the_sdk_date_formatter() {
    // SDK `arustdisplayprobe.cs` line 101: `"ds_tostring=["+context.localUtil.DToC( AV12D, 1, "/")+"]"`.
    let code = procedure(
        "&D = YMDtoD(2020, 1, 2)\nmsg(\"d=\" + &D.ToString(), status)\n",
        r#"{ "D": { "kind": "date" } }"#,
    );
    assert!(
        code.contains("context.localUtil.DToC(AV_D, 1, \"/\")"),
        "a date ToString is DToC with the SDK arguments:\n{code}"
    );
}

#[test]
fn a_datetime_to_string_is_written_with_the_sdk_time_formatter() {
    // SDK `arustdisplayprobe.cs` line 119: `context.localUtil.TToC( AV13DT, 8, 5, 1, 2, "/", ":", " ")`.
    let code = procedure(
        "&DT = YMDHMStoT(2020, 1, 2, 3, 4, 5)\nmsg(\"dt=\" + &DT.ToString(), status)\n",
        r#"{ "DT": { "kind": "date_time" } }"#,
    );
    assert!(
        code.contains("context.localUtil.TToC(AV_DT, 8, 5, 1, 2, \"/\", \":\", \" \")"),
        "a datetime ToString is TToC with the SDK arguments:\n{code}"
    );
}

#[test]
fn strreplace_is_written_as_the_sdk_string_replace() {
    // SDK `arusttextfuncprobe.cs` line 99: `StringUtil.StringReplace( "banana", "na", "X")`.
    let code = procedure(
        "msg(\"r=[\" + StrReplace(\"banana\", \"na\", \"X\") + \"]\", status)\n",
        "{}",
    );
    assert!(
        code.contains("StringUtil.StringReplace(\"banana\", \"na\", \"X\")"),
        "StrReplace(a, b, c) is StringUtil.StringReplace:\n{code}"
    );
}

#[test]
fn a_datetime_is_empty_is_written_as_the_sdk_minvalue_comparison() {
    // SDK `aodm42412500.cs` line 71 (and `odh42412500.cs` line 64): `(DateTime.MinValue==AV8Stamp)`.
    let code = procedure(
        "&DT = YMDHMStoT(2020, 1, 2, 3, 4, 5)\nmsg(\"empty=\" + &DT.IsEmpty(), status)\n",
        r#"{ "DT": { "kind": "date_time" } }"#,
    );
    assert!(
        code.contains("(DateTime.MinValue==AV_DT)"),
        "IsEmpty of a datetime is the SDK comparison:\n{code}"
    );
    assert!(
        !code.contains("DateTimeUtil.ISEMPTY"),
        "the engine must not write a DateTimeUtil.ISEMPTY that the SDK does not have:\n{code}"
    );
}

#[test]
fn second_of_a_datetime_is_written_as_the_sdk_datetimeutil_second() {
    // SDK `arustdisplayprobe.cs` line 125: `DateTimeUtil.Second( AV13DT)`. The method name is upper-cased by the
    // parser, so `SECOND` must map to the same SDK method as `SEC`.
    let code = procedure(
        "&DT = YMDHMStoT(2024, 3, 7, 14, 5, 9)\nmsg(\"sec=[\" + &DT.Second() + \"]\", status)\n",
        r#"{ "DT": { "kind": "date_time" } }"#,
    );
    assert!(
        code.contains("DateTimeUtil.Second(AV_DT)"),
        "Second() is DateTimeUtil.Second:\n{code}"
    );
    assert!(
        !code.contains("DateTimeUtil.SECOND"),
        "the engine must not write DateTimeUtil.SECOND:\n{code}"
    );
}

#[test]
fn a_date_plus_an_integer_is_written_as_the_sdk_dadd() {
    // SDK `arustdisplayprobe.cs` line 124: `AV12D = DateTimeUtil.DAdd(AV12D,+((int)(1)));`.
    let code = procedure(
        "&D = YMDtoD(2024, 3, 7)\n&D = &D + 1\nmsg(\"d=\" + &D.ToString(), status)\n",
        r#"{ "D": { "kind": "date" } }"#,
    );
    assert!(
        code.contains("DateTimeUtil.DAdd(AV_D, +((int)(1)))"),
        "a date plus an integer is DAdd:\n{code}"
    );
}

#[test]
fn a_numeric_message_is_refused_because_status_message_takes_a_string() {
    // `StatusMessage` takes a string, and the SDK's numeric message form depends on the expression's type
    // (arustdisplayprobe.cs line 77). A bare numeric message therefore stays refused, with the reason named.
    let mut object = gx_engine::catalog::KbObject::new(
        gx_engine::catalog::ObjectId::new("probe_proc").unwrap(),
        "ProbeProc",
        gx_engine::catalog::ObjectKind::Procedure,
    );
    object.source_parts.push(gx_engine::catalog::SourcePart {
        name: "SOURCE".into(),
        source: "&N = 42\nmsg(&N, status)\n".into(),
    });
    object.source_parts.push(gx_engine::catalog::SourcePart {
        name: "VARIABLES".into(),
        source: r#"{ "N": { "kind": "numeric", "precision": 5, "scale": 0 } }"#.into(),
    });
    let error = gx_engine::csharp::ProcedureClass::from_kb_object(&object)
        .expect("the procedure parses")
        .emit_checked()
        .unwrap_err();
    assert!(
        error.contains("message of a numeric value"),
        "the refusal names the numeric message:\n{error}"
    );
}

#[test]
fn a_date_in_a_text_concatenation_is_written_as_the_concatenation_not_as_arithmetic() {
    // SDK `arustdisplayprobe.cs` line 109: `"dv_min=["+AV12D+"]"`. A date joined to text with `+` is a concatenation
    // and must not reach the date-arithmetic refusal (this regressed on RustDisplayProbe line 30).
    let code = procedure(
        "&D = YMDtoD(2024, 3, 7)\nmsg(\"dv=[\" + &D + \"]\", status)\n",
        r#"{ "D": { "kind": "date" } }"#,
    );
    assert!(
        code.contains("AV_D") && !code.contains("DAdd"),
        "a date in a text concatenation is written as the concatenation:\n{code}"
    );
}

#[test]
fn the_text_of_is_empty_of_a_date_is_written_with_booltostr() {
    // SDK `rustouttypesprobe.cs` line 87: `"entry_d_empty="+StringUtil.BoolToStr( (DateTime.MinValue==AV10D))`.
    let code = procedure(
        "&D = YMDtoD(0, 0, 0)\nmsg(\"e=\" + &D.IsEmpty().ToString(), status)\n",
        r#"{ "D": { "kind": "date" } }"#,
    );
    assert!(
        code.contains("StringUtil.BoolToStr((DateTime.MinValue==AV_D))"),
        "the text of IsEmpty is BoolToStr:\n{code}"
    );
}
