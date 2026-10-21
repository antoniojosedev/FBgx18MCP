//! `messages.<lang>.txt` and `messages.<lang>.js` must match the GeneXus 18 SDK byte for byte.
//!
//! Fixtures (tests/fixtures/sdk_form): `messages.eng.txt` and `messages.eng.js` are the files the SDK wrote into
//! KBTeste's NetModel/Web (626 messages, all `Internal`). The install test reads the English text from
//! `Library/GAM/Platforms/NetCoreSql/messages.eng.resources` of the GeneXus 18 install (`GX18_INSTALL_DIR`,
//! default `C:/Program Files (x86)/GeneXus/GeneXus18`) and is skipped with a message when it is absent.

use gx_engine::csharp::messages::{
    MessageEntry, MessageError, MessageKind, emit_message_files, parse_resource_strings,
};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

const FIXTURES: &str = "tests/fixtures/sdk_form";
const SOURCE_RESOURCE: &str = "Library/GAM/Platforms/NetCoreSql/messages.eng.resources";
const DEFAULT_INSTALL: &str = "C:/Program Files (x86)/GeneXus/GeneXus18";

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(Path::new(FIXTURES).join(name)).expect("fixture present")
}

/// The SDK's txt lines as `(key, escaped value)`, in file order.
fn sdk_txt_lines() -> Vec<(String, String)> {
    let txt = String::from_utf8(fixture("messages.eng.txt")).expect("utf-8 txt");
    txt.split("\r\n")
        .filter(|line| !line.is_empty())
        .map(|line| {
            let (key, value) = line.split_once('=').expect("key=value");
            (key.to_owned(), value.to_owned())
        })
        .collect()
}

/// Reverses the txt escaping the SDK applies (`\u003D` for `=`, `\n` for LF).
fn unescape_txt(value: &str) -> String {
    value.replace("\\u003D", "=").replace("\\n", "\n")
}

fn entries_with_texts(texts: impl Fn(&str) -> Option<String>) -> Vec<MessageEntry> {
    sdk_txt_lines()
        .into_iter()
        .enumerate()
        .map(|(index, (key, _))| MessageEntry {
            id: index as i64 + 1,
            text: texts(&key),
            key,
            kind: MessageKind::Internal,
        })
        .collect()
}

fn sdk_fixture_texts() -> HashMap<String, String> {
    sdk_txt_lines()
        .into_iter()
        .map(|(key, value)| (key, unescape_txt(&value)))
        .collect()
}

#[test]
fn emitted_files_match_the_sdk_fixtures_byte_for_byte() {
    let texts = sdk_fixture_texts();
    let entries = entries_with_texts(|key| texts.get(key).cloned());
    assert_eq!(entries.len(), 626);

    let files = emit_message_files("eng", &entries).expect("emit");

    assert_eq!(files.txt.name, "messages.eng.txt");
    assert_eq!(files.js.name, "messages.eng.js");
    assert_eq!(
        files.txt.bytes,
        fixture("messages.eng.txt"),
        "messages.eng.txt differs"
    );
    assert_eq!(
        files.js.bytes,
        fixture("messages.eng.js"),
        "messages.eng.js differs"
    );
}

#[test]
fn install_resource_reproduces_the_sdk_files_when_gx18_is_installed() {
    let install = std::env::var_os("GX18_INSTALL_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(DEFAULT_INSTALL));
    let path = install.join(SOURCE_RESOURCE);
    if !path.is_file() {
        eprintln!(
            "skipped: {} not found; install GeneXus 18 or set GX18_INSTALL_DIR",
            path.display()
        );
        return;
    }
    let bytes = std::fs::read(&path).expect("read resource");
    let resources = parse_resource_strings(&bytes).expect("parse resource");
    let by_name: HashMap<String, String> = resources.into_iter().collect();

    let entries = entries_with_texts(|key| by_name.get(key).cloned());
    let files = emit_message_files("eng", &entries).expect("emit from install resource");

    assert_eq!(
        files.txt.bytes,
        fixture("messages.eng.txt"),
        "txt from install resource differs"
    );
    assert_eq!(
        files.js.bytes,
        fixture("messages.eng.js"),
        "js from install resource differs"
    );
}

#[test]
fn missing_english_text_is_refused_without_output() {
    let texts = sdk_fixture_texts();
    let mut entries = entries_with_texts(|key| texts.get(key).cloned());
    let key = entries[7].key.clone();
    entries[7].text = None;

    assert_eq!(
        emit_message_files("eng", &entries),
        Err(MessageError::MissingText(key))
    );
}

#[test]
fn user_messages_and_duplicates_are_refused() {
    let texts = sdk_fixture_texts();
    let mut user = entries_with_texts(|key| texts.get(key).cloned());
    user[0].kind = MessageKind::User;
    let key = user[0].key.clone();
    assert_eq!(
        emit_message_files("eng", &user),
        Err(MessageError::UserMessage(key))
    );

    let mut duplicate_id = entries_with_texts(|key| texts.get(key).cloned());
    duplicate_id[1].id = duplicate_id[0].id;
    assert_eq!(
        emit_message_files("eng", &duplicate_id),
        Err(MessageError::DuplicateId(1))
    );

    let mut duplicate_key = entries_with_texts(|key| texts.get(key).cloned());
    duplicate_key[1].key = duplicate_key[0].key.clone();
    let key = duplicate_key[0].key.clone();
    assert_eq!(
        emit_message_files("eng", &duplicate_key),
        Err(MessageError::DuplicateKey(key))
    );
}

#[test]
fn unverified_characters_are_refused() {
    for (text, ch) in [("100% done", '%'), ("a\\b", '\\'), ("tab\there", '\t')] {
        let entries = vec![MessageEntry {
            id: 1,
            key: "GXM_probe".to_owned(),
            kind: MessageKind::Internal,
            text: Some(text.to_owned()),
        }];
        assert_eq!(
            emit_message_files("eng", &entries),
            Err(MessageError::UnsupportedCharacter {
                key: "GXM_probe".to_owned(),
                ch
            }),
            "text {text:?}"
        );
    }
}

#[test]
fn empty_input_and_bad_language_are_refused() {
    assert_eq!(emit_message_files("eng", &[]), Err(MessageError::Empty));
    let entry = MessageEntry {
        id: 1,
        key: "GXM_probe".to_owned(),
        kind: MessageKind::Internal,
        text: Some("x".to_owned()),
    };
    assert_eq!(
        emit_message_files("../eng", std::slice::from_ref(&entry)),
        Err(MessageError::InvalidLanguage("../eng".to_owned()))
    );
}

fn write_7bit(out: &mut Vec<u8>, mut value: usize) {
    while value >= 0x80 {
        out.push((value as u8 & 0x7f) | 0x80);
        value >>= 7;
    }
    out.push(value as u8);
}

fn push_i32(out: &mut Vec<u8>, value: i32) {
    out.extend_from_slice(&value.to_le_bytes());
}

/// A minimal ResourceReader v2 file with string resources, laid out like `ResourceWriter` writes it.
fn build_resource(entries: &[(&str, &str)]) -> Vec<u8> {
    let mut out = Vec::new();
    out.extend_from_slice(&0xBEEF_CACEu32.to_le_bytes());
    push_i32(&mut out, 1); // ResourceManager header version
    push_i32(&mut out, 0); // header bytes to skip
    push_i32(&mut out, 2); // reader version
    push_i32(&mut out, entries.len() as i32);
    push_i32(&mut out, 0); // type names
    while out.len() % 8 != 0 {
        out.push(0);
    }
    for _ in entries {
        push_i32(&mut out, 0); // hashes, unused by the parser
    }
    let mut names = Vec::new();
    let mut data = Vec::new();
    let mut positions = Vec::new();
    for (name, value) in entries {
        positions.push(names.len() as i32);
        let utf16: Vec<u8> = name
            .encode_utf16()
            .flat_map(|unit| unit.to_le_bytes())
            .collect();
        write_7bit(&mut names, utf16.len());
        names.extend_from_slice(&utf16);
        push_i32(&mut names, data.len() as i32);
        write_7bit(&mut data, 1); // string type code
        write_7bit(&mut data, value.len());
        data.extend_from_slice(value.as_bytes());
    }
    for position in positions {
        push_i32(&mut out, position);
    }
    let data_section = (out.len() + 4 + names.len()) as i32;
    push_i32(&mut out, data_section);
    out.extend_from_slice(&names);
    out.extend_from_slice(&data);
    out
}

#[test]
fn resource_reader_returns_string_pairs_and_refuses_truncation() {
    let bytes = build_resource(&[
        ("GXM_newrow", "New row"),
        ("GXM_1001", "Dropping index {1}, table {0}."),
    ]);
    assert_eq!(
        parse_resource_strings(&bytes).expect("parse"),
        vec![
            ("GXM_newrow".to_owned(), "New row".to_owned()),
            (
                "GXM_1001".to_owned(),
                "Dropping index {1}, table {0}.".to_owned()
            ),
        ]
    );
    assert!(matches!(
        parse_resource_strings(&bytes[..bytes.len() - 3]),
        Err(MessageError::Resource(_))
    ));
    assert!(matches!(
        parse_resource_strings(b"nope"),
        Err(MessageError::Resource(_))
    ));
}

#[test]
fn placeholders_and_quotes_follow_the_sdk_js_rules() {
    let entries = vec![MessageEntry {
        id: 1,
        key: "GXM_probe".to_owned(),
        kind: MessageKind::Internal,
        text: Some("say \"{0}\" then {12}\nnext={0}".to_owned()),
    }];
    let files = emit_message_files("eng", &entries).expect("emit");
    assert_eq!(
        String::from_utf8(files.js.bytes).unwrap(),
        "gx.msg[\"GXM_probe\"]=\"say \\\"%1\\\" then %13\\nnext=%1\";\r\n"
    );
    assert_eq!(
        String::from_utf8(files.txt.bytes).unwrap(),
        "GXM_probe=say \"{0}\" then {12}\\nnext\\u003D{0}\r\n"
    );
}
