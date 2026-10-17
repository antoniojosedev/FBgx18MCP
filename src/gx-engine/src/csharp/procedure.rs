//! GXProcedure C# class generator.

use crate::{
    catalog::{KbObject, ObjectKind},
    csharp::{
        emitter::CSharpEmitter,
        types::{
            clean_ident, to_csharp_default_value, to_csharp_param_name, to_csharp_param_signature,
            to_csharp_type, to_csharp_var_name,
        },
    },
    frontend::{Procedure, Stmt},
    model::{DataType, ParamMode, ProcedureSource, parse_parameter},
};
use std::collections::BTreeMap;
use std::fmt::Write;

/// Standard imports matching official GeneXus IDE generated code.
pub const STANDARD_IMPORTS: &[&str] = &[
    "System",
    "System.Collections",
    "GeneXus.Utils",
    "GeneXus.Resources",
    "GeneXus.Application",
    "GeneXus.Metadata",
    "GeneXus.Cryptography",
    "com.genexus",
    "GeneXus.Data.ADO",
    "GeneXus.Data.NTier",
    "GeneXus.Data.NTier.ADO",
    "GeneXus.WebControls",
    "GeneXus.Http",
    "GeneXus.Procedure",
    "GeneXus.XML",
    "GeneXus.Search",
    "GeneXus.Encryption",
    "GeneXus.Http.Client",
    "System.Threading",
    "System.Xml.Serialization",
    "System.Runtime.Serialization",
];

/// Typed parameter definition for C# signature generation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CsharpParam {
    pub name: String,
    pub data_type: DataType,
    pub mode: ParamMode,
    pub param_name: String,
    pub var_name: String,
}

/// Represents an emittable GeneXus C# Procedure class (`GXProcedure`).
#[derive(Debug, Clone)]
pub struct ProcedureClass {
    pub name: String,
    pub namespace: String,
    pub theme: Option<String>,
    pub is_main: bool,
    pub params: Vec<CsharpParam>,
    pub variables: BTreeMap<String, DataType>,
    pub return_type: Option<DataType>,
    pub body: Vec<Stmt>,
    pub subroutines: Vec<(String, Vec<Stmt>)>,
}

impl ProcedureClass {
    /// Creates a new empty ProcedureClass with default namespace and settings.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            namespace: "GeneXus.Programs".to_string(),
            theme: Some("KBTeste".to_string()),
            is_main: false,
            params: Vec::new(),
            variables: BTreeMap::new(),
            return_type: None,
            body: Vec::new(),
            subroutines: Vec::new(),
        }
    }

    /// Sets the application theme for GxContext.
    pub fn with_theme(mut self, theme: impl Into<String>) -> Self {
        self.theme = Some(theme.into());
        self
    }

    /// Sets whether this procedure is an entry point / main executable.
    pub fn with_is_main(mut self, is_main: bool) -> Self {
        self.is_main = is_main;
        self
    }

    /// Sets custom namespace.
    pub fn with_namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = namespace.into();
        self
    }

    /// Adds a typed parameter.
    pub fn add_param(mut self, param: CsharpParam) -> Self {
        self.params.push(param);
        self
    }

    /// Adds a declared variable.
    pub fn add_variable(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.variables.insert(name.into(), data_type);
        self
    }

    /// Constructs a ProcedureClass from parsed AST and variable definitions.
    pub fn from_ast(
        name: &str,
        procedure: &Procedure,
        variables: &BTreeMap<String, DataType>,
    ) -> Self {
        let mut main_body = Vec::new();
        let mut subroutines = Vec::new();

        for stmt in &procedure.body {
            if let Stmt::Sub(sub_name, sub_body) = stmt {
                subroutines.push((sub_name.clone(), sub_body.clone()));
            } else {
                main_body.push(stmt.clone());
            }
        }

        let mut params = Vec::with_capacity(procedure.params.len());
        for (i, p) in procedure.params.iter().enumerate() {
            let (mode, param_ident) = parse_parameter(p).unwrap_or((ParamMode::InOut, p.as_str()));
            let lookup_key = param_ident.trim_start_matches('&');
            let fallback_name = clean_ident(param_ident);
            let (matched_name, data_type) = variables
                .iter()
                .find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(param_ident)
                })
                .map(|(k, v)| (k.clone(), v.clone()))
                .unwrap_or_else(|| {
                    (
                        fallback_name,
                        DataType::Numeric {
                            precision: 10,
                            scale: 0,
                        },
                    )
                });

            let clean = clean_ident(&matched_name);
            let param_name = to_csharp_param_name(i, &matched_name);
            let var_name = to_csharp_var_name(&matched_name);
            params.push(CsharpParam {
                name: clean,
                data_type,
                mode,
                param_name,
                var_name,
            });
        }

        Self {
            name: name.to_string(),
            namespace: "GeneXus.Programs".to_string(),
            theme: Some("KBTeste".to_string()),
            is_main: false,
            params,
            variables: variables.clone(),
            return_type: None,
            body: main_body,
            subroutines,
        }
    }

    /// Constructs a ProcedureClass from a `ProcedureSource` definition.
    pub fn from_source(source: &ProcedureSource) -> Result<Self, String> {
        let body = crate::frontend::parse_body(&source.source)
            .map_err(|e| format!("procedure {}: {e}", source.name))?;
        let ast = Procedure {
            name: source.name.clone(),
            params: source.params.clone(),
            body,
        };
        let mut proc = Self::from_ast(&source.name, &ast, &source.variables);
        proc.return_type = source.return_type.clone();
        Ok(proc)
    }

    /// Constructs a ProcedureClass directly from a portable `KbObject`.
    pub fn from_kb_object(object: &KbObject) -> Result<Self, String> {
        if object.kind != ObjectKind::Procedure {
            return Err(format!("expected Procedure, found {:?}", object.kind));
        }

        let mut source_code = "";
        let mut variables: BTreeMap<String, DataType> = BTreeMap::new();
        let mut params: Vec<String> = Vec::new();

        for part in &object.source_parts {
            match part.name.as_str() {
                "SOURCE" => source_code = &part.source,
                "VARIABLES" => {
                    if let Ok(vars) =
                        serde_json::from_str::<BTreeMap<String, DataType>>(&part.source)
                    {
                        variables = vars;
                    }
                }
                "RULES" => {
                    if let Ok(rules) = crate::rules::parse_rules(&part.source) {
                        for rule in rules {
                            if let crate::rules::Rule::Parm(args) = rule {
                                for arg in args {
                                    if arg.mode.is_empty() {
                                        params.push(arg.name);
                                    } else {
                                        params.push(format!("{}:{}", arg.mode, arg.name));
                                    }
                                }
                            }
                        }
                    }
                }
                _ => {}
            }
        }

        let body = if source_code.is_empty() {
            Vec::new()
        } else {
            crate::frontend::parse_body(source_code)
                .map_err(|e| format!("object {}: {e}", object.name))?
        };

        let ast = Procedure {
            name: object.name.clone(),
            params,
            body,
        };

        Ok(Self::from_ast(&object.name, &ast, &variables))
    }

    /// Emits complete C# source code (.cs) for this Procedure.
    pub fn emit(&self) -> String {
        let mut out = String::new();

        // 1. Standard imports
        for imp in crate::csharp::types::STANDARD_IMPORTS {
            let _ = writeln!(out, "using {imp};");
        }

        // 2. Namespace
        let _ = writeln!(out, "namespace {} {{", self.namespace);

        // 3. Class declaration
        let _ = writeln!(out, "   public class {} : GXProcedure", self.name);
        let _ = writeln!(out, "   {{");

        if self.is_main {
            self.emit_main_entrypoint(&mut out);
        }

        self.emit_constructors(&mut out);

        let param_signatures = if self.params.is_empty() {
            String::new()
        } else {
            let sigs: Vec<String> = self
                .params
                .iter()
                .map(|p| to_csharp_param_signature(p.mode, &p.data_type, &p.param_name))
                .collect();
            format!(" {} ", sigs.join(" ,\n                           "))
        };

        self.emit_execute_methods(&mut out, &param_signatures);
        self.emit_execute_udp(&mut out);

        let mut emitter = CSharpEmitter::with_vars(&self.variables);
        self.emit_execute_private(&mut out, &mut emitter);
        self.emit_cleanup_and_initialize(&mut out);
        self.emit_subroutines(&mut out, &mut emitter);
        self.emit_member_fields(&mut out);

        let _ = writeln!(out, "   }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "}}");

        out
    }

    fn emit_main_entrypoint(&self, out: &mut String) {
        let _ = writeln!(out, "      public static int Main( string[] args )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         return new {}().MainImpl(args); ;", self.name);
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public int executeCmdLine( string[] args )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         return ExecuteCmdLine(args); ;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(
            out,
            "      protected override int ExecuteCmdLine( string[] args )"
        );
        let _ = writeln!(out, "      {{");
        if self.params.is_empty() {
            let _ = writeln!(out, "         execute();");
        } else {
            let mut cmd_args = Vec::new();
            for p in &self.params {
                match p.mode {
                    ParamMode::In => cmd_args.push(p.param_name.clone()),
                    ParamMode::Out => cmd_args.push(format!("out {}", p.param_name)),
                    ParamMode::InOut => cmd_args.push(format!("ref {}", p.param_name)),
                }
            }
            let _ = writeln!(out, "         execute({});", cmd_args.join(", "));
        }
        let _ = writeln!(out, "         return GX.GXRuntime.ExitCode ;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_constructors(&self, out: &mut String) {
        let _ = writeln!(out, "      public {}( )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         context = new GxContext(  );");
        let _ = writeln!(out, "         DataStoreUtil.LoadDataStores( context);");
        let _ = writeln!(out, "         IsMain = true;");
        if let Some(theme) = &self.theme {
            let _ = writeln!(out, "         context.SetDefaultTheme(\"{theme}\", true);");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public {}( IGxContext context )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         this.context = context;");
        let _ = writeln!(out, "         IsMain = false;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_execute_methods(&self, out: &mut String, param_signatures: &str) {
        // execute
        let _ = writeln!(out, "      public void execute({param_signatures})");
        let _ = writeln!(out, "      {{");
        for p in &self.params {
            match p.mode {
                ParamMode::In | ParamMode::InOut => {
                    let _ = writeln!(out, "         this.{} = {};", p.var_name, p.param_name);
                }
                ParamMode::Out => {
                    let default_val = to_csharp_default_value(&p.data_type);
                    let _ = writeln!(out, "         this.{} = {} ;", p.var_name, default_val);
                }
            }
        }
        let _ = writeln!(out, "         initialize();");
        let _ = writeln!(out, "         ExecuteImpl();");
        for p in &self.params {
            match p.mode {
                ParamMode::Out | ParamMode::InOut => {
                    let _ = writeln!(out, "         {} = this.{};", p.param_name, p.var_name);
                }
                ParamMode::In => {}
            }
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        // executeSubmit
        let _ = writeln!(out, "      public void executeSubmit({param_signatures})");
        let _ = writeln!(out, "      {{");
        for p in &self.params {
            match p.mode {
                ParamMode::In | ParamMode::InOut => {
                    let _ = writeln!(out, "         this.{} = {};", p.var_name, p.param_name);
                }
                ParamMode::Out => {
                    let default_val = to_csharp_default_value(&p.data_type);
                    let _ = writeln!(out, "         this.{} = {} ;", p.var_name, default_val);
                }
            }
        }
        let _ = writeln!(out, "         SubmitImpl();");
        for p in &self.params {
            match p.mode {
                ParamMode::Out | ParamMode::InOut => {
                    let _ = writeln!(out, "         {} = this.{};", p.param_name, p.var_name);
                }
                ParamMode::In => {}
            }
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_execute_udp(&self, out: &mut String) {
        let udp_target = self
            .params
            .iter()
            .rposition(|p| p.mode == ParamMode::Out)
            .map(|idx| &self.params[idx]);

        if let Some(out_p) = udp_target {
            let ret_type = to_csharp_type(&out_p.data_type);
            let in_sigs: Vec<String> = self
                .params
                .iter()
                .filter(|p| p.mode != ParamMode::Out)
                .map(|p| to_csharp_param_signature(p.mode, &p.data_type, &p.param_name))
                .collect();
            let in_sig_str = if in_sigs.is_empty() {
                String::new()
            } else {
                format!(" {} ", in_sigs.join(" , "))
            };

            let all_args: Vec<String> = self
                .params
                .iter()
                .map(|p| match p.mode {
                    ParamMode::In => p.param_name.clone(),
                    ParamMode::Out => format!("out {}", p.param_name),
                    ParamMode::InOut => format!("ref {}", p.param_name),
                })
                .collect();

            let _ = writeln!(out, "      public {ret_type} executeUdp({in_sig_str})");
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         execute({});", all_args.join(", "));
            let _ = writeln!(out, "         return {} ;", out_p.var_name);
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        } else if let Some(ret_type) = &self.return_type {
            let cs_ret = to_csharp_type(ret_type);
            let in_sigs: Vec<String> = self
                .params
                .iter()
                .map(|p| to_csharp_param_signature(p.mode, &p.data_type, &p.param_name))
                .collect();
            let in_sig_str = if in_sigs.is_empty() {
                String::new()
            } else {
                format!(" {} ", in_sigs.join(" , "))
            };

            let all_args: Vec<String> = self
                .params
                .iter()
                .map(|p| match p.mode {
                    ParamMode::In => p.param_name.clone(),
                    ParamMode::Out => format!("out {}", p.param_name),
                    ParamMode::InOut => format!("ref {}", p.param_name),
                })
                .collect();

            let _ = writeln!(out, "      public {cs_ret} executeUdp({in_sig_str})");
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         execute({});", all_args.join(", "));
            let _ = writeln!(out, "         return AV_Result ;");
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }
    }

    fn emit_execute_private(&self, out: &mut String, emitter: &mut CSharpEmitter) {
        let _ = writeln!(out, "      protected override void ExecutePrivate( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* GeneXus formulas */");
        let _ = writeln!(out, "         /* Output device settings */");

        emitter.indented(|e| {
            e.indented(|e| {
                e.indented(|e| {
                    for stmt in &self.body {
                        out.push_str(&e.emit_stmt(stmt, &self.variables));
                    }
                });
            });
        });

        let _ = writeln!(out, "         cleanup();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_cleanup_and_initialize(&self, out: &mut String) {
        let _ = writeln!(out, "      public override void cleanup( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         CloseCursors();");
        let _ = writeln!(out, "         if ( IsMain )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            context.CloseConnections();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "         ExitApp();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public override void initialize( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* GeneXus formulas. */");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_subroutines(&self, out: &mut String, emitter: &mut CSharpEmitter) {
        for (sub_name, sub_body) in &self.subroutines {
            let clean = clean_ident(sub_name);
            let _ = writeln!(out, "      private void Sub_{clean}( )");
            let _ = writeln!(out, "      {{");
            emitter.indented(|e| {
                e.indented(|e| {
                    e.indented(|e| {
                        for stmt in sub_body {
                            out.push_str(&e.emit_stmt(stmt, &self.variables));
                        }
                    });
                });
            });
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }
    }

    fn emit_member_fields(&self, out: &mut String) {
        for (var_name, data_type) in &self.variables {
            let cs_type = to_csharp_type(data_type);
            let cs_name = to_csharp_var_name(var_name);
            let _ = writeln!(out, "      private {cs_type} {cs_name} ;");
        }
        if let Some(ref ret_type) = self.return_type {
            if !self.variables.contains_key("Result") {
                let cs_ret = to_csharp_type(ret_type);
                let _ = writeln!(out, "      private {cs_ret} AV_Result ;");
            }
        }
        for p in &self.params {
            let cs_type = to_csharp_type(&p.data_type);
            let _ = writeln!(out, "      private {cs_type} {} ;", p.param_name);
        }
    }
}
