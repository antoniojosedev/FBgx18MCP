use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    exchange,
    model::DataType,
    transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure},
};
use std::path::PathBuf;

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let dir = std::env::temp_dir().join(format!("{prefix}-{now}"));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).unwrap()
}

fn run_xml(input_xml: &str, entry: &str) -> std::process::Output {
    let directory = unique_temp_dir("gx-trn-xml-run");
    let input = directory.join("authored.xml");
    std::fs::write(&input, input_xml).unwrap();
    let output = std::process::Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("xml-run")
        .arg(&input)
        .arg(entry)
        .arg(directory.join("output"))
        .output()
        .unwrap();
    assert_eq!(std::fs::read_to_string(&input).unwrap(), input_xml);
    std::fs::remove_dir_all(&directory).unwrap();
    output
}

fn normalize_crlf(value: &str) -> String {
    value.replace("\r\n", "\n")
}

#[test]
fn transaction_structure_in_catalog() {
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
        name: "CustomerStatus".into(),
        is_key: false,
        data_type: Some(DataType::Boolean {}),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let structure = TransactionStructure::new(root);
    assert!(structure.validate().is_ok());
    assert_eq!(structure.root_primary_key(), vec!["CustomerId"]);
    assert_eq!(
        structure.all_attributes(),
        vec!["CustomerId", "CustomerName", "CustomerStatus"]
    );
}

#[test]
fn transaction_nested_levels_in_catalog() {
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
        name: "InvoiceDate".into(),
        is_key: false,
        data_type: Some(DataType::Date {}),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let mut lines = TransactionLevel::new("Lines");
    lines.attributes.push(TransactionAttributeRef {
        name: "InvoiceLineId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    lines.attributes.push(TransactionAttributeRef {
        name: "ProductId".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 6,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: true,
    });
    lines.attributes.push(TransactionAttributeRef {
        name: "ProductQty".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    lines.attributes.push(TransactionAttributeRef {
        name: "LineTotal".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 10,
            scale: 2,
        }),
        formula: Some("ProductPrice * ProductQty".into()),
        is_nullable: false,
        is_foreign_key: false,
    });

    root.levels.push(lines);

    let structure = TransactionStructure::new(root);
    assert!(structure.validate().is_ok());
    assert_eq!(structure.root_primary_key(), vec!["InvoiceId"]);
    assert_eq!(
        structure.all_attributes(),
        vec![
            "InvoiceId",
            "InvoiceDate",
            "InvoiceLineId",
            "ProductId",
            "ProductQty",
            "LineTotal"
        ]
    );
}

#[test]
fn transaction_with_referenced_attributes_in_catalog() {
    let mut catalog = Catalog::empty();

    // 1. Attribute CustomerId
    let mut attr_id = KbObject::new(id("att-cid"), "CustomerId", ObjectKind::Attribute);
    attr_id.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Numeric {
            precision: 8,
            scale: 0,
        })
        .unwrap(),
    });

    // 2. Attribute CustomerName
    let mut attr_name = KbObject::new(id("att-cname"), "CustomerName", ObjectKind::Attribute);
    attr_name.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Varchar { length: 40 }).unwrap(),
    });

    // 3. Transaction Customer
    let mut trn_root = TransactionLevel::new("Customer");
    trn_root.attributes.push(TransactionAttributeRef {
        name: "CustomerId".into(),
        is_key: true,
        data_type: None, // resolved from Attribute
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    trn_root.attributes.push(TransactionAttributeRef {
        name: "CustomerName".into(),
        is_key: false,
        data_type: None, // resolved from Attribute
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    let trn_struct = TransactionStructure::new(trn_root);

    let mut trn = KbObject::new(id("trn-cust"), "Customer", ObjectKind::Transaction);
    trn.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: serde_json::to_string(&trn_struct).unwrap(),
    });
    trn.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("att-cid"),
    });
    trn.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("att-cname"),
    });

    // 4. Procedure TestCustomer
    let mut proc = KbObject::new(id("proc-test"), "TestCustomer", ObjectKind::Procedure);
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: r#"
&Cust = new()
&Cust.CustomerId = 42
&Cust.CustomerName = "Alice Corp"
Msg(&Cust.CustomerName, status)
"#
        .into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"Cust":{"kind":"record","schema":"Customer"}}"#.into(),
    });
    proc.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("trn-cust"),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(attr_id),
                Edit::Upsert(attr_name),
                Edit::Upsert(trn),
                Edit::Upsert(proc),
            ],
        )
        .unwrap();

    let project = gx_engine::kb::project_from_catalog(&catalog, &id("proc-test"), None).unwrap();
    let rust_code = gx_engine::compile(&project).unwrap();
    assert!(rust_code.contains("CUSTOMER"));
}

#[test]
fn transaction_fails_closed_on_missing_key() {
    let mut root = TransactionLevel::new("InvalidNoKey");
    root.attributes.push(TransactionAttributeRef {
        name: "CustomerName".into(),
        is_key: false,
        data_type: Some(DataType::Character { length: 50 }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let structure = TransactionStructure::new(root);
    let err = structure.validate().unwrap_err();
    assert!(err.contains("must have at least one key attribute"));
}

#[test]
fn transaction_fails_closed_on_duplicate_level_name() {
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

    let mut child = TransactionLevel::new("Invoice"); // duplicate level name
    child.attributes.push(TransactionAttributeRef {
        name: "LineId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.levels.push(child);

    let structure = TransactionStructure::new(root);
    let err = structure.validate().unwrap_err();
    assert!(err.contains("duplicate Transaction level name 'Invoice'"));
}

#[test]
fn transaction_fails_closed_on_duplicate_attribute() {
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
        name: "customerid".into(), // duplicate (case-insensitive)
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 6,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let structure = TransactionStructure::new(root);
    let err = structure.validate().unwrap_err();
    assert!(err.contains("duplicate attribute 'customerid'"));
}

#[test]
fn transaction_fails_closed_on_excessive_depth() {
    let mut current = TransactionLevel::new("L0");
    current.attributes.push(TransactionAttributeRef {
        name: "K0".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    // Nest 17 levels deep
    for i in 1..=17 {
        let mut next = TransactionLevel::new(format!("L{i}"));
        next.attributes.push(TransactionAttributeRef {
            name: format!("K{i}"),
            is_key: true,
            data_type: Some(DataType::Numeric {
                precision: 4,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        });
        current.levels.push(next);
        current = current.levels.pop().unwrap();
    }

    let root = current; // innermost
    let structure = TransactionStructure::new(root);
    let _ = structure.validate(); // innermost alone passes

    // Now test building full chain
    let mut chain = TransactionLevel::new("Depth0");
    chain.attributes.push(TransactionAttributeRef {
        name: "Key0".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    let mut cursor = &mut chain;
    for i in 1..=17 {
        let mut next = TransactionLevel::new(format!("Depth{i}"));
        next.attributes.push(TransactionAttributeRef {
            name: format!("Key{i}"),
            is_key: true,
            data_type: Some(DataType::Numeric {
                precision: 4,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        });
        cursor.levels.push(next);
        cursor = cursor.levels.last_mut().unwrap();
    }

    let structure = TransactionStructure::new(chain);
    let err = structure.validate().unwrap_err();
    assert!(err.contains("exceeds maximum depth of 16"));
}

#[test]
fn transaction_fails_closed_on_invalid_level_name() {
    let mut root = TransactionLevel::new("Invalid Level Name!");
    root.attributes.push(TransactionAttributeRef {
        name: "Id".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let structure = TransactionStructure::new(root);
    let err = structure.validate().unwrap_err();
    assert!(err.contains("contains invalid characters"));
}

#[test]
fn exportfile_xml_with_transaction_structure() {
    let xml = r#"<ExportFile><Objects>
<Object guid="dom-code" name="CustomerCode" type="00972a17-9975-449e-aab1-d26165d51393">
  <Properties>
    <Property><Name>Name</Name><Value>CustomerCode</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
    <Property><Name>Length</Name><Value>6</Value></Property>
    <Property><Name>Decimals</Name><Value>0</Value></Property>
  </Properties>
</Object>
<Object guid="att-cid" name="CustomerId" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>CustomerId</Value></Property>
    <Property><Name>DomainBasedOn</Name><Value>CustomerCode</Value></Property>
  </Properties>
</Object>
<Object guid="att-cname" name="CustomerName" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>CustomerName</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Character</Value></Property>
    <Property><Name>Length</Name><Value>50</Value></Property>
  </Properties>
</Object>
<Object guid="trn-cust" name="Customer" type="1db606f2-af09-4cf9-a3b5-b481519d28f6">
  <Part type="1608677c-a7a2-4a00-8809-6d2466085a5a">
    <Level Name="Customer">
      <Attribute Name="CustomerId">
        <Properties>
          <Property><Name>IsKey</Name><Value>True</Value></Property>
        </Properties>
      </Attribute>
      <Attribute Name="CustomerName">
        <Properties>
          <Property><Name>IsKey</Name><Value>False</Value></Property>
        </Properties>
      </Attribute>
    </Level>
  </Part>
  <Part type="9b0a32a3-de6d-4be1-a4dd-1b85d3741534">
    <Source><![CDATA[Error('Invalid') if CustomerId = 0;]]></Source>
  </Part>
  <Properties>
    <Property><Name>Name</Name><Value>Customer</Value></Property>
  </Properties>
</Object>
</Objects></ExportFile>"#;

    let catalog = exchange::catalog_from_xml(xml).unwrap();
    assert_eq!(catalog.objects().count(), 4);
    assert_eq!(
        catalog
            .objects()
            .filter(|o| o.kind == ObjectKind::Transaction)
            .count(),
        1
    );

    let trn = catalog
        .objects()
        .find(|o| o.kind == ObjectKind::Transaction)
        .unwrap();
    assert_eq!(trn.name, "CUSTOMER");
    assert_eq!(trn.references.len(), 2); // CustomerId and CustomerName

    let struct_part = trn
        .source_parts
        .iter()
        .find(|p| p.name == "STRUCTURE")
        .unwrap();
    let structure: TransactionStructure = serde_json::from_str(&struct_part.source).unwrap();
    assert_eq!(structure.root_primary_key(), vec!["CustomerId"]);
    assert_eq!(
        structure.all_attributes(),
        vec!["CustomerId", "CustomerName"]
    );
}

#[test]
fn exportfile_xml_transaction_with_attributes_and_procedure_run() {
    let xml = r#"<ExportFile><Objects>
<Object guid="dom-qty" name="Qty" type="00972a17-9975-449e-aab1-d26165d51393">
  <Properties>
    <Property><Name>Name</Name><Value>Qty</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
    <Property><Name>Length</Name><Value>4</Value></Property>
    <Property><Name>Decimals</Name><Value>0</Value></Property>
  </Properties>
</Object>
<Object guid="att-inv-id" name="InvId" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>InvId</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
    <Property><Name>Length</Name><Value>8</Value></Property>
    <Property><Name>Decimals</Name><Value>0</Value></Property>
  </Properties>
</Object>
<Object guid="att-inv-qty" name="InvQty" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>InvQty</Value></Property>
    <Property><Name>DomainBasedOn</Name><Value>Qty</Value></Property>
  </Properties>
</Object>
<Object guid="trn-invoice" name="Invoice" type="1db606f2-af09-4cf9-a3b5-b481519d28f6">
  <Part type="1608677c-a7a2-4a00-8809-6d2466085a5a">
    <Level Name="Invoice">
      <Attribute Name="InvId" IsKey="True" />
      <Attribute Name="InvQty" />
    </Level>
  </Part>
  <Properties>
    <Property><Name>Name</Name><Value>Invoice</Value></Property>
  </Properties>
</Object>
<Object guid="proc-run" name="ProcessInvoice" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
  <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
    <Source><![CDATA[&Invoice = new()
&Invoice.InvId = 1001
&Invoice.InvQty = 5
&Total = &Invoice.InvQty * 10
Msg(Str(&Total), status)]]></Source>
  </Part>
  <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
    <Variable Name="Invoice">
      <Properties>
        <Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Invoice</Value></Property>
      </Properties>
    </Variable>
    <Variable Name="Total">
      <Properties>
        <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
        <Property><Name>Length</Name><Value>8</Value></Property>
        <Property><Name>Decimals</Name><Value>0</Value></Property>
      </Properties>
    </Variable>
  </Part>
  <Properties>
    <Property><Name>CALL_PROTOCOL</Name><Value>CLINE</Value></Property>
  </Properties>
</Object>
</Objects></ExportFile>"#;

    let catalog = exchange::catalog_from_xml(xml).unwrap();
    assert_eq!(catalog.objects().count(), 5);

    let project = exchange::project_from_xml(xml, "ProcessInvoice", None).unwrap();
    assert!(gx_engine::compile(&project).is_ok());

    let output = run_xml(xml, "ProcessInvoice");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(normalize_crlf(stdout.trim()), "50");
}

#[test]
fn test_business_component_multilevel_crud_and_validations() {
    use gx_engine::transaction::{BcLevelRow, BcMode, BcStore, BusinessComponent, TransactionRule};

    // Construct a multi-level Transaction: Invoice (Root) -> Lines (Level 2)
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
        name: "CustomerId".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 6,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: true,
    });
    root.attributes.push(TransactionAttributeRef {
        name: "InvoiceStatus".into(),
        is_key: false,
        data_type: Some(DataType::Character { length: 20 }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let mut lines_level = TransactionLevel::new("Lines");
    lines_level.attributes.push(TransactionAttributeRef {
        name: "LineId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    lines_level.attributes.push(TransactionAttributeRef {
        name: "ProductId".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 6,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: true,
    });
    lines_level.attributes.push(TransactionAttributeRef {
        name: "LineQty".into(),
        is_key: false,
        data_type: Some(DataType::Numeric {
            precision: 5,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.levels.push(lines_level);

    let structure = TransactionStructure::new(root)
        .with_business_component(true)
        .with_rule(TransactionRule::Default {
            attribute: "InvoiceStatus".into(),
            value: "Pending".into(),
        });

    assert!(structure.validate().is_ok());

    let mut store = BcStore::new();

    // 1. Initial BC instance
    let mut bc = BusinessComponent::new(structure);
    assert_eq!(bc.mode, BcMode::Insert);
    assert!(bc.success());
    assert!(!bc.fail());

    // 2. Load non-existent key fails
    assert!(!bc.load(&store, "1001"));
    assert!(bc.fail());
    assert!(!bc.success());
    assert_eq!(bc.errors().len(), 1);
    assert_eq!(bc.errors()[0].id, "GXM001");

    // 3. Save without primary key fails
    assert!(!bc.save(&mut store));
    assert!(bc.fail());

    // 4. Save with valid root data & child rows
    bc.set_value("InvoiceId", "1001");
    bc.set_value("CustomerId", "42");
    // InvoiceStatus not set, will be populated by Default rule

    let mut line1 = BcLevelRow::new();
    line1.set("LineId", "1");
    line1.set("ProductId", "99");
    line1.set("LineQty", "5");
    bc.add_child("Lines", line1);

    let mut line2 = BcLevelRow::new();
    line2.set("LineId", "2");
    line2.set("ProductId", "105");
    line2.set("LineQty", "10");
    bc.add_child("Lines", line2);

    assert!(bc.save(&mut store));
    assert!(bc.success());
    assert!(!bc.fail());
    assert_eq!(bc.mode, BcMode::Update);
    assert_eq!(bc.get_value("InvoiceStatus"), Some("Pending"));
    assert_eq!(store.count(), 1);

    // 5. Load persisted BC into a new instance
    let mut bc_loaded = BusinessComponent::new(bc.structure.clone());
    assert!(bc_loaded.load(&store, "1001"));
    assert!(bc_loaded.success());
    assert_eq!(bc_loaded.get_value("CustomerId"), Some("42"));
    assert_eq!(bc_loaded.get_value("InvoiceStatus"), Some("Pending"));

    let loaded_lines = bc_loaded.get_children("Lines");
    assert_eq!(loaded_lines.len(), 2);
    assert_eq!(loaded_lines[0].get("LineId"), Some("1"));
    assert_eq!(loaded_lines[0].get("LineQty"), Some("5"));
    assert_eq!(loaded_lines[1].get("LineId"), Some("2"));
    assert_eq!(loaded_lines[1].get("LineQty"), Some("10"));

    // 6. Delete operation
    assert!(bc_loaded.delete(&mut store));
    assert!(bc_loaded.success());
    assert_eq!(bc_loaded.mode, BcMode::Delete);
    assert_eq!(store.count(), 0);

    // Subsequent load fails
    let mut bc_after_del = BusinessComponent::new(bc.structure.clone());
    assert!(!bc_after_del.load(&store, "1001"));
    assert!(bc_after_del.fail());
}

#[test]
fn test_business_component_duplicate_child_keys_rejected() {
    use gx_engine::transaction::{BcLevelRow, BcStore, BusinessComponent};

    let mut root = TransactionLevel::new("Order");
    root.attributes.push(TransactionAttributeRef {
        name: "OrderId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 8,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });

    let mut lines = TransactionLevel::new("OrderLines");
    lines.attributes.push(TransactionAttributeRef {
        name: "LineId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    });
    root.levels.push(lines);

    let structure = TransactionStructure::new(root).with_business_component(true);
    let mut bc = BusinessComponent::new(structure);
    bc.set_value("OrderId", "500");

    // Add two rows with duplicate LineId = "1"
    let mut l1 = BcLevelRow::new();
    l1.set("LineId", "1");
    bc.add_child("OrderLines", l1);

    let mut l2 = BcLevelRow::new();
    l2.set("LineId", "1");
    bc.add_child("OrderLines", l2);

    let mut store = BcStore::new();
    assert!(!bc.save(&mut store));
    assert!(bc.fail());
    assert!(bc.errors().iter().any(|e| e.id == "GXM006"));
    assert_eq!(store.count(), 0);
}
