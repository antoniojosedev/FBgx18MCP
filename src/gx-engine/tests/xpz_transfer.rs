use gx_engine::catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::xpz::{ImportStrategy, XpzObject, XpzPackage};

#[test]
fn test_xpz_xml_roundtrip() {
    let xml = r#"<?xml version="1.0" encoding="utf-8"?>
<ExportFile Generator="GeneXus" Version="18.0" ExportedAt="2026-10-17T10:00:00Z">
  <Object Guid="11111111-2222-3333-4444-555555555555" Type="Procedure" Name="GetCustomer">
    <Description>Fetch customer details</Description>
    <Part Name="Source">
      <Source><![CDATA[Print("Customer 123")]]></Source>
    </Part>
  </Object>
  <Object Guid="66666666-7777-8888-9999-000000000000" Type="Transaction" Name="Customer">
    <Part Name="Structure">
      <Source><![CDATA[CustomerId* : Numeric(5); CustomerName : Char(50);]]></Source>
    </Part>
  </Object>
</ExportFile>
"#;

    let package = XpzPackage::parse_xml(xml).expect("XPZ XML should parse");
    assert_eq!(package.genexus_version, "18.0");
    assert_eq!(package.objects.len(), 2);

    let proc_obj = &package.objects[0];
    assert_eq!(proc_obj.name, "GetCustomer");
    assert_eq!(proc_obj.object_type, "Procedure");
    assert_eq!(
        proc_obj.description.as_deref(),
        Some("Fetch customer details")
    );
    assert_eq!(proc_obj.parts.len(), 1);
    assert_eq!(proc_obj.parts[0].source, "Print(\"Customer 123\")");

    let regenerated = package.to_xml();
    let reparsed = XpzPackage::parse_xml(&regenerated).expect("Regenerated XML should parse");
    assert_eq!(package, reparsed);
}

#[test]
fn test_xpz_export_from_catalog() {
    let mut catalog = Catalog::empty();
    let id = ObjectId::new("proca").unwrap();
    let mut obj = KbObject::new(id, "ProcA", ObjectKind::Procedure);
    obj.source_parts = vec![SourcePart {
        name: "SOURCE".to_string(),
        source: "&X = 1".to_string(),
    }];
    catalog
        .apply(catalog.revision(), &[Edit::Upsert(obj)])
        .unwrap();

    let package = XpzPackage::from_catalog(&catalog, "18.0");
    assert_eq!(package.objects.len(), 1);
    assert_eq!(package.objects[0].name, "ProcA");
    assert_eq!(package.objects[0].object_type, "Procedure");

    let xml = package.to_xml();
    assert!(xml.contains("<ExportFile"));
    assert!(xml.contains("Name=\"ProcA\""));
}

#[test]
fn test_xpz_merge_into_catalog_overwrite_strategy() {
    let mut catalog = Catalog::empty();
    let id = ObjectId::new("myproc").unwrap();
    let mut obj = KbObject::new(id, "MyProc", ObjectKind::Procedure);
    obj.source_parts = vec![SourcePart {
        name: "SOURCE".to_string(),
        source: "&Old = 1".to_string(),
    }];
    catalog
        .apply(catalog.revision(), &[Edit::Upsert(obj)])
        .unwrap();

    let package = XpzPackage {
        genexus_version: "18.0".to_string(),
        exported_at: "".to_string(),
        objects: vec![XpzObject {
            guid: "guid-1".to_string(),
            object_type: "Procedure".to_string(),
            name: "myproc".to_string(),
            description: None,
            parts: vec![SourcePart {
                name: "SOURCE".to_string(),
                source: "&New = 2".to_string(),
            }],
        }],
    };

    let count = package
        .merge_into_catalog(&mut catalog, ImportStrategy::Overwrite)
        .expect("Merge should succeed");
    assert_eq!(count, 1);
    let myproc = catalog.get(&ObjectId::new("myproc").unwrap()).unwrap();
    assert_eq!(myproc.source_parts[0].source, "&New = 2");
}

#[test]
fn test_xpz_merge_into_catalog_keep_existing_strategy() {
    let mut catalog = Catalog::empty();
    let id = ObjectId::new("myproc").unwrap();
    let mut obj = KbObject::new(id, "MyProc", ObjectKind::Procedure);
    obj.source_parts = vec![SourcePart {
        name: "SOURCE".to_string(),
        source: "&Keep = 1".to_string(),
    }];
    catalog
        .apply(catalog.revision(), &[Edit::Upsert(obj)])
        .unwrap();

    let package = XpzPackage {
        genexus_version: "18.0".to_string(),
        exported_at: "".to_string(),
        objects: vec![XpzObject {
            guid: "guid-1".to_string(),
            object_type: "Procedure".to_string(),
            name: "myproc".to_string(),
            description: None,
            parts: vec![SourcePart {
                name: "SOURCE".to_string(),
                source: "&Incoming = 2".to_string(),
            }],
        }],
    };

    let count = package
        .merge_into_catalog(&mut catalog, ImportStrategy::KeepExisting)
        .expect("Merge should succeed");
    assert_eq!(count, 0); // Kept existing
    let myproc = catalog.get(&ObjectId::new("myproc").unwrap()).unwrap();
    assert_eq!(myproc.source_parts[0].source, "&Keep = 1");
}

#[test]
fn test_xpz_merge_into_catalog_fail_on_conflict_strategy() {
    let mut catalog = Catalog::empty();
    let id = ObjectId::new("conflictingproc").unwrap();
    let obj = KbObject::new(id, "ConflictingProc", ObjectKind::Procedure);
    catalog
        .apply(catalog.revision(), &[Edit::Upsert(obj)])
        .unwrap();

    let package = XpzPackage {
        genexus_version: "18.0".to_string(),
        exported_at: "".to_string(),
        objects: vec![XpzObject {
            guid: "guid-1".to_string(),
            object_type: "Procedure".to_string(),
            name: "conflictingproc".to_string(),
            description: None,
            parts: vec![],
        }],
    };

    let err = package
        .merge_into_catalog(&mut catalog, ImportStrategy::FailOnConflict)
        .unwrap_err();
    assert!(err.contains("Import conflict: Object 'Procedure:conflictingproc' already exists"));
}

#[test]
fn test_xpz_fail_closed_on_corrupted_xml() {
    let err = XpzPackage::parse_xml("<ExportFile><Object></ExportFile>").unwrap_err();
    assert!(err.contains("Corrupted XPZ XML"));
}

#[test]
fn test_xpz_fail_closed_on_missing_object_name() {
    let xml = r#"<ExportFile><Object Type="Procedure"></Object></ExportFile>"#;
    let err = XpzPackage::parse_xml(xml).unwrap_err();
    assert!(err.contains("Object missing Name attribute"));
}

#[test]
fn test_xpz_fail_closed_on_duplicate_object_guid() {
    let xml = r#"<ExportFile>
        <Object Guid="dup-guid" Type="Procedure" Name="P1"></Object>
        <Object Guid="dup-guid" Type="Procedure" Name="P2"></Object>
    </ExportFile>"#;
    let err = XpzPackage::parse_xml(xml).unwrap_err();
    assert!(err.contains("Duplicate Object GUID 'dup-guid'"));
}

#[test]
fn test_xpz_fail_closed_on_unsupported_object_type() {
    let mut catalog = Catalog::empty();
    let package = XpzPackage {
        genexus_version: "18.0".to_string(),
        exported_at: "".to_string(),
        objects: vec![XpzObject {
            guid: "guid-1".to_string(),
            object_type: "UnknownWidget".to_string(),
            name: "badobj".to_string(),
            description: None,
            parts: vec![],
        }],
    };
    let err = package
        .merge_into_catalog(&mut catalog, ImportStrategy::Overwrite)
        .unwrap_err();
    assert!(err.contains("Unsupported object type 'UnknownWidget'"));
}

#[test]
fn test_xpz_json_serialization() {
    let package = XpzPackage {
        genexus_version: "18.0".to_string(),
        exported_at: "2026-10-17T00:00:00Z".to_string(),
        objects: vec![],
    };
    let json = serde_json::to_string(&package).unwrap();
    let roundtrip: XpzPackage = serde_json::from_str(&json).unwrap();
    assert_eq!(package, roundtrip);
}
