//! The stored structure of a Structured Data Type, decoded strictly: the served `STRUCTURE` XML and
//! each node's stored `EntityVersionProperties`, taken from the disposable KBTeste. A node the
//! decoder cannot prove refuses the SDT by name; nothing is dropped or guessed.

use gx_engine::model::DataType;
use gx_engine::udm::stored_sdt::{self, Lookup, SdtNode};
use std::collections::BTreeMap;

/// The stored properties of one node, in the form of its `EntityVersionProperties` row.
fn row<V: AsRef<str>>(pairs: &[(&str, V)]) -> String {
    let mut out = String::from("<Properties>");
    for (name, value) in pairs {
        out.push_str(&format!(
            "<Property><Name>{name}</Name><Value>{}</Value></Property>",
            value
                .as_ref()
                .replace('&', "&amp;")
                .replace('<', "&lt;")
                .replace('>', "&gt;")
        ));
    }
    out.push_str("</Properties>");
    out
}

/// The `ATTCUSTOMTYPE` value of an item with the given type code.
fn code(code: &str) -> (&'static str, String) {
    (
        "ATTCUSTOMTYPE",
        format!("<AttCustomType><DataType>{code}</DataType></AttCustomType>"),
    )
}

/// Decodes one structure with the nodes given as `(id, stored properties)`, and the SDT and domain
/// tables the test needs.
fn decode_with(
    xml: &str,
    nodes: &BTreeMap<i32, String>,
    domains: &BTreeMap<i32, DataType>,
) -> Result<stored_sdt::SdtStructure, String> {
    let node_properties = |id: i32| nodes.get(&id).cloned();
    let sdt_name = |id: i32| match id {
        179 => Some("LiveSdtMember60744600".to_string()),
        _ => None,
    };
    let domain_type = |id: i32| domains.get(&id).cloned();
    let lookup = Lookup {
        node_properties: &node_properties,
        sdt_name: &sdt_name,
        domain_type: &domain_type,
    };
    stored_sdt::decode(xml, "RSN03944700", &lookup)
}

/// The served structure of RSN03944700 (the simple, nested and collection SDT on KBTeste). The
/// node ids and properties are the stored rows of its `SDTStructure` version 1.
fn rsn03944700_nodes() -> BTreeMap<i32, String> {
    let mut nodes = BTreeMap::new();
    nodes.insert(
        218,
        row(&[
            ("Name", "Child".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            ("AttCollection", "False".to_string()),
        ]),
    );
    nodes.insert(
        219,
        row(&[
            ("Name", "Rows".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            ("AttCollection", "True".to_string()),
        ]),
    );
    let amount = row(&[
        ("Name", "Amount".to_string()),
        code("4"),
        ("Length", "4".to_string()),
        ("Decimals", "0".to_string()),
    ]);
    let label = row(&[
        ("Name", "Label".to_string()),
        code("13"),
        ("Length", "6".to_string()),
        ("Decimals", "0".to_string()),
    ]);
    let fixed = row(&[
        ("Name", "Fixed".to_string()),
        code("5"),
        ("Length", "6".to_string()),
        ("Decimals", "0".to_string()),
    ]);
    nodes.insert(543, amount);
    nodes.insert(544, label);
    nodes.insert(545, fixed);
    nodes.insert(
        546,
        row(&[
            ("Name", "Amount".to_string()),
            code("4"),
            ("Length", "4".to_string()),
            ("Decimals", "0".to_string()),
        ]),
    );
    nodes.insert(
        547,
        row(&[
            ("Name", "Amount".to_string()),
            code("4"),
            ("Length", "4".to_string()),
            ("Decimals", "0".to_string()),
        ]),
    );
    nodes
}

const RSN03944700_XML: &str = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="217"><Item Id="543" /><Item Id="544" /><Item Id="545" /><Level Id="218"><Item Id="546" /></Level><Level Id="219"><Item Id="547" /></Level></Level>"#;

#[test]
fn simple_nested_and_collection_structure_decodes_to_the_sdk_shape() {
    let decoded = decode_with(RSN03944700_XML, &rsn03944700_nodes(), &BTreeMap::new())
        .expect("RSN03944700 decodes");
    assert_eq!(decoded.nodes.len(), 5);
    assert_eq!(
        decoded.nodes[0],
        SdtNode::Field {
            name: "Amount".into(),
            data_type: DataType::Numeric {
                precision: 4,
                scale: 0
            }
        }
    );
    assert_eq!(
        decoded.nodes[1],
        SdtNode::Field {
            name: "Label".into(),
            data_type: DataType::Varchar { length: 6 }
        }
    );
    assert_eq!(
        decoded.nodes[2],
        SdtNode::Field {
            name: "Fixed".into(),
            data_type: DataType::Character { length: 6 }
        }
    );
    match &decoded.nodes[3] {
        SdtNode::Level {
            name,
            collection,
            children,
            ..
        } => {
            assert_eq!(name, "Child");
            assert!(!collection, "Child is a nested record, not a collection");
            assert_eq!(children.len(), 1);
        }
        other => panic!("expected the nested level Child, got {other:?}"),
    }
    match &decoded.nodes[4] {
        SdtNode::Level {
            name, collection, ..
        } => {
            assert_eq!(name, "Rows");
            assert!(collection, "Rows is a collection");
        }
        other => panic!("expected the collection level Rows, got {other:?}"),
    }
}

#[test]
fn a_based_on_item_takes_its_domain_type() {
    // BeaconState.Proximity (stored row EntityId 249, version 1 on KBTeste) is based on the
    // BeaconProximity domain (EntityId 59), whose stored type is NUMERIC(1).
    let mut nodes = BTreeMap::new();
    nodes.insert(
        249,
        row(&[
            ("Name", "Proximity"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-59"),
        ]),
    );
    let mut domains = BTreeMap::new();
    domains.insert(
        59,
        DataType::Numeric {
            precision: 1,
            scale: 0,
        },
    );
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="249" /></Level>"#;
    let decoded = decode_with(xml, &nodes, &domains).expect("based-on decodes");
    assert_eq!(
        decoded.nodes,
        vec![SdtNode::Field {
            name: "Proximity".into(),
            data_type: DataType::Numeric {
                precision: 1,
                scale: 0
            }
        }]
    );
    assert_eq!(decoded.domain_entity_ids, vec![59]);
}

#[test]
fn a_based_on_item_whose_domain_has_no_stored_type_is_refused_by_name() {
    // MapRegion.Status (stored row EntityId 271) is based on the RegionStatus domain (EntityId 46),
    // which has no stored type this engine maps. The item is refused, not typed as anything.
    let mut nodes = BTreeMap::new();
    nodes.insert(
        271,
        row(&[
            ("Name", "Status"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-46"),
        ]),
    );
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="271" /></Level>"#;
    let error = decode_with(xml, &nodes, &BTreeMap::new()).unwrap_err();
    assert!(error.contains("item 'Status'"), "{error}");
    assert!(error.contains("no stored type this engine maps"), "{error}");
}

/// Decodes an SDT whose only item is the given stored row, and returns that item's field type.
fn only_item_type(id: i32, props: Vec<(&str, &str)>) -> Result<DataType, String> {
    let mut nodes = BTreeMap::new();
    nodes.insert(id, row(&props));
    let xml = format!(
        r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="{id}" /></Level>"#
    );
    let decoded = decode_with(&xml, &nodes, &BTreeMap::new())?;
    match decoded.nodes.as_slice() {
        [SdtNode::Field { data_type, .. }] => Ok(data_type.clone()),
        other => Err(format!("expected one field, got {other:?}")),
    }
}

#[test]
fn an_untyped_item_with_a_length_is_numeric_at_that_length_as_the_sdk_reads_it() {
    // Delivery.Expiration (stored row EntityId 65): no type code and no domain, Length 18. The SDK
    // text reads `Expiration : NUMERIC(18)`.
    let data_type = only_item_type(
        65,
        vec![
            ("Name", "Expiration"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("Length", "18"),
            ("AttMaxLen", "18"),
        ],
    )
    .expect("Expiration decodes");
    assert_eq!(
        data_type,
        DataType::Numeric {
            precision: 18,
            scale: 0
        }
    );
}

#[test]
fn an_untyped_item_with_no_length_is_numeric_four_as_the_sdk_reads_it() {
    // GeolocationProximityAlert.Radius: a row with only Name and OBJ_TYPE. The SDK text reads
    // `Radius : NUMERIC(4)`.
    let data_type = only_item_type(7, vec![("Name", "Radius"), ("OBJ_TYPE", "id_OTYPE_SDT")])
        .expect("Radius decodes");
    assert_eq!(
        data_type,
        DataType::Numeric {
            precision: 4,
            scale: 0
        }
    );
}

#[test]
fn an_untyped_item_with_decimals_is_refused_by_name() {
    // SYNTHETIC row: no stored row of this shape exists on the evidence KBs, and the SDK has no
    // form for decimals on an untyped item. It must stay refused, not be read as NUMERIC.
    let error = only_item_type(
        9,
        vec![
            ("Name", "Amount"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("Length", "8"),
            ("Decimals", "2"),
        ],
    )
    .unwrap_err();
    assert!(error.contains("item 'Amount'"), "{error}");
    assert!(error.contains("carries decimals"), "{error}");
}

#[test]
fn a_character_item_with_no_stored_length_takes_the_sdk_length_twenty() {
    // AnalyticsPurchase.CurrencyCode (stored row, ATTCUSTOMTYPE 5, no Length): the SDK text reads
    // `CurrencyCode : CHARACTER(20)`.
    let data_type = only_item_type(
        11,
        vec![
            ("Name", "CurrencyCode"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>5</DataType></AttCustomType>",
            ),
        ],
    )
    .expect("CurrencyCode decodes");
    assert_eq!(data_type, DataType::Character { length: 20 });
}

#[test]
fn a_varchar_item_with_no_stored_length_takes_the_sdk_length_forty() {
    // AnalyticsKeyValue.Key (stored row, ATTCUSTOMTYPE 13, no Length, no AttMaxLen): the SDK text
    // reads `Key : VARCHAR(40)`.
    let data_type = only_item_type(
        13,
        vec![
            ("Name", "Key"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>13</DataType></AttCustomType>",
            ),
        ],
    )
    .expect("Key decodes");
    assert_eq!(data_type, DataType::Varchar { length: 40 });
}

#[test]
fn a_varchar_item_with_only_an_attmaxlen_is_refused_not_defaulted() {
    // SYNTHETIC row: a VARCHAR that stores AttMaxLen but no Length. The default applies only when no
    // length-bearing value is stored, so this row is refused rather than read as VARCHAR(40).
    let error = only_item_type(
        15,
        vec![
            ("Name", "Note"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>13</DataType></AttCustomType>",
            ),
            ("AttMaxLen", "100"),
        ],
    )
    .unwrap_err();
    assert!(error.contains("item 'Note'"), "{error}");
    assert!(error.contains("no stored length"), "{error}");
}

#[test]
fn a_typed_numeric_item_with_no_stored_length_reads_as_numeric_4_0_as_the_sdk_does() {
    // RustRecordProbe1015.Amount (SDTStructure 103|174, item 152|528). The stored row is 292 bytes and holds
    // exactly Name `Amount`, OBJ_TYPE `id_OTYPE_SDT` and ATTCUSTOMTYPE `<AttCustomType><DataType>4</DataType>`
    // (read-only sqlcmd on the disposable KBTeste). The SDK read of the same item gives `Length = 4`,
    // `Decimals = 0` (`SDTREAD` log, GX18, C:/tmp/gxe-sdt/readback/readback-clean.log) and the SDT text
    // `Amount : NUMERIC(4)`. The control `RustFreshSdt091817.Amount` stores Length 4, Decimals 0 and the SDK
    // generates it as `short gxTpr_Amount`, which is the same width the engine emits for NUMERIC(4,0).
    let data_type = only_item_type(
        17,
        vec![
            ("Name", "Amount"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>4</DataType></AttCustomType>",
            ),
        ],
    )
    .expect("the SDK reads a typed NUMERIC item with no stored length as NUMERIC(4,0)");
    assert_eq!(
        data_type,
        DataType::Numeric {
            precision: 4,
            scale: 0
        }
    );
}

#[test]
fn a_numeric_item_with_only_an_attmaxlen_is_refused_not_defaulted() {
    // SYNTHETIC row: a NUMERIC that stores AttMaxLen but no Length. The SDK read rule covers the plain leaf
    // with no length-bearing value at all; this row stays refused.
    let error = only_item_type(
        18,
        vec![
            ("Name", "Total"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>4</DataType></AttCustomType>",
            ),
            ("AttMaxLen", "8"),
        ],
    )
    .unwrap_err();
    assert!(error.contains("item 'Total'"), "{error}");
    assert!(error.contains("no stored length"), "{error}");
}

#[test]
fn a_numeric_item_with_decimals_and_no_length_is_refused_not_defaulted() {
    // SYNTHETIC row: a NUMERIC that stores Decimals 2 but no Length. The read rule covers no stored Length
    // and no stored decimals; this row stays refused.
    let error = only_item_type(
        19,
        vec![
            ("Name", "Rate"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>4</DataType></AttCustomType>",
            ),
            ("Decimals", "2"),
        ],
    )
    .unwrap_err();
    assert!(error.contains("item 'Rate'"), "{error}");
    assert!(error.contains("no stored length"), "{error}");
}

#[test]
fn a_structure_whose_node_has_no_row_is_refused_not_emitted_empty() {
    // Item 999 is named by the served XML but has no stored row: the structure is inconsistent.
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="999" /></Level>"#;
    let error = decode_with(xml, &BTreeMap::new(), &BTreeMap::new()).unwrap_err();
    assert!(error.contains("item 999"), "{error}");
    assert!(error.contains("has no stored row"), "{error}");
}

#[test]
fn a_structure_with_no_items_is_refused_not_emitted_empty() {
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"></Level>"#;
    let error = decode_with(xml, &BTreeMap::new(), &BTreeMap::new()).unwrap_err();
    assert!(error.contains("has no items"), "{error}");
}

#[test]
fn an_sdt_whose_served_items_decode_to_no_field_is_an_error_not_an_empty_class() {
    // The stored form has items (the served XML names two) but the decoder would produce no field:
    // an SDT that decodes to zero fields while its structure has items is an error, never an empty
    // class. The guard is the root-level no-items check in `decode`.
    let mut nodes = BTreeMap::new();
    // Each item has no type and carries decimals, which no SDK form covers, so each refuses.
    nodes.insert(
        601,
        row(&[
            ("Name", "Ghost"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("Decimals", "2"),
        ]),
    );
    nodes.insert(
        602,
        row(&[
            ("Name", "Ghost2"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("Decimals", "2"),
        ]),
    );
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="601" /><Item Id="602" /></Level>"#;
    // Both items refuse, so the SDT is an error, not a class with zero fields.
    let error = decode_with(xml, &nodes, &BTreeMap::new()).unwrap_err();
    assert!(error.contains("no stored type"), "{error}");
}

#[test]
fn a_nested_sdt_structure_type_is_refused_by_name() {
    // A structure reference to a non-SDT type GUID is a nested SDT level, which is not mapped.
    let mut nodes = BTreeMap::new();
    nodes.insert(
        603,
        row(&[
            ("Name", "Inner"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>254</DataType><Guid>&lt;StructureTypeReference&gt;&lt;Type&gt;a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3&lt;/Type&gt;&lt;Id&gt;151&lt;/Id&gt;&lt;/StructureTypeReference&gt;</Guid></AttCustomType>",
            ),
        ]),
    );
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="603" /></Level>"#;
    let error = decode_with(xml, &nodes, &BTreeMap::new()).unwrap_err();
    assert!(error.contains("item 'Inner'"), "{error}");
    assert!(
        error.contains("nested SDT levels are not mapped"),
        "{error}"
    );
}

#[test]
fn a_collection_of_scalars_is_refused_by_name() {
    // FacebookAccessToken.Permissions (stored row EntityId 124) is a CHARACTER collection. A scalar
    // collection (`GxSimpleCollection<T>`) is not mapped by the item decoder, so it is refused.
    let mut nodes = BTreeMap::new();
    nodes.insert(
        124,
        row(&[
            ("Name", "Permissions"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>5</DataType></AttCustomType>",
            ),
            ("AttCollection", "True"),
        ]),
    );
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Item Id="124" /></Level>"#;
    let error = decode_with(xml, &nodes, &BTreeMap::new()).unwrap_err();
    assert!(error.contains("item 'Permissions'"), "{error}");
    assert!(error.contains("collection item is not mapped"), "{error}");
    assert!(
        error.contains("no captured SDK C# form for an item-level collection"),
        "{error}"
    );
}

#[test]
fn an_unknown_element_in_the_served_structure_is_refused() {
    let xml = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Other Id="5" /></Level>"#;
    let error = decode_with(xml, &BTreeMap::new(), &BTreeMap::new()).unwrap_err();
    assert!(error.contains("unsupported element <Other>"), "{error}");
}

#[test]
fn the_portable_json_round_trips_through_the_emitter_form() {
    let decoded = decode_with(RSN03944700_XML, &rsn03944700_nodes(), &BTreeMap::new())
        .expect("RSN03944700 decodes");
    let json = decoded.to_portable_json().expect("portable json");
    let parsed: stored_sdt::PortableSdt = serde_json::from_str(&json).expect("parse");
    assert_eq!(parsed.fields, decoded.nodes);
}

/// The stored levels of RSN03944700 name their classes as the SDK declares them. `Rows` is a collection:
/// its item class is `RSN03944700_RowsItem` (SDK: `type_SdtRSN03944700_RowsItem.cs`, `XmlRoot`
/// `RSN03944700.RowsItem`; the Procedure variable `RTDX765000A.Row` declares it). `Child` is a
/// non-collection level, class `RSN03944700_Child` (SDK: `type_SdtRSN03944700_Child.cs`).
#[test]
fn the_stored_levels_of_rsn03944700_name_their_classes_as_the_sdk_declares_them() {
    let decoded =
        decode_with(RSN03944700_XML, &rsn03944700_nodes(), &BTreeMap::new()).expect("decodes");
    let rows = decoded
        .levels
        .iter()
        .find(|l| l.id == 219)
        .expect("level 219");
    assert_eq!(rows.schema, "RSN03944700_RowsItem");
    assert_eq!(rows.dotted, "RSN03944700.RowsItem");
    assert!(rows.collection_item);
    let child = decoded
        .levels
        .iter()
        .find(|l| l.id == 218)
        .expect("level 218");
    assert_eq!(child.schema, "RSN03944700_Child");
    assert_eq!(child.dotted, "RSN03944700.Child");
    assert!(!child.collection_item);
}

/// SYNTHETIC structure over the real stored properties of level 221 (`Rows`, `idCollectionItemName`
/// `Entry`, the SDT `RCE06829400`). The class is `RCE06829400_Entry` (SDK: `type_SdtRCE06829400_Entry.cs`;
/// `AV10Row = new SdtRCE06829400_Entry(context)` in `artm49305800.cs`).
#[test]
fn a_stored_item_name_is_the_class_of_its_collection_item() {
    let xml = r#"<Level Id="1"><Level Id="221"><Item Id="222" /></Level></Level>"#;
    let mut nodes = BTreeMap::new();
    nodes.insert(
        221,
        row(&[
            ("Name", "Rows".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            ("AttCollection", "True".to_string()),
            ("idCollectionItemName", "Entry".to_string()),
        ]),
    );
    nodes.insert(
        222,
        row(&[
            ("Name", "Amount".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            code("4"),
            ("Length", "4".to_string()),
        ]),
    );
    let node_properties = |id: i32| nodes.get(&id).cloned();
    let sdt_name = |_: i32| -> Option<String> { None };
    let domain_type = |_: i32| -> Option<DataType> { None };
    let lookup = Lookup {
        node_properties: &node_properties,
        sdt_name: &sdt_name,
        domain_type: &domain_type,
    };
    let decoded = stored_sdt::decode(xml, "RCE06829400", &lookup).expect("decodes");
    let entry = decoded
        .levels
        .iter()
        .find(|l| l.id == 221)
        .expect("level 221");
    assert_eq!(entry.schema, "RCE06829400_Entry");
    assert_eq!(entry.dotted, "RCE06829400.Entry");
    assert!(entry.collection_item);
}

/// SYNTHETIC structure over the real stored properties of `NativeDeepSdt85299100`: `Child` (not a
/// collection) holds `NestedCollection` (a collection with no stored item name), which holds `NestedValue`
/// VARCHAR(24). The item class is `NativeDeepSdt85299100_Child_NestedCollectionItem` (SDK:
/// `type_SdtNativeDeepSdt85299100_Child_NestedCollectionItem.cs`; `AV8V = new ...` in `artda386800e.cs`),
/// and its stored dotted name is the `DataTypeString` the variable `RTDA386800E.V` carries.
#[test]
fn a_collection_under_a_nested_level_is_named_under_that_level() {
    let xml = r#"<Level Id="1"><Level Id="230"><Level Id="231"><Item Id="232" /></Level></Level></Level>"#;
    let mut nodes = BTreeMap::new();
    nodes.insert(
        230,
        row(&[
            ("Name", "Child".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            ("AttCollection", "False".to_string()),
        ]),
    );
    nodes.insert(
        231,
        row(&[
            ("Name", "NestedCollection".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            ("AttCollection", "True".to_string()),
        ]),
    );
    nodes.insert(
        232,
        row(&[
            ("Name", "NestedValue".to_string()),
            ("OBJ_TYPE", "id_OTYPE_SDT".to_string()),
            code("13"),
            ("Length", "24".to_string()),
        ]),
    );
    let node_properties = |id: i32| nodes.get(&id).cloned();
    let sdt_name = |_: i32| -> Option<String> { None };
    let domain_type = |_: i32| -> Option<DataType> { None };
    let lookup = Lookup {
        node_properties: &node_properties,
        sdt_name: &sdt_name,
        domain_type: &domain_type,
    };
    let decoded = stored_sdt::decode(xml, "NativeDeepSdt85299100", &lookup).expect("decodes");
    let item = decoded
        .levels
        .iter()
        .find(|l| l.id == 231)
        .expect("level 231");
    assert_eq!(
        item.schema,
        "NativeDeepSdt85299100_Child_NestedCollectionItem"
    );
    assert_eq!(
        item.dotted,
        "NativeDeepSdt85299100.Child.NestedCollectionItem"
    );
    assert!(item.collection_item);
    let child = decoded
        .levels
        .iter()
        .find(|l| l.id == 230)
        .expect("level 230");
    assert!(!child.collection_item);
}

/// The decoder's level names and the emitter's class names are two namings of one rule. For the real
/// RSN03944700 structure, every level the decoder records is a class the emitter writes, under the same
/// name.
#[test]
fn every_stored_level_is_a_class_the_emitter_writes_under_the_same_name() {
    use gx_engine::csharp::sdt::SdtClass;
    let decoded =
        decode_with(RSN03944700_XML, &rsn03944700_nodes(), &BTreeMap::new()).expect("decodes");
    let mut class = SdtClass::new("RSN03944700");
    class.nodes = decoded.nodes.clone();
    let emitted: Vec<String> = class.level_classes().into_iter().map(|c| c.name).collect();
    for level in &decoded.levels {
        assert!(
            emitted.contains(&level.schema),
            "level {} is {} but the emitter writes {emitted:?}",
            level.id,
            level.schema
        );
    }
    assert_eq!(emitted.len(), decoded.levels.len(), "{emitted:?}");
}

/// The served structure of SDT ProgramNames (EntityId 147, KBTeste). Its root is the collection level 181,
/// and the three items sit directly under the root: the SDK has no class for the root, only the item
/// class `ProgramNames_ProgramName` (SDK: `listprograms.cs`, `GXBaseCollection<SdtProgramNames_ProgramName>`
/// with `"ProgramName"`, and `gxTpr_Name`, `gxTpr_Description`, `gxTpr_Link` on the item).
const PROGRAM_NAMES_XML: &str = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="181"><Item Id="481" /><Item Id="482" /><Item Id="483" /></Level>"#;

/// The stored rows of SDT ProgramNames, as the KBTeste EntityVersion table holds them (version 1).
fn program_names_nodes() -> BTreeMap<i32, String> {
    let mut nodes = BTreeMap::new();
    nodes.insert(
        181,
        row(&[
            ("Name", "ProgramNames"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("AttCollection", "True"),
            ("idCollectionItemName", "ProgramName"),
        ]),
    );
    nodes.insert(
        481,
        row(&[
            ("Name", "Name"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-51"),
        ]),
    );
    nodes.insert(
        482,
        row(&[
            ("Name", "Description"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            (
                "ATTCUSTOMTYPE",
                "<AttCustomType><DataType>13</DataType></AttCustomType>",
            ),
            ("Length", "255"),
            ("Decimals", "0"),
            ("AttMaxLen", "255"),
        ]),
    );
    nodes.insert(
        483,
        row(&[
            ("Name", "Link"),
            ("OBJ_TYPE", "id_OTYPE_SDT"),
            ("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-8"),
        ]),
    );
    nodes
}

/// Decodes a structure under the SDT name `sdt`, with the domains `domains` for its based-on items.
fn decode_named(
    sdt: &str,
    xml: &str,
    nodes: &BTreeMap<i32, String>,
    domains: &BTreeMap<i32, DataType>,
) -> Result<stored_sdt::SdtStructure, String> {
    let node_properties = |id: i32| nodes.get(&id).cloned();
    let sdt_name = |_: i32| None;
    let domain_type = |id: i32| domains.get(&id).cloned();
    let lookup = Lookup {
        node_properties: &node_properties,
        sdt_name: &sdt_name,
        domain_type: &domain_type,
    };
    stored_sdt::decode(xml, sdt, &lookup)
}

/// A collection root is not a record: its items are the item class, and the root level is recorded as that
/// class, so a Procedure variable typed by the root level (`ProgramName`) names `ProgramNames_ProgramName`.
#[test]
fn a_collection_root_is_its_item_class_and_its_level_names_that_class() {
    let mut domains = BTreeMap::new();
    domains.insert(51, DataType::Varchar { length: 100 });
    domains.insert(8, DataType::Varchar { length: 1000 });
    let decoded = decode_named(
        "ProgramNames",
        PROGRAM_NAMES_XML,
        &program_names_nodes(),
        &domains,
    )
    .expect("the collection root decodes");
    assert_eq!(decoded.root_collection.as_deref(), Some("ProgramName"));
    let names: Vec<&str> = decoded
        .nodes
        .iter()
        .map(|node| match node {
            SdtNode::Field { name, .. } => name.as_str(),
            SdtNode::Level { name, .. } => name.as_str(),
        })
        .collect();
    assert_eq!(names, vec!["Name", "Description", "Link"]);
    assert_eq!(
        decoded.levels,
        vec![stored_sdt::StoredLevel {
            id: 181,
            schema: "ProgramNames_ProgramName".into(),
            dotted: "ProgramNames.ProgramName".into(),
            collection_item: true,
        }]
    );
    let json = decoded.to_portable_json().expect("portable json");
    let parsed: stored_sdt::PortableSdt = serde_json::from_str(&json).expect("parse");
    assert_eq!(parsed.root_collection.as_deref(), Some("ProgramName"));
    assert_eq!(parsed.fields, decoded.nodes);
}

/// The root test reads only the root: a record root reports no collection, a collection root its item.
#[test]
fn the_root_collection_test_reads_only_the_root_flag() {
    let nodes = program_names_nodes();
    let node_properties = |id: i32| nodes.get(&id).cloned();
    assert_eq!(
        stored_sdt::root_collection_item(PROGRAM_NAMES_XML, "ProgramNames", &node_properties)
            .expect("reads the root"),
        Some("ProgramName".to_string())
    );
    let record_nodes = rsn03944700_nodes();
    let record_properties = |id: i32| record_nodes.get(&id).cloned();
    assert_eq!(
        stored_sdt::root_collection_item(RSN03944700_XML, "RSN03944700", &record_properties)
            .expect("reads the root"),
        None
    );
}
