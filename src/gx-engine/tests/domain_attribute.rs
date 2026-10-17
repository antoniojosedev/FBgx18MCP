use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    exchange, kb,
    model::DataType,
};

fn unique_temp_dir(prefix: &str) -> std::path::PathBuf {
    use std::sync::atomic::{AtomicU64, Ordering};
    static SEQUENCE: AtomicU64 = AtomicU64::new(0);
    let directory = std::env::temp_dir().join(format!(
        "{prefix}-{}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos(),
        SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    std::fs::create_dir(&directory).unwrap();
    directory
}

fn run_xml(input_xml: &str, entry: &str) -> std::process::Output {
    let directory = unique_temp_dir("gx-domain-xml-run");
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
fn procedure_variables_based_on_domain_and_attribute_in_catalog() {
    let mut catalog = Catalog::empty();

    // 1. Domain CustCode: Numeric(6, 0)
    let mut domain = KbObject::new(
        ObjectId::new("dom-custcode").unwrap(),
        "CustCode",
        ObjectKind::Domain,
    );
    domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Numeric {
            precision: 6,
            scale: 0,
        })
        .unwrap(),
    });

    // 2. Attribute CustId: Based on Domain CustCode
    let mut attr = KbObject::new(
        ObjectId::new("att-custid").unwrap(),
        "CustId",
        ObjectKind::Attribute,
    );
    attr.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-custcode").unwrap(),
    });
    attr.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"CustCode"}"#.into(),
    });

    // 3. Procedure Main
    let mut proc = KbObject::new(
        ObjectId::new("proc-main").unwrap(),
        "Main",
        ObjectKind::Procedure,
    );
    proc.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-custcode").unwrap(),
    });
    proc.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("att-custid").unwrap(),
    });
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "&Code = 120\n&Id = 350\n&Total = &Code + &Id\nMsg(Str(&Total), status)".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{
            "Code": {"based_on": "CustCode"},
            "Id": {"based_on": "CustId"},
            "Total": {"kind": "numeric", "precision": 10, "scale": 0}
        }"#
        .into(),
    });

    catalog
        .apply(
            0,
            &[Edit::Upsert(domain), Edit::Upsert(attr), Edit::Upsert(proc)],
        )
        .unwrap();

    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("proc-main").unwrap(), None).unwrap();

    assert_eq!(project.entry, "MAIN");
    assert_eq!(project.procedures.len(), 1);
    let compiled = gx_engine::compile(&project);
    assert!(compiled.is_ok(), "{:?}", compiled.err());
}

#[test]
fn chained_domains_resolve_in_catalog() {
    let mut catalog = Catalog::empty();

    let mut base_domain = KbObject::new(
        ObjectId::new("dom-base").unwrap(),
        "BaseDomain",
        ObjectKind::Domain,
    );
    base_domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Varchar { length: 30 }).unwrap(),
    });

    let mut mid_domain = KbObject::new(
        ObjectId::new("dom-mid").unwrap(),
        "MidDomain",
        ObjectKind::Domain,
    );
    mid_domain.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-base").unwrap(),
    });
    mid_domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"BaseDomain"}"#.into(),
    });

    let mut leaf_domain = KbObject::new(
        ObjectId::new("dom-leaf").unwrap(),
        "LeafDomain",
        ObjectKind::Domain,
    );
    leaf_domain.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-mid").unwrap(),
    });
    leaf_domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"MidDomain"}"#.into(),
    });

    let mut proc = KbObject::new(
        ObjectId::new("proc-main").unwrap(),
        "Main",
        ObjectKind::Procedure,
    );
    proc.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-leaf").unwrap(),
    });
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "&Text = \"chained-domain-resolved\"\nMsg(&Text, status)".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"Text": {"based_on": "LeafDomain"}}"#.into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(base_domain),
                Edit::Upsert(mid_domain),
                Edit::Upsert(leaf_domain),
                Edit::Upsert(proc),
            ],
        )
        .unwrap();

    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("proc-main").unwrap(), None).unwrap();

    assert!(gx_engine::compile(&project).is_ok());
}

#[test]
fn domain_cannot_be_based_on_attribute_in_catalog() {
    let mut catalog = Catalog::empty();

    let mut attr = KbObject::new(
        ObjectId::new("att-code").unwrap(),
        "CodeAttr",
        ObjectKind::Attribute,
    );
    attr.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Numeric {
            precision: 4,
            scale: 0,
        })
        .unwrap(),
    });

    let mut bad_domain = KbObject::new(
        ObjectId::new("dom-bad").unwrap(),
        "BadDomain",
        ObjectKind::Domain,
    );
    bad_domain.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("att-code").unwrap(),
    });
    bad_domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"CodeAttr"}"#.into(),
    });

    let mut proc = KbObject::new(
        ObjectId::new("proc-main").unwrap(),
        "Main",
        ObjectKind::Procedure,
    );
    proc.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-bad").unwrap(),
    });
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "&X = 1".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"X": {"based_on": "BadDomain"}}"#.into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(attr),
                Edit::Upsert(bad_domain),
                Edit::Upsert(proc),
            ],
        )
        .unwrap();

    let err =
        kb::project_from_catalog(&catalog, &ObjectId::new("proc-main").unwrap(), None).unwrap_err();

    assert!(
        err.contains("cannot be based on non-domain"),
        "unexpected error: {err}"
    );
}

#[test]
fn cycle_detection_in_domains_in_catalog() {
    let mut catalog = Catalog::empty();

    let mut dom_a = KbObject::new(ObjectId::new("dom-a").unwrap(), "DomA", ObjectKind::Domain);
    dom_a.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-b").unwrap(),
    });
    dom_a.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"DomB"}"#.into(),
    });

    let mut dom_b = KbObject::new(ObjectId::new("dom-b").unwrap(), "DomB", ObjectKind::Domain);
    dom_b.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-a").unwrap(),
    });
    dom_b.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"based_on":"DomA"}"#.into(),
    });

    let mut proc = KbObject::new(
        ObjectId::new("proc-main").unwrap(),
        "Main",
        ObjectKind::Procedure,
    );
    proc.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-a").unwrap(),
    });
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "&X = 1".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"X": {"based_on": "DomA"}}"#.into(),
    });

    catalog
        .apply(
            0,
            &[Edit::Upsert(dom_a), Edit::Upsert(dom_b), Edit::Upsert(proc)],
        )
        .unwrap();

    let err =
        kb::project_from_catalog(&catalog, &ObjectId::new("proc-main").unwrap(), None).unwrap_err();

    assert!(
        err.contains("cycle detected") || err.contains("exceeds max depth"),
        "unexpected error: {err}"
    );
}

#[test]
fn missing_direct_based_on_reference_rejected() {
    let mut catalog = Catalog::empty();

    let mut domain = KbObject::new(
        ObjectId::new("dom-code").unwrap(),
        "Code",
        ObjectKind::Domain,
    );
    domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Numeric {
            precision: 5,
            scale: 0,
        })
        .unwrap(),
    });

    let mut proc = KbObject::new(
        ObjectId::new("proc-main").unwrap(),
        "Main",
        ObjectKind::Procedure,
    );
    // Deliberately omit reference to dom-code
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "&X = 1".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"X": {"based_on": "Code"}}"#.into(),
    });

    catalog
        .apply(0, &[Edit::Upsert(domain), Edit::Upsert(proc)])
        .unwrap();

    let err =
        kb::project_from_catalog(&catalog, &ObjectId::new("proc-main").unwrap(), None).unwrap_err();

    assert!(
        err.contains("without an explicit direct BasedOn reference"),
        "unexpected error: {err}"
    );
}

#[test]
fn sdt_item_based_on_domain_in_catalog() {
    let mut catalog = Catalog::empty();

    let mut domain = KbObject::new(
        ObjectId::new("dom-phone").unwrap(),
        "PhoneDomain",
        ObjectKind::Domain,
    );
    domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: serde_json::to_string(&DataType::Character { length: 15 }).unwrap(),
    });

    let mut sdt = KbObject::new(
        ObjectId::new("sdt-contact").unwrap(),
        "Contact",
        ObjectKind::StructuredDataType,
    );
    sdt.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: ObjectId::new("dom-phone").unwrap(),
    });
    sdt.source_parts.push(SourcePart {
        name: "Fields".into(),
        source: r#"{
            "Name": {"kind": "character", "length": 30},
            "Phone": {"based_on": "PhoneDomain"}
        }"#
        .into(),
    });

    let mut proc = KbObject::new(
        ObjectId::new("proc-main").unwrap(),
        "Main",
        ObjectKind::Procedure,
    );
    proc.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("sdt-contact").unwrap(),
    });
    proc.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "&C = new()\n&C.Name = \"Alice\"\n&C.Phone = \"555-1234\"\nMsg(&C.Phone, status)"
            .into(),
    });
    proc.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"C": {"kind": "record", "schema": "Contact"}}"#.into(),
    });

    catalog
        .apply(
            0,
            &[Edit::Upsert(domain), Edit::Upsert(sdt), Edit::Upsert(proc)],
        )
        .unwrap();

    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("proc-main").unwrap(), None).unwrap();

    assert_eq!(project.record_types.len(), 1);
    assert!(gx_engine::compile(&project).is_ok());
}

#[test]
fn exportfile_xml_with_domain_and_attribute() {
    let xml = r#"<ExportFile><Objects>
<Object guid="dom-id-1" name="UnitPrice" type="00972a17-9975-449e-aab1-d26165d51393">
  <Properties>
    <Property><Name>Name</Name><Value>UnitPrice</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
    <Property><Name>Length</Name><Value>8</Value></Property>
    <Property><Name>Decimals</Name><Value>2</Value></Property>
  </Properties>
</Object>
<Object guid="att-id-1" name="ProductPrice" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>ProductPrice</Value></Property>
    <Property><Name>DomainBasedOn</Name><Value>UnitPrice</Value></Property>
  </Properties>
</Object>
<Object guid="proc-id-1" name="ComputeTotal" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
  <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
    <Source><![CDATA[&Unit = 25.50
&Price = &Unit * 4
Msg(Str(&Price), status)]]></Source>
  </Part>
  <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
    <Variable Name="Unit">
      <Properties>
        <Property><Name>DomainBasedOn</Name><Value>UnitPrice</Value></Property>
      </Properties>
    </Variable>
    <Variable Name="Price">
      <Properties>
        <Property><Name>AttributeBasedOn</Name><Value>ProductPrice</Value></Property>
      </Properties>
    </Variable>
  </Part>
  <Properties>
    <Property><Name>CALL_PROTOCOL</Name><Value>CLINE</Value></Property>
  </Properties>
</Object>
</Objects></ExportFile>"#;

    let catalog = exchange::catalog_from_xml(xml).unwrap();
    assert_eq!(catalog.objects().count(), 3);
    assert_eq!(
        catalog
            .objects()
            .filter(|o| o.kind == ObjectKind::Domain)
            .count(),
        1
    );
    assert_eq!(
        catalog
            .objects()
            .filter(|o| o.kind == ObjectKind::Attribute)
            .count(),
        1
    );
    assert_eq!(
        catalog
            .objects()
            .filter(|o| o.kind == ObjectKind::Procedure)
            .count(),
        1
    );

    let project = exchange::project_from_xml(xml, "ComputeTotal", None).unwrap();
    assert!(gx_engine::compile(&project).is_ok());

    let output = run_xml(xml, "ComputeTotal");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(normalize_crlf(stdout.trim()), "102");
}

#[test]
fn exportfile_xml_with_sdt_item_based_on_domain() {
    let xml = r#"<ExportFile><Objects>
<Object guid="dom-id-msg" name="ShortMessage" type="00972a17-9975-449e-aab1-d26165d51393">
  <Properties>
    <Property><Name>Name</Name><Value>ShortMessage</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property>
    <Property><Name>Length</Name><Value>50</Value></Property>
  </Properties>
</Object>
<Object guid="sdt-id-note" name="Note" type="447527b5-9210-4523-898b-5dccb17be60a">
  <Part type="5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a">
    <Level Name="Note">
      <Item name="Body">
        <Properties>
          <Property><Name>DomainBasedOn</Name><Value>ShortMessage</Value></Property>
        </Properties>
      </Item>
    </Level>
  </Part>
</Object>
<Object guid="proc-id-msg" name="MainNote" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
  <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
    <Source><![CDATA[&N = new()
&N.Body = "domain-backed-sdt"
Msg(&N.Body, status)]]></Source>
  </Part>
  <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
    <Variable Name="N">
      <Properties>
        <Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Note</Value></Property>
      </Properties>
    </Variable>
  </Part>
  <Properties>
    <Property><Name>CALL_PROTOCOL</Name><Value>CLINE</Value></Property>
  </Properties>
</Object>
</Objects></ExportFile>"#;

    let catalog = exchange::catalog_from_xml(xml).unwrap();
    assert_eq!(catalog.objects().count(), 3);

    let project = exchange::project_from_xml(xml, "MainNote", None).unwrap();
    assert_eq!(project.record_types.len(), 1);
    assert!(gx_engine::compile(&project).is_ok());

    let output = run_xml(xml, "MainNote");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(normalize_crlf(stdout.trim()), "domain-backed-sdt");
}

#[test]
fn exportfile_xml_rejects_unresolved_based_on() {
    let xml = r#"<ExportFile><Objects>
<Object guid="proc-id-err" name="ProcErr" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
  <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
    <Source>Msg("err", status)</Source>
  </Part>
  <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
    <Variable Name="Ghost">
      <Properties>
        <Property><Name>DomainBasedOn</Name><Value>NonExistentDomain</Value></Property>
      </Properties>
    </Variable>
  </Part>
</Object>
</Objects></ExportFile>"#;

    let err = exchange::catalog_from_xml(xml).unwrap_err();
    assert!(
        err.contains("unresolved XML variable BasedOn target NonExistentDomain"),
        "unexpected error: {err}"
    );
}

#[test]
fn exportfile_xml_rejects_domain_based_on_attribute() {
    let xml = r#"<ExportFile><Objects>
<Object guid="att-id-2" name="BaseAttr" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>BaseAttr</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
    <Property><Name>Length</Name><Value>5</Value></Property>
  </Properties>
</Object>
<Object guid="dom-id-err" name="InvalidDomain" type="00972a17-9975-449e-aab1-d26165d51393">
  <Properties>
    <Property><Name>Name</Name><Value>InvalidDomain</Value></Property>
    <Property><Name>DomainBasedOn</Name><Value>BaseAttr</Value></Property>
  </Properties>
</Object>
</Objects></ExportFile>"#;

    let err = exchange::catalog_from_xml(xml).unwrap_err();
    assert!(
        err.contains("cannot be based on non-domain"),
        "unexpected error: {err}"
    );
}
