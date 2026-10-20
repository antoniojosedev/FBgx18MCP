//! The value of a C# string literal, as the SDK's compiled file holds it.
//!
//! Stored text is a sequence of characters, and each stands for the cp1252 byte the SDK wrote to its
//! C# file. The SDK writes these files without a byte order mark, and the compiler reads a file as
//! UTF-8 only when the WHOLE file is valid UTF-8; otherwise it reads the whole file as cp1252. So the
//! value of a literal depends on its file: one byte that is not valid UTF-8 makes every literal in the
//! file cp1252, including the literals whose own bytes are valid UTF-8.
//!
//! Evidence: a scratch `csc` run (Roslyn 4.11, SDK 8.0.426; files in C:/tmp/t15/encexp). A file whose
//! literal holds bytes E1 E9 compiles to U+00E1 U+00E9. A file whose literal holds bytes C2 A0 61 (valid
//! UTF-8) compiles to U+00A0 and `a`. A file with E1 in one literal and C3 A9 in another compiles the
//! C3 A9 literal as U+00C3 U+00A9: the whole file is read as cp1252.
//!
//! The emitter writes every stored character as an escape of that character, so a generated file is
//! ASCII. `resolve_sdk_literals` runs on the text the writer receives and gives each literal the value
//! its file's compiled literal holds.

use std::fmt::Write;

/// The cp1252 byte of a non-ASCII character, or `None` when cp1252 has no byte for it.
fn cp1252_byte(ch: char) -> Option<u8> {
    let code = ch as u32;
    if (0xA0..=0xFF).contains(&code) {
        return Some(code as u8);
    }
    // The 0x80..=0x9F block: cp1252 maps these to typographic characters, not to controls.
    const HIGH: [(char, u8); 27] = [
        ('\u{20AC}', 0x80),
        ('\u{201A}', 0x82),
        ('\u{0192}', 0x83),
        ('\u{201E}', 0x84),
        ('\u{2026}', 0x85),
        ('\u{2020}', 0x86),
        ('\u{2021}', 0x87),
        ('\u{02C6}', 0x88),
        ('\u{2030}', 0x89),
        ('\u{0160}', 0x8A),
        ('\u{2039}', 0x8B),
        ('\u{0152}', 0x8C),
        ('\u{017D}', 0x8E),
        ('\u{2018}', 0x91),
        ('\u{2019}', 0x92),
        ('\u{201C}', 0x93),
        ('\u{201D}', 0x94),
        ('\u{2022}', 0x95),
        ('\u{2013}', 0x96),
        ('\u{2014}', 0x97),
        ('\u{02DC}', 0x98),
        ('\u{2122}', 0x99),
        ('\u{0161}', 0x9A),
        ('\u{203A}', 0x9B),
        ('\u{0153}', 0x9C),
        ('\u{017E}', 0x9E),
        ('\u{0178}', 0x9F),
    ];
    HIGH.iter().find(|(c, _)| *c == ch).map(|(_, b)| *b)
}

/// Whether cp1252 has a byte for a character (the SDK writes such a character raw).
pub fn has_cp1252_byte(ch: char) -> bool {
    byte_of(ch).is_some()
}

/// Returns the byte a stored character stands for in the SDK's file: an ASCII character is its own byte.
fn byte_of(ch: char) -> Option<u8> {
    if ch.is_ascii() {
        Some(ch as u8)
    } else {
        cp1252_byte(ch)
    }
}

/// The bytes of a generated C# file as the SDK writes them: CRLF line ends, and each character as its
/// cp1252 byte (the SDK writes its files in cp1252 without a byte order mark; arusttextfuncprobe.cs holds
/// `"\xE1\xE9..."` as raw bytes). A character cp1252 has no byte for is written as UTF-8.
pub fn sdk_file_bytes(text: &str) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(text.len() + text.len() / 32);
    let mut previous = '\0';
    for ch in text.chars() {
        if ch == '\n' && previous != '\r' {
            bytes.push(b'\r');
        }
        match byte_of(ch) {
            Some(byte) => bytes.push(byte),
            None => {
                let mut buffer = [0u8; 4];
                bytes.extend_from_slice(ch.encode_utf8(&mut buffer).as_bytes());
            }
        }
        previous = ch;
    }
    bytes
}

/// One piece of a C# string literal, as the emitter wrote it.
#[derive(Debug, Clone, PartialEq)]
enum Piece {
    /// Text kept as written: an ASCII character, or an escape such as `\\`, `\"` or `\n`.
    Written(String),
    /// A stored character: a `\u` or `\U` escape of it, or the character itself when written raw.
    Stored(char),
}

/// A part of a generated file: source outside string literals, or the pieces of one literal.
#[derive(Debug, Clone, PartialEq)]
enum Part {
    Code(String),
    Literal(Vec<Piece>),
}

/// Resolves the value of every string literal in a generated C# file to the value the SDK's compiled
/// file holds (see the module documentation). The emitter writes a stored character as an escape of
/// that character; this decides, for the whole file, whether those characters are read as UTF-8 bytes
/// or as cp1252 characters. A file without a stored non-ASCII character is returned unchanged.
pub fn resolve_sdk_literals(source: &str) -> String {
    let parts = split(source);
    let has_non_ascii = parts.iter().any(|part| match part {
        Part::Literal(pieces) => pieces.iter().any(is_non_ascii_stored),
        Part::Code(_) => false,
    });
    if !has_non_ascii {
        return source.to_string();
    }
    // The compiler reads the file as UTF-8 only when every byte of the file is valid UTF-8. The
    // literal bytes are the only non-ASCII bytes a generated file holds, so the check is over them.
    let file_is_utf8 = parts.iter().all(|part| match part {
        Part::Literal(pieces) if is_byte_literal(pieces) => {
            std::str::from_utf8(&literal_bytes(pieces)).is_ok()
        }
        _ => true,
    });
    let mut out = String::with_capacity(source.len());
    for part in &parts {
        match part {
            Part::Code(text) => out.push_str(text),
            Part::Literal(pieces) => {
                out.push('"');
                if file_is_utf8 && is_byte_literal(pieces) {
                    render_decoded(pieces, &mut out);
                } else {
                    render_stored(pieces, &mut out);
                }
                out.push('"');
            }
        }
    }
    out
}

fn is_non_ascii_stored(piece: &Piece) -> bool {
    matches!(piece, Piece::Stored(ch) if !ch.is_ascii())
}

/// A byte literal holds a non-ASCII character and only characters that cp1252 has a byte for. Any other
/// literal keeps its stored characters as they are, as the emitter always did for them.
fn is_byte_literal(pieces: &[Piece]) -> bool {
    pieces.iter().any(is_non_ascii_stored)
        && pieces.iter().all(|piece| match piece {
            Piece::Written(_) => true,
            Piece::Stored(ch) => byte_of(*ch).is_some(),
        })
}

/// The bytes a byte literal occupies in the file: each stored character as its cp1252 byte, and the
/// escape text as written.
fn literal_bytes(pieces: &[Piece]) -> Vec<u8> {
    let mut bytes = Vec::new();
    for piece in pieces {
        match piece {
            Piece::Written(text) => bytes.extend_from_slice(text.as_bytes()),
            Piece::Stored(ch) => {
                if let Some(byte) = byte_of(*ch) {
                    bytes.push(byte);
                }
            }
        }
    }
    bytes
}

/// Writes a byte literal as the UTF-8 text its bytes decode to. A run of stored characters is one
/// multi-byte sequence or more; the escape text between runs is kept as written.
fn render_decoded(pieces: &[Piece], out: &mut String) {
    let mut run: Vec<(char, u8)> = Vec::new();
    for piece in pieces {
        match piece {
            Piece::Stored(ch) => run.push((*ch, byte_of(*ch).unwrap_or(b'?'))),
            Piece::Written(text) => {
                flush_run(&mut run, out);
                out.push_str(text);
            }
        }
    }
    flush_run(&mut run, out);
}

fn flush_run(run: &mut Vec<(char, u8)>, out: &mut String) {
    if run.is_empty() {
        return;
    }
    let bytes: Vec<u8> = run.iter().map(|(_, byte)| *byte).collect();
    match std::str::from_utf8(&bytes) {
        Ok(text) => text.chars().for_each(|ch| push_escape(ch, out)),
        // Unreachable for a file that passed the whole-file check; the stored characters are kept.
        Err(_) => run.iter().for_each(|(ch, _)| push_escape(*ch, out)),
    }
    run.clear();
}

/// Writes a literal with its stored characters as they are (the cp1252 reading of the file).
fn render_stored(pieces: &[Piece], out: &mut String) {
    for piece in pieces {
        match piece {
            Piece::Written(text) => out.push_str(text),
            Piece::Stored(ch) => push_escape(*ch, out),
        }
    }
}

/// Writes one character of a literal's value: printable ASCII as itself, anything else as a `\u` or
/// `\U` escape, so the file stays ASCII.
fn push_escape(ch: char, out: &mut String) {
    if ch.is_ascii() && !ch.is_control() && ch != '\\' && ch != '"' {
        out.push(ch);
    } else if (ch as u32) <= 0xFFFF {
        let _ = write!(out, "\\u{:04X}", ch as u32);
    } else {
        let _ = write!(out, "\\U{:08X}", ch as u32);
    }
}

/// Splits a C# file into code and string literals. Quotes inside line comments, block comments and
/// character literals are code, not the start of a literal.
fn split(source: &str) -> Vec<Part> {
    let chars: Vec<char> = source.chars().collect();
    let mut parts = Vec::new();
    let mut code = String::new();
    let mut i = 0;
    while i < chars.len() {
        let ch = chars[i];
        if ch == '/' && chars.get(i + 1) == Some(&'/') {
            while i < chars.len() && chars[i] != '\n' {
                code.push(chars[i]);
                i += 1;
            }
        } else if ch == '/' && chars.get(i + 1) == Some(&'*') {
            code.push_str("/*");
            i += 2;
            while i < chars.len() && !(chars[i] == '*' && chars.get(i + 1) == Some(&'/')) {
                code.push(chars[i]);
                i += 1;
            }
            if i < chars.len() {
                code.push_str("*/");
                i += 2;
            }
        } else if ch == '\'' {
            code.push(ch);
            i += 1;
            while i < chars.len() && chars[i] != '\'' && chars[i] != '\n' {
                if chars[i] == '\\' && i + 1 < chars.len() {
                    code.push(chars[i]);
                    i += 1;
                }
                code.push(chars[i]);
                i += 1;
            }
            if i < chars.len() && chars[i] == '\'' {
                code.push('\'');
                i += 1;
            }
        } else if ch == '"' {
            match literal_at(&chars, i + 1) {
                Some((pieces, next)) => {
                    if !code.is_empty() {
                        parts.push(Part::Code(std::mem::take(&mut code)));
                    }
                    parts.push(Part::Literal(pieces));
                    i = next;
                }
                None => {
                    // An unterminated literal is not one the emitter writes: its text stays code.
                    code.push(ch);
                    i += 1;
                }
            }
        } else {
            code.push(ch);
            i += 1;
        }
    }
    if !code.is_empty() {
        parts.push(Part::Code(code));
    }
    parts
}

/// Parses the body of a literal that starts at `from` (just after its opening quote). Returns its
/// pieces and the index after the closing quote, or `None` when the line ends first.
fn literal_at(chars: &[char], from: usize) -> Option<(Vec<Piece>, usize)> {
    let mut pieces = Vec::new();
    let mut i = from;
    while i < chars.len() {
        match chars[i] {
            '"' => return Some((pieces, i + 1)),
            '\n' => return None,
            '\\' if i + 1 < chars.len() => {
                if let Some((value, len)) = unicode_escape(&chars[i..]) {
                    pieces.push(Piece::Stored(value));
                    i += len;
                } else {
                    pieces.push(Piece::Written(format!("\\{}", chars[i + 1])));
                    i += 2;
                }
            }
            ch if ch.is_ascii() => {
                pieces.push(Piece::Written(ch.to_string()));
                i += 1;
            }
            ch => {
                pieces.push(Piece::Stored(ch));
                i += 1;
            }
        }
    }
    None
}

/// The character of a `\uXXXX` or `\UXXXXXXXX` escape at the start of `chars`, and the escape's length.
fn unicode_escape(chars: &[char]) -> Option<(char, usize)> {
    let digits = match chars.get(1)? {
        'u' => 4,
        'U' => 8,
        _ => return None,
    };
    let hex: String = chars.get(2..2 + digits)?.iter().collect();
    if !hex.chars().all(|ch| ch.is_ascii_hexdigit()) {
        return None;
    }
    let code = u32::from_str_radix(&hex, 16).ok()?;
    Some((char::from_u32(code)?, 2 + digits))
}
