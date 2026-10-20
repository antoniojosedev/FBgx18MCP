//! Runtime configuration files a GeneXus 18 .NET web model writes into `NetModel/web`: the log4net
//! configuration (`log.config`, `log.console.config`, and their `bin/` copies) and `CloudServices.config`.
//!
//! Mechanism, from the GeneXus 18 binaries (`Packages/Artech.Packages.GenexusBL.dll`):
//! - `LogTargetsService.GenerateLogConfig` renders `Log/<folder>/Log.config` with the `$`-delimited template
//!   engine. `Log/main.log.config` maps generator 41 (.NET Core) to folder `Dotnet`, `log.config` to the
//!   `*.web.config` appenders and `log.console.config` to the `*.console.config` appenders. `$LogAppender$`
//!   is the concatenation of the selected `LOG_OUTPUT` folder's appender files (`Log/Dotnet/RollingFile/`).
//!   `WriteTemplate` writes UTF-8 without BOM and also copies every file into `bin/`.
//! - `CloudServicesManagerService.GenerateRuntimeConfiguration` reads `<TYPE>_PROVIDER` for every
//!   `ServiceType`, and serialises the matching service (`Services/<Type>/<Name>/`) through an `XmlTextWriter`
//!   on UTF-8 with indentation: BOM, `<?xml version="1.0" encoding="utf-8"?>`, CRLF, no trailing newline.
//!
//! Settings this module does not understand are refused, not guessed: other log targets, other levels,
//! log formats, secure or procedure-bound notification handlers, other service providers.

/// The only `LOG_OUTPUT` whose appender is implemented here.
pub const LOG_OUTPUT_ROLLING_FILE: &str = "RollingFile";

/// `ServiceType` names as the model stores them (`<NAME>_PROVIDER`), in the SDK enum order.
const SERVICE_TYPES: [&str; 12] = [
    "NOTIFICATIONS",
    "STORAGE",
    "CACHE",
    "WEBNOTIFICATIONS",
    "MAPS",
    "MAPSAPPLE",
    "ADS",
    "LOG",
    "ANALYTICS",
    "REMOTECONFIG",
    "OBSERVABILITY",
    "SESSION",
];

const LOG_LEVELS: [&str; 7] = ["OFF", "FATAL", "ERROR", "WARN", "INFO", "DEBUG", "ALL"];

/// The web notification handler properties, in the order the SDK writes them.
const WEBNOTIFICATIONS_HANDLERS: [&str; 4] = [
    "WEBNOTIFICATIONS_RECEIVED_HANDLER",
    "WEBNOTIFICATIONS_ONOPEN_HANDLER",
    "WEBNOTIFICATIONS_ONCLOSE_HANDLER",
    "WEBNOTIFICATIONS_ONERROR_HANDLER",
];

/// Model values the runtime files depend on. Names are the model property names.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RuntimeSettings<'a> {
    /// `LOG_OUTPUT`; only [`LOG_OUTPUT_ROLLING_FILE`] is implemented.
    pub log_output: &'a str,
    /// `threshold` (the log level); one of `OFF`, `FATAL`, `ERROR`, `WARN`, `INFO`, `DEBUG`, `ALL`.
    pub threshold: &'a str,
    /// `LOG_USER_LEVEL`; same values as `threshold`.
    pub log_user_level: &'a str,
    /// `LOG_FORMAT`; only the default (empty) is implemented.
    pub log_format: &'a str,
    /// `TRACE_FILE`, the appender's file. Plain path characters only (no quotes, markup or `&`).
    pub trace_file: &'a str,
    /// `<NAME>_PROVIDER` for each service type the model sets, e.g. `("WEBNOTIFICATIONS", "INPROCESS")`.
    /// `NONE` means no service. A type that is absent from the list is treated as not set.
    pub providers: &'a [(&'a str, &'a str)],
    /// `WEBNOTIFICATIONS_*_HANDLER` values, in SDK order (received, open, close, error). Only empty values
    /// are implemented: a procedure reference is serialised through the KB name and is not verified here.
    pub webnotifications_handlers: [&'a str; 4],
}

/// One generated runtime file, with its path relative to the web model folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeFile {
    pub path: &'static str,
    pub bytes: Vec<u8>,
}

/// The files the SDK writes for `settings`, or an error naming the first setting it does not implement.
pub fn runtime_config_files(settings: &RuntimeSettings<'_>) -> Result<Vec<RuntimeFile>, String> {
    validate_log(settings)?;
    let web = log_config(WEB_APPENDER, settings);
    let console = log_config(CONSOLE_APPENDER, settings);
    let cloud = cloud_services_config(settings)?;
    Ok(vec![
        RuntimeFile {
            path: "log.config",
            bytes: web.clone().into_bytes(),
        },
        RuntimeFile {
            path: "bin/log.config",
            bytes: web.into_bytes(),
        },
        RuntimeFile {
            path: "log.console.config",
            bytes: console.clone().into_bytes(),
        },
        RuntimeFile {
            path: "bin/log.console.config",
            bytes: console.into_bytes(),
        },
        RuntimeFile {
            path: "CloudServices.config",
            bytes: cloud,
        },
    ])
}

fn validate_log(settings: &RuntimeSettings<'_>) -> Result<(), String> {
    if settings.log_output != LOG_OUTPUT_ROLLING_FILE {
        return Err(format!(
            "LOG_OUTPUT '{}' is not implemented; only {LOG_OUTPUT_ROLLING_FILE} is",
            settings.log_output
        ));
    }
    for (name, value) in [
        ("threshold", settings.threshold),
        ("LOG_USER_LEVEL", settings.log_user_level),
    ] {
        if !LOG_LEVELS.contains(&value) {
            return Err(format!("{name} '{value}' is not a log level"));
        }
    }
    if !settings.log_format.is_empty() {
        return Err(format!(
            "LOG_FORMAT '{}' is not implemented",
            settings.log_format
        ));
    }
    let trace_ok = !settings.trace_file.is_empty()
        && settings.trace_file.chars().all(|c| {
            c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-' | '/' | '\\' | ':' | ' ')
        });
    if !trace_ok {
        return Err(format!(
            "TRACE_FILE '{}' has characters this emitter does not escape",
            settings.trace_file
        ));
    }
    Ok(())
}

// The appenders are the SDK's `rollingfile.web.config` and `rollingfile.console.config` (one line per
// entry). The emitter prefixes each line with the two spaces of the template's `  $LogAppender$` line.
const WEB_APPENDER: &[&str] = &[
    " <appender  name=\"RollingFile\" type=\"log4net.Appender.RollingFileAppender\">",
    "      <file  value=\"{trace_file}\"/>",
    "      <appendToFile  value=\"true\"/>",
    "      <maximumFileSize  value=\"9000KB\"/>",
    "      <maxSizeRollBackups  value=\"4\"/>",
    "      <rollingStyle  value=\"Size\"/>",
    "      <layout  type=\"log4net.Layout.PatternLayout\">",
    "         <conversionPattern  value=\"%d{ISO8601} [%property{threadid}] %-5p %c - %m%n\"/>",
    "      </layout>",
    "      <lockingModel  type=\"log4net.Appender.FileAppender+ExclusiveLock\"/>",
    "</appender>",
];

const CONSOLE_APPENDER: &[&str] = &[
    " <appender  name=\"RollingFile\" type=\"log4net.Appender.RollingFileAppender\">",
    "      <file  value=\"{trace_file}\"/>",
    "      <appendToFile  value=\"true\"/>",
    "      <maximumFileSize  value=\"9000KB\"/>",
    "      <maxSizeRollBackups  value=\"4\"/>",
    "      <rollingStyle  value=\"Size\"/>",
    "      <layout  type=\"log4net.Layout.PatternLayout\">",
    "         <conversionPattern  value=\"%d{ISO8601} [%t] %-5p %c - %m%n\"/>",
    "      </layout>",
    "      <lockingModel  type=\"log4net.Appender.FileAppender+MinimalLock\"/>",
    "</appender>",
];

const LOG_HEAD: &[&str] = &[
    "<log4net>",
    "\t<appender name=\"ConsoleAppender\" type=\"log4net.Appender.ConsoleAppender\">",
    "\t\t<layout type=\"log4net.Layout.PatternLayout\">",
    "\t\t\t<conversionPattern value=\"%d{ISO8601} [%property{threadid}] %-5p %c - %m%n\"/>",
    "\t\t</layout>",
    "\t</appender>",
    "",
];

const LOG_TAIL: &[&str] = &[
    "",
    "\t<root>",
    "\t\t<level value=\"{threshold}\"/>",
    "\t\t<appender-ref ref=\"{output}\"/>",
    "\t</root>",
    "\t",
    "\t<!-- GeneXus User Log Logger 'GX_LOG_LEVEL_USER' !-->",
    "\t<logger name=\"GeneXusUserLog\" additivity=\"false\">",
    "\t\t<level value=\"{user_level}\" />",
    "\t\t<appender-ref ref=\"{output}\"/>",
    "\t</logger>",
    "",
    "\t<!-- Reference to AllAppenders in order to be able to change them dynamically via ENVVAR 'GX_LOG_OUTPUT' !-->",
    "\t<logger name=\"AllAppenders\" additivity=\"false\">",
    "\t\t<level value=\"off\" />",
    "\t\t<appender-ref ref=\"ConsoleAppender\"/>",
    "\t\t<appender-ref ref=\"{output}\"/>",
    "\t</logger>",
    "",
    "\t<!-- GeneXus Standard Classes Logging !-->",
    "\t<!-- ",
    "\t\t<logger name=\"GeneXus\" additivity=\"false\">",
    "\t\t\t<level value=\"{threshold}\" />",
    "\t\t\t<appender-ref ref=\"{output}\"/>",
    "\t\t</logger> ",
    "\t!-->",
    "\t",
    "</log4net>",
];

fn log_config(appender: &[&str], settings: &RuntimeSettings<'_>) -> String {
    let mut lines: Vec<String> = LOG_HEAD.iter().map(|line| line.to_string()).collect();
    lines.extend(appender.iter().map(|line| format!("  {line}")));
    lines.extend(LOG_TAIL.iter().map(|line| line.to_string()));
    let mut text = lines.join("\r\n");
    text.push_str("\r\n");
    text.replace("{trace_file}", settings.trace_file)
        .replace("{threshold}", settings.threshold)
        .replace("{user_level}", settings.log_user_level)
        .replace("{output}", settings.log_output)
}

fn cloud_services_config(settings: &RuntimeSettings<'_>) -> Result<Vec<u8>, String> {
    let mut services = Vec::new();
    for (name, provider) in settings.providers {
        if !SERVICE_TYPES.contains(name) {
            return Err(format!(
                "service type '{name}' is not a GeneXus 18 service type"
            ));
        }
        match (*name, *provider) {
            (_, "NONE") => {}
            ("WEBNOTIFICATIONS", "INPROCESS") => services.push(*provider),
            (_, other) => {
                return Err(format!("{name}_PROVIDER '{other}' is not implemented"));
            }
        }
    }
    if services.is_empty() {
        return Err(
            "no service is set; the SDK's empty CloudServices.config is not verified".to_string(),
        );
    }
    if settings
        .webnotifications_handlers
        .iter()
        .any(|value| !value.is_empty())
    {
        return Err("WEBNOTIFICATIONS handler procedures are not implemented".to_string());
    }
    let mut lines = vec![
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>".to_string(),
        "<Services>".to_string(),
        "  <Service>".to_string(),
        "    <Name>INPROCESS</Name>".to_string(),
        "    <Type>WebNotifications</Type>".to_string(),
        "    <ClassName>GeneXus.Http.WebSocket.WSHandler</ClassName>".to_string(),
        "    <Properties>".to_string(),
    ];
    for handler in WEBNOTIFICATIONS_HANDLERS {
        lines.push("      <Property>".to_string());
        lines.push(format!("        <Name>{handler}</Name>"));
        lines.push("        <Value />".to_string());
        lines.push("      </Property>".to_string());
    }
    lines.push("    </Properties>".to_string());
    lines.push("  </Service>".to_string());
    lines.push("</Services>".to_string());
    let mut bytes = vec![0xEF, 0xBB, 0xBF];
    bytes.extend_from_slice(lines.join("\r\n").as_bytes());
    Ok(bytes)
}
