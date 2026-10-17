//! Visual Studio Solution (.sln), Directory.build.props, and GxDeps.csproj generator.

use crate::csharp::project::CsprojConfig;
use std::fmt::Write;

/// Represents a project entry inside a Visual Studio solution.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SolutionProject {
    pub name: String,
    pub path: String,
    pub guid: String,
    pub type_guid: String,
}

impl SolutionProject {
    /// Creates a new SolutionProject with a deterministic GUID.
    pub fn new(name: impl Into<String>, path: impl Into<String>) -> Self {
        let name_str = name.into();
        let guid = deterministic_guid(&name_str);
        // C# SDK project type GUID
        let type_guid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}".to_string();
        Self {
            name: name_str,
            path: path.into(),
            guid,
            type_guid,
        }
    }

    /// Sets explicit project GUID.
    pub fn with_guid(mut self, guid: impl Into<String>) -> Self {
        self.guid = guid.into();
        self
    }

    /// Sets explicit project type GUID.
    pub fn with_type_guid(mut self, type_guid: impl Into<String>) -> Self {
        self.type_guid = type_guid.into();
        self
    }
}

/// Generates deterministic GUID string from an input name without external dependencies.
pub fn deterministic_guid(name: &str) -> String {
    let mut h1: u64 = 0xcbf29ce484222325;
    let mut h2: u64 = 0x100000001b3;
    for &b in name.as_bytes() {
        h1 = (h1 ^ (b as u64)).wrapping_mul(0x100000001b3);
        h2 = (h2 ^ (b as u64)).wrapping_mul(0xcbf29ce484222325);
    }
    let b1 = h1.to_be_bytes();
    let b2 = h2.to_be_bytes();
    format!(
        "{{{:02X}{:02X}{:02X}{:02X}-{:02X}{:02X}-{:02X}{:02X}-{:02X}{:02X}-{:02X}{:02X}{:02X}{:02X}{:02X}{:02X}}}",
        b1[0],
        b1[1],
        b1[2],
        b1[3],
        b1[4],
        b1[5],
        b1[6],
        b1[7],
        b2[0],
        b2[1],
        b2[2],
        b2[3],
        b2[4],
        b2[5],
        b2[6],
        b2[7]
    )
}

/// Configuration options for complete solution generation.
#[derive(Debug, Clone)]
pub struct SolutionConfig {
    pub name: String,
    pub projects: Vec<SolutionProject>,
    pub target_framework: String,
    pub genexus_version: String,
    pub office_support: bool,
    pub pdf_support: bool,
    pub mail_support: bool,
    pub postgresql_support: bool,
    pub mysql_support: bool,
    pub oracle_support: bool,
}

impl SolutionConfig {
    /// Creates a base SolutionConfig with default settings.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            projects: Vec::new(),
            target_framework: "net8.0".to_string(),
            genexus_version: "1.30.0".to_string(),
            office_support: false,
            pdf_support: false,
            mail_support: false,
            postgresql_support: false,
            mysql_support: false,
            oracle_support: false,
        }
    }

    /// Sets the target framework (e.g. `net8.0` or `net9.0`).
    pub fn with_target_framework(mut self, tfm: impl Into<String>) -> Self {
        self.target_framework = tfm.into();
        self
    }

    /// Sets the GeneXus standard classes version.
    pub fn with_genexus_version(mut self, version: impl Into<String>) -> Self {
        self.genexus_version = version.into();
        self
    }

    /// Enables or disables PostgreSQL driver support.
    pub fn with_postgresql(mut self, enabled: bool) -> Self {
        self.postgresql_support = enabled;
        self
    }

    /// Enables or disables MS Office / Excel support.
    pub fn with_office(mut self, enabled: bool) -> Self {
        self.office_support = enabled;
        self
    }

    /// Enables or disables PDF report generator support.
    pub fn with_pdf(mut self, enabled: bool) -> Self {
        self.pdf_support = enabled;
        self
    }

    /// Enables or disables Mail support.
    pub fn with_mail(mut self, enabled: bool) -> Self {
        self.mail_support = enabled;
        self
    }

    /// Adds a project to the solution.
    pub fn add_project(mut self, project: SolutionProject) -> Self {
        self.projects.push(project);
        self
    }

    /// Adds a project given its name and relative file path.
    pub fn add_project_path(mut self, name: impl Into<String>, path: impl Into<String>) -> Self {
        self.projects.push(SolutionProject::new(name, path));
        self
    }

    /// Constructs a solution configuration from a collection of CsprojConfig.
    pub fn from_csproj_configs(solution_name: &str, configs: &[CsprojConfig]) -> Self {
        let mut sol = Self::new(solution_name);
        if let Some(first) = configs.first() {
            sol.target_framework = first.target_framework.clone();
            sol.postgresql_support = first.postgresql_support;
            sol.office_support = first.office_support;
            sol.mail_support = first.mail_support;
        }

        for cfg in configs {
            let path = format!("{}.csproj", cfg.assembly_name);
            sol = sol.add_project_path(&cfg.assembly_name, path);
        }

        sol
    }
}

/// Emits Visual Studio Solution (.sln), Directory.build.props, and GxDeps.csproj files.
pub struct SolutionEmitter;

impl SolutionEmitter {
    /// Emits complete Visual Studio Solution (.sln) content.
    pub fn emit_sln(config: &SolutionConfig) -> String {
        let mut sln = String::new();
        let _ = writeln!(
            sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00"
        );
        let _ = writeln!(sln, "# Visual Studio Version 17");
        let _ = writeln!(sln, "VisualStudioVersion = 17.0.31903.59");
        let _ = writeln!(sln, "MinimumVisualStudioVersion = 10.0.40219.1");

        // Project declarations
        for proj in &config.projects {
            let _ = writeln!(
                sln,
                "Project(\"{}\") = \"{}\", \"{}\", \"{}\"",
                proj.type_guid, proj.name, proj.path, proj.guid
            );
            let _ = writeln!(sln, "EndProject");
        }

        let _ = writeln!(sln, "Global");

        // Solution Configuration Platforms
        let _ = writeln!(
            sln,
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution"
        );
        let _ = writeln!(sln, "\t\tDebug|Any CPU = Debug|Any CPU");
        let _ = writeln!(sln, "\t\tRelease|Any CPU = Release|Any CPU");
        let _ = writeln!(sln, "\tEndGlobalSection");

        // Project Configuration Platforms
        let _ = writeln!(
            sln,
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution"
        );
        for proj in &config.projects {
            let _ = writeln!(
                sln,
                "\t\t{}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                proj.guid
            );
            let _ = writeln!(
                sln,
                "\t\t{}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                proj.guid
            );
            let _ = writeln!(
                sln,
                "\t\t{}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                proj.guid
            );
            let _ = writeln!(
                sln,
                "\t\t{}.Release|Any CPU.Build.0 = Release|Any CPU",
                proj.guid
            );
        }
        let _ = writeln!(sln, "\tEndGlobalSection");

        // Solution properties
        let _ = writeln!(sln, "\tGlobalSection(SolutionProperties) = preSolution");
        let _ = writeln!(sln, "\t\tHideSolutionNode = FALSE");
        let _ = writeln!(sln, "\tEndGlobalSection");

        let _ = writeln!(sln, "EndGlobal");

        sln
    }

    /// Emits MSBuild `Directory.build.props` (or `Directory.Build.props`) XML string.
    pub fn emit_directory_build_props(config: &SolutionConfig) -> String {
        let mut xml = String::new();
        let _ = writeln!(xml, "<Project>");
        let _ = writeln!(xml, "\t<PropertyGroup>");
        let _ = writeln!(
            xml,
            "\t\t<TargetFramework>{}</TargetFramework>",
            config.target_framework
        );
        let _ = writeln!(xml, "\t\t<Nullable>disable</Nullable>");
        let _ = writeln!(xml, "\t\t<ImplicitUsings>disable</ImplicitUsings>");
        let _ = writeln!(
            xml,
            "\t\t<GenStdVersion>{}</GenStdVersion>",
            config.genexus_version
        );
        let _ = writeln!(
            xml,
            "\t\t<OfficeSupport>{}</OfficeSupport>",
            config.office_support
        );
        let _ = writeln!(xml, "\t\t<PDFSupport>{}</PDFSupport>", config.pdf_support);
        let _ = writeln!(
            xml,
            "\t\t<MailSupport>{}</MailSupport>",
            config.mail_support
        );
        let _ = writeln!(
            xml,
            "\t\t<PostgresqlSupport>{}</PostgresqlSupport>",
            config.postgresql_support
        );
        let _ = writeln!(
            xml,
            "\t\t<MysqlSupport>{}</MysqlSupport>",
            config.mysql_support
        );
        let _ = writeln!(
            xml,
            "\t\t<OracleSupport>{}</OracleSupport>",
            config.oracle_support
        );
        let _ = writeln!(xml, "\t</PropertyGroup>");
        let _ = writeln!(xml, "</Project>");

        xml
    }

    /// Emits `GxDeps.csproj` project XML file linking all dependencies.
    pub fn emit_gxdeps_csproj(config: &SolutionConfig) -> String {
        let mut xml = String::new();
        let _ = writeln!(xml, r#"<Project Sdk="Microsoft.NET.Sdk">"#);
        let _ = writeln!(xml, "\t<PropertyGroup>");
        let _ = writeln!(
            xml,
            "\t\t<EnableDefaultCompileItems>False</EnableDefaultCompileItems>"
        );
        let _ = writeln!(
            xml,
            "\t\t<TargetFramework>{}</TargetFramework>",
            config.target_framework
        );
        let _ = writeln!(xml, "\t\t<Configuration>Release</Configuration>");
        let _ = writeln!(
            xml,
            "\t\t<OfficeSupport>{}</OfficeSupport>",
            config.office_support
        );
        let _ = writeln!(xml, "\t\t<PDFSupport>{}</PDFSupport>", config.pdf_support);
        let _ = writeln!(
            xml,
            "\t\t<MailSupport>{}</MailSupport>",
            config.mail_support
        );
        let _ = writeln!(
            xml,
            "\t\t<PostgresqlSupport>{}</PostgresqlSupport>",
            config.postgresql_support
        );
        let _ = writeln!(xml, "\t\t<GenInit>true</GenInit>");
        let _ = writeln!(xml, "\t</PropertyGroup>");

        // Standard GeneXus package references
        let _ = writeln!(xml, "\t<ItemGroup>");
        let _ = writeln!(
            xml,
            "\t\t<PackageReference Include=\"GeneXus.Classes.Core\" Version=\"{}\" />",
            config.genexus_version
        );
        let _ = writeln!(
            xml,
            "\t\t<PackageReference Include=\"GeneXus.Classes.Web.Core\" Version=\"{}\" />",
            config.genexus_version
        );
        let _ = writeln!(
            xml,
            "\t\t<PackageReference Include=\"GeneXus.Maps.Core\" Version=\"{}\" />",
            config.genexus_version
        );
        let _ = writeln!(
            xml,
            "\t\t<PackageReference Include=\"GeneXus.NetCoreStartup\" Version=\"{}\" />",
            config.genexus_version
        );
        let _ = writeln!(xml, "\t</ItemGroup>");

        // Conditional package references
        if config.office_support {
            let _ = writeln!(
                xml,
                "\t<ItemGroup Condition=\"'$(OfficeSupport)'=='true'\">"
            );
            let _ = writeln!(
                xml,
                "\t\t<PackageReference Include=\"GeneXus.Excel.Core\" Version=\"{}\" />",
                config.genexus_version
            );
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        if config.pdf_support {
            let _ = writeln!(xml, "\t<ItemGroup Condition=\"'$(PDFSupport)'=='true'\">");
            let _ = writeln!(
                xml,
                "\t\t<PackageReference Include=\"GeneXus.PdfReportsCS.Core\" Version=\"{}\" />",
                config.genexus_version
            );
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        if config.mail_support {
            let _ = writeln!(xml, "\t<ItemGroup Condition=\"'$(MailSupport)'=='true'\">");
            let _ = writeln!(
                xml,
                "\t\t<PackageReference Include=\"GeneXus.Mail.Core\" Version=\"{}\" />",
                config.genexus_version
            );
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        if config.postgresql_support {
            let _ = writeln!(
                xml,
                "\t<ItemGroup Condition=\"'$(PostgresqlSupport)'=='true'\">"
            );
            let _ = writeln!(
                xml,
                "\t\t<PackageReference Include=\"Npgsql\" Version=\"8.0.3\" />"
            );
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        // Project references
        if !config.projects.is_empty() {
            let _ = writeln!(xml, "\t<ItemGroup>");
            for proj in &config.projects {
                let _ = writeln!(xml, "\t\t<ProjectReference Include=\"{}\" />", proj.path);
            }
            let _ = writeln!(xml, "\t</ItemGroup>");
        }

        let _ = writeln!(xml, "\t<ItemGroup>");
        let _ = writeln!(xml, "\t\t<Compile Remove=\"**/*.cs\" />");
        let _ = writeln!(xml, "\t</ItemGroup>");

        let _ = writeln!(xml, "</Project>");

        xml
    }
}
