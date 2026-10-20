//! .csproj project file generator for GeneXus C# assemblies.

use std::fmt::Write;

/// Assembly output type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OutputType {
    Exe,
    Library,
}

impl OutputType {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Exe => "Exe",
            Self::Library => "Library",
        }
    }
}

/// Configuration options for .csproj generation.
#[derive(Debug, Clone)]
pub struct CsprojConfig {
    pub assembly_name: String,
    pub target_framework: String,
    pub output_type: OutputType,
    /// The class whose `Main` starts an executable (`<StartupObject>`). Unset for a library, and
    /// the emitted project then has no such element.
    pub startup_object: Option<String>,
    pub generate_dependency_file: bool,
    pub copy_local_lock_file_assemblies: bool,
    pub postgresql_support: bool,
    pub office_support: bool,
    pub mail_support: bool,
    pub grpc_client_support: bool,
    pub pdf_library: String,
    pub project_references: Vec<String>,
    pub package_references: Vec<(String, String)>,
    pub compile_includes: Vec<String>,
    pub import_targets: Option<String>,
}

impl CsprojConfig {
    /// Creates a base configuration with common defaults.
    pub fn new(assembly_name: impl Into<String>) -> Self {
        let name = assembly_name.into();
        Self {
            assembly_name: name.clone(),
            target_framework: "net8.0".to_string(),
            output_type: OutputType::Exe,
            generate_dependency_file: true,
            copy_local_lock_file_assemblies: true,
            postgresql_support: false,
            office_support: false,
            mail_support: false,
            grpc_client_support: false,
            pdf_library: String::new(),
            project_references: Vec::new(),
            package_references: vec![(
                "GeneXus.Classes.Web.Core".to_string(),
                "1.30.0".to_string(),
            )],
            compile_includes: vec![format!("{name}.cs")],
            import_targets: None,
            startup_object: None,
        }
    }

    /// Modern .NET 8 SDK project configuration referencing GeneXus.Classes.Web.Core.
    pub fn net8(name: impl Into<String>, is_exe: bool) -> Self {
        let mut cfg = Self::new(name);
        cfg.target_framework = "net8.0".to_string();
        cfg.output_type = if is_exe {
            OutputType::Exe
        } else {
            OutputType::Library
        };
        cfg
    }

    /// Modern .NET 9 SDK project configuration referencing GeneXus.Classes.Web.Core.
    pub fn net9(name: impl Into<String>, is_exe: bool) -> Self {
        let mut cfg = Self::new(name);
        cfg.target_framework = "net9.0".to_string();
        cfg.output_type = if is_exe {
            OutputType::Exe
        } else {
            OutputType::Library
        };
        cfg
    }

    /// GeneXus NetModel build configuration matching `arusttimingprobe.csproj`.
    pub fn genexus_build(name: impl Into<String>) -> Self {
        let name_str = name.into();
        Self {
            assembly_name: name_str.clone(),
            target_framework: "$(TargetFramework)".to_string(),
            output_type: OutputType::Exe,
            generate_dependency_file: true,
            copy_local_lock_file_assemblies: true,
            postgresql_support: false,
            office_support: false,
            mail_support: false,
            grpc_client_support: false,
            pdf_library: String::new(),
            project_references: vec![
                r".\..\GeneXus.Programs.Common\GeneXus.Programs.Common.csproj".to_string(),
            ],
            package_references: Vec::new(),
            compile_includes: vec![format!(r"$(WebDirectory)\{name_str}.cs")],
            import_targets: Some(r"..\DotNetCoreBaseProject.targets".to_string()),
            startup_object: None,
        }
    }

    /// Makes this project an executable that starts at `Main` of `startup_object` (a fully qualified
    /// class name). The SDK builds the same output set for it: the assembly, `runtimeconfig.json` and
    /// `deps.json`, with the entry point in the assembly metadata.
    pub fn as_executable(mut self, startup_object: impl Into<String>) -> Self {
        self.output_type = OutputType::Exe;
        self.startup_object = Some(startup_object.into());
        self
    }

    /// Adds a project reference.
    pub fn add_project_reference(mut self, project_path: impl Into<String>) -> Self {
        self.project_references.push(project_path.into());
        self
    }

    /// Adds a package reference.
    pub fn add_package_reference(
        mut self,
        package_name: impl Into<String>,
        version: impl Into<String>,
    ) -> Self {
        self.package_references
            .push((package_name.into(), version.into()));
        self
    }

    /// Adds a compile item.
    pub fn add_compile_include(mut self, item: impl Into<String>) -> Self {
        self.compile_includes.push(item.into());
        self
    }
}

/// Emits XML project files (.csproj).
pub struct CsprojEmitter;

impl CsprojEmitter {
    /// Emits complete .csproj XML string.
    pub fn emit(config: &CsprojConfig) -> String {
        let mut xml = String::new();
        let _ = writeln!(
            xml,
            r#"<Project Sdk="Microsoft.NET.Sdk" ToolsVersion="Current">"#
        );
        let _ = writeln!(xml, "\t<PropertyGroup>");
        let _ = writeln!(
            xml,
            "\t\t<AssemblyName>{}</AssemblyName>",
            config.assembly_name
        );
        let _ = writeln!(
            xml,
            "\t\t<TargetFramework>{}</TargetFramework>",
            config.target_framework
        );
        let _ = writeln!(
            xml,
            "\t\t<OutputType>{}</OutputType>",
            config.output_type.as_str()
        );
        if let Some(startup) = &config.startup_object {
            let _ = writeln!(xml, "\t\t<StartupObject>{startup}</StartupObject>");
        }
        let _ = writeln!(
            xml,
            "\t\t<GenerateDependencyFile>{}</GenerateDependencyFile>",
            config.generate_dependency_file
        );
        let _ = writeln!(
            xml,
            "\t\t<CopyLocalLockFileAssemblies>{}</CopyLocalLockFileAssemblies>",
            config.copy_local_lock_file_assemblies
        );
        let _ = writeln!(
            xml,
            "\t\t<PostgresqlSupport>{}</PostgresqlSupport>",
            config.postgresql_support
        );
        let _ = writeln!(
            xml,
            "\t\t<OfficeSupport>{}</OfficeSupport>",
            config.office_support
        );
        let _ = writeln!(
            xml,
            "\t\t<MailSupport>{}</MailSupport>",
            config.mail_support
        );
        let _ = writeln!(
            xml,
            "\t\t<GrpcClientSupport>{}</GrpcClientSupport>",
            config.grpc_client_support
        );
        let _ = writeln!(xml, "\t\t<PdfLibrary>{}</PdfLibrary>", config.pdf_library);
        if !config.compile_includes.is_empty() {
            // The compile items are listed explicitly, as GeneXus lists them. With the SDK's
            // default globbing on, every listed file is included twice (NETSDK1022).
            let _ = writeln!(
                xml,
                "\t\t<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
            );
        }
        let _ = writeln!(xml, "\t</PropertyGroup>");

        if !config.project_references.is_empty() {
            let _ = writeln!(xml, "\t<ItemGroup>");
            for pr in &config.project_references {
                let _ = writeln!(
                    xml,
                    "\t\t<ProjectReference Include=\"{pr}\">\n\t\t\t<OutputItemType>$(CommonOutputItemType)</OutputItemType>\n\t\t</ProjectReference>"
                );
            }
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        if !config.package_references.is_empty() {
            let _ = writeln!(xml, "\t<ItemGroup>");
            for (pkg, ver) in &config.package_references {
                let _ = writeln!(
                    xml,
                    "\t\t<PackageReference Include=\"{pkg}\" Version=\"{ver}\" />"
                );
            }
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        if !config.compile_includes.is_empty() {
            let _ = writeln!(xml, "\t<ItemGroup>");
            for item in &config.compile_includes {
                let _ = writeln!(xml, "\t\t<Compile Include=\"{item}\" />");
            }
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        if let Some(targets) = &config.import_targets {
            let _ = writeln!(xml, "\t<Import Project=\"{targets}\" />");
        }

        let _ = writeln!(xml, "</Project>");
        xml
    }
}
