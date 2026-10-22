//! Standalone query classification, not Gateway admission or code-generation policy.
#![forbid(unsafe_code)]

use gx_engine::kb_ops::{MAX_QUERY_LIMIT, query_objects};
use gx_engine::udm::UdmClient;
use gx_engine::udm::client::{
    EntityVersionRecord, ModelCrossReferenceRecord, ModelEntityVersionRecord,
};

// Projection of the ModelId=1 active rows and WEB_COMP properties captured by the parent's
// SELECT-only oracle, 2026-10-21: C:/tmp/gxe-live-read-followup-v3/webpanel-storage.json.
// None means WEB_COMP is absent in valid Properties XML, not absent/unreadable properties.
const STORED_WEBPANELS: [(i32, i32, &str, Option<&str>); 15] = [
    (94, 1, "Gx0010", None),
    (95, 1, "Gx0020", None),
    (96, 1, "Gx0040", None),
    (97, 1, "Gx0050", None),
    (98, 1, "Gx0060", None),
    (99, 1, "Gx0070", None),
    (100, 1, "Gx0080", None),
    (3, 6, "LiveImportWebPanel", None),
    (2, 1, "MasterPrompt", Some("Master")),
    (1, 1, "MasterUnanimoSidebar", Some("Master")),
    (64, 1, "NotAuthorized", None),
    (4, 1, "TestVldWpf5317a", None),
    (67, 3, "Trn353General", Some("Yes")),
    (65, 3, "ViewTrn353", None),
    (66, 4, "WWTrn353", None),
];

// Independent, complete settled SDK type:webpanel results, not derived from the flags above:
// C:/tmp/gxe-live-read-followup-v3/webpanel-inventory.json (12 total, hasMore=false).
const SDK_WEBPANEL_NAMES: [&str; 12] = [
    "Gx0010",
    "Gx0020",
    "Gx0040",
    "Gx0050",
    "Gx0060",
    "Gx0070",
    "Gx0080",
    "LiveImportWebPanel",
    "NotAuthorized",
    "TestVldWpf5317a",
    "ViewTrn353",
    "WWTrn353",
];

fn insert_row(client: &UdmClient, kind: i32, id: i32, version: i32, name: &str) {
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: kind,
            entity_id: id,
            entity_version_id: version,
            name: name.into(),
        })
        .unwrap();
}

fn insert_properties(client: &UdmClient, id: i32, version: i32, properties: Option<&str>) {
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 47,
            entity_id: id,
            entity_version_id: version,
            // Classification must not decode payload data or depend on source parts.
            data: vec![0xff],
            properties: properties.map(str::to_string),
        })
        .unwrap();
}

fn properties(name: &str, flag: Option<&str>) -> String {
    let flag = flag.map_or_else(String::new, |flag| {
        format!("<Property><Name>WEB_COMP</Name><Value>{flag}</Value></Property>")
    });
    format!(
        "<Properties><Property><Name>Name</Name><Value>{name}</Value></Property>{flag}</Properties>"
    )
}

fn stored_client() -> UdmClient {
    let client = UdmClient::new_in_memory();
    for (id, version, name, flag) in STORED_WEBPANELS {
        insert_row(&client, 47, id, version, name);
        insert_properties(&client, id, version, Some(&properties(name, flag)));
    }
    client
}

#[test]
fn query_webpanel_matches_independent_complete_sdk_inventory() {
    let result = query_objects(&stored_client(), None, Some("WebPanel"), None, 100).unwrap();
    assert_eq!(result.total, 12);
    assert!(!result.truncated);
    assert_eq!(
        result
            .objects
            .iter()
            .map(|row| row.name.as_str())
            .collect::<Vec<_>>(),
        SDK_WEBPANEL_NAMES
    );
    assert!(
        result
            .objects
            .iter()
            .all(|row| row.object_type == "WebPanel")
    );
}

#[test]
fn query_webpanel_classifies_before_paging_and_preserves_limits_and_ordinal_order() {
    let client = stored_client();
    let result = query_objects(&client, None, Some("web_panel"), None, 10).unwrap();
    assert_eq!(result.total, 12);
    assert!(result.truncated);
    assert_eq!(
        result
            .objects
            .iter()
            .map(|row| row.name.as_str())
            .collect::<Vec<_>>(),
        &SDK_WEBPANEL_NAMES[..10]
    );
    // Default and capped limits remain unchanged after classification.
    let default = query_objects(&client, None, Some("WebPanel"), None, 0).unwrap();
    assert_eq!(default.objects.len(), 12);
    for id in 200..405 {
        let name = format!("Z{id}");
        insert_row(&client, 47, id, 1, &name);
        insert_properties(&client, id, 1, Some(&properties(&name, None)));
    }
    let capped = query_objects(&client, None, Some("WebPanel"), None, usize::MAX).unwrap();
    assert_eq!(capped.total, 217);
    assert_eq!(capped.objects.len(), MAX_QUERY_LIMIT);
    assert!(capped.truncated);
    let default = query_objects(&client, None, Some("WebPanel"), None, 0).unwrap();
    assert_eq!(default.objects.len(), 50);
    assert!(default.truncated);
}

#[test]
fn query_webpanel_uses_active_metadata_not_names_or_master_page_links() {
    let client = UdmClient::new_in_memory();
    insert_row(&client, 47, 1, 2, "MasterMisleadingGeneral");
    insert_properties(&client, 1, 1, Some(&properties("Old", Some("Master"))));
    insert_properties(&client, 1, 3, Some(&properties("Future", Some("Yes"))));
    insert_properties(
        &client,
        1,
        2,
        Some(
            "<Properties><Property><Name>Name</Name><Value>MasterMisleadingGeneral</Value></Property><Property><Name>MasterPage</Name><Value>2</Value></Property></Properties>",
        ),
    );
    insert_row(&client, 47, 2, 1, "OrdinaryName");
    insert_properties(
        &client,
        2,
        1,
        Some(&properties("OrdinaryName", Some("Master"))),
    );
    insert_row(&client, 47, 3, 1, "AnotherPlainName");
    insert_properties(
        &client,
        3,
        1,
        Some(&properties("AnotherPlainName", Some("Yes"))),
    );
    let result = query_objects(&client, None, Some("WebPanel"), None, 100).unwrap();
    assert_eq!(result.total, 1);
    assert_eq!(result.objects[0].name, "MasterMisleadingGeneral");
    let component = query_objects(
        &client,
        Some("anotherplainname"),
        Some("WebPanel"),
        None,
        100,
    )
    .unwrap();
    assert_eq!(component.total, 0);
}

#[test]
fn query_unfiltered_reports_sdk_descriptors_without_changing_module_priority() {
    let client = stored_client();
    insert_row(&client, 12, 20, 1, "ZModule");
    insert_row(&client, 38, 21, 1, "AProcedure");
    let result = query_objects(&client, None, None, None, 100).unwrap();
    assert_eq!(result.total, 17);
    assert_eq!(result.objects[0].name, "ZModule");
    assert_eq!(result.objects[1].name, "AProcedure");
    assert!(!result.truncated);
    // SDK unfiltered name queries, independently captured after index-ready/idle:
    // C:/tmp/gxe-query-webpanel-descriptors/summary.json (exactly one descriptor each).
    for (name, expected_type) in [
        ("MasterPrompt", "MasterPage"),
        ("MasterUnanimoSidebar", "MasterPage"),
        ("Trn353General", "WebComponent"),
    ] {
        let object = result.objects.iter().find(|row| row.name == name).unwrap();
        assert_eq!(object.object_type, expected_type);
        let exact = query_objects(&client, Some(name), None, None, 100).unwrap();
        assert_eq!(exact.total, 1);
        assert_eq!(exact.objects[0].object_type, expected_type);
    }
}

#[test]
fn query_webpanel_missing_or_unknown_classification_fails_closed() {
    let invalid = [
        None,
        Some(""),
        Some("  "),
        Some("<Properties"),
        Some("<Other/>"),
        Some(
            "<Properties><Wrapper><Property><Name>WEB_COMP</Name><Value>Yes</Value></Property></Wrapper></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Name>Other</Name><Value>Yes</Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name><Nested>WEB_COMP</Nested></Name><Value>Yes</Value></Property></Properties>",
        ),
        Some("<Properties><Property><Name>WEB_COMP</Name></Property></Properties>"),
        Some("<Properties><Property><Name>WEB_COMP</Name><Value/></Property></Properties>"),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value>Yes</Value><Value>Master</Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value><Nested>Yes</Nested></Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value>yes</Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value> Master </Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value>No</Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value>Future</Value></Property></Properties>",
        ),
        Some(
            "<Properties><Property><Name>WEB_COMP</Name><Value>Yes</Value></Property><Property><Name>WEB_COMP</Name><Value>Master</Value></Property></Properties>",
        ),
    ];
    for properties in invalid {
        let client = UdmClient::new_in_memory();
        insert_row(&client, 47, 1, 1, "Unclassified");
        insert_properties(&client, 1, 1, properties);
        let error = query_objects(&client, None, Some("WebPanel"), None, 100).unwrap_err();
        assert!(error.contains("Unclassified"), "{error}");
        // Unfiltered queries also refuse unknown metadata rather than silently hiding a row.
        assert!(query_objects(&client, None, None, None, 100).is_err());
    }
    let client = UdmClient::new_in_memory();
    insert_row(&client, 47, 1, 2, "MissingActiveVersion");
    insert_properties(&client, 1, 1, Some(&properties("Old", None)));
    let error = query_objects(&client, None, Some("WebPanel"), None, 100).unwrap_err();
    assert!(error.contains("MissingActiveVersion"), "{error}");
}

#[test]
fn query_webpanel_rejects_non_text_scalar_boundaries() {
    let invalid = [
        "<Name><!--boundary-->WEB_COMP</Name><Value>Yes</Value>",
        "<Name>WEB_COMP<!--boundary-->Future</Name><Value>Yes</Value>",
        "<Name>WEB_COMP<?boundary data?></Name><Value>Yes</Value>",
        "<Name>WEB_COMP</Name><Value>Yes<!--boundary-->Future</Value>",
        "<Name>WEB_COMP</Name><Value><!--boundary-->Yes</Value>",
        "<Name>WEB_COMP</Name><Value>Yes<?boundary data?>Future</Value>",
        "<Name>WEB_COMP</Name><Value>Yes<!--boundary--></Value>",
        "<Name/><Value>Yes</Value>",
    ];
    for inner in invalid {
        let client = UdmClient::new_in_memory();
        insert_row(&client, 47, 1, 1, "SplitScalar");
        let xml = format!("<Properties><Property>{inner}</Property></Properties>");
        insert_properties(&client, 1, 1, Some(&xml));
        assert!(
            query_objects(&client, None, Some("WebPanel"), None, 100).is_err(),
            "accepted malformed scalar: {inner}"
        );
    }
}

#[test]
fn query_webpanel_respects_name_reference_and_other_kind_prefilters() {
    let client = stored_client();
    insert_row(&client, 47, 500, 1, "UnknownProperties"); // no version: irrelevant to prefiltered queries
    // Every currently Gateway-admitted type stays descriptor-only, even with an unclassifiable
    // physical WebPanel in the same model. No property record is needed for these other kinds.
    for (index, (type_id, kind)) in [
        (38, "Procedure"),
        (43, "Transaction"),
        (41, "Table"),
        (29, "DataProvider"),
        (40, "SDT"),
        (27, "Attribute"),
        (33, "Domain"),
        (30, "DataSelector"),
        (12, "Module"),
        (57, "DesignSystem"),
        (31, "DataView"),
    ]
    .into_iter()
    .enumerate()
    {
        insert_row(&client, type_id, 20 + index as i32, 1, kind);
        let result = query_objects(&client, None, Some(kind), None, 100).unwrap();
        assert_eq!(result.total, 1);
        assert_eq!(result.objects[0].object_type, kind);
    }
    for id in [94, 2, 67] {
        client
            .insert_cross_reference(ModelCrossReferenceRecord {
                model_id: 1,
                from_entity_type_id: 47,
                from_entity_id: id,
                to_entity_type_id: 38,
                to_entity_id: 20,
                reference_type: 1,
            })
            .unwrap();
    }
    let result = query_objects(&client, None, Some("WebPanel"), Some("Procedure"), 100).unwrap();
    assert_eq!(result.total, 1);
    assert_eq!(result.objects[0].name, "Gx0010");
    let result = query_objects(&client, Some("masterprompt"), Some("WebPanel"), None, 100).unwrap();
    assert_eq!(result.total, 0);
    let result = query_objects(&client, Some("gx0010"), Some("WebPanel"), None, 100).unwrap();
    assert_eq!(result.total, 1);
    assert!(query_objects(&client, None, Some("MasterPage"), None, 100).is_err());
    assert!(query_objects(&client, None, Some("WebComponent"), None, 100).is_err());
}
