use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::model::DataType;
use gx_engine::text_tree::{
    TextTreeFile, TextTreeSnapshot, export_catalog_to_text_tree, import_text_tree_to_catalog,
    read_from_directory, write_to_directory,
};
use gx_engine::transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure};
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static TEMP_SEQ: AtomicU64 = AtomicU64::new(0);

fn temp_test_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "gx_text_tree_{label}_{}_{}",
        std::process::id(),
        TEMP_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&dir).expect("create temp dir");
    dir
}

#[test]
fn test_export_procedure_to_text_file() {
    let mut catalog = Catalog::empty();
    let proc_id = ObjectId::new("calcsum").unwrap();
    let mut proc = KbObject::new(proc_id.clone(), "CalcSum", ObjectKind::Procedure);
    proc.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: "Parm(in:&A, in:&B, out:&Result);".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: r#"{"A":{"kind":"numeric","precision":4,"scale":0},"B":{"kind":"numeric","precision":4,"scale":0},"Result":{"kind":"numeric","precision":8,"scale":0}}"#.into(),
    });
    proc.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Result = &A + &B\nmsg(Str(&Result))".into(),
    });

    catalog
        .apply(0, &[Edit::Upsert(proc)])
        .expect("catalog apply");

    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 1);

    let file = &snapshot.files[0];
    assert_eq!(file.path, "Objects/Procedures/CalcSum.prc");
    assert_eq!(file.kind, ObjectKind::Procedure);
    assert!(file.content.contains("=== Rules ==="));
    assert!(file.content.contains("Parm(in:&A, in:&B, out:&Result);"));
    assert!(file.content.contains("=== Variables ==="));
    assert!(file.content.contains(r#"{"A":{"kind":"numeric""#));
    assert!(file.content.contains("=== Source ==="));
    assert!(file.content.contains("&Result = &A + &B"));

    // Roundtrip import back to Catalog
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), 1);
    let imported_proc = imported.get(&proc_id).expect("proc in catalog");
    assert_eq!(imported_proc.name, "CalcSum");
    assert_eq!(imported_proc.kind, ObjectKind::Procedure);
    assert_eq!(imported_proc.source_parts.len(), 3);
}

#[test]
fn test_export_transaction_to_text_file() {
    let mut catalog = Catalog::empty();
    let trn_id = ObjectId::new("customer").unwrap();
    let mut trn = KbObject::new(trn_id.clone(), "Customer", ObjectKind::Transaction);

    let mut root = TransactionLevel::new("Customer");
    root.attributes.push(TransactionAttributeRef {
        name: "CustomerId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 6,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.attributes.push(TransactionAttributeRef {
        name: "CustomerName".into(),
        is_key: false,
        data_type: Some(DataType::Character { length: 50 }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.attributes.push(TransactionAttributeRef {
        name: "CustomerActive".into(),
        is_key: false,
        data_type: Some(DataType::Boolean {}),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let mut contacts = TransactionLevel::new("Contacts");
    contacts.attributes.push(TransactionAttributeRef {
        name: "ContactId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    contacts.attributes.push(TransactionAttributeRef {
        name: "ContactEmail".into(),
        is_key: false,
        data_type: Some(DataType::Character { length: 100 }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.levels.push(contacts);

    let structure = TransactionStructure::new(root);
    let struct_json = serde_json::to_string(&structure).expect("structure json");
    trn.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: struct_json,
    });

    catalog
        .apply(0, &[Edit::Upsert(trn)])
        .expect("catalog apply");

    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 1);

    let file = &snapshot.files[0];
    assert!(
        file.path
            .eq_ignore_ascii_case("Objects/Transactions/Customer.trn")
    );
    assert_eq!(file.kind, ObjectKind::Transaction);
    assert!(file.content.contains("Customer"));
    assert!(file.content.contains("*CustomerId"));
    assert!(file.content.contains("CustomerName"));
    assert!(file.content.contains("Contacts"));
    assert!(file.content.contains("*ContactId"));

    // Roundtrip import back to Catalog
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), 1);
    let imported_trn = imported.get(&trn_id).expect("trn in catalog");
    assert_eq!(imported_trn.name, "Customer");
    assert_eq!(imported_trn.kind, ObjectKind::Transaction);

    let struct_part = imported_trn
        .source_parts
        .iter()
        .find(|p| p.name == "STRUCTURE")
        .expect("STRUCTURE part");
    let re_parsed: TransactionStructure =
        serde_json::from_str(&struct_part.source).expect("reparse structure");
    assert_eq!(re_parsed.root_primary_key(), vec!["CustomerId"]);
    assert_eq!(
        re_parsed.all_attributes(),
        vec![
            "CustomerId",
            "CustomerName",
            "CustomerActive",
            "ContactId",
            "ContactEmail"
        ]
    );
}

#[test]
fn test_export_domain_and_attribute_to_text_files() {
    let mut catalog = Catalog::empty();
    let dom_id = ObjectId::new("custcode").unwrap();
    let mut domain = KbObject::new(dom_id.clone(), "CustCode", ObjectKind::Domain);
    domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Numeric {
            precision: 6,
            scale: 0,
        })
        .unwrap(),
    });

    let att_id = ObjectId::new("custid").unwrap();
    let mut attr = KbObject::new(att_id.clone(), "CustId", ObjectKind::Attribute);
    attr.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: dom_id.clone(),
    });
    attr.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"CustCode"}"#.into(),
    });

    catalog
        .apply(0, &[Edit::Upsert(domain), Edit::Upsert(attr)])
        .expect("catalog apply");

    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 2);

    let dom_file = snapshot
        .get("Definitions/Domains/CustCode.dom")
        .expect("dom file");
    assert_eq!(dom_file.kind, ObjectKind::Domain);
    assert!(dom_file.content.contains("Numeric(6, 0)"));

    let att_file = snapshot
        .get("Definitions/Attributes/CustId.att")
        .expect("att file");
    assert_eq!(att_file.kind, ObjectKind::Attribute);
    assert!(att_file.content.contains("BasedOn: CustCode"));

    // Roundtrip import back to Catalog
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), 2);
    let imported_att = imported.get(&att_id).expect("att in catalog");
    assert_eq!(imported_att.name, "CustId");
    assert_eq!(imported_att.references.len(), 1);
    assert_eq!(imported_att.references[0].kind, ReferenceKind::BasedOn);
    assert_eq!(imported_att.references[0].target, dom_id);
}

#[test]
fn test_export_sdt_to_text_file() {
    let mut catalog = Catalog::empty();
    let sdt_id = ObjectId::new("productsdt").unwrap();
    let mut sdt = KbObject::new(sdt_id.clone(), "ProductSDT", ObjectKind::StructuredDataType);
    sdt.source_parts.push(SourcePart {
        name: "FIELDS".into(),
        source: r#"{"Description":{"kind":"character","length":100},"Price":{"kind":"numeric","precision":10,"scale":2},"ProductId":{"kind":"numeric","precision":6,"scale":0}}"#.into(),
    });

    catalog
        .apply(0, &[Edit::Upsert(sdt)])
        .expect("catalog apply");

    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 1);

    let file = &snapshot.files[0];
    assert!(
        file.path
            .eq_ignore_ascii_case("Definitions/SDTs/ProductSDT.sdt")
    );
    assert_eq!(file.kind, ObjectKind::StructuredDataType);
    assert!(file.content.contains("ProductId"));
    assert!(file.content.contains("Price"));
    assert!(file.content.contains("Description"));

    // Roundtrip import back to Catalog
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), 1);
    let imported_sdt = imported.get(&sdt_id).expect("sdt in catalog");
    assert_eq!(imported_sdt.name, "ProductSDT");
    let fields_part = &imported_sdt.source_parts[0];
    assert!(fields_part.source.contains("ProductId"));
    assert!(fields_part.source.contains("Price"));
}

#[test]
fn test_export_dataprovider_and_dataselector() {
    let mut catalog = Catalog::empty();
    let dp_id = ObjectId::new("customerdp").unwrap();
    let mut dp = KbObject::new(dp_id.clone(), "CustomerDP", ObjectKind::DataProvider);
    let dp_source = "Customer\n{\n    CustomerId = 101\n    CustomerName = 'Acme Corp'\n}\n";
    dp.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: dp_source.into(),
    });

    let ds_id = ObjectId::new("activecustomers").unwrap();
    let mut ds = KbObject::new(ds_id.clone(), "ActiveCustomers", ObjectKind::DataSelector);
    ds.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: "parm(in:&MinBalance);".into(),
    });
    ds.source_parts.push(SourcePart {
        name: "CONDITIONS".into(),
        source: "Where CustomerBalance >= &MinBalance;".into(),
    });

    catalog
        .apply(0, &[Edit::Upsert(dp), Edit::Upsert(ds)])
        .expect("catalog apply");

    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 2);

    let dp_file = snapshot
        .get("Objects/DataProviders/CustomerDP.dp")
        .expect("dp file");
    assert_eq!(dp_file.kind, ObjectKind::DataProvider);
    assert!(dp_file.content.contains("CustomerName = 'Acme Corp'"));

    let ds_file = snapshot
        .get("Objects/DataSelectors/ActiveCustomers.ds")
        .expect("ds file");
    assert_eq!(ds_file.kind, ObjectKind::DataSelector);
    assert!(ds_file.content.contains("parm(in:&MinBalance);"));
    assert!(
        ds_file
            .content
            .contains("Where CustomerBalance >= &MinBalance;")
    );

    // Roundtrip import back to Catalog
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), 2);
    let imported_dp = imported.get(&dp_id).expect("dp in catalog");
    assert_eq!(imported_dp.name, "CustomerDP");
    let imported_ds = imported.get(&ds_id).expect("ds in catalog");
    assert_eq!(imported_ds.name, "ActiveCustomers");
}

#[test]
fn test_export_design_system_object() {
    let mut catalog = Catalog::empty();
    let dso_id = ObjectId::new("themebase").unwrap();
    let mut dso = KbObject::new(dso_id.clone(), "ThemeBase", ObjectKind::DesignSystem);
    let dso_source = "styles ThemeBase\n{\n    .btn {\n        color: #ffffff;\n        background-color: #007bff;\n    }\n}\n";
    dso.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: dso_source.into(),
    });

    catalog
        .apply(0, &[Edit::Upsert(dso)])
        .expect("catalog apply");

    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 1);

    let file = &snapshot.files[0];
    assert!(
        file.path
            .eq_ignore_ascii_case("Objects/DesignSystems/ThemeBase.dso")
    );
    assert_eq!(file.kind, ObjectKind::DesignSystem);
    assert!(file.content.contains(".btn"));
    assert!(file.content.contains("#007bff"));

    // Roundtrip import back to Catalog
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), 1);
    let imported_dso = imported.get(&dso_id).expect("dso in catalog");
    assert_eq!(imported_dso.name, "ThemeBase");
    assert_eq!(imported_dso.kind, ObjectKind::DesignSystem);
}

#[test]
fn test_full_catalog_round_trip_export_import() {
    let mut catalog = Catalog::empty();

    // 1. Domain
    let mut domain = KbObject::new(
        ObjectId::new("domamount").unwrap(),
        "Amount",
        ObjectKind::Domain,
    );
    domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Numeric {
            precision: 12,
            scale: 2,
        })
        .unwrap(),
    });

    // 2. Attribute
    let mut attr = KbObject::new(
        ObjectId::new("invoicetotal").unwrap(),
        "InvoiceTotal",
        ObjectKind::Attribute,
    );
    attr.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("domamount").unwrap(),
    });
    attr.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"Amount"}"#.into(),
    });

    // 3. SDT
    let mut sdt = KbObject::new(
        ObjectId::new("invoicesdt").unwrap(),
        "InvoiceSDT",
        ObjectKind::StructuredDataType,
    );
    sdt.source_parts.push(SourcePart {
        name: "FIELDS".into(),
        source: r#"{"Total":{"kind":"numeric","precision":12,"scale":2}}"#.into(),
    });

    // 4. Procedure
    let mut proc = KbObject::new(
        ObjectId::new("calculatetax").unwrap(),
        "CalculateTax",
        ObjectKind::Procedure,
    );
    proc.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: "Parm(in:&Subtotal, out:&Tax);".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Tax = &Subtotal * 0.1".into(),
    });

    // 5. Transaction
    let mut trn = KbObject::new(
        ObjectId::new("invoice").unwrap(),
        "Invoice",
        ObjectKind::Transaction,
    );
    let mut root = TransactionLevel::new("Invoice");
    root.attributes.push(TransactionAttributeRef {
        name: "InvoiceId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 8,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.attributes.push(TransactionAttributeRef {
        name: "InvoiceTotal".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 12,
            scale: 2,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    let trn_struct = TransactionStructure::new(root);
    trn.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: serde_json::to_string(&trn_struct).unwrap(),
    });

    // 6. DataProvider
    let mut dp = KbObject::new(
        ObjectId::new("invoicedp").unwrap(),
        "InvoiceDP",
        ObjectKind::DataProvider,
    );
    dp.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "Invoice { InvoiceId = 1 }".into(),
    });

    // 7. DataSelector
    let mut ds = KbObject::new(
        ObjectId::new("recentinvoices").unwrap(),
        "RecentInvoices",
        ObjectKind::DataSelector,
    );
    ds.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "Where InvoiceDate >= Today()".into(),
    });

    // 8. DesignSystem
    let mut dso = KbObject::new(
        ObjectId::new("invoicetheme").unwrap(),
        "InvoiceTheme",
        ObjectKind::DesignSystem,
    );
    dso.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "styles InvoiceTheme { .total { font-weight: bold; } }".into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(domain),
                Edit::Upsert(attr),
                Edit::Upsert(sdt),
                Edit::Upsert(proc),
                Edit::Upsert(trn),
                Edit::Upsert(dp),
                Edit::Upsert(ds),
                Edit::Upsert(dso),
            ],
        )
        .expect("catalog apply");

    let initial_count = catalog.objects().count();
    assert_eq!(initial_count, 8);

    // Export to text tree
    let snapshot = export_catalog_to_text_tree(&catalog);
    assert_eq!(snapshot.files.len(), 8);

    // Import back from text tree
    let imported = import_text_tree_to_catalog(&snapshot).expect("import catalog");
    assert_eq!(imported.objects().count(), initial_count);

    // Compare all object names
    for original_obj in catalog.objects() {
        let matching = imported
            .objects()
            .find(|o| o.name.eq_ignore_ascii_case(&original_obj.name));
        assert!(
            matching.is_some(),
            "Missing matching object for {}",
            original_obj.name
        );
        let matching = matching.unwrap();
        assert_eq!(matching.kind, original_obj.kind);
    }
}

#[test]
fn test_file_system_write_and_read_roundtrip() {
    let temp_dir = temp_test_dir("fs_roundtrip");

    let snapshot = TextTreeSnapshot::new(vec![
        TextTreeFile::new(
            "Objects/Procedures/ProcA.prc",
            "=== Source ===\nmsg('hello from A')",
            ObjectKind::Procedure,
        ),
        TextTreeFile::new(
            "Objects/Transactions/TrnB.trn",
            "TrnB\n{\n  *TrnBId : Numeric(4, 0)\n}\n",
            ObjectKind::Transaction,
        ),
        TextTreeFile::new(
            "Definitions/SDTs/SdtC.sdt",
            "SDT SdtC\n{\n  ItemCode : Character(20)\n}\n",
            ObjectKind::StructuredDataType,
        ),
        TextTreeFile::new(
            "Definitions/Domains/DomD.dom",
            "Type: Numeric(10, 2)\n",
            ObjectKind::Domain,
        ),
    ]);

    // Write to disk
    write_to_directory(&snapshot, &temp_dir).expect("write to disk");

    // Verify files exist on disk
    assert!(temp_dir.join("Objects/Procedures/ProcA.prc").exists());
    assert!(temp_dir.join("Objects/Transactions/TrnB.trn").exists());
    assert!(temp_dir.join("Definitions/SDTs/SdtC.sdt").exists());
    assert!(temp_dir.join("Definitions/Domains/DomD.dom").exists());

    // Read back from directory
    let loaded_snapshot = read_from_directory(&temp_dir).expect("read from disk");
    assert_eq!(loaded_snapshot.files.len(), 4);

    for orig_file in &snapshot.files {
        let loaded_file = loaded_snapshot
            .get(&orig_file.path)
            .unwrap_or_else(|| panic!("missing file: {}", orig_file.path));
        assert_eq!(loaded_file.kind, orig_file.kind);
        assert_eq!(loaded_file.content.trim(), orig_file.content.trim());
    }

    // Clean up
    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn test_import_fails_closed_on_corrupted_extension() {
    // 1. Corrupted unknown extension
    let bad_ext_snapshot = TextTreeSnapshot::new(vec![TextTreeFile::new(
        "Objects/Procedures/MyProc.corrupted",
        "msg('hi')",
        ObjectKind::Procedure,
    )]);
    let err = import_text_tree_to_catalog(&bad_ext_snapshot).unwrap_err();
    assert!(
        err.contains("unrecognized or corrupted file extension"),
        "Unexpected error: {err}"
    );

    // 2. Missing extension
    let no_ext_snapshot = TextTreeSnapshot::new(vec![TextTreeFile::new(
        "Objects/Procedures/MyProc",
        "msg('hi')",
        ObjectKind::Procedure,
    )]);
    let err2 = import_text_tree_to_catalog(&no_ext_snapshot).unwrap_err();
    assert!(
        err2.contains("has no file extension"),
        "Unexpected error: {err2}"
    );

    // 3. Extension kind mismatch
    let mismatch_snapshot = TextTreeSnapshot::new(vec![TextTreeFile::new(
        "Objects/Procedures/MyProc.trn",
        "MyProc\n{\n  *Id : Numeric(4, 0)\n}\n",
        ObjectKind::Procedure,
    )]);
    let err3 = import_text_tree_to_catalog(&mismatch_snapshot).unwrap_err();
    assert!(
        err3.contains("file kind mismatch"),
        "Unexpected error: {err3}"
    );
}

#[test]
fn test_text_tree_json_serialization() {
    let snapshot = TextTreeSnapshot::new(vec![
        TextTreeFile::new(
            "Objects/Procedures/Calc.prc",
            "=== Source ===\nmsg('test')",
            ObjectKind::Procedure,
        ),
        TextTreeFile::new(
            "Definitions/Domains/Amount.dom",
            "Type: Numeric(12, 2)\n",
            ObjectKind::Domain,
        ),
    ]);

    let json = serde_json::to_string_pretty(&snapshot).expect("serialize snapshot");
    let deserialized: TextTreeSnapshot = serde_json::from_str(&json).expect("deserialize snapshot");

    assert_eq!(snapshot, deserialized);
    assert_eq!(deserialized.files.len(), 2);
    assert_eq!(deserialized.files[0].kind, ObjectKind::Procedure);
    assert_eq!(deserialized.files[1].kind, ObjectKind::Domain);
}
