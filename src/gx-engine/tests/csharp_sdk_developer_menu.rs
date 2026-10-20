//! Developer Menu files of a GeneXus 18 .NET web model.
//!
//! `DeveloperMenu.xml` is checked byte for byte against the SDK's output for KBTeste. The static
//! files are copied from the install's `gxnetcore/` folder, and the copies are checked against the
//! same install files (MD5 recorded in the fixture folder). The escaping test uses a synthetic
//! description and is labelled as such.

use std::fs;
use std::path::{Path, PathBuf};

use gx_engine::csharp::developer_menu::{
    DeveloperMenuObject, OBJ_CLS_TRANSACTION, OBJ_CLS_WEB_PANEL, developer_menu_static_copies,
    developer_menu_xml,
};

/// The `<Path>` the SDK wrote for the KBTeste install.
const INSTALL_URI: &str = "file:///C:/Program Files (x86)/GeneXus/GeneXus18/";

fn fixtures() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("sdk_form")
}

fn read_fixture(relative: &str) -> Vec<u8> {
    fs::read(fixtures().join(relative)).unwrap_or_else(|e| panic!("fixture {relative}: {e}"))
}

/// The ten menu entries of the SDK's KBTeste `DeveloperMenu.xml`, in its order.
fn kbteste_objects() -> Vec<DeveloperMenuObject> {
    let entry = |obj_cls, name: &str, description: &str| DeveloperMenuObject {
        obj_cls,
        name: name.to_string(),
        description: description.to_string(),
    };
    vec![
        entry(
            OBJ_CLS_TRANSACTION,
            "TrnGroupProbeBase",
            "Trn Group Probe Base",
        ),
        entry(
            OBJ_CLS_TRANSACTION,
            "TrnGroupProbeSub",
            "Trn Group Probe Sub",
        ),
        entry(OBJ_CLS_TRANSACTION, "Plan070Trn", "Plan070 Trn"),
        entry(OBJ_CLS_TRANSACTION, "Plan071Trn", "Plan071 Trn"),
        entry(
            OBJ_CLS_TRANSACTION,
            "LiveMoveAttribute",
            "Live Move Attribute",
        ),
        entry(
            OBJ_CLS_TRANSACTION,
            "LiveMoveAttributeRules",
            "Live Move Attribute Rules",
        ),
        entry(
            OBJ_CLS_TRANSACTION,
            "LiveMoveAttributeRules2",
            "Live Move Attribute Rules2",
        ),
        entry(
            OBJ_CLS_WEB_PANEL,
            "LiveImportWebPanel",
            "Live Import Web Panel",
        ),
        entry(OBJ_CLS_WEB_PANEL, "TestVldWpf5317a", "Test Vld Wpf5317a"),
        entry(OBJ_CLS_WEB_PANEL, "WWTrn353", "Trn353s"),
    ]
}

#[test]
fn developer_menu_xml_matches_the_sdk_output_for_kbteste() {
    let generated = developer_menu_xml(INSTALL_URI, &kbteste_objects()).expect("render");
    let sdk = read_fixture("DeveloperMenu.xml");
    assert_eq!(generated.len(), sdk.len(), "byte length");
    assert!(
        generated == sdk,
        "DeveloperMenu.xml differs from the SDK output"
    );
}

#[test]
fn static_copies_match_the_install_files_for_kbteste() {
    let root = fixtures().join("developer_menu");
    let copies = developer_menu_static_copies(&root).expect("copies");
    let paths: Vec<&str> = copies.iter().map(|(p, _)| p.as_str()).collect();
    assert_eq!(
        paths,
        [
            "developermenu.html",
            "prgs.xsl",
            "devmenu/developermenu.css",
            "devmenu/developermenu.js"
        ]
    );
    for (relative, bytes) in &copies {
        let expected = fs::read(root.join(relative)).expect("fixture file");
        assert!(
            *bytes == expected,
            "{relative} differs from the install copy"
        );
    }
}

#[test]
fn xml_escapes_description_and_derives_lowercase_link_synthetic() {
    // Synthetic: exercises the escaping and ObjLink rules, not a KB object.
    let objects = vec![DeveloperMenuObject {
        obj_cls: OBJ_CLS_TRANSACTION,
        name: "SyntheticEntry".to_string(),
        description: "A & B <C> \"D\"".to_string(),
    }];
    let xml = String::from_utf8(developer_menu_xml(INSTALL_URI, &objects).unwrap()).unwrap();
    assert!(
        xml.contains("      <ObjDesc>A &amp; B &lt;C&gt; &quot;D&quot;</ObjDesc>\r\n"),
        "{xml}"
    );
    assert!(
        xml.contains("      <ObjLink>syntheticentry.aspx</ObjLink>\r\n"),
        "{xml}"
    );
}

#[test]
fn refuses_unobserved_class_and_non_identifier_names() {
    let bad_class = vec![DeveloperMenuObject {
        obj_cls: 1,
        name: "Anything".to_string(),
        description: String::new(),
    }];
    assert!(developer_menu_xml(INSTALL_URI, &bad_class).is_err());

    for name in ["", "1Starts", "has space", "dot.name", "acentuação"] {
        let bad_name = vec![DeveloperMenuObject {
            obj_cls: OBJ_CLS_WEB_PANEL,
            name: name.to_string(),
            description: String::new(),
        }];
        assert!(
            developer_menu_xml(INSTALL_URI, &bad_name).is_err(),
            "name {name:?} accepted"
        );
    }
}
