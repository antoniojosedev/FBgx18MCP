//! `/* ... */` block comments in a Procedure body are stripped before lexing, as the rules,
//! DataProvider and DataSelector parsers already do. A comment delimiter inside a string literal is
//! text, and an unterminated comment is refused by name, not read to the end of the source.

use gx_engine::frontend::parse_body;

/// The real `SOURCE` part of the Procedure `IsAuthorized` (GX18 `KBTeste`), read with `kb-read` in full.
/// Its only statement is `&Authorized = True`; the rest is a block comment around inactive code.
const IS_AUTHORIZED_SOURCE: &str = "&Authorized = True\n\n/*\nLoadContext(&Context)\nIf (&GxObject = 'ViewInvoices' and &Context.UserLevel = 0)\n        &Authorized = False\nEndif\n*/\n";

#[test]
fn a_block_comment_is_removed_and_the_statements_around_it_parse() {
    let body = parse_body("&a = 1 /* note */\n&b = 2").expect("a block comment is stripped");
    assert_eq!(body.len(), 2, "{body:?}");
}

#[test]
fn a_block_comment_closes_at_the_first_star_slash_and_does_not_nest() {
    // `/* a /* b */` is one comment; the `*/` closes it. The `c = 3` after it is code.
    let body = parse_body("/* a /* b */\n&c = 3").expect("the first */ closes the comment");
    assert_eq!(body.len(), 1, "{body:?}");
}

#[test]
fn the_first_star_slash_closes_a_comment_even_when_a_later_one_follows() {
    // Two closers: the first one ends the comment, so `&c = 3` is code and `&d = 4` is code too.
    // A comment that took the last closer would hide both statements.
    let body =
        parse_body("/* a */\n&c = 3\n/* b */\n&d = 4").expect("each comment closes at its own */");
    assert_eq!(body.len(), 2, "{body:?}");
}

#[test]
fn a_block_comment_delimiter_inside_a_string_literal_is_text() {
    let body = parse_body("&s = '/* not a comment */'").expect("a string literal keeps its text");
    assert_eq!(body.len(), 1, "{body:?}");
    let body = parse_body("&s = \"/* still text\"\n&t = 1")
        .expect("a double-quoted literal keeps its text");
    assert_eq!(body.len(), 2, "{body:?}");
}

#[test]
fn an_unterminated_block_comment_is_refused_by_name_with_a_location() {
    let error = parse_body("&a = 1\n/* never closed\n&b = 2").unwrap_err();
    assert!(error.contains("unclosed block comment"), "{error}");
    assert!(error.contains("line 2"), "{error}");
}

#[test]
fn the_real_is_authorized_body_parses_to_its_one_statement() {
    let body = parse_body(IS_AUTHORIZED_SOURCE).expect("IsAuthorized's body parses");
    assert_eq!(body.len(), 1, "only `&Authorized = True` is code: {body:?}");
}

#[test]
fn a_line_comment_still_ends_at_the_line_break() {
    let body = parse_body("// a note\n&a = 1").expect("a line comment ends at the line break");
    assert_eq!(body.len(), 1, "{body:?}");
}

#[test]
fn a_block_comment_that_spans_lines_keeps_the_later_error_on_its_real_line() {
    // Two line breaks are inside the comment; the bad token is on line 4.
    let error = parse_body("/* a\nb\nc */\n&x = &").unwrap_err();
    assert!(error.contains("line 4"), "{error}");
}
