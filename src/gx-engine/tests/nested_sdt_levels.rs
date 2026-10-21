//! A Procedure variable typed by a nested SDT level is read through the reader, end to end: the level
//! map is built from the stored SDT structures, the variable takes its class, and the SDT that holds the
//! level is an explicit `Uses` reference.
//!
//! The level ids, stored properties and structure shapes follow the rows read on the disposable
//! KBTeste (`RSN03944700`: level 218 `Child`, not a collection; level 219 `Rows`, a collection with no
//! stored item name; `RCE06829400`: level 221 `Rows` with the stored item name `Entry`). The SDT names
//! (`NestedProbe`, `NestedEntry`) and the Procedures are synthetic. Each synthetic name is marked below.

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart};
use gx_engine::model::DataType;
use gx_engine::udm::mock::{EntityVersionCompositionRecord, EntityVersionRecord};
use gx_engine::udm::{UdmClient, UdmReader, UdmWriter};
use std::collections::BTreeMap;
use std::sync::Arc;

const SDT_ENTITY_TYPE: i32 = 40;
const SDT_STRUCTURE_TYPE: i32 = 103;
const LEVEL_TYPE: i32 = 151;
const ITEM_TYPE: i32 = 152;
const VARIABLE_STRUCTURE_REF: &str = "a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3";

/// The stored form of an `EntityVersionProperties` row.
fn props(pairs: &[(&str, &str)]) -> String {
    let mut out = String::from("<Properties>");
    for (name, value) in pairs {
        out.push_str(&format!(
            "<Property><Name>{name}</Name><Value>{}</Value></Property>",
            escape(value)
        ));
    }
    out.push_str("</Properties>");
    out
}

fn escape(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

/// An `ATTCUSTOMTYPE` value, as stored (unescaped XML; `props` escapes it once).
fn code(code: &str) -> String {
    format!("<AttCustomType><DataType>{code}</DataType></AttCustomType>")
}

/// Creates an SDT with its `STRUCTURE` (the stored `SDTStructure` XML), then stores each node row and
/// composes it under the structure, as the Knowledge Base does. Returns the SDT's entity id.
fn seed_sdt(
    client: &UdmClient,
    name: &str,
    structure_xml: &str,
    nodes: &[(i32, i32, String)],
) -> i32 {
    let mut object = KbObject::new(
        ObjectId::new(name).unwrap(),
        name,
        ObjectKind::StructuredDataType,
    );
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: structure_xml.into(),
    });
    let identity = UdmWriter::new(Arc::new(client.clone()))
        .create_object(&object)
        .expect("create the SDT");
    let structure = client
        .get_compositions(SDT_ENTITY_TYPE, identity.entity_id, identity.version_id)
        .expect("compositions of the SDT")
        .into_iter()
        .find(|c| c.component_entity_type_id == SDT_STRUCTURE_TYPE)
        .expect("the SDT has a structure part");
    for (entity_type, id, row) in nodes {
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: *entity_type,
                entity_id: *id,
                entity_version_id: 1,
                data: Vec::new(),
                properties: Some(row.clone()),
            })
            .expect("store the node row");
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: SDT_STRUCTURE_TYPE,
                compound_entity_id: structure.component_entity_id,
                compound_entity_version_id: structure.component_entity_version_id,
                component_entity_type_id: *entity_type,
                component_entity_id: *id,
                component_entity_version_id: 1,
            })
            .expect("compose the node under the structure");
    }
    identity.entity_id
}

/// A Procedure whose VARIABLES part declares one variable typed by the nested level `level_id`.
fn procedure_typed_by_level(
    name: &str,
    variable: &str,
    level_id: i32,
    declared: Option<&str>,
) -> KbObject {
    let guid = format!(
        "&lt;StructureTypeReference&gt;&lt;Type&gt;{VARIABLE_STRUCTURE_REF}&lt;/Type&gt;&lt;Id&gt;{level_id}&lt;/Id&gt;&lt;/StructureTypeReference&gt;"
    );
    let fragment =
        format!("<AttCustomType><DataType>254</DataType><Guid>{guid}</Guid></AttCustomType>");
    let mut pairs: Vec<(&str, &str)> =
        vec![("OBJ_TYPE", "id_OTYPE_VAR"), ("ATTCUSTOMTYPE", &fragment)];
    if let Some(declared) = declared {
        pairs.push(("DataTypeString", declared));
    }
    let variables = format!(
        "<Variables><Variable Name=\"{variable}\"><Properties>{}</Properties></Variable></Variables>",
        pairs
            .iter()
            .map(|(k, v)| format!(
                "<Property><Name>{k}</Name><Value>{}</Value></Property>",
                escape(v)
            ))
            .collect::<String>()
    );
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Page = 1\n".into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: variables,
    });
    object
}

fn write_and_read(client: &UdmClient, object: &KbObject) -> KbObject {
    UdmWriter::new(Arc::new(client.clone()))
        .create_object(object)
        .expect("create the Procedure");
    UdmReader::new(Arc::new(client.clone()))
        .read_object_by_name(&object.name)
        .expect("read the Procedure")
        .expect("the Procedure exists")
}

fn held_variables(object: &KbObject) -> Option<BTreeMap<String, DataType>> {
    let part = object.source_parts.iter().find(|p| p.name == "VARIABLES")?;
    Some(serde_json::from_str(&part.source).expect("the held part is the portable declarations"))
}

/// The structure of `NestedProbe`, shaped like RSN03944700: a root item, a non-collection level `Child`
/// (218) holding one item, and a collection level `Rows` (219) with no stored item name.
const NESTED_PROBE_XML: &str = "<Level Id=\"1\"><Item Id=\"3\"/><Level Id=\"218\"><Item Id=\"221\"/></Level><Level Id=\"219\"><Item Id=\"220\"/></Level></Level>";

/// The structure of `NestedEntry`, shaped like RCE06829400: a collection level `Rows` (221) whose stored
/// item name is `Entry`.
const NESTED_ENTRY_XML: &str =
    "<Level Id=\"1\"><Level Id=\"221\"><Item Id=\"222\"/></Level></Level>";

/// Seeds both synthetic SDTs and returns the entity id of `NestedProbe`.
fn seed_both(client: &UdmClient) -> i32 {
    let probe = seed_sdt(
        client,
        "NestedProbe",
        NESTED_PROBE_XML,
        &[
            (
                ITEM_TYPE,
                3,
                props(&[
                    ("Name", "Amount"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("ATTCUSTOMTYPE", &code("4")),
                    ("Length", "9"),
                ]),
            ),
            (
                LEVEL_TYPE,
                218,
                props(&[
                    ("Name", "Child"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("AttCollection", "False"),
                ]),
            ),
            (
                ITEM_TYPE,
                221,
                props(&[
                    ("Name", "Amount"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("ATTCUSTOMTYPE", &code("4")),
                    ("Length", "3"),
                ]),
            ),
            (
                LEVEL_TYPE,
                219,
                props(&[
                    ("Name", "Rows"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("AttCollection", "True"),
                ]),
            ),
            (
                ITEM_TYPE,
                220,
                props(&[
                    ("Name", "Amount"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("ATTCUSTOMTYPE", &code("4")),
                    ("Length", "4"),
                ]),
            ),
        ],
    );
    seed_sdt(
        client,
        "NestedEntry",
        NESTED_ENTRY_XML,
        &[
            (
                LEVEL_TYPE,
                221,
                props(&[
                    ("Name", "Rows"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("AttCollection", "True"),
                    ("idCollectionItemName", "Entry"),
                ]),
            ),
            (
                ITEM_TYPE,
                222,
                props(&[
                    ("Name", "Amount"),
                    ("OBJ_TYPE", "id_OTYPE_SDT"),
                    ("ATTCUSTOMTYPE", &code("4")),
                    ("Length", "4"),
                ]),
            ),
        ],
    );
    probe
}

/// The real `Row` of `RTDX765000A`: level 219 is a collection item of `NestedProbe` (`RowsItem`).
#[test]
fn a_collection_item_level_is_typed_by_its_class_and_uses_its_sdt() {
    let client = UdmClient::new_in_memory();
    seed_both(&client);
    let read = write_and_read(
        &client,
        &procedure_typed_by_level("ProbeRow", "Row", 219, Some("NestedProbe.RowsItem")),
    );
    assert!(read.refusal.is_none(), "{:?}", read.refusal);
    assert_eq!(
        held_variables(&read)
            .expect("the variables are held")
            .get("Row"),
        Some(&DataType::Record {
            schema: "NestedProbe_RowsItem".into()
        })
    );
    assert!(
        read.references.contains(&Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new("NestedProbe").unwrap(),
        }),
        "the SDT that holds the level is a Uses reference: {:?}",
        read.references
    );
}

/// The real `Row` of `RCE06829400` (level 221): its stored item name `Entry` is the class name.
#[test]
fn a_collection_item_with_a_stored_item_name_takes_that_name() {
    let client = UdmClient::new_in_memory();
    seed_both(&client);
    let read = write_and_read(
        &client,
        &procedure_typed_by_level("EntryRow", "Row", 221, None),
    );
    assert!(read.refusal.is_none(), "{:?}", read.refusal);
    assert_eq!(
        held_variables(&read)
            .expect("the variables are held")
            .get("Row"),
        Some(&DataType::Record {
            schema: "NestedEntry_Entry".into()
        })
    );
}

/// SYNTHETIC: a non-collection level (`Child`, 218) is not mapped, so a variable typed by it refuses
/// the Procedure by name.
#[test]
fn a_non_collection_level_refuses_the_procedure_by_name() {
    let client = UdmClient::new_in_memory();
    seed_both(&client);
    let read = write_and_read(
        &client,
        &procedure_typed_by_level("ChildVar", "Child", 218, None),
    );
    let refusal = read.refusal.expect("the Procedure is refused");
    assert!(refusal.contains("variable 'Child'"), "{refusal}");
    assert!(
        refusal.contains("is not one item of a collection"),
        "{refusal}"
    );
}

/// SYNTHETIC: a level id no SDT of the model holds refuses the Procedure by name.
#[test]
fn a_level_no_sdt_holds_refuses_the_procedure_by_name() {
    let client = UdmClient::new_in_memory();
    seed_both(&client);
    let read = write_and_read(
        &client,
        &procedure_typed_by_level("GhostVar", "Ghost", 999, None),
    );
    let refusal = read.refusal.expect("the Procedure is refused");
    assert!(refusal.contains("variable 'Ghost'"), "{refusal}");
    assert!(
        refusal.contains("is not a level of an SDT this engine maps"),
        "{refusal}"
    );
}

/// The served structure of `NestedProbe` names the collection `Rows` and its item as the SDK does:
/// `item_name` is null when no item name is stored. This is what the emitter's index is built from.
#[test]
fn the_served_structure_has_the_collection_the_variable_names() {
    let client = UdmClient::new_in_memory();
    seed_both(&client);
    let read = UdmReader::new(Arc::new(client.clone()))
        .read_object_by_name("NestedProbe")
        .expect("read")
        .expect("exists");
    assert!(
        read.refusal.is_none(),
        "NestedProbe refusal: {:?}",
        read.refusal
    );
    let structure = read
        .source_parts
        .iter()
        .find(|p| p.name == "STRUCTURE")
        .expect("the served structure");
    assert!(
        structure
            .source
            .contains("\"name\":\"Rows\",\"collection\":true,\"item_name\":null"),
        "{}",
        structure.source
    );
}

/// SYNTHETIC: a third SDT whose own structure names the stored level 219 and its item 220, so two
/// structures hold one level. No variable may pick one of them: a Procedure variable typed by level 219
/// is refused by name, with the reason that the level is held by two structures.
#[test]
fn a_level_held_by_two_structures_refuses_the_variable_by_name() {
    let client = UdmClient::new_in_memory();
    seed_both(&client);
    let shared = seed_sdt(
        &client,
        "NestedShared",
        "<Level Id=\"1\"><Level Id=\"219\"><Item Id=\"220\"/></Level></Level>",
        &[],
    );
    let shared_structure = client
        .get_compositions(SDT_ENTITY_TYPE, shared, 1)
        .expect("compositions")
        .into_iter()
        .find(|c| c.component_entity_type_id == SDT_STRUCTURE_TYPE)
        .expect("NestedShared has a structure");
    for (entity_type, id) in [(LEVEL_TYPE, 219), (ITEM_TYPE, 220)] {
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: SDT_STRUCTURE_TYPE,
                compound_entity_id: shared_structure.component_entity_id,
                compound_entity_version_id: shared_structure.component_entity_version_id,
                component_entity_type_id: entity_type,
                component_entity_id: id,
                component_entity_version_id: 1,
            })
            .expect("compose the shared node");
    }
    let read = write_and_read(
        &client,
        &procedure_typed_by_level("SharedRow", "Row", 219, None),
    );
    let refusal = read.refusal.expect("the Procedure is refused");
    assert!(refusal.contains("variable 'Row'"), "{refusal}");
    assert!(refusal.contains("held by two structures"), "{refusal}");
}
