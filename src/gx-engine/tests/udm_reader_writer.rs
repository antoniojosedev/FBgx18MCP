//! Integration tests for Track A: UDM Client, Reader, Writer, Catalog Hydration, and Locks.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart};
use gx_engine::udm::{
    DEFAULT_DESIGN_MODEL_ID, KbConnectionInfo, ModelEntityVersionRecord, UdmClient, UdmReader,
    UdmRow, UdmWriter,
};
use std::path::Path;
use std::sync::Arc;

#[test]
fn test_udm_client_sql_queries_and_row_extraction() {
    let client = UdmClient::new_in_memory();

    // 1. Insert ModelEntityVersion record
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38, // Procedure
            entity_id: 10,
            entity_version_id: 1,
            name: "PCalculateTax".to_string(),
        })
        .expect("insert model entity");

    // 2. Query with SELECT
    let rows: Vec<UdmRow> = client
        .query("SELECT * FROM ModelEntityVersion WHERE ModelId = 1")
        .expect("query model entity");

    assert_eq!(rows.len(), 1);
    let row = &rows[0];
    assert_eq!(row.get_i32("ModelId"), Some(1));
    assert_eq!(row.get_i32("EntityTypeId"), Some(38));
    assert_eq!(row.get_i32("EntityId"), Some(10));
    assert_eq!(row.get_i32("EntityVersionId"), Some(1));
    assert_eq!(row.get_str("ModelEntityVersionName"), Some("PCalculateTax"));

    // 3. Test EntityType query
    let types = client
        .query("SELECT * FROM EntityType")
        .expect("query entity types");
    assert!(types.len() >= 12, "Should have seeded default entity types");
    let proc_type = types
        .iter()
        .find(|r| r.get_str("EntityTypeName") == Some("Procedure"));
    assert!(proc_type.is_some());
    assert_eq!(proc_type.unwrap().get_i32("EntityTypeId"), Some(38));
}

#[test]
fn test_udm_transactional_boundaries_commit_rollback() {
    let client = UdmClient::new_in_memory();

    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 100,
            entity_version_id: 1,
            name: "PStableProc".to_string(),
        })
        .unwrap();

    // Begin transaction
    client.begin_transaction().expect("begin transaction");

    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 101,
            entity_version_id: 1,
            name: "PTempProc".to_string(),
        })
        .unwrap();

    assert_eq!(client.get_active_objects(1).unwrap().len(), 2);

    // Rollback
    client.rollback_transaction().expect("rollback transaction");
    let active_after_rollback = client.get_active_objects(1).unwrap();
    assert_eq!(active_after_rollback.len(), 1);
    assert_eq!(active_after_rollback[0].name, "PStableProc");

    // Begin and commit
    client.begin_transaction().expect("begin transaction 2");
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 102,
            entity_version_id: 1,
            name: "PCommittedProc".to_string(),
        })
        .unwrap();
    client.commit_transaction().expect("commit transaction");

    let active_after_commit = client.get_active_objects(1).unwrap();
    assert_eq!(active_after_commit.len(), 2);
}

#[test]
fn test_udm_writer_and_reader_procedure_roundtrip() {
    let client = Arc::new(UdmClient::new_in_memory());
    let writer = UdmWriter::new(client.clone());
    let reader = UdmReader::new(client);

    // 1. Create a Procedure with Source (100), Rules (102), Conditions (89), Variables (105)
    let mut proc = KbObject::new(
        ObjectId::new("pcustomerservice").unwrap(),
        "PCustomerService",
        ObjectKind::Procedure,
    );

    let source_code = r#"For Each Customer
    Where CustomerStatus = 'Active'
    &TotalBalance += CustomerBalance
EndFor"#;

    let rules_code = "Parm(in:&CustomerId, out:&TotalBalance);";
    let conditions_code = "CustomerBalance > 0;";
    // The stored form: each declaration carries its type in ATTCUSTOMTYPE (code 4 = NUMERIC,
    // Length = precision, Decimals = scale), as GeneXus writes it.
    let variables_xml = r#"<Variables><Variable Name="CustomerId"><Properties><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>10</Value></Property></Properties></Variable><Variable Name="TotalBalance"><Properties><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>12</Value></Property><Property><Name>Decimals</Name><Value>2</Value></Property></Properties></Variable></Variables>"#;

    proc.source_parts.push(SourcePart {
        name: "SOURCE".to_string(),
        source: source_code.to_string(),
    });
    proc.source_parts.push(SourcePart {
        name: "RULES".to_string(),
        source: rules_code.to_string(),
    });
    proc.source_parts.push(SourcePart {
        name: "CONDITIONS".to_string(),
        source: conditions_code.to_string(),
    });
    proc.source_parts.push(SourcePart {
        name: "VARIABLES".to_string(),
        source: variables_xml.to_string(),
    });

    let identity = writer.create_object(&proc).expect("create procedure");
    assert_eq!(identity.entity_type_id, 38);
    assert_eq!(identity.version_id, 1);
    assert_eq!(identity.name, "PCustomerService");

    // 2. Read back using UdmReader
    let read_obj = reader
        .read_object_by_name("PCustomerService")
        .expect("read procedure")
        .expect("object should exist");

    assert_eq!(read_obj.name, "PCustomerService");
    assert_eq!(read_obj.kind, ObjectKind::Procedure);
    assert_eq!(read_obj.source_parts.len(), 4);

    let src = read_obj
        .source_parts
        .iter()
        .find(|p| p.name == "SOURCE")
        .expect("source part");
    assert_eq!(src.source, source_code);

    let rules = read_obj
        .source_parts
        .iter()
        .find(|p| p.name == "RULES")
        .expect("rules part");
    assert_eq!(rules.source, rules_code);

    let conds = read_obj
        .source_parts
        .iter()
        .find(|p| p.name == "CONDITIONS")
        .expect("conditions part");
    assert_eq!(conds.source, conditions_code);

    let vars = read_obj
        .source_parts
        .iter()
        .find(|p| p.name == "VARIABLES")
        .expect("variables part");
    // The engine holds the declarations in their portable form, not the stored XML.
    let held: std::collections::BTreeMap<String, gx_engine::model::DataType> =
        serde_json::from_str(&vars.source).expect("portable declarations");
    assert_eq!(
        held["CustomerId"],
        gx_engine::model::DataType::Numeric {
            precision: 10,
            scale: 0
        }
    );
    assert_eq!(
        held["TotalBalance"],
        gx_engine::model::DataType::Numeric {
            precision: 12,
            scale: 2
        }
    );

    // 3. Update using save_object (version bump to 2)
    let updated_source = "For Each Customer\n    Msg(CustomerName)\nEndFor";
    proc.source_parts
        .iter_mut()
        .find(|p| p.name == "SOURCE")
        .unwrap()
        .source = updated_source.to_string();

    let identity_v2 = writer.save_object(&proc).expect("save procedure v2");
    assert_eq!(identity_v2.version_id, 2);

    let read_v2 = reader
        .read_object_by_name("PCustomerService")
        .unwrap()
        .unwrap();
    let src_v2 = read_v2
        .source_parts
        .iter()
        .find(|p| p.name == "SOURCE")
        .unwrap();
    assert_eq!(src_v2.source, updated_source);
}

#[test]
fn test_udm_writer_and_reader_transaction_with_structure_events_rules() {
    let client = Arc::new(UdmClient::new_in_memory());
    let writer = UdmWriter::new(client.clone());
    let reader = UdmReader::new(client);

    let mut trn = KbObject::new(
        ObjectId::new("customer").unwrap(),
        "Customer",
        ObjectKind::Transaction,
    );

    let structure_xml = r#"<Structure><Level Name="Customer"><Attribute Name="CustomerId" Key="True"/><Attribute Name="CustomerName"/></Level></Structure>"#;
    let rules_text = "Error('Name is mandatory') If CustomerName.IsEmpty();";
    let events_text = "Event After Trn\n    Commit\nEndEvent";
    let webform_xml =
        r#"<WebForm Layout="Responsive"><Row><Column>Customer Info</Column></Row></WebForm>"#;

    trn.source_parts.push(SourcePart {
        name: "STRUCTURE".to_string(),
        source: structure_xml.to_string(),
    });
    trn.source_parts.push(SourcePart {
        name: "RULES".to_string(),
        source: rules_text.to_string(),
    });
    trn.source_parts.push(SourcePart {
        name: "EVENTS".to_string(),
        source: events_text.to_string(),
    });
    trn.source_parts.push(SourcePart {
        name: "WEBFORM".to_string(),
        source: webform_xml.to_string(),
    });

    let identity = writer.create_object(&trn).expect("create transaction");
    assert_eq!(identity.entity_type_id, 43); // Transaction EntityType

    let read_trn = reader.read_object_by_name("Customer").unwrap().unwrap();
    assert_eq!(read_trn.name, "Customer");
    assert_eq!(read_trn.kind, ObjectKind::Transaction);
    assert_eq!(read_trn.source_parts.len(), 4);

    let struct_part = read_trn
        .source_parts
        .iter()
        .find(|p| p.name == "STRUCTURE")
        .unwrap();
    assert_eq!(struct_part.source, structure_xml);

    let rules_part = read_trn
        .source_parts
        .iter()
        .find(|p| p.name == "RULES")
        .unwrap();
    assert_eq!(rules_part.source, rules_text);

    let events_part = read_trn
        .source_parts
        .iter()
        .find(|p| p.name == "EVENTS")
        .unwrap();
    assert_eq!(events_part.source, events_text);

    let webform_part = read_trn
        .source_parts
        .iter()
        .find(|p| p.name == "WEBFORM")
        .unwrap();
    assert_eq!(webform_part.source, webform_xml);
}

#[test]
fn test_udm_catalog_hydration_and_validation() {
    let client = Arc::new(UdmClient::new_in_memory());
    let writer = UdmWriter::new(client.clone());
    let reader = UdmReader::new(client);

    // Create 3 objects with dependency links:
    // PChild
    let p_child = KbObject::new(
        ObjectId::new("pchild").unwrap(),
        "PChild",
        ObjectKind::Procedure,
    );
    writer.create_object(&p_child).expect("create pchild");

    // PParent calls PChild
    let mut p_parent = KbObject::new(
        ObjectId::new("pparent").unwrap(),
        "PParent",
        ObjectKind::Procedure,
    );
    p_parent.source_parts.push(SourcePart {
        name: "SOURCE".to_string(),
        source: "Call(PChild)".to_string(),
    });
    p_parent.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("pchild").unwrap(),
    });
    writer.create_object(&p_parent).expect("create pparent");

    // Table Customer uses Domain
    let mut table = KbObject::new(
        ObjectId::new("customertable").unwrap(),
        "CustomerTable",
        ObjectKind::Table,
    );
    table.source_parts.push(SourcePart {
        name: "STRUCTURE".to_string(),
        source: "<TableStructure Name=\"CustomerTable\"/>".to_string(),
    });
    writer.create_object(&table).expect("create table");

    // Hydrate catalog
    let catalog = reader.read_catalog().expect("hydrate catalog");
    assert_eq!(catalog.objects().count(), 3);

    let parent_in_catalog = catalog
        .get(&ObjectId::new("pparent").unwrap())
        .expect("find pparent in catalog");
    assert_eq!(parent_in_catalog.references.len(), 1);
    assert_eq!(parent_in_catalog.references[0].kind, ReferenceKind::Calls);
    assert_eq!(
        parent_in_catalog.references[0].target,
        ObjectId::new("pchild").unwrap()
    );

    // Verify catalog serializes and validates cleanly
    let json = catalog.to_json().expect("serialize catalog to json");
    assert!(!json.is_empty());
    assert!(json.contains("pparent"));
    assert!(json.contains("pchild"));
    assert!(json.contains("customertable"));
}

#[test]
fn test_udm_state_lock_concurrency_and_reentrancy() {
    let client = Arc::new(UdmClient::new_in_memory());
    let writer = UdmWriter::new(client);

    // 1. Agent 1 acquires lock on object 'Order'
    let acq1 = writer
        .acquire_lock("Order", "AgentAlpha", "guid-100", "WORKSTATION-1")
        .expect("agent 1 acquire");
    assert!(acq1);
    assert!(writer.is_locked("Order").unwrap());

    // 2. Agent 2 tries to acquire lock on object 'Order' -> Conflict
    let acq2 = writer
        .acquire_lock("Order", "AgentBeta", "guid-200", "WORKSTATION-2")
        .expect("agent 2 acquire");
    assert!(!acq2, "Agent 2 lock attempt must be rejected");

    // 3. Agent 1 re-entrant acquisition with same guid -> OK
    let acq_reentrant = writer
        .acquire_lock("Order", "AgentAlpha", "guid-100", "WORKSTATION-1")
        .expect("agent 1 reentrant");
    assert!(acq_reentrant);

    // 4. Inspect lock record
    let lock_rec = writer.get_lock("Order").unwrap().expect("lock record");
    assert_eq!(lock_rec.state_name, "Order");
    assert_eq!(lock_rec.state_user_name, "AgentAlpha");
    assert_eq!(lock_rec.state_guid, "guid-100");
    assert_eq!(lock_rec.owner_machine_name, "WORKSTATION-1");

    // 5. Agent 1 releases lock
    let rel = writer
        .release_lock("Order", "guid-100")
        .expect("release lock");
    assert!(rel);
    assert!(!writer.is_locked("Order").unwrap());

    // 6. Agent 2 can now acquire lock
    let acq2_again = writer
        .acquire_lock("Order", "AgentBeta", "guid-200", "WORKSTATION-2")
        .expect("agent 2 acquire after release");
    assert!(acq2_again);
}

#[test]
fn test_udm_writer_save_with_lock_exclusive() {
    let client = Arc::new(UdmClient::new_in_memory());
    let writer = UdmWriter::new(client);

    let obj = KbObject::new(
        ObjectId::new("psecurecalc").unwrap(),
        "PSecureCalc",
        ObjectKind::Procedure,
    );

    let id = writer
        .save_with_lock(&obj, "agent_worker", "lease-777", "DESKTOP-RUST")
        .expect("save with lock");
    assert_eq!(id.name, "PSecureCalc");

    // Lock must be released after completion
    assert!(!writer.is_locked("PSecureCalc").unwrap());
}

#[test]
fn test_udm_writer_delete_object_cascade() {
    let client = Arc::new(UdmClient::new_in_memory());
    let writer = UdmWriter::new(client.clone());
    let reader = UdmReader::new(client);

    let obj = KbObject::new(
        ObjectId::new("ptodelete").unwrap(),
        "PToDelete",
        ObjectKind::Procedure,
    );
    writer.create_object(&obj).expect("create object");
    assert!(reader.read_object_by_name("PToDelete").unwrap().is_some());

    let deleted = writer.delete_object("PToDelete").expect("delete object");
    assert!(deleted);
    assert!(reader.read_object_by_name("PToDelete").unwrap().is_none());

    // Deleting again returns false
    let deleted_again = writer
        .delete_object("PToDelete")
        .expect("delete nonexistent");
    assert!(!deleted_again);
}

#[test]
fn test_udm_real_kb_connection_info_integration() {
    let kb_paths = [
        "C:\\KBs\\KBTeste",
        "C:\\KBs\\KBTeste17",
        "C:\\KBs\\KBTeste16",
    ];

    for kb_path in kb_paths {
        let conn_file = Path::new(kb_path).join("knowledgebase.connection");
        if conn_file.exists() {
            let info = KbConnectionInfo::from_file(&conn_file).expect("parse real connection");
            let client = UdmClient::from_connection_info(info.clone());

            assert!(client.connection_info().is_some());
            assert_eq!(client.connection_info().unwrap().db_name, info.db_name);
            assert!(
                info.connection_string()
                    .contains("TrustServerCertificate=True")
            );

            // Reader and Writer can be instantiated directly with real connection info
            let reader = UdmReader::from_client(client.clone());
            let writer = UdmWriter::from_client(client);

            assert_eq!(reader.model_id(), DEFAULT_DESIGN_MODEL_ID);
            assert_eq!(writer.model_id(), DEFAULT_DESIGN_MODEL_ID);
        }
    }
}

/// Reads a real Procedure of the disposable GeneXus 18 KB. Every step is asserted:
/// a read that fails, or a KB with no Procedure, fails the test instead of passing
/// silently. Skipped only when the KB is not present on this machine.
#[test]
fn test_udm_real_kb_live_read_procedure() {
    let kb_path = "C:\\KBs\\KBTeste";
    let conn_file = Path::new(kb_path).join("knowledgebase.connection");
    if !conn_file.exists() {
        return;
    }
    let client = Arc::new(UdmClient::connect_kb(kb_path).expect("connect KBTeste"));
    let reader = UdmReader::new(client.clone());
    let procedure_id = client
        .entity_type_resolver()
        .expect("read KBTeste vocabulary")
        .object_type_id(ObjectKind::Procedure)
        .expect("KBTeste declares Procedure");
    let records = client
        .get_active_objects(DEFAULT_DESIGN_MODEL_ID)
        .expect("read active objects");
    let proc_rec = records
        .iter()
        .find(|r| r.entity_type_id == procedure_id)
        .expect("KBTeste has an active Procedure");

    let kb_obj = reader
        .read_object(proc_rec.entity_type_id, proc_rec.entity_id)
        .expect("read the Procedure");
    assert_eq!(kb_obj.name, proc_rec.name);
    assert_eq!(kb_obj.kind, ObjectKind::Procedure);
    assert!(
        kb_obj
            .source_parts
            .iter()
            .all(|p| !p.name.eq_ignore_ascii_case("UNKNOWN")),
        "no served part may be labelled UNKNOWN"
    );
}
