use super::*;
use crate::catalog::{Edit, KbObject, ObjectId, Reference, SourcePart};

/// The stored layout and variables of the two plain panels in KBTeste (`LiveImportWebPanel`,
/// entity 3, three rows; `TestVldWpf5317a`, entity 4, no rows), copied from the KB.
const LIVEIMPORT_WEBFORM: &str =
    include_str!("../../../tests/fixtures/webform/liveimportwebpanel.webform.xml");
const TESTVLD_WEBFORM: &str =
    include_str!("../../../tests/fixtures/webform/testvldwpf5317a.webform.xml");
const STORED_VARIABLES: &str = include_str!("../../../tests/fixtures/webform/plain.variables.xml");
/// The SDK's generated classes for the same two panels.
const LIVEIMPORT_SDK: &str = include_str!("../../../tests/fixtures/webform/liveimportwebpanel.cs");
const TESTVLD_SDK: &str = include_str!("../../../tests/fixtures/webform/testvldwpf5317a.cs");

/// Stands in for the KB's `GxWebStd.cs`; the emitter only checks its presence.
const SUPPORT_SOURCE: &str = "namespace GeneXus.Programs { public class GxWebStd { } }";

const LIVEIMPORT_STAMP: &str = "202610150405993";
const TESTVLD_STAMP: &str = "202610150405997";

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).unwrap()
}

fn settings_part(entity_id: i32, declared: &str, web_comp: Option<&str>) -> SourcePart {
    WebPanelSettings {
        entity_id,
        declared_name: declared.into(),
        description: None,
        master_page_id: None,
        web_comp: web_comp.map(str::to_string),
    }
    .to_part()
    .unwrap()
}

fn master_object() -> KbObject {
    let mut master = KbObject::new(
        id("masterunanimosidebar"),
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
    );
    master
        .source_parts
        .push(settings_part(1, "MasterUnanimoSidebar", Some("Master")));
    master
}

/// A plain panel `name` with entity `entity_id`, its stored `webform`, and a `Uses` reference to
/// the master.
fn panel(name: &str, entity_id: i32, webform: &str) -> KbObject {
    let mut object = KbObject::new(id(&name.to_ascii_lowercase()), name, ObjectKind::WebPanel);
    object.source_parts.push(SourcePart {
        name: "WEBFORM".into(),
        source: webform.into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: STORED_VARIABLES.into(),
    });
    object
        .source_parts
        .push(settings_part(entity_id, name, None));
    object.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("masterunanimosidebar"),
    });
    object
}

/// A catalog holding `objects`, applied once.
fn catalog_of(objects: Vec<KbObject>) -> Catalog {
    let mut catalog = Catalog::empty();
    let edits: Vec<Edit> = objects.into_iter().map(Edit::Upsert).collect();
    catalog.apply(0, &edits).expect("catalog setup");
    catalog
}

fn env() -> WebPanelEnvironment {
    let mut master_classes = BTreeMap::new();
    master_classes.insert(
        "MasterUnanimoSidebar".to_string(),
        "general.ui.masterunanimosidebar".to_string(),
    );
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes,
        build_number: Some("239440".into()),
        support_class: Some(SUPPORT_SOURCE.into()),
        design_classes: BTreeMap::new(),
        object_guids: BTreeMap::new(),
        attributes: BTreeMap::new(),
        table_keys: BTreeMap::new(),
        subtype_groups: BTreeMap::new(),
    }
}

fn plan_in(
    panel: &KbObject,
    objects: Vec<KbObject>,
    env: &WebPanelEnvironment,
) -> Result<PlainWebPanel, String> {
    let mut all = objects;
    all.push(panel.clone());
    plan_plain_webpanel(panel, &catalog_of(all), env)
}

fn plan(panel: &KbObject, env: &WebPanelEnvironment) -> Result<PlainWebPanel, String> {
    plan_in(panel, vec![master_object()], env)
}

fn liveimport() -> KbObject {
    panel("LiveImportWebPanel", 3, LIVEIMPORT_WEBFORM)
}

fn testvld() -> KbObject {
    panel("TestVldWpf5317a", 4, TESTVLD_WEBFORM)
}

#[test]
fn liveimport_matches_the_sdk_output_byte_for_byte() {
    let plan = plan(&liveimport(), &env()).expect("plain panel plans");
    assert_eq!(plan.rows, 3);
    assert_eq!(plan.form_number, "032");
    assert_eq!(plan.section_number, "030");
    assert_eq!(plan.description, "Live Import Web Panel");
    assert_eq!(plan.emit(LIVEIMPORT_STAMP), LIVEIMPORT_SDK);
    assert_eq!(plan.file_name(), "liveimportwebpanel.cs");
}

#[test]
fn testvld_matches_the_sdk_output_byte_for_byte() {
    let plan = plan(&testvld(), &env()).expect("plain panel plans");
    assert_eq!(plan.rows, 0);
    assert_eq!(plan.form_number, "042");
    assert_eq!(plan.section_number, "040");
    assert_eq!(plan.description, "Test Vld Wpf5317a");
    assert_eq!(plan.emit(TESTVLD_STAMP), TESTVLD_SDK);
}

#[test]
fn emitted_text_has_no_placeholder_left() {
    let code = plan(&liveimport(), &env()).unwrap().emit(LIVEIMPORT_STAMP);
    assert!(!code.contains("@@"), "placeholder left in the output");
}

#[test]
fn stored_description_replaces_the_name_split_when_set() {
    let mut object = liveimport();
    object.source_parts.retain(|p| p.name != SETTINGS_PART);
    object.source_parts.push(
        WebPanelSettings {
            entity_id: 3,
            declared_name: "LiveImportWebPanel".into(),
            description: Some("Import Demo".into()),
            master_page_id: None,
            web_comp: None,
        }
        .to_part()
        .unwrap(),
    );
    let plan = plan(&object, &env()).unwrap();
    assert_eq!(plan.description, "Import Demo");
    assert!(
        plan.emit(LIVEIMPORT_STAMP)
            .contains("return \"Import Demo\" ;")
    );
}

#[test]
fn a_stored_description_with_quotes_or_placeholders_is_refused() {
    for bad in ["Quote \" here", "Inject @@F@@", "Amp & co"] {
        let mut object = liveimport();
        object.source_parts.retain(|p| p.name != SETTINGS_PART);
        object.source_parts.push(
            WebPanelSettings {
                entity_id: 3,
                declared_name: "LiveImportWebPanel".into(),
                description: Some(bad.into()),
                master_page_id: None,
                web_comp: None,
            }
            .to_part()
            .unwrap(),
        );
        let reason = plan(&object, &env()).unwrap_err();
        assert!(reason.contains("not matched"), "{bad}: {reason}");
    }
}

#[test]
fn description_splits_camel_case_and_refuses_unmatched_shapes() {
    assert_eq!(
        describe_name("LiveImportWebPanel").unwrap(),
        "Live Import Web Panel"
    );
    assert_eq!(
        describe_name("TestVldWpf5317a").unwrap(),
        "Test Vld Wpf5317a"
    );
    assert!(describe_name("WWTrn353").is_err(), "consecutive capitals");
    assert!(
        describe_name("Trn353General").is_err(),
        "digit before capital"
    );
    assert!(describe_name("With_Underscore").is_err());
}

#[test]
fn member_numbers_follow_the_entity_id() {
    assert_eq!(three_digits(3, 2).unwrap(), "032");
    assert_eq!(three_digits(4, 2).unwrap(), "042");
    assert_eq!(three_digits(1, 0).unwrap(), "010");
    assert_eq!(three_digits(3, 0).unwrap(), "030");
    assert_eq!(three_digits(67, 0).unwrap(), "1V0", "Trn353General (WB1V0)");
    assert_eq!(three_digits(94, 2).unwrap(), "2M2", "Gx0010 (WS2M2)");
    assert!(
        three_digits(36 * 36, 2).is_err(),
        "no evidence beyond two base-36 digits"
    );
}

#[test]
fn stamp_formats_the_civil_time() {
    assert_eq!(stamp_from_unix_seconds(1_700_000_000), "20231114221320");
    assert_eq!(stamp_from_unix_seconds(0), "19700101000000");
}

#[test]
fn build_number_is_read_from_gxcfg() {
    let dir = std::env::temp_dir().join(format!("gx_webform_cfg_{}", std::process::id()));
    let web = dir.join("NetModel").join("Web");
    std::fs::create_dir_all(&web).unwrap();
    std::fs::write(
        web.join("GXCFG.WEB"),
        "<configuration><appSettings><add  key=\"GX_BUILD_NUMBER\" value=\"239440\"/></appSettings></configuration>",
    )
    .unwrap();
    assert_eq!(web_build_number_from_kb(&dir).as_deref(), Some("239440"));
    std::fs::remove_dir_all(&dir).ok();
    assert_eq!(web_build_number_from_kb(&dir), None);
}

#[test]
fn unset_configuration_refuses_each_plain_panel_naming_the_option() {
    let base = env();
    let no_theme = WebPanelEnvironment {
        theme: None,
        ..base.clone()
    };
    assert!(
        plan(&liveimport(), &no_theme)
            .unwrap_err()
            .contains("--web-theme")
    );
    let no_generator = WebPanelEnvironment {
        generator: None,
        ..base.clone()
    };
    assert!(
        plan(&liveimport(), &no_generator)
            .unwrap_err()
            .contains("--web-generator")
    );
    let no_master = WebPanelEnvironment {
        master_classes: BTreeMap::new(),
        ..base.clone()
    };
    let reason = plan(&liveimport(), &no_master).unwrap_err();
    assert!(
        reason.contains("--web-master MasterUnanimoSidebar="),
        "{reason}"
    );
    let no_build = WebPanelEnvironment {
        build_number: None,
        ..base
    };
    assert!(
        plan(&liveimport(), &no_build)
            .unwrap_err()
            .contains("GXCFG")
    );
}

#[test]
fn a_configured_value_with_unmatched_characters_is_refused() {
    let bad_theme = WebPanelEnvironment {
        theme: Some("Theme\"x".into()),
        ..env()
    };
    assert!(
        plan(&liveimport(), &bad_theme)
            .unwrap_err()
            .contains("--web-theme")
    );
    let bad_class = WebPanelEnvironment {
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui-master".to_string(),
        )]),
        ..env()
    };
    assert!(
        plan(&liveimport(), &bad_class)
            .unwrap_err()
            .contains("not a dotted C# name")
    );
}

#[test]
fn a_panel_with_no_recorded_master_or_two_masters_is_refused() {
    let mut no_reference = liveimport();
    no_reference.references.clear();
    assert!(
        plan(&no_reference, &env())
            .unwrap_err()
            .contains("records no master page")
    );

    let mut second = KbObject::new(id("othermaster"), "OtherMaster", ObjectKind::WebPanel);
    second
        .source_parts
        .push(settings_part(9, "OtherMaster", Some("Master")));
    let mut two = liveimport();
    two.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("othermaster"),
    });
    let reason = plan_in(&two, vec![master_object(), second], &env()).unwrap_err();
    assert!(reason.contains("2 master pages"), "{reason}");
}

#[test]
fn explicit_master_page_and_master_layouts_are_refused() {
    let mut explicit = liveimport();
    explicit.source_parts.retain(|p| p.name != SETTINGS_PART);
    explicit.source_parts.push(
        WebPanelSettings {
            entity_id: 3,
            declared_name: "LiveImportWebPanel".into(),
            description: None,
            master_page_id: Some(2),
            web_comp: None,
        }
        .to_part()
        .unwrap(),
    );
    assert!(
        plan(&explicit, &env())
            .unwrap_err()
            .contains("MasterPage property 2")
    );

    let mut master = master_object();
    master.source_parts.retain(|p| p.name != SETTINGS_PART);
    master
        .source_parts
        .push(settings_part(1, "MasterUnanimoSidebar", Some("Master")));
    let reason = plan(&master, &env()).unwrap_err();
    assert!(
        reason.contains("master page 'MasterUnanimoSidebar'"),
        "{reason}"
    );
}

#[test]
fn a_master_page_reference_is_refused_by_the_name_of_the_master_it_resolves_to() {
    let mut panel = liveimport();
    panel.references.clear();
    panel.source_parts.retain(|p| p.name != SETTINGS_PART);
    panel.source_parts.push(
        WebPanelSettings {
            entity_id: 3,
            declared_name: "LiveImportWebPanel".into(),
            description: None,
            master_page_id: Some(2),
            web_comp: None,
        }
        .to_part()
        .unwrap(),
    );
    let mut prompt = KbObject::new(id("masterprompt"), "MASTERPROMPT", ObjectKind::WebPanel);
    prompt
        .source_parts
        .push(settings_part(2, "MasterPrompt", Some("Master")));
    panel.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("masterprompt"),
    });
    let reason = plan_in(&panel, vec![prompt], &env()).unwrap_err();
    assert!(
        reason.contains("names master page 'MasterPrompt' (MasterPage property 2)"),
        "{reason}"
    );
}

#[test]
fn a_master_page_property_that_resolves_to_no_single_master_is_refused_without_a_guessed_name() {
    let mut panel = liveimport();
    panel.source_parts.retain(|p| p.name != SETTINGS_PART);
    panel.source_parts.push(
        WebPanelSettings {
            entity_id: 3,
            declared_name: "LiveImportWebPanel".into(),
            description: None,
            master_page_id: Some(2),
            web_comp: None,
        }
        .to_part()
        .unwrap(),
    );
    let mut second = KbObject::new(id("othermaster"), "OtherMaster", ObjectKind::WebPanel);
    second
        .source_parts
        .push(settings_part(9, "OtherMaster", Some("Master")));
    panel.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("othermaster"),
    });
    let reason = plan_in(&panel, vec![master_object(), second], &env()).unwrap_err();
    assert!(
        reason.contains("names a master page (MasterPage property 2)"),
        "{reason}"
    );
    assert!(!reason.contains("'"), "no name may be guessed: {reason}");
}

#[test]
fn code_user_variables_and_unmatched_layouts_are_refused() {
    let mut with_events = liveimport();
    with_events.source_parts.push(SourcePart {
        name: "EVENTS".into(),
        source: "event Start\n  Msg('x')\nendevent".into(),
    });
    assert!(plan(&with_events, &env()).unwrap_err().contains("events"));

    let mut user_variable = liveimport();
    user_variable.source_parts.retain(|p| p.name != "VARIABLES");
    user_variable.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: r#"<Variables><Variable Id="9" Name="&amp;Customer"/></Variables>"#.into(),
    });
    assert!(
        plan(&user_variable, &env())
            .unwrap_err()
            .contains("&Customer")
    );

    let with_content = LIVEIMPORT_WEBFORM.replacen(
        "<cell id=\"ef4dd512-4236-4fe7-a0fe-cd36ace476d0\" />",
        "<cell id=\"ef4dd512-4236-4fe7-a0fe-cd36ace476d0\"><control/></cell>",
        1,
    );
    assert!(
        plan(&panel("LiveImportWebPanel", 3, &with_content), &env())
            .unwrap_err()
            .contains("content")
    );
}

#[test]
fn a_web_component_is_refused_as_a_component_not_as_a_master() {
    let mut component = liveimport();
    component.source_parts.retain(|p| p.name != SETTINGS_PART);
    component
        .source_parts
        .push(settings_part(3, "LiveImportWebPanel", Some("Yes")));
    let reason = plan(&component, &env()).unwrap_err();
    assert!(reason.contains("web component (WEB_COMP Yes)"), "{reason}");
    assert!(!reason.contains("master page"), "{reason}");
}

#[test]
fn a_kb_without_the_gxwebstd_helper_refuses_the_panel_naming_it() {
    let no_helper = WebPanelEnvironment {
        support_class: None,
        ..env()
    };
    let reason = plan(&liveimport(), &no_helper).unwrap_err();
    assert!(reason.contains("GxWebStd"), "{reason}");
}

#[test]
fn a_table_shape_outside_the_matched_pairs_is_refused() {
    // Three rows without a class: the class and the row count are matched as a pair.
    let without_class =
        LIVEIMPORT_WEBFORM.replacen(" class=\"d52f9833-90c9-4096-8e42-27485741f78a-453\"", "", 1);
    let reason = plan(&panel("LiveImportWebPanel", 3, &without_class), &env()).unwrap_err();
    assert!(
        reason.contains("no SDK output has been matched"),
        "{reason}"
    );

    // One row, with a class.
    let one_row = LIVEIMPORT_WEBFORM
        .replacen(
            "<row id=\"5666d13a-bfa8-4178-bf8b-8b99c6376ded\">\n            <cell id=\"7a8a62c3-3f73-454f-a120-5bb4eac4ff32\" />\n          </row>",
            "",
            1,
        )
        .replacen(
            "<row id=\"d6b5d597-11e4-474f-84ae-e1554e316f04\">\n            <cell id=\"13733249-9693-4d36-8cac-6b15df249f02\" />\n          </row>",
            "",
            1,
        );
    assert_ne!(one_row, LIVEIMPORT_WEBFORM, "the fixture edit applied");
    let reason = plan(&panel("LiveImportWebPanel", 3, &one_row), &env()).unwrap_err();
    assert!(reason.contains("1 row(s)"), "{reason}");
}

/// Mutation guard: the emitted section must keep the close-divs that balance the open ones. A
/// change to `section_block` that drops one close fails this test.
#[test]
fn section_divs_are_balanced_for_every_matched_row_count() {
    for rows in [0, 3] {
        let block = section_block(rows);
        let opens = block.matches("gx_div_start(").count();
        let closes = block.matches("gx_div_end(").count();
        assert_eq!(opens, closes, "rows={rows}");
    }
}
