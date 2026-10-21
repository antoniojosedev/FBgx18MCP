//! Regression tests for the Knowledge Base vocabulary. Entity type ids are read from
//! each Knowledge Base's own `EntityType` table; no GeneXus 18 numbering is assumed.
//!
//! The GeneXus 16 rows in `gx16_vocabulary` were read from the disposable
//! `KBTeste16` Knowledge Base (`EntityType`, 156 rows). Only the served names are
//! listed, plus `Documentation`, `Help` and `Diagram` as unserved neighbours.
//! In GeneXus 16 `Procedure` is object 31 and its source part 81; in GeneXus 18 the
//! same names are 38 and 100. A test that passes on GeneXus 18 ids but not on these
//! has the bug this file guards against.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart};
use gx_engine::udm::{
    EntityTypeRecord, EntityTypeResolver, EntityVersionCompositionRecord, EntityVersionRecord,
    ModelEntityVersionRecord, UdmClient, UdmPartKind, UdmReader, UdmWriter, build_token_data_list,
    encode_blob,
};
use std::path::Path;
use std::sync::Arc;

fn rec(entity_type_id: i32, name: &str, namespace: &str) -> EntityTypeRecord {
    EntityTypeRecord {
        entity_type_id,
        name: name.to_string(),
        namespace: namespace.to_string(),
    }
}

/// The GeneXus 16 `EntityType` rows of `KBTeste16` that the engine serves, plus neighbours.
fn gx16_vocabulary() -> Vec<EntityTypeRecord> {
    vec![
        rec(17, "Module", "Objects"),
        rec(20, "Attribute", "Attributes"),
        rec(22, "DataProvider", "Objects"),
        rec(23, "DataSelector", "Objects"),
        rec(24, "DataView", "DataViews"),
        rec(26, "Domain", "Domains"),
        rec(31, "Procedure", "Objects"),
        rec(33, "SDT", "Objects"),
        rec(34, "Table", "PhysicalModel"),
        rec(36, "Transaction", "Objects"),
        rec(40, "WebPanel", "Objects"),
        rec(145, "DesignSystem", "Themes"),
        rec(53, "Diagram", "Objects"),
        rec(70, "Conditions", ""),
        rec(76, "Documentation", ""),
        rec(78, "Events", ""),
        rec(79, "Help", ""),
        rec(80, "DataProviderSource", ""),
        rec(81, "Procedure", ""),
        rec(83, "Rules", ""),
        rec(85, "Structure", ""),
        rec(86, "Variables", ""),
        rec(88, "WebForm", ""),
        rec(119, "PatternInstance", ""),
    ]
}

fn gx16_client() -> Arc<UdmClient> {
    Arc::new(UdmClient::new_in_memory_with_entity_types(gx16_vocabulary()))
}

/// Inserts one object of `entity_type` with the given parts, each part a
/// `(part entity type, text)` pair. Ids start at `entity_id` for the object.
fn insert_object(
    client: &UdmClient,
    entity_type: i32,
    entity_id: i32,
    name: &str,
    parts: &[(i32, &str)],
) {
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: entity_type,
            entity_id,
            entity_version_id: 1,
            name: name.to_string(),
        })
        .expect("insert object");
    for (index, (part_type, text)) in parts.iter().enumerate() {
        let part_id = entity_id * 10 + index as i32 + 1;
        let blob = encode_blob(&build_token_data_list(text), true).expect("encode part");
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: *part_type,
                entity_id: part_id,
                entity_version_id: 1,
                data: blob,
                properties: None,
            })
            .expect("insert part");
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: entity_type,
                compound_entity_id: entity_id,
                compound_entity_version_id: 1,
                component_entity_type_id: *part_type,
                component_entity_id: part_id,
                component_entity_version_id: 1,
            })
            .expect("insert composition");
    }
}

#[test]
fn gx16_ids_resolve_by_name_and_namespace() {
    let resolver = EntityTypeResolver::from_records(&gx16_vocabulary()).expect("resolver");

    assert_eq!(resolver.object_type_id(ObjectKind::Procedure), Ok(31));
    assert_eq!(resolver.object_type_id(ObjectKind::DataView), Ok(24));
    assert_eq!(resolver.object_type_id(ObjectKind::DesignSystem), Ok(145));
    assert_eq!(resolver.object_type_id(ObjectKind::Attribute), Ok(20));
    assert_eq!(resolver.object_type_id(ObjectKind::Table), Ok(34));
    assert_eq!(resolver.object_type_id(ObjectKind::Module), Ok(17));

    assert_eq!(
        resolver.part_type_id(ObjectKind::Procedure, UdmPartKind::Source),
        Ok(81)
    );
    assert_eq!(
        resolver.part_type_id(ObjectKind::DataProvider, UdmPartKind::Source),
        Ok(80)
    );
    assert_eq!(
        resolver.part_type_id(ObjectKind::Procedure, UdmPartKind::Rules),
        Ok(83)
    );

    assert_eq!(resolver.object_kind(31), Some(ObjectKind::Procedure));
    assert_eq!(resolver.object_kind(24), Some(ObjectKind::DataView));
    assert_eq!(resolver.part_kind(78), Some(UdmPartKind::Events));
    assert_eq!(resolver.part_kind(119), Some(UdmPartKind::PatternInstance));

    // GeneXus 18 ids mean nothing in this Knowledge Base.
    assert_eq!(resolver.object_kind(38), None);
    assert_eq!(resolver.part_kind(100), None);
    // Unserved neighbours are not parts and not objects.
    assert_eq!(resolver.part_kind(76), None);
    assert_eq!(resolver.object_kind(53), None);
}

#[test]
fn gx16_listing_reports_each_object_with_its_own_kind() {
    let client = gx16_client();
    insert_object(&client, 31, 1001, "PGx16Proc", &[]);
    insert_object(&client, 24, 2001, "DVGx16", &[]);

    let rows = UdmReader::from_client(client.as_ref().clone())
        .list_objects()
        .expect("list objects");

    let procedure = rows
        .iter()
        .find(|row| row.name == "PGx16Proc")
        .expect("procedure row");
    assert_eq!(procedure.kind, ObjectKind::Procedure);
    assert_eq!(procedure.entity_type_id, 31);

    // The bug this guards: GeneXus 16 DataView (24) was reported as a Procedure
    // because the served table was keyed by the GeneXus 18 numbering.
    let data_view = rows
        .iter()
        .find(|row| row.name == "DVGx16")
        .expect("data view row");
    assert_eq!(data_view.kind, ObjectKind::DataView);
}

#[test]
fn gx16_object_hydrates_its_served_parts_and_skips_the_rest() {
    let client = gx16_client();
    insert_object(
        &client,
        31,
        100,
        "PGx16Proc",
        &[
            (81, "Msg('hello')"),
            (83, "NoAccept(&UserId);"),
            (76, "documentation text the engine does not serve"),
        ],
    );

    let reader = UdmReader::from_client(client.as_ref().clone());
    let loaded = reader
        .load_object_by_name("PGx16Proc", None)
        .expect("load")
        .expect("object exists");

    assert_eq!(loaded.object.kind, ObjectKind::Procedure);
    assert_eq!(loaded.stored_parts, vec!["SOURCE", "RULES"]);
    let names: Vec<&str> = loaded
        .object
        .source_parts
        .iter()
        .map(|p| p.name.as_str())
        .collect();
    assert_eq!(names, vec!["SOURCE", "RULES"]);
    assert_eq!(loaded.object.source_parts[0].source, "Msg('hello')");
    assert_eq!(loaded.object.source_parts[1].source, "NoAccept(&UserId);");

    let catalog = reader.read_catalog().expect("catalog validates");
    assert_eq!(catalog.objects().count(), 1);
}

#[test]
fn gx16_writer_stores_the_knowledge_base_ids_and_refuses_unserved_parts() {
    let client = gx16_client();
    let writer = UdmWriter::from_client(client.as_ref().clone());

    let mut object = KbObject::new(
        ObjectId::new("pgx16written").unwrap(),
        "PGx16Written",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".to_string(),
        source: "Msg('written')".to_string(),
    });
    object.source_parts.push(SourcePart {
        name: "RULES".to_string(),
        source: "NoAccept(&UserId);".to_string(),
    });

    let identity = writer.create_object(&object).expect("create");
    assert_eq!(identity.entity_type_id, 31);

    let compositions = client
        .get_compositions(31, identity.entity_id, identity.version_id)
        .expect("compositions");
    let mut component_types: Vec<i32> = compositions
        .iter()
        .map(|c| c.component_entity_type_id)
        .collect();
    component_types.sort_unstable();
    assert_eq!(component_types, vec![81, 83]);

    // A part the engine does not serve is refused before anything is written.
    let mut unserved = KbObject::new(
        ObjectId::new("pgx16unserved").unwrap(),
        "PGx16Unserved",
        ObjectKind::Procedure,
    );
    unserved.source_parts.push(SourcePart {
        name: "DOCUMENTATION".to_string(),
        source: "text".to_string(),
    });
    let error = writer.create_object(&unserved).unwrap_err();
    assert!(error.contains("not a part this engine stores"), "{error}");
    let active = client.get_active_objects(1).expect("active");
    assert!(
        active.iter().all(|r| r.name != "PGx16Unserved"),
        "a refused write must leave no object behind"
    );
}

#[test]
fn writer_refuses_an_object_kind_the_vocabulary_does_not_declare() {
    // This Knowledge Base declares no DataView.
    let vocabulary: Vec<EntityTypeRecord> = gx16_vocabulary()
        .into_iter()
        .filter(|r| r.name != "DataView")
        .collect();
    let client = Arc::new(UdmClient::new_in_memory_with_entity_types(vocabulary));
    let writer = UdmWriter::from_client(client.as_ref().clone());

    let view = KbObject::new(
        ObjectId::new("dvmissing").unwrap(),
        "DVMissing",
        ObjectKind::DataView,
    );
    let error = writer.create_object(&view).unwrap_err();
    assert!(error.contains("DataView"), "{error}");
}

#[test]
fn writer_refuses_a_dangling_reference_but_keeps_a_self_reference() {
    let client = gx16_client();
    let writer = UdmWriter::from_client(client.as_ref().clone());

    let mut dangling = KbObject::new(
        ObjectId::new("pcaller").unwrap(),
        "PCaller",
        ObjectKind::Procedure,
    );
    dangling.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("pmissing").unwrap(),
    });
    let error = writer.create_object(&dangling).unwrap_err();
    assert!(error.contains("not an object of this model"), "{error}");

    let mut recursive = KbObject::new(
        ObjectId::new("precursive").unwrap(),
        "PRecursive",
        ObjectKind::Procedure,
    );
    recursive.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("precursive").unwrap(),
    });
    let identity = writer.create_object(&recursive).expect("self reference");
    let references = client
        .get_cross_references_for_model(1)
        .expect("references");
    assert!(
        references
            .iter()
            .any(|r| r.from_entity_id == identity.entity_id
                && r.to_entity_id == identity.entity_id
                && r.to_entity_type_id == 31),
        "the self reference must point at the object itself"
    );
}

/// Opt-in live check against the disposable GeneXus 16 KB. Skipped when the KB is
/// not on this machine; when present, every step must succeed.
#[test]
fn live_kbteste16_vocabulary_matches_the_gx16_fixture() {
    let kb_path = "C:\\KBs\\KBTeste16";
    if !Path::new(kb_path).join("knowledgebase.connection").exists() {
        return;
    }
    let client = UdmClient::connect_kb(kb_path).expect("connect KBTeste16");
    let resolver = client
        .entity_type_resolver()
        .expect("read KBTeste16 vocabulary");

    assert_eq!(resolver.object_type_id(ObjectKind::Procedure), Ok(31));
    assert_eq!(resolver.object_type_id(ObjectKind::DataView), Ok(24));
    assert_eq!(
        resolver.part_type_id(ObjectKind::Procedure, UdmPartKind::Source),
        Ok(81)
    );
    assert_eq!(
        resolver.part_type_id(ObjectKind::Procedure, UdmPartKind::Rules),
        Ok(83)
    );
    assert_eq!(resolver.object_kind(38), None);
}
