//! Unit and integration tests for WebPanel, Transaction, and Solution C# code generation.
//! Track D of Plan 130.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::{
        CsprojConfig, SolutionConfig, SolutionEmitter, SolutionProject, TransactionClass,
        WebPanelClass, deterministic_guid,
    },
    model::DataType,
    transaction::{
        TransactionAttributeRef, TransactionLevel, TransactionRule, TransactionStructure,
    },
    webpanel::{LayoutControl, ScreenVariable, WebPanel, WebPanelEvent},
};

#[test]
fn test_webpanel_basic_emission() {
    let mut panel = WebPanelClass::new("CustomerManager")
        .with_theme("Carmine")
        .with_base_class("GXWebPanel")
        .with_namespace("GeneXus.Programs")
        .with_is_main(true);

    // Add controls
    panel = panel
        .add_control(LayoutControl::textblock("lblHeader", "Customer Management"))
        .add_control(LayoutControl::button(
            "btnSave",
            "Save Record",
            "SaveCustomer",
        ))
        .add_control(LayoutControl::grid("GridCustomers", None, vec![]))
        .add_control(LayoutControl::table("tblActions", vec![]))
        .add_control(LayoutControl::edit("edtEmail", "CustomerEmail"));

    // Add variables
    panel = panel
        .add_variable(
            "CustomerId",
            DataType::Numeric {
                precision: 10,
                scale: 0,
            },
        )
        .add_variable("CustomerName", DataType::Varchar { length: 100 })
        .add_variable("IsActive", DataType::Boolean {});

    // Add events
    panel = panel
        .add_event(WebPanelEvent::new(
            "Start",
            "&CustomerId = 1\n&CustomerName = \"Test\"",
        ))
        .add_event(WebPanelEvent::new("Refresh", "&IsActive = true"))
        .add_event(WebPanelEvent::new("Load", ""))
        .add_event(WebPanelEvent::new(
            "SaveCustomer",
            "&CustomerId = &CustomerId + 1",
        ));

    let code = panel.emit();

    // Verify class declaration & inheritance
    assert!(code.contains("public class CustomerManager : GXWebPanel"));
    assert!(code.contains("namespace GeneXus.Programs {"));

    // Verify constructors
    assert!(code.contains("public CustomerManager( )"));
    assert!(code.contains("public CustomerManager( IGxContext context )"));
    assert!(code.contains("context.SetDefaultTheme(\"Carmine\", true);"));

    // Verify lifecycle methods
    assert!(code.contains("public void execute()"));
    assert!(code.contains("protected override void ExecutePrivate( )"));
    assert!(code.contains("protected void webExecute( )"));
    assert!(code.contains("protected override void createObjects( )"));
    assert!(code.contains("public override void initialize( )"));
    assert!(code.contains("public override void cleanup( )"));

    // Verify control fields
    assert!(code.contains("private GXTextBlock lblHeader ;"));
    assert!(code.contains("private GXButton bttSave ;"));
    assert!(code.contains("private GXGrid GridCustomers ;"));
    assert!(code.contains("private GXTable tblActions ;"));
    assert!(code.contains("private GXEdit edtEmail ;"));

    // Verify createObjects control instantiations
    assert!(code.contains("lblHeader = new GXTextBlock();"));
    assert!(code.contains("bttSave = new GXButton();"));
    assert!(code.contains("GridCustomers = new GXGrid();"));
    assert!(code.contains("tblActions = new GXTable();"));
    assert!(code.contains("edtEmail = new GXEdit();"));

    // Verify variable fields
    assert!(code.contains("private long AV_CustomerId ;"));
    assert!(code.contains("private string AV_CustomerName ;"));
    assert!(code.contains("private bool AV_IsActive ;"));

    // Verify event dispatch and event handlers
    assert!(code.contains("public void ExecuteUserEvent( string eventName )"));
    assert!(code.contains("E12_Start();"));
    assert!(code.contains("E11_Refresh();"));
    assert!(code.contains("E13_Load();"));
    assert!(code.contains("E14_SaveCustomer();"));

    assert!(code.contains("protected void E12_Start( )"));
    assert!(code.contains("protected void E11_Refresh( )"));
    assert!(code.contains("protected void E13_Load( )"));
    assert!(code.contains("protected void E14_SaveCustomer( )"));
}

#[test]
fn test_webpanel_from_ast_dsl() {
    let dsl = r#"
        webpanel OrderViewer {
            theme = "KBTeste";
            master_page = "AppMaster";
            variables {
                &OrderId: numeric(10, 0);
                &CustomerName: varchar(60);
            }
            layout {
                textblock Title { caption = "Order Details"; }
                button ConfirmBtn { caption = "Confirm Order"; event = "Confirm"; }
                grid OrdersGrid {
                    edit ColId { binding = "OrderId"; }
                }
            }
            events {
                Event 'Start'
                    &OrderId = 100
                EndEvent
                Event 'Confirm'
                    &OrderId = &OrderId + 1
                EndEvent
            }
        }
    "#;

    let wp = WebPanel::parse("OrderViewer", dsl).expect("parse webpanel DSL");
    let panel_class = WebPanelClass::from_webpanel(&wp);
    let code = panel_class.emit();

    assert!(code.contains("public class OrderViewer : GXWebPanel"));
    assert!(code.contains("private long AV_OrderId ;"));
    assert!(code.contains("private string AV_CustomerName ;"));
    assert!(code.contains("private GXTextBlock lblTitle ;"));
    assert!(code.contains("private GXButton bttConfirmBtn ;"));
    assert!(code.contains("private GXGrid GridOrdersGrid ;"));
    assert!(code.contains("E12_Start();"));
    assert!(code.contains("E14_Confirm();"));
}

#[test]
fn test_webpanel_from_kb_object() {
    let kb_obj = KbObject {
        id: ObjectId::new("WebPanel_101").unwrap(),
        name: "InvoicePanel".to_string(),
        kind: ObjectKind::WebPanel,
        source_parts: vec![
            SourcePart {
                name: "WEBPANEL".to_string(),
                source: r#"{
                    "name": "InvoicePanel",
                    "variables": [
                        { "name": "Total", "data_type": { "kind": "numeric", "precision": 12, "scale": 2 } }
                    ],
                    "controls": [
                        { "name": "btnPay", "kind": "button", "caption": "Pay Now", "event_name": "Pay" }
                    ],
                    "events": [
                        { "name": "Start", "body": "" }
                    ],
                    "rules": []
                }"#.to_string(),
            },
        ],
        references: vec![],
    };

    let panel_class = WebPanelClass::from_kb_object(&kb_obj).expect("from kb object");
    let code = panel_class.emit();

    assert!(code.contains("public class InvoicePanel : GXWebPanel"));
    assert!(code.contains("private decimal AV_Total ;"));
    assert!(code.contains("private GXButton bttPay ;"));
    assert!(code.contains("protected void E12_Start( )"));
}

#[test]
fn test_webpanel_with_parameters_and_rules() {
    let mut panel = WebPanelClass::new("ParamWebPanel");
    panel.rules = vec!["parm(in:&ClientId, out:&Status);".to_string()];
    panel.variables.insert(
        "ClientId".to_string(),
        DataType::Numeric {
            precision: 8,
            scale: 0,
        },
    );
    panel
        .variables
        .insert("Status".to_string(), DataType::Varchar { length: 50 });

    let wp = WebPanel {
        name: "ParamWebPanel".to_string(),
        rules: vec!["parm(in:&ClientId, out:&Status);".to_string()],
        variables: vec![
            ScreenVariable::new(
                "ClientId",
                DataType::Numeric {
                    precision: 8,
                    scale: 0,
                },
            ),
            ScreenVariable::new("Status", DataType::Varchar { length: 50 }),
        ],
        ..Default::default()
    };

    let panel_class = WebPanelClass::from_webpanel(&wp);
    let code = panel_class.emit();

    // Verify parameter signature in execute
    assert!(code.contains("public void execute("));
    assert!(code.contains("int aP0_ClientId"));
    assert!(code.contains("out string aP1_Status"));
    assert!(code.contains("this.AV_ClientId = aP0_ClientId;"));
    assert!(code.contains("aP1_Status = this.AV_Status;"));
}

#[test]
fn test_transaction_bc_basic_emission() {
    let root_level = TransactionLevel {
        name: "Customer".to_string(),
        description: Some("Customer Master".to_string()),
        attributes: vec![
            TransactionAttributeRef {
                name: "CustomerId".to_string(),
                is_key: true,
                data_type: Some(DataType::Numeric {
                    precision: 10,
                    scale: 0,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
            TransactionAttributeRef {
                name: "CustomerName".to_string(),
                is_key: false,
                data_type: Some(DataType::Varchar { length: 100 }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
            TransactionAttributeRef {
                name: "CustomerCredit".to_string(),
                is_key: false,
                data_type: Some(DataType::Numeric {
                    precision: 12,
                    scale: 2,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
        ],
        levels: vec![],
    };

    let mut structure = TransactionStructure::new(root_level).with_business_component(true);
    structure = structure
        .with_rule(TransactionRule::Default {
            attribute: "CustomerCredit".to_string(),
            value: "0".to_string(),
        })
        .with_rule(TransactionRule::Error {
            message: "Customer Name cannot be empty".to_string(),
            condition: Some("CustomerName.IsEmpty()".to_string()),
        })
        .with_rule(TransactionRule::Msg {
            message: "Customer verified".to_string(),
            condition: None,
        });

    let tc = TransactionClass::from_structure("Customer", structure)
        .with_theme("Carmine")
        .with_base_class("GXDataComponent")
        .with_namespace("GeneXus.Programs");

    let code = tc.emit();

    // Verify class declaration & inheritance
    assert!(code.contains("public class Customer : GXDataComponent"));
    assert!(code.contains("namespace GeneXus.Programs {"));

    // Verify constructors
    assert!(code.contains("public Customer( )"));
    assert!(code.contains("public Customer( IGxContext context )"));
    assert!(code.contains("context.SetDefaultTheme(\"Carmine\", true);"));

    // Verify Business Component API methods
    assert!(code.contains("public void Load( long aP_CustomerId )"));
    assert!(code.contains("public void Load( )"));
    assert!(code.contains("public void Save( )"));
    assert!(code.contains("public void Delete( )"));
    assert!(code.contains("public bool Success( )"));
    assert!(code.contains("public bool Fail( )"));
    assert!(code.contains("public msglist GetMessages( )"));

    // Verify ValidateRules
    assert!(code.contains("public void ValidateRules( )"));
    assert!(code.contains("/* Default rule: CustomerCredit = 0 */"));
    assert!(code.contains("LclMsgLst.addItem(\"Customer Name cannot be empty\");"));
    assert!(code.contains("LclMsgLst.addItem(\"Customer verified\");"));

    // Verify database cursor stubs
    assert!(code.contains("protected void getByPrimaryKey( )"));
    assert!(code.contains("protected void CheckOptimisticConcurrency( )"));
    assert!(code.contains("protected void ExecuteSave( )"));
    assert!(code.contains("protected void ExecuteDelete( )"));

    // Verify attribute fields and public properties
    assert!(code.contains("private long A_CustomerId ;"));
    assert!(code.contains("private string A_CustomerName ;"));
    assert!(code.contains("private decimal A_CustomerCredit ;"));

    assert!(code.contains("public long CustomerId"));
    assert!(code.contains("public string CustomerName"));
    assert!(code.contains("public decimal CustomerCredit"));
}

#[test]
fn test_transaction_multilevel_emission() {
    let child_level = TransactionLevel {
        name: "Lines".to_string(),
        description: Some("Invoice Lines".to_string()),
        attributes: vec![
            TransactionAttributeRef {
                name: "InvoiceId".to_string(),
                is_key: true,
                data_type: Some(DataType::Numeric {
                    precision: 10,
                    scale: 0,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: true,
            },
            TransactionAttributeRef {
                name: "LineId".to_string(),
                is_key: true,
                data_type: Some(DataType::Numeric {
                    precision: 5,
                    scale: 0,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
            TransactionAttributeRef {
                name: "LineTotal".to_string(),
                is_key: false,
                data_type: Some(DataType::Numeric {
                    precision: 12,
                    scale: 2,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
        ],
        levels: vec![],
    };

    let root_level = TransactionLevel {
        name: "Invoice".to_string(),
        description: Some("Header".to_string()),
        attributes: vec![
            TransactionAttributeRef {
                name: "InvoiceId".to_string(),
                is_key: true,
                data_type: Some(DataType::Numeric {
                    precision: 10,
                    scale: 0,
                }),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
            TransactionAttributeRef {
                name: "InvoiceDate".to_string(),
                is_key: false,
                data_type: Some(DataType::Date {}),
                formula: None,
                is_nullable: false,
                is_foreign_key: false,
            },
        ],
        levels: vec![child_level],
    };

    let tc = TransactionClass::from_level("Invoice", root_level);
    let code = tc.emit();

    // Verify root class
    assert!(code.contains("public class Invoice : GXDataComponent"));
    assert!(code.contains("public long InvoiceId"));
    assert!(code.contains("public DateTime InvoiceDate"));

    // Verify child nested class
    assert!(code.contains("public class Invoice_Lines : GXDataComponent"));
    assert!(code.contains("private int A_LineId ;"));
    assert!(code.contains("private decimal A_LineTotal ;"));
    assert!(code.contains("public int LineId"));
    assert!(code.contains("public decimal LineTotal"));
}

#[test]
fn test_transaction_from_kb_object() {
    let structure_dsl = r#"
        Customer {
            *CustomerId: numeric(10, 0);
            CustomerName: varchar(50);
        }
    "#;

    let kb_obj = KbObject {
        id: ObjectId::new("Transaction_202").unwrap(),
        name: "Customer".to_string(),
        kind: ObjectKind::Transaction,
        source_parts: vec![
            SourcePart {
                name: "STRUCTURE".to_string(),
                source: structure_dsl.to_string(),
            },
            SourcePart {
                name: "RULES".to_string(),
                source: r#"[
                    { "kind": "default", "attribute": "CustomerName", "value": "Unknown" }
                ]"#
                .to_string(),
            },
        ],
        references: vec![],
    };

    let tc = TransactionClass::from_kb_object(&kb_obj).expect("from kb object");
    let code = tc.emit();

    assert!(code.contains("public class Customer : GXDataComponent"));
    assert!(code.contains("private long A_CustomerId ;"));
    assert!(code.contains("private string A_CustomerName ;"));
    assert!(code.contains("/* Default rule: CustomerName = Unknown */"));
}

#[test]
fn test_solution_emission_sln() {
    let mut sol = SolutionConfig::new("KBTesteSolution")
        .with_target_framework("net8.0")
        .with_genexus_version("1.30.0");

    sol = sol
        .add_project(SolutionProject::new(
            "GeneXus.Programs.Common",
            r".\build\GeneXus.Programs.Common\GeneXus.Programs.Common.csproj",
        ))
        .add_project(SolutionProject::new("Customer", r".\Web\Customer.csproj"))
        .add_project(SolutionProject::new(
            "CustomerManager",
            r".\Web\CustomerManager.csproj",
        ))
        .add_project(SolutionProject::new("GxDeps", r".\Web\GxDeps.csproj"));

    let sln = SolutionEmitter::emit_sln(&sol);

    assert!(sln.contains("Microsoft Visual Studio Solution File, Format Version 12.00"));
    assert!(sln.contains("# Visual Studio Version 17"));
    assert!(sln.contains(
        "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"GeneXus.Programs.Common\""
    ));
    assert!(sln.contains("Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Customer\""));
    assert!(
        sln.contains("Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"CustomerManager\"")
    );
    assert!(sln.contains("Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"GxDeps\""));

    assert!(sln.contains("GlobalSection(SolutionConfigurationPlatforms) = preSolution"));
    assert!(sln.contains("GlobalSection(ProjectConfigurationPlatforms) = postSolution"));
    assert!(sln.contains(".Debug|Any CPU.ActiveCfg = Debug|Any CPU"));
    assert!(sln.contains(".Release|Any CPU.Build.0 = Release|Any CPU"));
}

#[test]
fn test_solution_emission_directory_build_props() {
    let sol = SolutionConfig::new("KBTesteSolution")
        .with_target_framework("net8.0")
        .with_genexus_version("1.30.0")
        .with_office(true)
        .with_pdf(true)
        .with_mail(true)
        .with_postgresql(true);

    let props_xml = SolutionEmitter::emit_directory_build_props(&sol);

    assert!(props_xml.contains("<TargetFramework>net8.0</TargetFramework>"));
    assert!(props_xml.contains("<GenStdVersion>1.30.0</GenStdVersion>"));
    assert!(props_xml.contains("<OfficeSupport>true</OfficeSupport>"));
    assert!(props_xml.contains("<PDFSupport>true</PDFSupport>"));
    assert!(props_xml.contains("<MailSupport>true</MailSupport>"));
    assert!(props_xml.contains("<PostgresqlSupport>true</PostgresqlSupport>"));

    // Verify well-formed XML
    let doc = roxmltree::Document::parse(&props_xml);
    assert!(
        doc.is_ok(),
        "Directory.build.props must be valid XML: {:?}",
        doc.err()
    );
}

#[test]
fn test_solution_emission_gxdeps_csproj() {
    let mut sol = SolutionConfig::new("KBTesteSolution")
        .with_target_framework("net8.0")
        .with_genexus_version("1.30.0")
        .with_office(true)
        .with_postgresql(true);

    sol = sol
        .add_project_path("Common", r"..\build\Common\Common.csproj")
        .add_project_path("Customer", r".\Customer.csproj");

    let gxdeps_xml = SolutionEmitter::emit_gxdeps_csproj(&sol);

    assert!(gxdeps_xml.contains(r#"<Project Sdk="Microsoft.NET.Sdk">"#));
    assert!(gxdeps_xml.contains("<TargetFramework>net8.0</TargetFramework>"));
    assert!(
        gxdeps_xml
            .contains(r#"<PackageReference Include="GeneXus.Classes.Core" Version="1.30.0" />"#)
    );
    assert!(
        gxdeps_xml.contains(
            r#"<PackageReference Include="GeneXus.Classes.Web.Core" Version="1.30.0" />"#
        )
    );
    assert!(
        gxdeps_xml
            .contains(r#"<PackageReference Include="GeneXus.Excel.Core" Version="1.30.0" />"#)
    );
    assert!(gxdeps_xml.contains(r#"<PackageReference Include="Npgsql" Version="8.0.3" />"#));
    assert!(gxdeps_xml.contains(r#"<ProjectReference Include="..\build\Common\Common.csproj" />"#));
    assert!(gxdeps_xml.contains(r#"<ProjectReference Include=".\Customer.csproj" />"#));
    assert!(gxdeps_xml.contains(r#"<Compile Remove="**/*.cs" />"#));

    // Verify well-formed XML
    let doc = roxmltree::Document::parse(&gxdeps_xml);
    assert!(
        doc.is_ok(),
        "GxDeps.csproj must be valid XML: {:?}",
        doc.err()
    );
}

#[test]
fn test_solution_from_csproj_configs() {
    let cfg1 = CsprojConfig::net8("Proc1", true);
    let cfg2 = CsprojConfig::net8("Proc2", false);

    let sol = SolutionConfig::from_csproj_configs("MyProcs", &[cfg1, cfg2]);
    assert_eq!(sol.name, "MyProcs");
    assert_eq!(sol.projects.len(), 2);
    assert_eq!(sol.projects[0].name, "Proc1");
    assert_eq!(sol.projects[0].path, "Proc1.csproj");
    assert_eq!(sol.projects[1].name, "Proc2");
    assert_eq!(sol.projects[1].path, "Proc2.csproj");
}

#[test]
fn test_deterministic_guid() {
    let guid1 = deterministic_guid("Customer");
    let guid2 = deterministic_guid("Customer");
    let guid3 = deterministic_guid("Invoice");

    assert_eq!(
        guid1, guid2,
        "Deterministic GUID must produce identical results for same input"
    );
    assert_ne!(
        guid1, guid3,
        "Deterministic GUID should differ for different inputs"
    );
    assert!(guid1.starts_with('{') && guid1.ends_with('}'));
    assert_eq!(
        guid1.len(),
        38,
        "GUID length must be 38 chars formatted with braces"
    );
}
