use super::*;
use crate::udm::client::*;
use crate::udm::decoder::{build_token_data_list, encode_blob};

#[test]
fn read_procedure_with_parts_and_references() {
    let client = Arc::new(UdmClient::new_in_memory());

    // Target called procedure
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38, // Procedure
            entity_id: 200,
            entity_version_id: 1,
            name: "PChildProcess".to_string(),
        })
        .unwrap();

    // Main caller procedure
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 100,
            entity_version_id: 1,
            name: "PMainCaller".to_string(),
        })
        .unwrap();

    // Source part (Procedure, id 100 in GeneXus 18)
    let source_code = "For Each\n    Call(PChildProcess)\nEndFor";
    let source_xml = build_token_data_list(source_code);
    let source_blob = encode_blob(&source_xml, true).unwrap();

    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 100, // Source Part
            entity_id: 1001,
            entity_version_id: 1,
            data: source_blob,
            properties: None,
        })
        .unwrap();

    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 38,
            compound_entity_id: 100,
            compound_entity_version_id: 1,
            component_entity_type_id: 100,
            component_entity_id: 1001,
            component_entity_version_id: 1,
        })
        .unwrap();

    // Rules part (Type 102)
    let rules_code = "NoAccept(&UserId);";
    let rules_xml = build_token_data_list(rules_code);
    let rules_blob = encode_blob(&rules_xml, true).unwrap();

    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 102, // Rules Part
            entity_id: 1002,
            entity_version_id: 1,
            data: rules_blob,
            properties: None,
        })
        .unwrap();

    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 38,
            compound_entity_id: 100,
            compound_entity_version_id: 1,
            component_entity_type_id: 102,
            component_entity_id: 1002,
            component_entity_version_id: 1,
        })
        .unwrap();

    // Cross reference: PMainCaller calls PChildProcess
    client
        .insert_cross_reference(ModelCrossReferenceRecord {
            model_id: 1,
            from_entity_type_id: 38,
            from_entity_id: 100,
            to_entity_type_id: 38,
            to_entity_id: 200,
            reference_type: 1, // Calls
        })
        .unwrap();

    let reader = UdmReader::new(client);
    let catalog = reader.read_catalog().expect("read catalog");

    let obj = catalog
        .get(&ObjectId::new("pmaincaller").unwrap())
        .expect("find pmaincaller");

    assert_eq!(obj.name, "PMainCaller");
    assert_eq!(obj.kind, ObjectKind::Procedure);
    assert_eq!(obj.source_parts.len(), 2);

    let src = obj
        .source_parts
        .iter()
        .find(|p| p.name == "SOURCE")
        .unwrap();
    assert_eq!(src.source, source_code);

    let rules = obj.source_parts.iter().find(|p| p.name == "RULES").unwrap();
    assert_eq!(rules.source, rules_code);

    assert_eq!(obj.references.len(), 1);
    assert_eq!(obj.references[0].kind, ReferenceKind::Calls);
    assert_eq!(
        obj.references[0].target,
        ObjectId::new("pchildprocess").unwrap()
    );
}

#[test]
fn read_part_selectively() {
    let client = Arc::new(UdmClient::new_in_memory());

    let code = "Event 'Click'\n    Msg('Hello')\nEndEvent";
    let xml = build_token_data_list(code);
    let blob = encode_blob(&xml, true).unwrap();

    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 97, // Events
            entity_id: 501,
            entity_version_id: 1,
            data: blob,
            properties: None,
        })
        .unwrap();

    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 47, // WebPanel
            compound_entity_id: 50,
            compound_entity_version_id: 1,
            component_entity_type_id: 97,
            component_entity_id: 501,
            component_entity_version_id: 1,
        })
        .unwrap();

    let reader = UdmReader::new(client);
    let events = reader
        .read_part(47, 50, 1, UdmPartKind::Events)
        .expect("read events part")
        .expect("part should exist");

    assert_eq!(events, code);

    let non_existent = reader
        .read_part(47, 50, 1, UdmPartKind::WebForm)
        .expect("read webform part");
    assert!(non_existent.is_none());
}

/// A component part outside the served space (Documentation, 95) used to be
/// emitted as `UNKNOWN`; two of them made the catalog refuse the object.
#[test]
fn hydrate_skips_parts_outside_the_served_space_instead_of_naming_them_unknown() {
    let client = Arc::new(UdmClient::new_in_memory());
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 100,
            entity_version_id: 1,
            name: "PWithDocs".to_string(),
        })
        .unwrap();
    for (component_type, component_id) in [(95, 1), (98, 2), (100, 3)] {
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: component_type,
                entity_id: component_id,
                entity_version_id: 1,
                data: encode_blob(&build_token_data_list("x = 1"), true).unwrap(),
                properties: None,
            })
            .unwrap();
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 38,
                compound_entity_id: 100,
                compound_entity_version_id: 1,
                component_entity_type_id: component_type,
                component_entity_id: component_id,
                component_entity_version_id: 1,
            })
            .unwrap();
    }

    let reader = UdmReader::new(client);
    let objects = reader.read_all_objects().expect("hydrate");
    let names: Vec<&str> = objects[0]
        .source_parts
        .iter()
        .map(|p| p.name.as_str())
        .collect();
    assert_eq!(names, vec!["SOURCE"]);
    assert!(reader.read_catalog().is_ok());
}

/// An empty stored payload is an empty part, not a missing one.
#[test]
fn empty_payload_is_served_as_an_empty_part() {
    let client = Arc::new(UdmClient::new_in_memory());
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 100,
            entity_version_id: 1,
            name: "PEmptyRules".to_string(),
        })
        .unwrap();
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 102,
            entity_id: 7,
            entity_version_id: 1,
            data: Vec::new(),
            properties: None,
        })
        .unwrap();
    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 38,
            compound_entity_id: 100,
            compound_entity_version_id: 1,
            component_entity_type_id: 102,
            component_entity_id: 7,
            component_entity_version_id: 1,
        })
        .unwrap();

    let reader = UdmReader::new(client);
    let loaded = reader
        .load_object_by_name("PEmptyRules", Some(&["RULES".to_string()]))
        .expect("load")
        .expect("object");
    assert_eq!(loaded.object.source_parts.len(), 1);
    assert_eq!(loaded.object.source_parts[0].name, "RULES");
    assert_eq!(loaded.object.source_parts[0].source, "");
    assert_eq!(loaded.stored_parts, vec!["RULES"]);
}

/// A composition whose version row is missing is an inconsistent model. It is
/// refused, never reported as an empty or absent part.
#[test]
fn composition_without_an_entity_version_row_fails_the_read() {
    let client = Arc::new(UdmClient::new_in_memory());
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 100,
            entity_version_id: 1,
            name: "PDangling".to_string(),
        })
        .unwrap();
    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 38,
            compound_entity_id: 100,
            compound_entity_version_id: 1,
            component_entity_type_id: 100,
            component_entity_id: 9,
            component_entity_version_id: 1,
        })
        .unwrap();

    let reader = UdmReader::new(client);
    let error = reader.load_object_by_name("PDangling", None).unwrap_err();
    assert!(error.contains("no EntityVersion row"), "{error}");
}

/// `WWTrn353` (KBTeste) has two cross-reference rows to one target under the same
/// reference type and different link types. The catalog allows one (kind, target) pair,
/// so the reader must emit it once instead of refusing the object.
#[test]
fn repeated_cross_reference_pair_is_one_reference() {
    let client = Arc::new(UdmClient::new_in_memory());
    for (id, name) in [(100, "PCaller"), (200, "PCallee")] {
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: id,
                entity_version_id: 1,
                name: name.to_string(),
            })
            .unwrap();
    }
    let row = ModelCrossReferenceRecord {
        model_id: 1,
        from_entity_type_id: 38,
        from_entity_id: 100,
        to_entity_type_id: 38,
        to_entity_id: 200,
        reference_type: 0,
    };
    client.insert_cross_reference(row.clone()).unwrap();
    client.insert_cross_reference(row).unwrap();

    let catalog = UdmReader::new(client).read_catalog().expect("catalog");
    let caller = catalog
        .get(&ObjectId::new("pcaller").unwrap())
        .expect("caller");
    assert_eq!(caller.references.len(), 1, "{:?}", caller.references);
}

/// Two objects that map to one catalog id would overwrite each other in the catalog.
/// The read must refuse, never drop one of them.
#[test]
fn two_procedures_with_one_name_are_refused_not_dropped() {
    let client = Arc::new(UdmClient::new_in_memory());
    for id in [100, 101] {
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: id,
                entity_version_id: 1,
                name: "PDup".to_string(),
            })
            .unwrap();
    }
    // Two Procedures of one name would be two classes of one name: the catalog refuses the
    // pair by name. Neither object is dropped.
    let error = UdmReader::new(client).read_catalog().unwrap_err();
    assert!(error.contains("duplicate object name 'PDup'"), "{error}");
}

/// A token list that does not extract must fail; the raw XML is never served as
/// the part's source.
#[test]
fn malformed_token_list_is_an_error_not_raw_xml() {
    let client = Arc::new(UdmClient::new_in_memory());
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 38,
            entity_id: 100,
            entity_version_id: 1,
            name: "PBrokenToken".to_string(),
        })
        .unwrap();
    let broken = "<TokenDataList><Word>unclosed";
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 100,
            entity_id: 8,
            entity_version_id: 1,
            data: encode_blob(broken, true).unwrap(),
            properties: None,
        })
        .unwrap();
    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 38,
            compound_entity_id: 100,
            compound_entity_version_id: 1,
            component_entity_type_id: 100,
            component_entity_id: 8,
            component_entity_version_id: 1,
        })
        .unwrap();

    let reader = UdmReader::new(client);
    assert!(reader.load_object_by_name("PBrokenToken", None).is_err());
}

#[test]
fn a_procedure_with_no_source_part_is_refused_by_name_not_skipped() {
    // KBTeste Plan072Proc (38|15), GenerateObject True: its active version has no SOURCE part among the
    // composition rows the reader reads. The engine refuses it by name and never marks it not generated.
    let mut procedure = crate::catalog::KbObject::new(
        crate::catalog::ObjectId::new("plan072proc").unwrap(),
        "Plan072Proc",
        ObjectKind::Procedure,
    );
    refuse_without_parts(&mut procedure, ObjectKind::Procedure, 0, false);
    assert_eq!(procedure.refusal.as_deref(), Some(NO_SOURCE_REFUSAL));
    assert!(
        procedure.not_generated.is_none(),
        "{:?}",
        procedure.not_generated
    );
}

#[test]
fn a_procedure_with_parts_but_no_source_part_is_refused_by_name() {
    let mut procedure = crate::catalog::KbObject::new(
        crate::catalog::ObjectId::new("rulesonly").unwrap(),
        "RulesOnly",
        ObjectKind::Procedure,
    );
    refuse_without_parts(&mut procedure, ObjectKind::Procedure, 2, false);
    assert_eq!(procedure.refusal.as_deref(), Some(NO_SOURCE_REFUSAL));
    assert!(procedure.not_generated.is_none());
}

#[test]
fn a_procedure_with_a_source_part_is_not_refused_by_the_source_rule() {
    let mut procedure = crate::catalog::KbObject::new(
        crate::catalog::ObjectId::new("withsource").unwrap(),
        "WithSource",
        ObjectKind::Procedure,
    );
    refuse_without_parts(&mut procedure, ObjectKind::Procedure, 1, true);
    assert!(procedure.refusal.is_none(), "{:?}", procedure.refusal);
    assert!(procedure.not_generated.is_none());
}

#[test]
fn a_module_with_no_parts_is_exempt() {
    let mut module = crate::catalog::KbObject::new(
        crate::catalog::ObjectId::new("folder").unwrap(),
        "Folder",
        ObjectKind::Module,
    );
    refuse_without_parts(&mut module, ObjectKind::Module, 0, false);
    assert!(module.refusal.is_none(), "{:?}", module.refusal);
}

#[test]
fn a_generate_object_false_procedure_with_no_parts_stays_not_generated() {
    // The stored GenerateObject False decided the object before the parts were read: its reason stays,
    // and no refusal is added on top of it.
    let mut procedure = crate::catalog::KbObject::new(
        crate::catalog::ObjectId::new("plan072proc").unwrap(),
        "Plan072Proc",
        ObjectKind::Procedure,
    );
    procedure.not_generated = Some("GenerateObject is False".into());
    refuse_without_parts(&mut procedure, ObjectKind::Procedure, 0, false);
    assert_eq!(
        procedure.not_generated.as_deref(),
        Some("GenerateObject is False")
    );
    assert!(procedure.refusal.is_none(), "{:?}", procedure.refusal);
}

#[test]
fn primitive_procedure_reads_do_not_scan_unrelated_sdt_roots() {
    let client = Arc::new(UdmClient::new_in_memory());
    let mut procedure = KbObject::new(
        ObjectId::new("PrimitiveOnly").unwrap(),
        "PrimitiveOnly",
        ObjectKind::Procedure,
    );
    procedure.source_parts = vec![
        SourcePart { name: "SOURCE".into(), source: "&Flag = True".into() },
        SourcePart { name: "VARIABLES".into(), source: "<Variables><Variable Name=\"Flag\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;15&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property></Properties></Variable></Variables>".into() },
    ];
    crate::udm::writer::UdmWriter::new(client.clone())
        .create_object(&procedure)
        .unwrap();
    for mode in 0..3 {
        let reader = UdmReader::new(client.clone());
        match mode {
            0 => {
                assert!(
                    reader
                        .read_object_by_name("PrimitiveOnly")
                        .unwrap()
                        .unwrap()
                        .refusal
                        .is_none()
                );
            }
            1 => {
                reader
                    .load_object_by_name("PrimitiveOnly", None)
                    .unwrap()
                    .unwrap();
            }
            _ => {
                reader
                    .read_closure_by_name("PrimitiveOnly")
                    .unwrap()
                    .unwrap();
            }
        }
        assert!(
            reader.root_collections.get().is_none(),
            "mode {mode} scanned every SDT for primitive variables"
        );
    }
}

#[test]
fn a_structured_data_type_with_no_parts_stays_refused() {
    let mut sdt = crate::catalog::KbObject::new(
        crate::catalog::ObjectId::new("emptysdt").unwrap(),
        "EmptySdt",
        ObjectKind::StructuredDataType,
    );
    refuse_without_parts(&mut sdt, ObjectKind::StructuredDataType, 0, false);
    assert!(sdt.refusal.is_some());
    assert!(sdt.not_generated.is_none());
}
