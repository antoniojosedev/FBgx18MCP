//! Procedures read with their stored variable ids are written in the SDK's own form, byte for byte.
//!
//! Fixtures (tests/fixtures/sdk_form): `<name>.kb.json` is `gx-engine kb-read C:/KBs/KBTeste <Name>` and
//! `<name>.cs` / `a<name>.cs` are the files GeneXus 18 wrote for that Procedure in KBTeste's NetModel/Web
//! (SpecifyAllOnly + GenerateOnly), copied as bytes: CRLF line ends, cp1252 text. Only the `?<stamp>`
//! build timestamp is masked.
//!
//! Each fixture is a self-contained Procedure (no calls; one SDT, `RSC79223900`) and covers one rule of the SDK form:
//! the `Gx_page` loop (frc12224800), parentheses, `-0`, `&&`/`||` grouping and `long` literals
//! (rustedge7476154929200), raw cp1252 and UTF-8 bytes in literals (rtm41852400, rusttextfuncsemantics),
//! Character fields before VarChar ones (rusttextfuncrisk), `executeUdp` and parameter fields
//! (dch37961700), and a DateTime output (odh42412500).

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::ProcedureClass;
use gx_engine::csharp::literals::sdk_file_bytes;
use gx_engine::csharp::sdt::{SdtClass, SdtIndex};
use gx_engine::model::DataType;
use gx_engine::udm::stored_sdt::SdtNode;
use std::path::Path;
use std::sync::Arc;

const FIXTURES: &str = "tests/fixtures/sdk_form";

fn object(stem: &str) -> KbObject {
    let path = Path::new(FIXTURES).join(format!("{stem}.kb.json"));
    let read: serde_json::Value =
        serde_json::from_slice(&std::fs::read(&path).expect("fixture")).expect("kb-read JSON");
    let name = read["name"].as_str().expect("name");
    let mut object = KbObject::new(ObjectId::new(stem).unwrap(), name, ObjectKind::Procedure);
    for part in read["parts"].as_array().expect("parts") {
        object.source_parts.push(SourcePart {
            name: part["name"].as_str().unwrap().to_string(),
            source: part["source"].as_str().unwrap().to_string(),
        });
    }
    object
}

/// The bytes with the SDK's `"?<digits>"` build stamp masked.
fn masked(bytes: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i..].starts_with(b"\"?20") {
            out.extend_from_slice(b"\"?STAMP");
            i += 2;
            while i < bytes.len() && bytes[i].is_ascii_digit() {
                i += 1;
            }
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    out
}

fn sdk(file: &str) -> Vec<u8> {
    masked(&std::fs::read(Path::new(FIXTURES).join(file)).expect("SDK fixture"))
}

fn assert_same(file: &str, emitted: &str) {
    let ours = masked(&sdk_file_bytes(emitted));
    let theirs = sdk(file);
    if ours != theirs {
        let ours = String::from_utf8_lossy(&ours);
        let theirs = String::from_utf8_lossy(&theirs);
        let line = ours
            .lines()
            .zip(theirs.lines())
            .position(|(a, b)| a != b)
            .unwrap_or(0);
        panic!(
            "{file} differs from the SDK at line {}:\n ours: {:?}\n  sdk: {:?}",
            line + 1,
            ours.lines().nth(line),
            theirs.lines().nth(line)
        );
    }
}

/// The one SDT the fixtures use: KBTeste's `RSC79223900`, whose `Label` is a VarChar
/// (type_SdtRSC79223900.cs declares `string gxTv_SdtRSC79223900_Label`).
fn sdts() -> Arc<SdtIndex> {
    let mut class = SdtClass::new("RSC79223900");
    class.nodes = vec![SdtNode::Field {
        name: "Label".into(),
        data_type: DataType::Varchar { length: 40 },
    }];
    Arc::new(SdtIndex::from_classes([class]).expect("one class"))
}

fn class(stem: &str, main: bool) -> ProcedureClass {
    ProcedureClass::from_kb_object(&object(stem))
        .expect("plans")
        .with_theme("KBTeste")
        .with_is_main(main)
        .with_sdts(sdts())
}

#[test]
fn a_main_procedure_and_its_wrapper_are_the_sdks_bytes() {
    for stem in [
        "frc12224800",
        "rustedge7476154929200",
        "rtm41852400",
        "rusttextfuncsemantics",
        "rusttextfuncrisk",
    ] {
        // A main program without parameters is split: `a<name>` holds the body, `<name>` the wrapper
        // that starts it.
        let class = class(stem, true);
        assert_same(
            &format!("a{stem}.cs"),
            &class.emit_checked().expect("emits"),
        );
        let wrapper = class.emit_wrapper().expect("a main program has a wrapper");
        assert_same(&format!("{stem}.cs"), &wrapper);
    }
}

#[test]
fn a_called_procedure_is_the_sdks_bytes() {
    for stem in ["dch37961700", "odh42412500"] {
        let class = class(stem, false);
        assert_same(&format!("{stem}.cs"), &class.emit_checked().expect("emits"));
    }
}

#[test]
fn without_variable_ids_the_portable_form_is_kept() {
    // A catalog read before the reader held the ids (or a portable source) is written as before.
    let mut object = object("frc12224800");
    object
        .source_parts
        .retain(|part| part.name != gx_engine::udm::reader::VARIABLE_IDS_PART);
    let code = ProcedureClass::from_kb_object(&object)
        .expect("plans")
        .emit_checked()
        .expect("emits");
    assert!(code.contains("AV_Page"), "{code}");
    assert!(!code.contains("Gx_page"), "{code}");
}
