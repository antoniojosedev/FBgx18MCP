//! Regression tests for the value of a C# string literal in a generated file.
//!
//! The SDK writes its C# files without a byte order mark, and the compiler reads a file as UTF-8 only
//! when the WHOLE file is valid UTF-8; otherwise it reads the whole file as cp1252. A scratch `csc` run
//! (C:/tmp/t15/encexp) showed it: a file whose literal holds E1 E9 compiles to U+00E1 U+00E9; a file
//! whose literal holds C2 A0 61 compiles to U+00A0 a; a file with an invalid literal also reads its
//! valid C3 A9 literal as U+00C3 U+00A9. The emitter writes each stored character as a `\u` escape, and
//! `resolve_sdk_literals` settles the value for the file. The literal bodies below are escape text, as
//! the emitter writes it.

use gx_engine::csharp::literals::resolve_sdk_literals;

/// A generated file with one string literal whose body is `body` (escape text).
fn file(body: &str) -> String {
    format!("public class A {{\n    string s = \"{body}\";\n}}\n")
}

#[test]
fn a_valid_utf8_file_reads_its_literal_as_utf8() {
    // RTM41852400: stored characters U+00C2 U+00A0 around an `a` are the bytes C2 A0 61 C2 A0, valid
    // UTF-8, so the compiled literal holds one U+00A0 per pair.
    let input = file(r"\u00C2\u00A0a\u00C2\u00A0");
    assert_eq!(resolve_sdk_literals(&input), file(r"\u00A0a\u00A0"));
}

#[test]
fn an_invalid_utf8_file_reads_every_literal_as_cp1252() {
    // RustTextFuncProbe: the stored characters U+00E1 U+00E9 are the bytes E1 E9, which are not valid
    // UTF-8. The file is read as cp1252, so the stored characters are the value: a-acute, e-acute.
    let input = file(r"\u00E1\u00E9");
    assert_eq!(resolve_sdk_literals(&input), input);
}

#[test]
fn a_valid_utf8_literal_in_a_file_with_an_invalid_one_keeps_its_stored_characters() {
    // The file holds E1 (invalid) and C3 A9 (valid). The whole file is cp1252, so C3 A9 is read as
    // U+00C3 U+00A9, not as e-acute: the per-literal reading would give the wrong value here.
    let input =
        "public class A {\n    string a = \"\\u00E1\";\n    string b = \"\\u00C3\\u00A9\";\n}\n";
    assert_eq!(resolve_sdk_literals(input), input);
}

#[test]
fn a_character_outside_cp1252_is_kept_as_its_code_point_beside_a_utf8_literal() {
    // U+4E2D has no cp1252 byte, so its literal keeps its code point whatever the file's reading. The
    // other literal is valid UTF-8 and is decoded.
    let input =
        "public class A {\n    string a = \"\\u4E2D\";\n    string b = \"\\u00C2\\u00A0\";\n}\n";
    let expected =
        "public class A {\n    string a = \"\\u4E2D\";\n    string b = \"\\u00A0\";\n}\n";
    assert_eq!(resolve_sdk_literals(input), expected);
}

#[test]
fn a_quote_in_a_comment_or_a_character_literal_does_not_open_a_literal() {
    // A commented-out literal is code: its quotes do not open a literal, so its text is not resolved.
    // The character literal `'"'` is code too. The string after them is a literal and is valid UTF-8.
    let input =
        "// old: \"\\u00C2\\u00A0\"\n    char c = '\"';\n    string s = \"\\u00C2\\u00A0\";\n";
    let expected = "// old: \"\\u00C2\\u00A0\"\n    char c = '\"';\n    string s = \"\\u00A0\";\n";
    assert_eq!(resolve_sdk_literals(input), expected);
}

#[test]
fn escape_text_inside_a_utf8_literal_is_kept_as_written() {
    // A backslash and a quote are escape text, not stored characters; they stay as written.
    let input = file(r#"a\\b\u00C2\u00A0\""#);
    assert_eq!(resolve_sdk_literals(&input), file(r#"a\\b\u00A0\""#));
}

#[test]
fn a_file_without_a_stored_non_ascii_character_is_returned_unchanged() {
    let input = file(r#"plain \\ text \" and \n newline"#);
    assert_eq!(resolve_sdk_literals(&input), input);
}
