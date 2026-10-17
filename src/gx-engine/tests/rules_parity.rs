use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    exchange,
    rules::{self, ParmArg, Rule},
    transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure},
};

#[test]
fn parse_rules_standalone_parm() {
    let source = "Parm(in:&CustomerId, out:&CustomerName, inout:&Status);\r\n";
    let rules = rules::parse_rules(source).unwrap();
    assert_eq!(rules.len(), 1);
    match &rules[0] {
        Rule::Parm(args) => {
            assert_eq!(
                args,
                &[
                    ParmArg {
                        mode: "IN".into(),
                        name: "CUSTOMERID".into(),
                        is_variable: true
                    },
                    ParmArg {
                        mode: "OUT".into(),
                        name: "CUSTOMERNAME".into(),
                        is_variable: true
                    },
                    ParmArg {
                        mode: "INOUT".into(),
                        name: "STATUS".into(),
                        is_variable: true
                    },
                ]
            );
        }
        other => panic!("expected Rule::Parm, got {:?}", other),
    }
}

#[test]
fn parse_rules_with_single_line_and_multiline_comments() {
    let source = r#"
        // Interface definition for sales calculator
        Parm(in:&Quantity, in:&UnitPrice, out:&Total);

        /* Ordering specifications:
           Order by Total descending */
        Order(none);
    "#;
    let parsed = rules::parse_rules(source).unwrap();
    assert_eq!(parsed.len(), 2);
    assert!(matches!(&parsed[0], Rule::Parm(_)));
    assert_eq!(
        parsed[1],
        Rule::Order {
            attributes: vec!["none".into()]
        }
    );

    let params = rules::extract_procedure_parameters(source).unwrap();
    assert_eq!(params, ["IN:QUANTITY", "IN:UNITPRICE", "OUT:TOTAL"]);
}

#[test]
fn parse_rules_procedure_rules_suite() {
    let source = r#"
        Parm(in:&CustomerId, out:&Status);
        Error("Customer is inactive; contact support") if &Status = 0;
        Msg("Customer processed successfully") if &Status = 1;
        Prompt(PromptCustomer, &CustomerId);
        Order(CustomerId);
    "#;
    let parsed = rules::parse_rules(source).unwrap();
    assert_eq!(parsed.len(), 5);

    match &parsed[1] {
        Rule::Error { message, condition } => {
            assert_eq!(message, "\"Customer is inactive; contact support\"");
            assert_eq!(condition.as_deref(), Some("&Status = 0"));
        }
        other => panic!("expected Rule::Error, got {:?}", other),
    }

    match &parsed[2] {
        Rule::Msg { message, condition } => {
            assert_eq!(message, "\"Customer processed successfully\"");
            assert_eq!(condition.as_deref(), Some("&Status = 1"));
        }
        other => panic!("expected Rule::Msg, got {:?}", other),
    }

    match &parsed[3] {
        Rule::Prompt {
            object,
            parameters,
            condition,
        } => {
            assert_eq!(object, "PromptCustomer");
            assert_eq!(parameters, &["&CustomerId"]);
            assert!(condition.is_none());
        }
        other => panic!("expected Rule::Prompt, got {:?}", other),
    }

    match &parsed[4] {
        Rule::Order { attributes } => {
            assert_eq!(attributes, &["CustomerId"]);
        }
        other => panic!("expected Rule::Order, got {:?}", other),
    }
}

#[test]
fn parse_rules_transaction_rules_suite() {
    let source = r#"
        // Transaction signature receiving BC mode and primary key
        Parm(in:&Mode, in:CustomerId);

        Default(CustomerStatus, 'ACTIVE');
        NoAccept(CustomerId) if not &Mode = 'INS';
        Accept(CustomerNotes);
        Error('Customer Name is mandatory') if CustomerName.IsEmpty();
        Serial(InvoiceLineId, 1);
        Hidden(AuditToken);
    "#;
    let parsed = rules::parse_rules(source).unwrap();
    assert_eq!(parsed.len(), 7);

    // Parm with variable and attribute
    match &parsed[0] {
        Rule::Parm(args) => {
            assert_eq!(
                args,
                &[
                    ParmArg {
                        mode: "IN".into(),
                        name: "MODE".into(),
                        is_variable: true
                    },
                    ParmArg {
                        mode: "IN".into(),
                        name: "CUSTOMERID".into(),
                        is_variable: false
                    },
                ]
            );
        }
        other => panic!("expected Rule::Parm, got {:?}", other),
    }

    assert_eq!(
        parsed[1],
        Rule::Default {
            target: "CustomerStatus".into(),
            value: "'ACTIVE'".into(),
        }
    );

    assert_eq!(
        parsed[2],
        Rule::NoAccept {
            target: "CustomerId".into(),
            condition: Some("not &Mode = 'INS'".into()),
        }
    );

    assert_eq!(
        parsed[3],
        Rule::Accept {
            target: "CustomerNotes".into(),
        }
    );

    match &parsed[4] {
        Rule::Error { message, condition } => {
            assert_eq!(message, "'Customer Name is mandatory'");
            assert_eq!(condition.as_deref(), Some("CustomerName.IsEmpty()"));
        }
        other => panic!("expected Rule::Error, got {:?}", other),
    }

    assert_eq!(
        parsed[5],
        Rule::Serial {
            attribute: "InvoiceLineId".into(),
            step: "1".into(),
            range: None,
        }
    );

    assert_eq!(
        parsed[6],
        Rule::Hidden {
            target: "AuditToken".into(),
        }
    );
}

#[test]
fn parse_rules_semicolons_and_escaped_quotes_inside_strings() {
    let source = r#"
        Error("Invalid format; please retry; code: 404") if &Code = 404;
        Error('Customer''s record is locked; please wait') if &Locked = 1;
    "#;
    let parsed = rules::parse_rules(source).unwrap();
    assert_eq!(parsed.len(), 2);

    match &parsed[0] {
        Rule::Error { message, condition } => {
            assert_eq!(message, "\"Invalid format; please retry; code: 404\"");
            assert_eq!(condition.as_deref(), Some("&Code = 404"));
        }
        other => panic!("expected Rule::Error, got {:?}", other),
    }

    match &parsed[1] {
        Rule::Error { message, condition } => {
            assert_eq!(message, "'Customer''s record is locked; please wait'");
            assert_eq!(condition.as_deref(), Some("&Locked = 1"));
        }
        other => panic!("expected Rule::Error, got {:?}", other),
    }
}

#[test]
fn parse_rules_fails_closed_on_invalid_rules() {
    // 1. Unique rule is not allowed in GeneXus transactions
    let err = rules::parse_rules("Unique(CustomerId);").unwrap_err();
    assert!(err.contains("src0295"), "{err}");
    assert!(err.contains("create_index"), "{err}");

    // 2. Unknown keyword
    let err = rules::parse_rules("SomeUnknownRule(&A);").unwrap_err();
    assert!(err.contains("src0295"), "{err}");
    assert!(err.contains("SomeUnknownRule"), "{err}");

    // 3. Duplicate Parm
    let err = rules::parse_rules("Parm(&A); Parm(&B);").unwrap_err();
    assert!(err.contains("duplicate Parm rule"), "{err}");

    // 4. Parm with if condition
    let err = rules::parse_rules("Parm(&A) if &A > 0;").unwrap_err();
    assert!(
        err.contains("Parm rule does not support 'if' condition"),
        "{err}"
    );

    // 5. Default with if condition
    let err = rules::parse_rules("Default(Status, 1) if &A > 0;").unwrap_err();
    assert!(
        err.contains("Default rule does not support 'if' condition"),
        "{err}"
    );

    // 6. Unclosed string
    let err = rules::parse_rules("Error(\"unclosed string);").unwrap_err();
    assert!(err.contains("unclosed string literal"), "{err}");

    // 7. Unclosed block comment
    let err = rules::parse_rules("/* unclosed comment \nParm(&A);").unwrap_err();
    assert!(err.contains("unclosed block comment"), "{err}");

    // 8. Missing semicolon
    let err = rules::parse_rules("Parm(&A)").unwrap_err();
    assert!(err.contains("terminate with a semicolon"), "{err}");

    // 9. Empty statement
    let err = rules::parse_rules("Parm(&A);;").unwrap_err();
    assert!(err.contains("empty rule statement"), "{err}");

    // 10. Missing condition after if
    let err = rules::parse_rules("Error('Fail') if;").unwrap_err();
    assert!(err.contains("missing condition expression"), "{err}");
}

#[test]
fn extract_procedure_parameters_enforces_procedure_scope() {
    // Attributes in Procedure Parm are rejected
    let err = rules::extract_procedure_parameters("Parm(in:CustomerId);").unwrap_err();
    assert!(err.contains("must be &variables"), "{err}");

    // Transaction-only rules in Procedure are rejected
    let err = rules::extract_procedure_parameters("Parm(&A); Default(Status, 1);").unwrap_err();
    assert!(err.contains("Transactions"), "{err}");

    let err = rules::extract_procedure_parameters("Parm(&A); Serial(LineId, 1);").unwrap_err();
    assert!(err.contains("Transactions"), "{err}");
}

#[test]
fn procedure_with_comments_and_rules_in_catalog() {
    let mut catalog = Catalog::empty();
    let main = KbObject {
        id: ObjectId::new("main").unwrap(),
        name: "Main".into(),
        kind: ObjectKind::Procedure,
        references: vec![Reference {
            kind: ReferenceKind::Calls,
            target: ObjectId::new("calc").unwrap(),
        }],
        source_parts: vec![
            SourcePart {
                name: "Source".into(),
                source: "&Res = Calc(21)\nPrint(&Res)".into(),
            },
            SourcePart {
                name: "Variables".into(),
                source: r#"{"Res":{"kind":"numeric","precision":10,"scale":0}}"#.into(),
            },
        ],
    };

    let calc = KbObject {
        id: ObjectId::new("calc").unwrap(),
        name: "Calc".into(),
        kind: ObjectKind::Procedure,
        references: vec![],
        source_parts: vec![
            SourcePart {
                name: "Source".into(),
                source: "&Result = &Input * 2".into(),
            },
            SourcePart {
                name: "Rules".into(),
                source: r#"
                    // Calculation multiplier procedure
                    Parm(in:&Input, out:&Result);

                    /* Ordering rules */
                    Order(none);
                "#
                .into(),
            },
            SourcePart {
                name: "Variables".into(),
                source: r#"{"Input":{"kind":"numeric","precision":10,"scale":0},"Result":{"kind":"numeric","precision":10,"scale":0}}"#.into(),
            },
        ],
    };

    catalog
        .apply(0, &[Edit::Upsert(main), Edit::Upsert(calc)])
        .unwrap();

    let project =
        gx_engine::kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None)
            .unwrap();

    assert_eq!(project.procedures.len(), 2);
    assert_eq!(project.procedures[1].params, ["IN:INPUT", "OUT:RESULT"]);
    let emitted = gx_engine::compile(&project).unwrap();
    assert!(emitted.contains("fn p1("));
}

#[test]
fn transaction_with_rules_in_catalog() {
    let mut catalog = Catalog::empty();
    let structure = TransactionStructure::new(TransactionLevel {
        name: "Customer".into(),
        description: Some("Customer Master".into()),
        attributes: vec![
            TransactionAttributeRef {
                name: "CustomerId".into(),
                is_key: true,
                data_type: Some(gx_engine::model::DataType::Numeric {
                    precision: 10,
                    scale: 0,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
            TransactionAttributeRef {
                name: "CustomerName".into(),
                is_key: false,
                data_type: Some(gx_engine::model::DataType::Character { length: 50 }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
        ],
        levels: vec![],
    });

    let trn = KbObject {
        id: ObjectId::new("customer_trn").unwrap(),
        name: "Customer".into(),
        kind: ObjectKind::Transaction,
        references: vec![],
        source_parts: vec![
            SourcePart {
                name: "STRUCTURE".into(),
                source: serde_json::to_string(&structure).unwrap(),
            },
            SourcePart {
                name: "RULES".into(),
                source: r#"
                    // Business constraints
                    Default(CustomerName, 'Unknown');
                    Error('Customer Name is required') if CustomerName.IsEmpty();
                "#
                .into(),
            },
        ],
    };

    let proc = KbObject {
        id: ObjectId::new("proc_main").unwrap(),
        name: "ProcMain".into(),
        kind: ObjectKind::Procedure,
        references: vec![Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new("customer_trn").unwrap(),
        }],
        source_parts: vec![
            SourcePart {
                name: "Source".into(),
                source: "&Cust = new()\n&Cust.CustomerId = 100\nPrint(&Cust.CustomerId)".into(),
            },
            SourcePart {
                name: "Variables".into(),
                source: r#"{"Cust":{"kind":"record","schema":"Customer"}}"#.into(),
            },
        ],
    };

    catalog
        .apply(0, &[Edit::Upsert(trn), Edit::Upsert(proc)])
        .unwrap();

    let project =
        gx_engine::kb::project_from_catalog(&catalog, &ObjectId::new("proc_main").unwrap(), None)
            .unwrap();

    assert_eq!(project.record_types.len(), 1);
    assert_eq!(project.record_types[0].name, "CUSTOMER");
    let emitted = gx_engine::compile(&project).unwrap();
    assert!(emitted.contains("\"CUSTOMER\""));
}

#[test]
fn exportfile_xml_with_procedure_and_transaction_rules() {
    let xml = r#"<?xml version="1.0" encoding="utf-8"?>
<ExportFile>
  <Objects>
    <Object guid="11111111-1111-1111-1111-111111111111" name="OrderTrn" type="1db606f2-af09-4cf9-a3b5-b481519d28f6">
      <Part type="1608677c-a7a2-4a00-8809-6d2466085a5a">
        <Level Name="Order">
          <Attribute Name="OrderId">
            <Properties>
              <Property><Name>IsKey</Name><Value>True</Value></Property>
              <Property><Name>TYPE</Name><Value>Numeric</Value></Property>
              <Property><Name>Length</Name><Value>8</Value></Property>
            </Properties>
          </Attribute>
          <Attribute Name="OrderAmount">
            <Properties>
              <Property><Name>TYPE</Name><Value>Numeric</Value></Property>
              <Property><Name>Length</Name><Value>10</Value></Property>
              <Property><Name>Decimals</Name><Value>2</Value></Property>
            </Properties>
          </Attribute>
        </Level>
      </Part>
      <Part type="9b0a32a3-de6d-4be1-a4dd-1b85d3741534">
        <Source><![CDATA[
            // Transaction rules
            Default(OrderAmount, 0);
            Error('Order Amount must be positive') if OrderAmount < 0;
        ]]></Source>
      </Part>
    </Object>

    <Object guid="22222222-2222-2222-2222-222222222222" name="OrderProc" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
      <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
        <Source><![CDATA[
            &Amount = 500
            &Out = &Amount * 2
        ]]></Source>
      </Part>
      <Part type="9b0a32a3-de6d-4be1-a4dd-1b85d3741534">
        <Source><![CDATA[
            // Multi-rule procedure
            Parm(out:&Out);
            Order(none);
        ]]></Source>
      </Part>
      <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
        <Variable Name="Amount">
          <Properties>
            <Property><Name>TYPE</Name><Value>Numeric</Value></Property>
            <Property><Name>Length</Name><Value>8</Value></Property>
          </Properties>
        </Variable>
        <Variable Name="Out">
          <Properties>
            <Property><Name>TYPE</Name><Value>Numeric</Value></Property>
            <Property><Name>Length</Name><Value>8</Value></Property>
          </Properties>
        </Variable>
      </Part>
    </Object>

    <Object guid="33333333-3333-3333-3333-333333333333" name="MainProc" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
      <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
        <Source><![CDATA[
            OrderProc(&Res)
            Msg("Done", status)
        ]]></Source>
      </Part>
      <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
        <Variable Name="Res">
          <Properties>
            <Property><Name>TYPE</Name><Value>Numeric</Value></Property>
            <Property><Name>Length</Name><Value>8</Value></Property>
          </Properties>
        </Variable>
      </Part>
    </Object>
  </Objects>
</ExportFile>"#;

    let catalog = exchange::catalog_from_xml(xml).unwrap();
    assert_eq!(catalog.objects().count(), 3);

    let project = gx_engine::kb::project_from_catalog(
        &catalog,
        &ObjectId::new("33333333-3333-3333-3333-333333333333").unwrap(),
        None,
    )
    .unwrap();

    assert_eq!(project.procedures.len(), 2);
    assert_eq!(project.procedures[1].params, ["OUT:OUT"]);
    let emitted = gx_engine::compile(&project).unwrap();
    assert!(emitted.contains("fn p0("));
    assert!(emitted.contains("fn p1("));
}
