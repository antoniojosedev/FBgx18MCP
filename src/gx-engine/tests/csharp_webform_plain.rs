//! Rebuild All emits a plain WebPanel as a class in the SDK shape, and refuses it by name when its
//! configuration is unset. A master page is still skipped as web UI.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::webform::WebPanelEnvironment;
use gx_engine::rebuild::{RebuildOptions, RebuildReport, RebuildStatus, rebuild_all};
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

const LIVEIMPORT_WEBFORM: &str = include_str!("fixtures/webform/liveimportwebpanel.webform.xml");
const LIVEIMPORT_SDK: &str = include_str!("fixtures/webform/liveimportwebpanel.cs");
const PLAIN_VARIABLES: &str = include_str!("fixtures/webform/plain.variables.xml");

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

fn master() -> KbObject {
    let mut master = KbObject::new(
        ObjectId::new("masterunanimosidebar").unwrap(),
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
    );
    master
        .source_parts
        .push(settings_part(1, "MasterUnanimoSidebar", Some("Master")));
    master
}

fn live_import() -> KbObject {
    let mut panel = KbObject::new(
        ObjectId::new("liveimportwebpanel").unwrap(),
        "LiveImportWebPanel",
        ObjectKind::WebPanel,
    );
    panel.source_parts.push(SourcePart {
        name: "WEBFORM".into(),
        source: LIVEIMPORT_WEBFORM.into(),
    });
    panel.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: PLAIN_VARIABLES.into(),
    });
    panel
        .source_parts
        .push(settings_part(3, "LiveImportWebPanel", None));
    panel.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("masterunanimosidebar").unwrap(),
    });
    panel
}

fn catalog() -> Catalog {
    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(master()), Edit::Upsert(live_import())])
        .expect("catalog setup");
    catalog
}

fn configured() -> WebPanelEnvironment {
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        )]),
        build_number: Some("239440".into()),
        support_class: Some("namespace GeneXus.Programs { public class GxWebStd { } }".into()),
        design_classes: BTreeMap::new(),
        object_guids: BTreeMap::new(),
        attributes: BTreeMap::new(),
        table_keys: BTreeMap::new(),
        subtype_groups: BTreeMap::new(),
    }
}

fn temp_output(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("gx_webform_{tag}_{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    dir
}

fn run(tag: &str, env: WebPanelEnvironment) -> (RebuildReport, PathBuf) {
    let out = temp_output(tag);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true)
        .with_web_environment(env);
    let report = rebuild_all(&catalog(), &out, &options).expect("rebuild completes");
    (report, out)
}

/// The SDK file with its per-generation stamp masked, so the comparison is on the shape.
fn mask_stamp(text: &str) -> String {
    let mut out = String::new();
    let mut rest = text;
    while let Some(at) = rest.find("\"?") {
        let (head, tail) = rest.split_at(at + 2);
        out.push_str(head);
        let digits = tail.chars().take_while(|c| c.is_ascii_digit()).count();
        out.push_str("STAMP");
        rest = &tail[digits..];
    }
    out.push_str(rest);
    out
}

#[test]
fn a_plain_panel_is_emitted_in_the_sdk_shape_and_the_master_stays_skipped() {
    let (report, out) = run("configured", configured());

    assert!(report.errors.is_empty(), "errors: {:?}", report.errors);
    assert_eq!(
        report.status,
        RebuildStatus::Partial,
        "a skipped master keeps the run partial"
    );
    assert_eq!(
        report.generated_count, 7,
        "the plain panel, the GxWebStd helper it calls, and the five support files the SDK writes"
    );

    // The file is written as the SDK writes it, with CRLF line ends; the fixture is stored with LF.
    let emitted = fs::read_to_string(out.join("liveimportwebpanel.cs"))
        .expect("panel file written")
        .replace('\r', "");
    assert_eq!(
        mask_stamp(&emitted),
        mask_stamp(LIVEIMPORT_SDK),
        "the emitted class has the SDK shape"
    );

    assert!(
        out.join("GxWebStd.cs").exists(),
        "the helper the panel calls is written with it"
    );
    let csproj = fs::read_to_string(out.join("RebuildApp.csproj")).expect("project written");
    assert!(
        csproj.contains("liveimportwebpanel.cs"),
        "the project compiles the panel"
    );

    let master = report
        .skipped_objects
        .iter()
        .find(|s| s.name.eq_ignore_ascii_case("MasterUnanimoSidebar"))
        .expect("the master is skipped");
    assert!(
        master.reason.starts_with("web UI, not generated: "),
        "{}",
        master.reason
    );
    // The master is planned as a master page; this fixture stores no layout, so it is refused for that.
    assert!(
        master.reason.contains("it has no WEBFORM layout"),
        "{}",
        master.reason
    );
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn an_unset_theme_refuses_the_plain_panel_by_name_and_writes_nothing() {
    let (report, out) = run(
        "no_theme",
        WebPanelEnvironment {
            theme: None,
            ..configured()
        },
    );

    assert_eq!(report.generated_count, 0);
    assert!(
        !out.join("liveimportwebpanel.cs").exists(),
        "no file for a refused panel"
    );
    let panel = report
        .skipped_objects
        .iter()
        .find(|s| s.name.eq_ignore_ascii_case("LiveImportWebPanel"))
        .expect("the panel is reported");
    assert!(panel.reason.contains("--web-theme"), "{}", panel.reason);
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn an_unconfigured_master_class_refuses_the_plain_panel_naming_the_option() {
    let (report, out) = run(
        "no_master",
        WebPanelEnvironment {
            master_classes: BTreeMap::new(),
            ..configured()
        },
    );

    assert_eq!(report.generated_count, 0);
    let panel = report
        .skipped_objects
        .iter()
        .find(|s| s.name.eq_ignore_ascii_case("LiveImportWebPanel"))
        .expect("the panel is reported");
    assert!(
        panel.reason.contains("--web-master MasterUnanimoSidebar="),
        "{}",
        panel.reason
    );
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn event_bearing_panels_are_refused_before_artifacts_are_written() {
    // Rebuild uses the SDK-form webform planner, not the library WebPanelClass emitter.
    // Even otherwise-supported event statements stay outside this admitted space.
    let cases = [
        (
            "foreach",
            "EVENTS",
            "Event Start\nFor Each\nEndFor\nEndEvent",
        ),
        ("new", "EVENTS", "Event Start\nNew\nEndNew\nEndEvent"),
        ("delete", "EVENTS", "Event Start\nDelete\nEndEvent"),
        (
            "nested",
            "EVENTS",
            "Event Start\nIf 1 = 1\nDelete\nEndIf\nEndEvent",
        ),
        ("malformed", "EVENTS", "Event Start\nIf (\nEndEvent"),
        ("simple", "EVENTS", "Event Start\nMsg('hello')\nEndEvent"),
        ("rules", "RULES", "parm(in:&Value);"),
        ("conditions", "CONDITIONS", "1 = 1"),
    ];
    for (tag, part_name, source) in cases {
        let mut panel = live_import();
        panel.source_parts.push(SourcePart {
            name: part_name.into(),
            source: source.into(),
        });
        let mut catalog = Catalog::empty();
        catalog
            .apply(0, &[Edit::Upsert(master()), Edit::Upsert(panel)])
            .expect("catalog setup");
        let out = temp_output(tag);
        let options = RebuildOptions::new("net8.0")
            .with_clean(true)
            .with_skip_compile(true)
            .with_web_environment(configured());
        let report = rebuild_all(&catalog, &out, &options).expect("rebuild completes");
        let refusal = report
            .skipped_objects
            .iter()
            .find(|object| object.name == "LiveImportWebPanel")
            .expect("event-bearing panel is explicitly refused");
        assert!(
            refusal
                .reason
                .contains(&format!("{} code", part_name.to_ascii_lowercase())),
            "{tag}: {}",
            refusal.reason
        );
        assert_eq!(
            report.status,
            RebuildStatus::Partial,
            "{tag}: refusal is not success"
        );
        assert_eq!(
            report.generated_count, 0,
            "{tag}: no generated panel or support files"
        );
        assert!(
            !out.join("liveimportwebpanel.cs").exists(),
            "{tag}: no panel artifact"
        );
        let project = fs::read_to_string(out.join("RebuildApp.csproj")).expect("project readable");
        assert!(
            !project.contains("liveimportwebpanel.cs"),
            "{tag}: no compiler input for panel"
        );
        fs::remove_dir_all(&out).expect("temporary output removed");
    }
}

#[test]
fn the_mask_keeps_everything_but_the_stamp() {
    assert_eq!(
        mask_stamp("a \"?202610150405993\" b \"?20261015040599\" c"),
        "a \"?STAMP\" b \"?STAMP\" c"
    );
    assert_eq!(mask_stamp("no stamp here"), "no stamp here");
}
