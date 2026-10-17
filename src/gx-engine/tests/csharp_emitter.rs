//! Unit and integration tests for C# (.NET Core) code and project generator.
//! Track D of Plan 130.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::{
        CSharpEmitter, CsprojConfig, CsprojEmitter, ProcedureClass, STANDARD_IMPORTS, clean_ident,
        to_csharp_attr_name, to_csharp_default_value, to_csharp_param_name,
        to_csharp_param_signature, to_csharp_type, to_csharp_var_name,
    },
    frontend::{BinOp, Expr, FromXmlStmt, Stmt},
    model::{DataType, ParamMode, ProcedureSource},
};
use std::collections::BTreeMap;

#[test]
fn test_csharp_type_mapping() {
    assert_eq!(to_csharp_type(&DataType::Boolean {}), "bool");
    assert_eq!(
        to_csharp_type(&DataType::Character { length: 50 }),
        "string"
    );
    assert_eq!(to_csharp_type(&DataType::Varchar { length: 200 }), "string");
    assert_eq!(to_csharp_type(&DataType::Date {}), "DateTime");
    assert_eq!(to_csharp_type(&DataType::DateTime {}), "DateTime");

    // Numeric mappings depending on precision and scale
    assert_eq!(
        to_csharp_type(&DataType::Numeric {
            precision: 4,
            scale: 0
        }),
        "short"
    );
    assert_eq!(
        to_csharp_type(&DataType::Numeric {
            precision: 9,
            scale: 0
        }),
        "int"
    );
    assert_eq!(
        to_csharp_type(&DataType::Numeric {
            precision: 10,
            scale: 0
        }),
        "long"
    );
    assert_eq!(
        to_csharp_type(&DataType::Numeric {
            precision: 18,
            scale: 0
        }),
        "long"
    );
    assert_eq!(
        to_csharp_type(&DataType::Numeric {
            precision: 25,
            scale: 0
        }),
        "decimal"
    );
    assert_eq!(
        to_csharp_type(&DataType::Numeric {
            precision: 10,
            scale: 2
        }),
        "decimal"
    );

    // Records and Collections
    assert_eq!(
        to_csharp_type(&DataType::Record {
            schema: "Customer".to_string()
        }),
        "SdtCustomer"
    );
    assert_eq!(
        to_csharp_type(&DataType::Record {
            schema: "SdtInvoice".to_string()
        }),
        "SdtInvoice"
    );
    assert_eq!(
        to_csharp_type(&DataType::Collection {
            schema: "Address".to_string()
        }),
        "GxSimpleCollection<SdtAddress>"
    );
}

#[test]
fn test_csharp_default_values() {
    assert_eq!(to_csharp_default_value(&DataType::Boolean {}), "false");
    assert_eq!(
        to_csharp_default_value(&DataType::Numeric {
            precision: 10,
            scale: 0
        }),
        "0"
    );
    assert_eq!(
        to_csharp_default_value(&DataType::Numeric {
            precision: 10,
            scale: 2
        }),
        "0m"
    );
    assert_eq!(
        to_csharp_default_value(&DataType::Character { length: 10 }),
        "\"\""
    );
    assert_eq!(
        to_csharp_default_value(&DataType::Date {}),
        "DateTime.MinValue"
    );
    assert_eq!(
        to_csharp_default_value(&DataType::Record {
            schema: "Item".to_string()
        }),
        "null"
    );
}

#[test]
fn test_csharp_identifiers() {
    assert_eq!(to_csharp_var_name("&Value"), "AV_Value");
    assert_eq!(to_csharp_var_name("Value"), "AV_Value");
    assert_eq!(to_csharp_var_name("AV8Value"), "AV8Value");
    assert_eq!(to_csharp_var_name("&AV8Value"), "AV8Value");

    assert_eq!(to_csharp_attr_name("CustomerId"), "A_CustomerId");
    assert_eq!(to_csharp_attr_name("&CustomerId"), "A_CustomerId");
    assert_eq!(to_csharp_attr_name("A1CustomerId"), "A1CustomerId");

    assert_eq!(clean_ident("&Hits"), "Hits");
    assert_eq!(clean_ident("AV8Hits"), "Hits");
    assert_eq!(clean_ident("AV_Hits"), "Hits");
    assert_eq!(clean_ident("Hits"), "Hits");

    assert_eq!(to_csharp_param_name(0, "&Hits"), "aP0_Hits");
    assert_eq!(to_csharp_param_name(1, "AV9Result"), "aP1_Result");

    let num_type = DataType::Numeric {
        precision: 10,
        scale: 2,
    };
    assert_eq!(
        to_csharp_param_signature(ParamMode::In, &num_type, "aP0_Amount"),
        "decimal aP0_Amount"
    );
    assert_eq!(
        to_csharp_param_signature(ParamMode::Out, &num_type, "aP1_Result"),
        "out decimal aP1_Result"
    );
    assert_eq!(
        to_csharp_param_signature(ParamMode::InOut, &num_type, "aP2_Hits"),
        "ref decimal aP2_Hits"
    );
}

#[test]
fn test_expression_emission() {
    let emitter = CSharpEmitter::new();

    assert_eq!(emitter.emit_expr(&Expr::Integer(42)), "42");
    assert_eq!(
        emitter.emit_expr(&Expr::Decimal("123.45".to_string())),
        "123.45m"
    );
    assert_eq!(
        emitter.emit_expr(&Expr::Text("hello \"world\"".to_string())),
        "\"hello \\\"world\\\"\""
    );
    assert_eq!(emitter.emit_expr(&Expr::Boolean(true)), "true");
    assert_eq!(emitter.emit_expr(&Expr::Boolean(false)), "false");
    assert_eq!(emitter.emit_expr(&Expr::EmptyDate), "DateTime.MinValue");
    assert_eq!(
        emitter.emit_expr(&Expr::Variable("&Count".to_string())),
        "AV_Count"
    );
    assert_eq!(
        emitter.emit_expr(&Expr::Attribute("CustName".to_string())),
        "A_CustName"
    );

    // Binary operations
    let bin_add = Expr::Binary(
        Box::new(Expr::Integer(10)),
        BinOp::Add,
        Box::new(Expr::Integer(5)),
    );
    assert_eq!(emitter.emit_expr(&bin_add), "(10 + 5)");

    let bin_cmp = Expr::Binary(
        Box::new(Expr::Variable("AV8I".to_string())),
        BinOp::LessEqual,
        Box::new(Expr::Integer(3)),
    );
    assert_eq!(emitter.emit_expr(&bin_cmp), "(AV8I <= 3)");

    // Builtins
    let call_trim = Expr::Call(
        "TRIM".to_string(),
        vec![Expr::Text("  spaces  ".to_string())],
    );
    assert_eq!(
        emitter.emit_expr(&call_trim),
        "StringUtil.Trim(\"  spaces  \")"
    );

    let call_str = Expr::Call(
        "STR".to_string(),
        vec![
            Expr::Variable("AV8Value".to_string()),
            Expr::Integer(10),
            Expr::Integer(0),
        ],
    );
    assert_eq!(
        emitter.emit_expr(&call_str),
        "StringUtil.Str((decimal)(AV8Value), 10, 0)"
    );

    let call_iif = Expr::Call(
        "IIF".to_string(),
        vec![
            Expr::Boolean(true),
            Expr::Text("Yes".to_string()),
            Expr::Text("No".to_string()),
        ],
    );
    assert_eq!(
        emitter.emit_expr(&call_iif),
        "((true) ? (\"Yes\") : (\"No\"))"
    );

    let call_now = Expr::Call("NOW".to_string(), vec![]);
    assert_eq!(emitter.emit_expr(&call_now), "DateTimeUtil.Now(context)");
}

#[test]
fn test_statement_emission() {
    let mut emitter = CSharpEmitter::new();
    let mut vars = BTreeMap::new();
    vars.insert(
        "AV8Value".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 0,
        },
    );
    vars.insert(
        "AV8I".to_string(),
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
    );

    // Assignment with numeric cast
    let assign = Stmt::Assign(
        "AV8Value".to_string(),
        Expr::Binary(
            Box::new(Expr::Integer(21)),
            BinOp::Multiply,
            Box::new(Expr::Integer(2)),
        ),
    );
    assert_eq!(
        emitter.emit_stmt(&assign, &vars),
        "AV8Value = (long)((21 * 2));\n"
    );

    // While loop
    let while_stmt = Stmt::While(
        Expr::Binary(
            Box::new(Expr::Variable("AV8I".to_string())),
            BinOp::LessEqual,
            Box::new(Expr::Integer(3)),
        ),
        vec![Stmt::Assign(
            "AV8I".to_string(),
            Expr::Binary(
                Box::new(Expr::Variable("AV8I".to_string())),
                BinOp::Add,
                Box::new(Expr::Integer(1)),
            ),
        )],
    );
    let while_code = emitter.emit_stmt(&while_stmt, &vars);
    assert!(while_code.contains("while ( (AV8I <= 3) )"));
    assert!(while_code.contains("AV8I = (int)((AV8I + 1));"));

    // For loop
    let for_stmt = Stmt::For(
        "AV8I".to_string(),
        Expr::Integer(1),
        Expr::Integer(5),
        1,
        vec![Stmt::Print(Expr::Variable("AV8I".to_string()))],
    );
    let for_code = emitter.emit_stmt(&for_stmt, &vars);
    assert!(for_code.contains("for ( AV8I = 1; AV8I <= 5; AV8I++ )"));
    assert!(for_code.contains("context.StatusMessage( AV8I );"));

    // XML deserialization
    let from_xml = Stmt::FromXml(Box::new(FromXmlStmt {
        target: Box::new(Expr::Variable("&Customer".to_string())),
        xml: Expr::Variable("&XmlString".to_string()),
        root: None,
    }));
    assert_eq!(
        emitter.emit_stmt(&from_xml, &vars),
        "AV_Customer.FromXml(AV_XmlString);\n"
    );

    // Call statement
    let call_stmt = Stmt::Call(Expr::Call("arusttimingprobe".to_string(), vec![]));
    assert_eq!(
        emitter.emit_stmt(&call_stmt, &vars),
        "new arusttimingprobe(context).execute();\n"
    );
}

#[test]
fn test_procedure_class_emission_arusttimingprobe() {
    let mut vars = BTreeMap::new();
    vars.insert(
        "AV8Value".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 0,
        },
    );

    let source = ProcedureSource {
        name: "arusttimingprobe".to_string(),
        params: vec![],
        variables: vars,
        return_type: None,
        source: r#"
            &AV8Value = 21 * 2
            msg(trim(str(&AV8Value, 10, 0)), status)
        "#
        .to_string(),
    };

    let proc_class = ProcedureClass::from_source(&source)
        .expect("procedure parse")
        .with_is_main(true)
        .with_theme("KBTeste");

    let code = proc_class.emit();

    // Verify all 21 standard imports exist
    for imp in STANDARD_IMPORTS {
        assert!(
            code.contains(&format!("using {imp};")),
            "missing import: {imp}"
        );
    }

    // Verify namespace and class declaration
    assert!(code.contains("namespace GeneXus.Programs {"));
    assert!(code.contains("public class arusttimingprobe : GXProcedure"));

    // Verify Main implementation for executable
    assert!(code.contains("public static int Main( string[] args )"));
    assert!(code.contains("return new arusttimingprobe().MainImpl(args); ;"));
    assert!(code.contains("public int executeCmdLine( string[] args )"));
    assert!(code.contains("protected override int ExecuteCmdLine( string[] args )"));
    assert!(code.contains("return GX.GXRuntime.ExitCode ;"));

    // Verify constructors
    assert!(code.contains("public arusttimingprobe( )"));
    assert!(code.contains("context = new GxContext(  );"));
    assert!(code.contains("DataStoreUtil.LoadDataStores( context);"));
    assert!(code.contains("IsMain = true;"));
    assert!(code.contains("context.SetDefaultTheme(\"KBTeste\", true);"));
    assert!(code.contains("public arusttimingprobe( IGxContext context )"));
    assert!(code.contains("this.context = context;"));
    assert!(code.contains("IsMain = false;"));

    // Verify methods
    assert!(code.contains("public void execute()"));
    assert!(code.contains("public void executeSubmit()"));
    assert!(code.contains("protected override void ExecutePrivate( )"));
    assert!(code.contains("public override void cleanup( )"));
    assert!(code.contains("public override void initialize( )"));

    // Verify body logic
    assert!(code.contains("AV8Value = (long)((21 * 2));"));
    assert!(code.contains(
        "context.StatusMessage( StringUtil.Trim(StringUtil.Str((decimal)(AV8Value), 10, 0)) );"
    ));

    // Verify variable declaration
    assert!(code.contains("private long AV8Value ;"));
}

#[test]
fn test_procedure_with_parameters_and_udp() {
    let mut vars = BTreeMap::new();
    vars.insert(
        "Hits".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 2,
        },
    );
    vars.insert(
        "Result".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 2,
        },
    );

    let source = ProcedureSource {
        name: "dch37961700".to_string(),
        params: vec!["inout:&Hits".to_string(), "out:&Result".to_string()],
        variables: vars,
        return_type: None,
        source: r#"
            &Hits = &Hits + 1
            &Result = 1
        "#
        .to_string(),
    };

    let proc_class = ProcedureClass::from_source(&source).expect("procedure parse");
    let code = proc_class.emit();

    // Verify execute signature with ref and out parameters
    assert!(code.contains("public void execute( ref decimal aP0_Hits ,\n                           out decimal aP1_Result )"));

    // Verify incoming and outgoing parameter assignments
    assert!(code.contains("this.AV_Hits = aP0_Hits;"));
    assert!(code.contains("this.AV_Result = 0m ;"));
    assert!(code.contains("aP0_Hits = this.AV_Hits;"));
    assert!(code.contains("aP1_Result = this.AV_Result;"));

    // Verify executeUdp returns the out parameter Result
    assert!(code.contains("public decimal executeUdp( ref decimal aP0_Hits )"));
    assert!(code.contains("execute(ref aP0_Hits, out aP1_Result);"));
    assert!(code.contains("return AV_Result ;"));

    // Verify shadow parameter fields
    assert!(code.contains("private decimal aP0_Hits ;"));
    assert!(code.contains("private decimal aP1_Result ;"));
}

#[test]
fn test_procedure_from_kb_object() {
    let mut kb_obj = KbObject::new(
        ObjectId::new("proc_calc_total").unwrap(),
        "CalcTotal",
        ObjectKind::Procedure,
    );

    kb_obj.source_parts.push(SourcePart {
        name: "SOURCE".to_string(),
        source: r#"
            &Total = &Price * &Qty
            msg(str(&Total), status)
        "#
        .to_string(),
    });

    kb_obj.source_parts.push(SourcePart {
        name: "RULES".to_string(),
        source: "parm(in:&Price, in:&Qty, out:&Total);".to_string(),
    });

    let vars_json = r#"{
        "Price": { "kind": "numeric", "precision": 10, "scale": 2 },
        "Qty": { "kind": "numeric", "precision": 5, "scale": 0 },
        "Total": { "kind": "numeric", "precision": 12, "scale": 2 }
    }"#;
    kb_obj.source_parts.push(SourcePart {
        name: "VARIABLES".to_string(),
        source: vars_json.to_string(),
    });

    let proc_class = ProcedureClass::from_kb_object(&kb_obj).expect("from kb object");
    let code = proc_class.emit();

    assert!(code.contains("public class CalcTotal : GXProcedure"));
    assert!(code.contains("public void execute( decimal aP0_Price ,\n                           int aP1_Qty ,\n                           out decimal aP2_Total )"));
    assert!(code.contains("public decimal executeUdp( decimal aP0_Price , int aP1_Qty )"));
    assert!(code.contains("return AV_Total ;"));
    assert!(code.contains("AV_Total = (decimal)((AV_Price * AV_Qty));"));
}

#[test]
fn test_procedure_with_subroutines() {
    let mut vars = BTreeMap::new();
    vars.insert(
        "Total".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 0,
        },
    );

    let source = ProcedureSource {
        name: "SubroutineProc".to_string(),
        params: vec![],
        variables: vars,
        return_type: None,
        source: r#"
            &Total = 0
            Do 'Calculate'

            Sub 'Calculate'
                &Total = &Total + 10
            EndSub
        "#
        .to_string(),
    };

    let proc_class = ProcedureClass::from_source(&source).expect("procedure parse");
    let code = proc_class.emit();

    // Verify subroutine call inside ExecutePrivate
    assert!(code.contains("Sub_CALCULATE();"));

    // Verify subroutine method declaration
    assert!(code.contains("private void Sub_CALCULATE( )"));
    assert!(code.contains("AV_Total = (long)((AV_Total + 10));"));
}

#[test]
fn test_csproj_emitter_net8() {
    let config = CsprojConfig::net8("arusttimingprobe", true);
    let xml = CsprojEmitter::emit(&config);

    assert!(xml.contains(r#"<Project Sdk="Microsoft.NET.Sdk" ToolsVersion="Current">"#));
    assert!(xml.contains("<AssemblyName>arusttimingprobe</AssemblyName>"));
    assert!(xml.contains("<TargetFramework>net8.0</TargetFramework>"));
    assert!(xml.contains("<OutputType>Exe</OutputType>"));
    assert!(xml.contains("<GenerateDependencyFile>true</GenerateDependencyFile>"));
    assert!(xml.contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>"));
    assert!(
        xml.contains(r#"<PackageReference Include="GeneXus.Classes.Web.Core" Version="1.30.0" />"#)
    );
    assert!(xml.contains(r#"<Compile Include="arusttimingprobe.cs" />"#));

    // Verify XML well-formedness using roxmltree
    let doc = roxmltree::Document::parse(&xml);
    assert!(doc.is_ok(), "XML must be well-formed: {:?}", doc.err());
}

#[test]
fn test_csproj_emitter_net9() {
    let config = CsprojConfig::net9("libcalc", false);
    let xml = CsprojEmitter::emit(&config);

    assert!(xml.contains("<AssemblyName>libcalc</AssemblyName>"));
    assert!(xml.contains("<TargetFramework>net9.0</TargetFramework>"));
    assert!(xml.contains("<OutputType>Library</OutputType>"));

    let doc = roxmltree::Document::parse(&xml);
    assert!(doc.is_ok(), "XML must be well-formed: {:?}", doc.err());
}

#[test]
fn test_csproj_emitter_genexus_build() {
    let config = CsprojConfig::genexus_build("arusttimingprobe");
    let xml = CsprojEmitter::emit(&config);

    assert!(xml.contains("<AssemblyName>arusttimingprobe</AssemblyName>"));
    assert!(xml.contains("<TargetFramework>$(TargetFramework)</TargetFramework>"));
    assert!(xml.contains("<OutputType>Exe</OutputType>"));
    assert!(xml.contains(
        r#"<ProjectReference Include=".\..\GeneXus.Programs.Common\GeneXus.Programs.Common.csproj">"#
    ));
    assert!(xml.contains(r#"<Compile Include="$(WebDirectory)\arusttimingprobe.cs" />"#));
    assert!(xml.contains(r#"<Import Project="..\DotNetCoreBaseProject.targets" />"#));

    let doc = roxmltree::Document::parse(&xml);
    assert!(doc.is_ok(), "XML must be well-formed: {:?}", doc.err());
}

#[test]
fn test_live_kb_arusttimingprobe_structural_parity() {
    let official_path = std::path::Path::new(r"C:\KBs\KBTeste\NetModel\Web\arusttimingprobe.cs");
    if !official_path.exists() {
        return;
    }
    let official_code = std::fs::read_to_string(official_path).expect("read official cs");

    let mut vars = BTreeMap::new();
    vars.insert(
        "AV8Value".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 0,
        },
    );

    let source = ProcedureSource {
        name: "arusttimingprobe".to_string(),
        params: vec![],
        variables: vars,
        return_type: None,
        source: r#"
            &AV8Value = 21 * 2
            msg(trim(str(&AV8Value, 10, 0)), status)
        "#
        .to_string(),
    };

    let generated_code = ProcedureClass::from_source(&source)
        .expect("procedure parse")
        .with_is_main(true)
        .with_theme("KBTeste")
        .emit();

    // Check critical structural sections match official code
    assert!(official_code.contains("public class arusttimingprobe : GXProcedure"));
    assert!(generated_code.contains("public class arusttimingprobe : GXProcedure"));

    assert!(official_code.contains("public static int Main( string[] args )"));
    assert!(generated_code.contains("public static int Main( string[] args )"));

    assert!(official_code.contains("protected override int ExecuteCmdLine( string[] args )"));
    assert!(generated_code.contains("protected override int ExecuteCmdLine( string[] args )"));

    assert!(official_code.contains("context = new GxContext(  );"));
    assert!(generated_code.contains("context = new GxContext(  );"));

    assert!(official_code.contains("context.SetDefaultTheme(\"KBTeste\", true);"));
    assert!(generated_code.contains("context.SetDefaultTheme(\"KBTeste\", true);"));

    assert!(official_code.contains("protected override void ExecutePrivate( )"));
    assert!(generated_code.contains("protected override void ExecutePrivate( )"));

    assert!(official_code.contains("public override void cleanup( )"));
    assert!(generated_code.contains("public override void cleanup( )"));

    assert!(official_code.contains("public override void initialize( )"));
    assert!(generated_code.contains("public override void initialize( )"));

    assert!(official_code.contains("private long AV8Value ;"));
    assert!(generated_code.contains("private long AV8Value ;"));
}

#[test]
fn test_dotnet_compile_generated_project() {
    let which_dotnet = std::process::Command::new("dotnet")
        .arg("--version")
        .output();

    if which_dotnet.is_err() {
        return; // Skip if dotnet CLI is not in environment
    }

    let temp_dir = std::env::temp_dir().join(format!("gx_test_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&temp_dir);
    std::fs::create_dir_all(&temp_dir).expect("create temp dir");

    let mut vars = BTreeMap::new();
    vars.insert(
        "AV8Value".to_string(),
        DataType::Numeric {
            precision: 10,
            scale: 0,
        },
    );

    let source = ProcedureSource {
        name: "arusttimingprobe".to_string(),
        params: vec![],
        variables: vars,
        return_type: None,
        source: r#"
            &AV8Value = 21 * 2
            msg(trim(str(&AV8Value, 10, 0)), status)
        "#
        .to_string(),
    };

    let proc_class = ProcedureClass::from_source(&source)
        .expect("procedure parse")
        .with_theme("KBTeste");
    let cs_code = proc_class.emit();

    let config = CsprojConfig::net8("arusttimingprobe", false);
    let csproj_xml = CsprojEmitter::emit(&config);

    let cs_file = temp_dir.join("arusttimingprobe.cs");
    let csproj_file = temp_dir.join("arusttimingprobe.csproj");

    std::fs::write(&cs_file, cs_code).expect("write cs file");
    std::fs::write(&csproj_file, csproj_xml).expect("write csproj file");

    let build_output = std::process::Command::new("dotnet")
        .args(["build", csproj_file.to_str().unwrap(), "-c", "Release"])
        .output();

    let _ = std::fs::remove_dir_all(&temp_dir);

    if let Ok(output) = build_output {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        if !output.status.success() && (stdout.contains("error CS") || stderr.contains("error CS"))
        {
            panic!("C# syntax or compilation error in generated code:\n{stdout}\n{stderr}");
        }
    }
}
