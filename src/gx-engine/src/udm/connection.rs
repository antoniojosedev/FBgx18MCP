//! KnowledgeBase connection information parser for GeneXus physical storage.
//! Parses `knowledgebase.connection` XML files to determine SQL Server instance,
//! database name, authentication mode, and file locations.

#![forbid(unsafe_code)]

use roxmltree::Document;
use std::path::Path;

/// Authentication mode used to connect to the physical KB database.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum AuthMode {
    /// Windows Integrated Authentication (SSPI / Trusted_Connection).
    Integrated,
    /// SQL Server Authentication with username and optional password.
    SqlServer {
        user: String,
        password: Option<String>,
    },
}

/// Parsed connection information from `knowledgebase.connection`.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct KbConnectionInfo {
    /// SQL Server instance (e.g. `(LocalDB)\MSSQLLocalDB`, `localhost\SQLEXPRESS`).
    pub server_instance: String,
    /// Catalog/Database name (e.g. `GX_KB_KBTeste`).
    pub db_name: String,
    /// Authentication mode (Integrated vs SQL Server auth).
    pub auth_mode: AuthMode,
    /// Whether the database was created inside the KB directory.
    pub create_db_in_kb_folder: Option<bool>,
    /// Target directory path where the KB files are stored.
    pub directory: Option<String>,
    /// Primary data file (.mdf).
    pub data_file: Option<String>,
    /// Transaction log file (.ldf).
    pub log_file: Option<String>,
    /// Host name where the database was created.
    pub host_name: Option<String>,
    /// Whether to detach the database on close.
    pub detach_on_close: Option<bool>,
}

impl KbConnectionInfo {
    /// Parses connection information from raw XML content of `knowledgebase.connection`.
    pub fn parse_xml(xml: &str) -> Result<Self, String> {
        let clean_xml = xml.strip_prefix('\u{FEFF}').unwrap_or(xml);
        let doc =
            Document::parse(clean_xml).map_err(|e| format!("Malformed connection XML: {e}"))?;

        let root = doc.root_element();
        let conn_elem = if root
            .tag_name()
            .name()
            .eq_ignore_ascii_case("ConnectionInformation")
        {
            root
        } else {
            root.descendants()
                .find(|n| {
                    n.is_element()
                        && n.tag_name()
                            .name()
                            .eq_ignore_ascii_case("ConnectionInformation")
                })
                .ok_or_else(|| {
                    "Missing <ConnectionInformation> element in connection XML".to_string()
                })?
        };

        let mut server_instance: Option<String> = None;
        let mut db_name: Option<String> = None;
        let mut integrated_security: Option<bool> = None;
        let mut user_id: Option<String> = None;
        let mut password: Option<String> = None;
        let mut create_db_in_kb_folder: Option<bool> = None;
        let mut directory: Option<String> = None;
        let mut data_file: Option<String> = None;
        let mut log_file: Option<String> = None;
        let mut host_name: Option<String> = None;
        let mut detach_on_close: Option<bool> = None;

        for child in conn_elem.children().filter(|n| n.is_element()) {
            let tag = child.tag_name().name();
            let text = extract_node_text(child);

            if tag.eq_ignore_ascii_case("ServerInstance")
                || tag.eq_ignore_ascii_case("Server")
                || tag.eq_ignore_ascii_case("Instance")
            {
                server_instance = Some(text);
            } else if tag.eq_ignore_ascii_case("DBName") || tag.eq_ignore_ascii_case("Database") {
                db_name = Some(text);
            } else if tag.eq_ignore_ascii_case("IntegratedSecurity")
                || tag.eq_ignore_ascii_case("TrustedConnection")
            {
                integrated_security = parse_bool(&text);
            } else if tag.eq_ignore_ascii_case("UserId")
                || tag.eq_ignore_ascii_case("User")
                || tag.eq_ignore_ascii_case("UserName")
            {
                user_id = Some(text);
            } else if tag.eq_ignore_ascii_case("Password") || tag.eq_ignore_ascii_case("Pwd") {
                password = Some(text);
            } else if tag.eq_ignore_ascii_case("CreateDbInKbFolder") {
                create_db_in_kb_folder = parse_bool(&text);
            } else if tag.eq_ignore_ascii_case("Directory") {
                directory = Some(text);
            } else if tag.eq_ignore_ascii_case("DataFile") {
                data_file = Some(text);
            } else if tag.eq_ignore_ascii_case("LogFile") {
                log_file = Some(text);
            } else if tag.eq_ignore_ascii_case("HostName") || tag.eq_ignore_ascii_case("Host") {
                host_name = Some(text);
            } else if tag.eq_ignore_ascii_case("DetachOnClose") {
                detach_on_close = parse_bool(&text);
            }
        }

        let server_instance = server_instance
            .filter(|s| !s.trim().is_empty())
            .ok_or_else(|| "Missing or empty <ServerInstance> in connection XML".to_string())?;

        let db_name = db_name
            .filter(|s| !s.trim().is_empty())
            .ok_or_else(|| "Missing or empty <DBName> in connection XML".to_string())?;

        let auth_mode = if integrated_security.unwrap_or(true) {
            AuthMode::Integrated
        } else {
            AuthMode::SqlServer {
                user: user_id.unwrap_or_default(),
                password,
            }
        };

        Ok(Self {
            server_instance,
            db_name,
            auth_mode,
            create_db_in_kb_folder,
            directory,
            data_file,
            log_file,
            host_name,
            detach_on_close,
        })
    }

    /// Reads and parses `knowledgebase.connection` directly from a file path.
    pub fn from_file<P: AsRef<Path>>(path: P) -> Result<Self, String> {
        let p = path.as_ref();
        let content = std::fs::read_to_string(p)
            .map_err(|e| format!("Failed to read connection file {}: {e}", p.display()))?;
        Self::parse_xml(&content)
    }

    /// Reads and parses `knowledgebase.connection` from a Knowledge Base directory.
    pub fn from_kb_dir<P: AsRef<Path>>(kb_dir: P) -> Result<Self, String> {
        let conn_path = kb_dir.as_ref().join("knowledgebase.connection");
        Self::from_file(conn_path)
    }

    /// Returns `true` if the server instance is a Microsoft SQL Server LocalDB instance.
    pub fn is_local_db(&self) -> bool {
        self.server_instance
            .to_ascii_uppercase()
            .contains("LOCALDB")
    }

    /// Generates a standard ADO.NET / OLEDB style SQL connection string.
    pub fn connection_string(&self) -> String {
        match &self.auth_mode {
            AuthMode::Integrated => {
                format!(
                    "Server={};Database={};Integrated Security=SSPI;TrustServerCertificate=True;",
                    self.server_instance, self.db_name
                )
            }
            AuthMode::SqlServer { user, password } => {
                let pwd = match password {
                    Some(p) => format!("Password={p};"),
                    None => String::new(),
                };
                format!(
                    "Server={};Database={};User Id={};{}TrustServerCertificate=True;",
                    self.server_instance, self.db_name, user, pwd
                )
            }
        }
    }
}

fn extract_node_text(node: roxmltree::Node<'_, '_>) -> String {
    node.children()
        .filter(|n| n.is_text())
        .filter_map(|n| n.text())
        .collect::<String>()
        .trim()
        .to_string()
}

fn parse_bool(text: &str) -> Option<bool> {
    match text.trim().to_ascii_lowercase().as_str() {
        "true" | "1" | "yes" | "y" => Some(true),
        "false" | "0" | "no" | "n" => Some(false),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_valid_localdb_connection() {
        let xml = r#"
        <ConnectionInformation>
            <DBName>GX_KB_KBTeste</DBName>
            <IntegratedSecurity>True</IntegratedSecurity>
            <ServerInstance>(LocalDB)\MSSQLLocalDB</ServerInstance>
            <CreateDbInKbFolder>True</CreateDbInKbFolder>
            <Directory>C:\KBs\KBTeste</Directory>
            <DataFile>GX_KB_KBTeste.mdf</DataFile>
            <LogFile>GX_KB_KBTeste_log.ldf</LogFile>
            <HostName>DESKTOP-ULH7AS3</HostName>
            <DetachOnClose>False</DetachOnClose>
        </ConnectionInformation>
        "#;

        let info = KbConnectionInfo::parse_xml(xml).expect("parse valid connection");
        assert_eq!(info.server_instance, "(LocalDB)\\MSSQLLocalDB");
        assert_eq!(info.db_name, "GX_KB_KBTeste");
        assert_eq!(info.auth_mode, AuthMode::Integrated);
        assert_eq!(info.create_db_in_kb_folder, Some(true));
        assert_eq!(info.directory.as_deref(), Some("C:\\KBs\\KBTeste"));
        assert_eq!(info.data_file.as_deref(), Some("GX_KB_KBTeste.mdf"));
        assert_eq!(info.log_file.as_deref(), Some("GX_KB_KBTeste_log.ldf"));
        assert_eq!(info.host_name.as_deref(), Some("DESKTOP-ULH7AS3"));
        assert_eq!(info.detach_on_close, Some(false));
        assert!(info.is_local_db());
        assert!(
            info.connection_string()
                .contains("Integrated Security=SSPI")
        );
    }

    #[test]
    fn parse_sql_server_auth() {
        let xml = r#"
        <ConnectionInformation>
            <ServerInstance>192.168.1.100\SQLEXPRESS</ServerInstance>
            <DBName>MyRemoteKB</DBName>
            <IntegratedSecurity>False</IntegratedSecurity>
            <UserId>gxuser</UserId>
            <Password>Secret123!</Password>
        </ConnectionInformation>
        "#;

        let info = KbConnectionInfo::parse_xml(xml).expect("parse sql auth");
        assert_eq!(info.server_instance, "192.168.1.100\\SQLEXPRESS");
        assert_eq!(info.db_name, "MyRemoteKB");
        assert_eq!(
            info.auth_mode,
            AuthMode::SqlServer {
                user: "gxuser".to_string(),
                password: Some("Secret123!".to_string()),
            }
        );
        assert!(!info.is_local_db());
        assert!(info.connection_string().contains("User Id=gxuser"));
        assert!(info.connection_string().contains("Password=Secret123!"));
    }

    #[test]
    fn parse_missing_required_fields_fails_closed() {
        let no_server = r#"<ConnectionInformation><DBName>TestDB</DBName></ConnectionInformation>"#;
        assert!(KbConnectionInfo::parse_xml(no_server).is_err());

        let no_db = r#"<ConnectionInformation><ServerInstance>localhost</ServerInstance></ConnectionInformation>"#;
        assert!(KbConnectionInfo::parse_xml(no_db).is_err());

        let malformed = r#"<ConnectionInformation><DBName>unclosed"#;
        assert!(KbConnectionInfo::parse_xml(malformed).is_err());
    }
}
