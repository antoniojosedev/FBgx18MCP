//! Native Transaction STRUCTURE parts, from the stored blobs of KBTeste, decode to the physical table the
//! SDK generated for the same Transaction.
//!
//! The fixtures are the stored bytes of the STRUCTURE part (`EntityVersion` of the Structure entity) of
//! LiveMoveAttribute and LiveMoveAttributeRules2, read read-only from `GX_KB_KBTeste`. The attribute names
//! are the model names of the Attribute entities (EntityType 27) those blobs reference. The SDK's own SQL
//! for LiveMoveAttribute (`NetModel/web/livemoveattribute.cs`, `CursorDef` and `ParDef`) names the table
//! `[LiveMoveAttribute]`, its key `[LiveMoveAttributeId]` as `Int16(4,0)`, and the columns
//! `[LiveMoveAttributeCode]` and `[LiveMoveAttributeDescription]` as `NChar(20)`.

use gx_engine::catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::model::DataType;
use gx_engine::table::{
    SqlDialect, TableForeignKey, derive_tables_from_catalog, derive_tables_from_transaction,
    derive_tables_partial, generate_database_ddl,
};
use gx_engine::transaction::{KeyReference, TransactionStructure};
use gx_engine::udm::decode_blob;
use gx_engine::udm::stored_transaction::{Lookup, decode};

const LMA_HEX: &str = include_str!("fixtures/transactions/LiveMoveAttribute.STRUCTURE.hex.txt");
const LMA_XML: &str = include_str!("fixtures/transactions/LiveMoveAttribute.STRUCTURE.xml");
const LMR2_HEX: &str =
    include_str!("fixtures/transactions/LiveMoveAttributeRules2.STRUCTURE.hex.txt");
const LMR2_XML: &str = include_str!("fixtures/transactions/LiveMoveAttributeRules2.STRUCTURE.xml");

/// The model names of the Attribute entities the fixtures reference (EntityType 27, model 2).
fn attribute_names(id: i32) -> Option<String> {
    let name = match id {
        27 => "LiveMoveAttributeId",
        28 => "LiveMoveAttributeCode",
        29 => "LiveMoveAttributeDescription",
        33 => "LiveMoveAttributeRules2Id",
        34 => "LiveMoveAttributeRules2Description",
        35 => "LiveMoveAttributeRules2Code",
        _ => return None,
    };
    Some(name.to_string())
}

/// The stored type of each Attribute the fixtures reference, as its row decodes (the `stored_variables`
/// unit tests decode these same rows): the keys (27, 33) are Numeric from their stored `Length 4`, the
/// others are Character with no stored length, which takes the SDK's length 20.
fn attribute_types(id: i32) -> Result<DataType, String> {
    match id {
        27 | 33 => Ok(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        28 | 29 | 34 | 35 => Ok(DataType::Character { length: 20 }),
        other => Err(format!("no stored type for attribute {other}")),
    }
}

/// The stored bytes of a fixture: its hex text, without the line break.
fn stored_bytes(hex: &str) -> Vec<u8> {
    let hex = hex.trim();
    assert_eq!(hex.len() % 2, 0, "a hex fixture has an even length");
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).expect("hex digits"))
        .collect()
}

/// The structure of a stored blob, exactly as the reader decodes it: the envelope and gzip first, then
/// the XML, then the Transaction's own decoder.
fn structure_of(hex: &str, transaction: &str) -> TransactionStructure {
    let xml = decode_blob(&stored_bytes(hex)).expect("the stored blob decodes");
    let lookup = Lookup {
        attribute_name: &attribute_names,
        attribute_type: &attribute_types,
        attribute_super_key: &no_super_keys,
    };
    decode(&xml, transaction, &lookup).expect("the stored structure decodes")
}

/// The LiveMoveAttribute fixtures name no SuperTypeKey.
fn no_super_keys(_id: i32) -> Result<Option<String>, String> {
    Ok(None)
}

/// The SDK's types for the LiveMoveAttribute columns (`ParDef`): the key is `Int16(4,0)`, the others `NChar(20)`.
fn sdk_types(name: &str) -> Option<DataType> {
    match name {
        "LiveMoveAttributeId" => Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        "LiveMoveAttributeCode" | "LiveMoveAttributeDescription" => {
            Some(DataType::Character { length: 20 })
        }
        _ => None,
    }
}

#[test]
fn the_stored_blob_decodes_to_the_native_xml_it_was_read_from() {
    // The envelope (11 bytes) and the gzip stream are the only transformation: the text is the XML.
    assert_eq!(
        decode_blob(&stored_bytes(LMA_HEX)).expect("decodes"),
        LMA_XML
    );
    assert_eq!(
        decode_blob(&stored_bytes(LMR2_HEX)).expect("decodes"),
        LMR2_XML
    );
}

#[test]
fn livemoveattribute_names_its_attributes_by_model_name_and_keeps_its_key() {
    let structure = structure_of(LMA_HEX, "LiveMoveAttribute");
    assert_eq!(structure.root.name, "LiveMoveAttribute");
    let attributes: Vec<(&str, bool)> = structure
        .root
        .attributes
        .iter()
        .map(|a| (a.name.as_str(), a.is_key))
        .collect();
    assert_eq!(
        attributes,
        vec![
            ("LiveMoveAttributeId", true),
            ("LiveMoveAttributeCode", false),
            ("LiveMoveAttributeDescription", false),
        ]
    );
}

#[test]
fn the_physical_table_of_livemoveattribute_is_the_table_the_sdk_queries() {
    let structure = structure_of(LMA_HEX, "LiveMoveAttribute");
    let tables = derive_tables_from_transaction(&structure, sdk_types).expect("the table derives");
    assert_eq!(tables.len(), 1, "one root table, no child levels");
    let table = &tables[0];
    assert_eq!(table.name, "LiveMoveAttribute");
    let columns: Vec<(&str, &DataType, bool)> = table
        .columns
        .iter()
        .map(|c| (c.name.as_str(), &c.data_type, c.is_key))
        .collect();
    assert_eq!(
        columns,
        vec![
            (
                "LiveMoveAttributeId",
                &DataType::Numeric {
                    precision: 4,
                    scale: 0
                },
                true
            ),
            (
                "LiveMoveAttributeCode",
                &DataType::Character { length: 20 },
                false
            ),
            (
                "LiveMoveAttributeDescription",
                &DataType::Character { length: 20 },
                false
            ),
        ]
    );
    assert_eq!(table.primary_key, vec!["LiveMoveAttributeId".to_string()]);
}

#[test]
fn the_attributes_keep_the_stored_order_not_the_order_of_their_ids() {
    // Stored ids are 33, 35, 34: the document order, and so the column order, is 33, 35, 34.
    let structure = structure_of(LMR2_HEX, "LiveMoveAttributeRules2");
    let attributes: Vec<(&str, bool)> = structure
        .root
        .attributes
        .iter()
        .map(|a| (a.name.as_str(), a.is_key))
        .collect();
    assert_eq!(
        attributes,
        vec![
            ("LiveMoveAttributeRules2Id", true),
            ("LiveMoveAttributeRules2Code", false),
            ("LiveMoveAttributeRules2Description", false),
        ]
    );
}

#[test]
fn the_decoded_livemoveattribute_structure_derives_its_table_through_the_catalog() {
    // The reader holds the decoded structure as portable JSON, and the physical schema reads it from the
    // catalog. This is that path end to end, from the stored blob.
    let structure = structure_of(LMA_HEX, "LiveMoveAttribute");
    let mut transaction = KbObject::new(
        ObjectId::new("livemoveattribute-id").unwrap(),
        "LiveMoveAttribute",
        ObjectKind::Transaction,
    );
    transaction.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: serde_json::to_string(&structure).unwrap(),
    });
    let mut catalog = Catalog::empty();
    catalog.apply(0, &[Edit::Upsert(transaction)]).unwrap();

    let tables = derive_tables_from_catalog(&catalog).expect("the physical schema derives");
    let table = tables
        .iter()
        .find(|t| t.name == "LiveMoveAttribute")
        .expect("the LiveMoveAttribute table is derived");
    assert_eq!(table.primary_key, vec!["LiveMoveAttributeId".to_string()]);
    let columns: Vec<(&str, &DataType)> = table
        .columns
        .iter()
        .map(|c| (c.name.as_str(), &c.data_type))
        .collect();
    assert_eq!(
        columns,
        vec![
            (
                "LiveMoveAttributeId",
                &DataType::Numeric {
                    precision: 4,
                    scale: 0
                }
            ),
            ("LiveMoveAttributeCode", &DataType::Character { length: 20 }),
            (
                "LiveMoveAttributeDescription",
                &DataType::Character { length: 20 }
            ),
        ]
    );
}

#[test]
fn a_refused_transaction_names_its_refusal_in_the_physical_schema_error() {
    // Before this, the error said the STRUCTURE part was missing, which hid the reason the reader refused it.
    let mut refused = KbObject::new(
        ObjectId::new("plan070trn-id").unwrap(),
        "Plan070Trn",
        ObjectKind::Transaction,
    );
    refused.refusal = Some(
        "STRUCTURE: attribute 11 ('Plan070TrnId') of 'Plan070Trn': unsupported property SuperTypeKey"
            .into(),
    );
    let mut catalog = Catalog::empty();
    catalog.apply(0, &[Edit::Upsert(refused)]).unwrap();

    let error = derive_tables_from_catalog(&catalog).unwrap_err();
    assert!(
        error.contains(
            "Transaction 'Plan070Trn' is refused, so its tables cannot be derived: STRUCTURE: attribute 11"
        ),
        "{error}"
    );
    assert!(!error.contains("requires a STRUCTURE part"), "{error}");
}

/// One refused Transaction does not stop the others: every refusal is named, and the Transactions that
/// derive keep their tables. The refusal here is synthetic; LiveMoveAttribute still derives its table next to it.
#[test]
fn a_refused_transaction_is_named_and_the_others_still_derive() {
    let structure = structure_of(LMA_HEX, "LiveMoveAttribute");
    let mut derivable = KbObject::new(
        ObjectId::new("livemoveattribute-id").unwrap(),
        "LiveMoveAttribute",
        ObjectKind::Transaction,
    );
    derivable.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: serde_json::to_string(&structure).unwrap(),
    });
    let mut refused = KbObject::new(
        ObjectId::new("plan070trn-id").unwrap(),
        "Plan070Trn",
        ObjectKind::Transaction,
    );
    refused.refusal = Some(
        "STRUCTURE: attribute 11 ('Plan070TrnId') of 'Plan070Trn': synthetic refusal for the partial-derivation test"
            .into(),
    );
    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(derivable), Edit::Upsert(refused)])
        .unwrap();

    let (tables, failures) = derive_tables_partial(&catalog);

    assert!(
        tables.iter().any(|t| t.name == "LiveMoveAttribute"),
        "the derivable Transaction keeps its table: {:?}",
        tables.iter().map(|t| &t.name).collect::<Vec<_>>()
    );
    assert_eq!(failures.len(), 1, "{failures:?}");
    assert!(
        failures[0]
            .contains("Transaction 'Plan070Trn' is refused, so its tables cannot be derived")
            && failures[0].contains("synthetic refusal"),
        "{failures:?}"
    );
    // The fallible entry point still reports the refusal rather than returning a partial schema as success.
    let error = derive_tables_from_catalog(&catalog).unwrap_err();
    assert!(error.contains("Plan070Trn"), "{error}");
}

/// Plan070Trn (KBTeste, Transaction 43/5): one key, `Plan070TrnId` (Attribute 11), whose version 3 row stores
/// `SuperTypeKey` 12 and no type. Its type is inherited from attribute 12, `Plan071TrnId` (NUMERIC(4,0)).
const PLAN070TRN_HEX: &str = include_str!("fixtures/transactions/Plan070Trn.STRUCTURE.hex.txt");
const PLAN070TRN_XML: &str = include_str!("fixtures/transactions/Plan070Trn.STRUCTURE.xml");

fn numeric_4_0() -> DataType {
    DataType::Numeric {
        precision: 4,
        scale: 0,
    }
}

/// The model names of the Plan070Trn attributes (Attribute entities 11 and 12).
fn plan_attribute_names(id: i32) -> Option<String> {
    match id {
        11 => Some("Plan070TrnId".into()),
        12 => Some("Plan071TrnId".into()),
        _ => None,
    }
}

/// The types the reader resolves for those attributes: 11 inherits NUMERIC(4,0) from 12; 12 stores NUMERIC(4,0).
fn plan_attribute_types(id: i32) -> Result<DataType, String> {
    match id {
        11 | 12 => Ok(numeric_4_0()),
        other => Err(format!("no stored type for attribute {other}")),
    }
}

/// The SuperTypeKey of each attribute: 11 names 12, and 12 names none.
fn plan_super_keys(id: i32) -> Result<Option<String>, String> {
    match id {
        11 => Ok(Some("Plan071TrnId".into())),
        _ => Ok(None),
    }
}

/// The structure of the stored Plan070Trn blob, decoded as the reader decodes it.
fn plan070trn_structure() -> TransactionStructure {
    let xml = decode_blob(&stored_bytes(PLAN070TRN_HEX)).expect("the stored blob decodes");
    assert_eq!(xml, PLAN070TRN_XML, "the fixture is the stored XML");
    let lookup = Lookup {
        attribute_name: &plan_attribute_names,
        attribute_type: &plan_attribute_types,
        attribute_super_key: &plan_super_keys,
    };
    decode(&xml, "Plan070Trn", &lookup).expect("the stored structure decodes")
}

/// A synthetic Transaction whose only attribute is key `key` (Attribute `id`) of type `key_type`, optionally
/// naming `link` through SuperTypeKey.
fn one_key_structure(
    transaction: &str,
    id: i32,
    key: &str,
    key_type: DataType,
    link: Option<&str>,
) -> TransactionStructure {
    let xml = format!(
        r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Properties /><Attribute Id="{id}" IsKey="True"><Properties /></Attribute></Level>"#
    );
    let names = |i: i32| -> Option<String> { (i == id).then(|| key.to_string()) };
    let types = |i: i32| -> Result<DataType, String> {
        if i == id {
            Ok(key_type.clone())
        } else {
            Err(format!("no stored type for attribute {i}"))
        }
    };
    let links = |i: i32| -> Result<Option<String>, String> {
        Ok(if i == id {
            link.map(str::to_string)
        } else {
            None
        })
    };
    let lookup = Lookup {
        attribute_name: &names,
        attribute_type: &types,
        attribute_super_key: &links,
    };
    decode(&xml, transaction, &lookup).expect("the synthetic key decodes")
}

/// A catalog object holding one Transaction's STRUCTURE part, as the reader stores it.
fn transaction_object(name: &str, structure: &TransactionStructure) -> KbObject {
    let id = ObjectId::new(format!("{}-id", name.to_ascii_lowercase())).unwrap();
    let mut object = KbObject::new(id, name, ObjectKind::Transaction);
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: serde_json::to_string(structure).unwrap(),
    });
    object
}

fn catalog_of(objects: Vec<KbObject>) -> Catalog {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &objects.into_iter().map(Edit::Upsert).collect::<Vec<_>>(),
        )
        .unwrap();
    catalog
}

/// The stored Plan070Trn blob decodes to its native key, and the key records the link to Plan071TrnId.
#[test]
fn plan070trn_stored_blob_decodes_to_its_key_with_the_link_to_plan071trn() {
    let structure = plan070trn_structure();
    assert_eq!(structure.root.name, "Plan070Trn");
    let key = &structure.root.attributes[0];
    assert_eq!((key.name.as_str(), key.is_key), ("Plan070TrnId", true));
    assert_eq!(key.data_type, Some(numeric_4_0()));
    assert_eq!(
        structure.key_references,
        vec![KeyReference {
            attribute: "Plan070TrnId".into(),
            references: "Plan071TrnId".into(),
        }]
    );
}

/// The physical schema of Plan070Trn carries the foreign key its SuperTypeKey names: Plan070TrnId references the
/// whole primary key of Plan071Trn. The reference adds no index of its own, and the DDL renders the constraint.
#[test]
fn plan070trn_derives_its_table_with_the_foreign_key_to_the_key_it_references() {
    let plan071 = one_key_structure("Plan071Trn", 12, "Plan071TrnId", numeric_4_0(), None);
    let catalog = catalog_of(vec![
        transaction_object("Plan070Trn", &plan070trn_structure()),
        transaction_object("Plan071Trn", &plan071),
    ]);

    let tables = derive_tables_from_catalog(&catalog).expect("both Transactions derive");
    let plan070 = tables
        .iter()
        .find(|t| t.name == "Plan070Trn")
        .expect("the Plan070Trn table is derived");
    assert_eq!(plan070.primary_key, vec!["Plan070TrnId".to_string()]);
    assert_eq!(
        plan070.foreign_keys,
        vec![TableForeignKey::new(
            "FK_Plan070Trn_Plan071Trn",
            vec!["Plan070TrnId".into()],
            "Plan071Trn",
            vec!["Plan071TrnId".into()],
        )]
    );
    assert_eq!(plan070.indexes.len(), 1, "{:?}", plan070.indexes);
    assert!(plan070.indexes[0].is_primary);

    let ddl = generate_database_ddl(&tables, SqlDialect::SqlServer).expect("the DDL renders");
    assert!(
        ddl.contains(
            "CONSTRAINT [FK_Plan070Trn_Plan071Trn] FOREIGN KEY ([Plan070TrnId]) REFERENCES [Plan071Trn] ([Plan071TrnId])"
        ),
        "{ddl}"
    );
}

/// A key reference whose target Transaction is not derived refuses only the referencing Transaction, naming the
/// missing key. Nothing is defaulted and no table is emitted for it; LiveMoveAttribute still derives.
#[test]
fn a_key_reference_to_a_missing_key_refuses_only_its_transaction() {
    let lma = transaction_object(
        "LiveMoveAttribute",
        &structure_of(LMA_HEX, "LiveMoveAttribute"),
    );
    let catalog = catalog_of(vec![
        transaction_object("Plan070Trn", &plan070trn_structure()),
        lma,
    ]);

    let (tables, failures) = derive_tables_partial(&catalog);

    assert!(tables.iter().any(|t| t.name == "LiveMoveAttribute"));
    assert!(!tables.iter().any(|t| t.name == "Plan070Trn"));
    assert_eq!(
        failures,
        vec![
            "Transaction 'Plan070Trn': its primary key references Plan071TrnId, which is not the whole primary key of any derived table"
                .to_string()
        ]
    );
    assert!(derive_tables_from_catalog(&catalog).is_err());
}

/// Two derived tables whose whole primary key is the referenced key make the reference ambiguous: it is refused,
/// not resolved to either table.
#[test]
fn a_key_reference_to_an_ambiguous_key_refuses_its_transaction() {
    let plan071 = one_key_structure("Plan071Trn", 12, "Plan071TrnId", numeric_4_0(), None);
    let copy = one_key_structure("Plan071TrnCopy", 13, "Plan071TrnId", numeric_4_0(), None);
    let catalog = catalog_of(vec![
        transaction_object("Plan070Trn", &plan070trn_structure()),
        transaction_object("Plan071Trn", &plan071),
        transaction_object("Plan071TrnCopy", &copy),
    ]);

    let (tables, failures) = derive_tables_partial(&catalog);

    assert!(!tables.iter().any(|t| t.name == "Plan070Trn"));
    assert!(tables.iter().any(|t| t.name == "Plan071Trn"));
    assert!(tables.iter().any(|t| t.name == "Plan071TrnCopy"));
    assert_eq!(failures.len(), 1, "{failures:?}");
    assert!(
        failures[0]
            .starts_with("Transaction 'Plan070Trn': its primary key references Plan071TrnId")
            && failures[0].contains("ambiguous")
            && failures[0].contains("Plan071TrnCopy"),
        "{failures:?}"
    );
}

/// Two Transactions whose keys reference each other form a cycle: both are refused, and only they are.
#[test]
fn a_cycle_of_key_references_refuses_the_transactions_on_it() {
    let plan071 = one_key_structure(
        "Plan071Trn",
        12,
        "Plan071TrnId",
        numeric_4_0(),
        Some("Plan070TrnId"),
    );
    let lma = transaction_object(
        "LiveMoveAttribute",
        &structure_of(LMA_HEX, "LiveMoveAttribute"),
    );
    let catalog = catalog_of(vec![
        transaction_object("Plan070Trn", &plan070trn_structure()),
        transaction_object("Plan071Trn", &plan071),
        lma,
    ]);

    let (tables, failures) = derive_tables_partial(&catalog);

    assert!(tables.iter().any(|t| t.name == "LiveMoveAttribute"));
    assert!(
        !tables
            .iter()
            .any(|t| t.name == "Plan070Trn" || t.name == "Plan071Trn")
    );
    assert_eq!(failures.len(), 2, "{failures:?}");
    for name in ["Plan070Trn", "Plan071Trn"] {
        let prefix = format!("Transaction '{name}': its key references form a cycle");
        assert!(
            failures.iter().any(|f| f.starts_with(&prefix)),
            "{failures:?}"
        );
    }
}

/// A key reference is refused when the referenced key has another type: no foreign key is made across types.
#[test]
fn a_key_reference_to_a_key_of_another_type_refuses_its_transaction() {
    let plan071 = one_key_structure(
        "Plan071Trn",
        12,
        "Plan071TrnId",
        DataType::Character { length: 4 },
        None,
    );
    let catalog = catalog_of(vec![
        transaction_object("Plan070Trn", &plan070trn_structure()),
        transaction_object("Plan071Trn", &plan071),
    ]);

    let (tables, failures) = derive_tables_partial(&catalog);

    assert!(tables.iter().any(|t| t.name == "Plan071Trn"));
    assert!(!tables.iter().any(|t| t.name == "Plan070Trn"));
    assert_eq!(failures.len(), 1, "{failures:?}");
    assert!(
        failures[0].starts_with("Transaction 'Plan070Trn': its key 'Plan070TrnId' has type"),
        "{failures:?}"
    );
}

/// Exercise the reader's actual served-row wiring, not only the pure resolver/decoder.
fn read_inherited_key_transaction(target_version: i32, current_target: Option<&str>) -> KbObject {
    use gx_engine::udm::mock::{EntityVersionRecord, ModelEntityVersionRecord};
    use gx_engine::udm::{UdmClient, UdmReader, UdmWriter};
    use std::sync::Arc;
    let client = Arc::new(UdmClient::new_in_memory());
    for (id, name, version) in [
        (11, "Plan070TrnId", 3),
        (12, "Plan071TrnId", target_version),
    ] {
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 27,
                entity_id: id,
                entity_version_id: version,
                name: name.into(),
            })
            .unwrap();
    }
    let row = |id, version, properties: &str| {
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 27,
                entity_id: id,
                entity_version_id: version,
                data: vec![],
                properties: Some(properties.into()),
            })
            .unwrap();
    };
    row(
        11,
        3,
        r#"<Properties><Property><Name>Class</Name><Value>PRIMARY</Value></Property><Property><Name>SuperTypeKey</Name><Value>12</Value></Property></Properties>"#,
    );
    // A valid older row must never hide an absent or invalid served version.
    row(
        12,
        1,
        r#"<Properties><Property><Name>Class</Name><Value>PRIMARY</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>4</Value></Property></Properties>"#,
    );
    if let Some(properties) = current_target {
        row(12, target_version, properties);
    }
    let mut transaction = KbObject::new(
        ObjectId::new("reader-plan070").unwrap(),
        "Plan070Trn",
        ObjectKind::Transaction,
    );
    transaction.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: PLAN070TRN_XML.into(),
    });
    UdmWriter::new(client.clone())
        .create_object(&transaction)
        .unwrap();
    UdmReader::new(client)
        .read_object_by_name("Plan070Trn")
        .unwrap()
        .unwrap()
}

#[test]
fn reader_inherits_the_type_and_link_from_the_served_attribute_rows() {
    let read = read_inherited_key_transaction(1, None);
    let part = read
        .source_parts
        .iter()
        .find(|p| p.name == "STRUCTURE")
        .expect("reader holds decoded structure");
    let structure: TransactionStructure = serde_json::from_str(&part.source).unwrap();
    assert_eq!(structure.root.attributes[0].data_type, Some(numeric_4_0()));
    assert_eq!(
        structure.key_references,
        vec![KeyReference {
            attribute: "Plan070TrnId".into(),
            references: "Plan071TrnId".into()
        }]
    );
    assert!(read.not_generated.is_none());
}

#[test]
fn reader_does_not_fall_back_to_an_old_typed_attribute_row() {
    for current in [None, Some("<Properties />")] {
        let read = read_inherited_key_transaction(2, current);
        assert!(
            !read.source_parts.iter().any(|p| p.name == "STRUCTURE"),
            "{read:?}"
        );
        let reason = read
            .refusal
            .expect("missing served type refuses transaction");
        assert!(
            reason.contains("Plan070Trn") && reason.contains("attribute 12"),
            "{reason}"
        );
        assert!(read.not_generated.is_none());
    }
}
