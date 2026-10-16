use serde::{Deserialize, Serialize};

const FORMAT_VERSION: u32 = 1;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum DataType {
    Boolean {},
    Record { schema: String },
    Collection { schema: String },
    Date {},
    DateTime {},
    Numeric { precision: u8, scale: u8 },
    Character { length: u32 },
    Varchar { length: u32 },
}

const MAX_PROJECT_BYTES: usize = 1024 * 1024;
const MAX_PROCEDURES: usize = 1024;
const MAX_PARAMETERS: usize = 1024;
const MAX_IDENTIFIER_BYTES: usize = 128;

/// Explicit, portable input for the experimental compiler; this is not a native KB format.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Project {
    pub format_version: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub date_two_digit_year_max: Option<u16>,
    pub entry: String,
    pub procedures: Vec<ProcedureSource>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub record_types: Vec<RecordType>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecordType {
    pub name: String,
    #[serde(deserialize_with = "deserialize_variables")]
    pub fields: std::collections::BTreeMap<String, DataType>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProcedureSource {
    pub name: String,
    pub params: Vec<String>,
    #[serde(
        default,
        deserialize_with = "deserialize_variables",
        skip_serializing_if = "std::collections::BTreeMap::is_empty"
    )]
    pub variables: std::collections::BTreeMap<String, DataType>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub return_type: Option<DataType>,
    pub source: String,
}

fn deserialize_variables<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<std::collections::BTreeMap<String, DataType>, D::Error> {
    struct Variables;
    impl<'de> serde::de::Visitor<'de> for Variables {
        type Value = std::collections::BTreeMap<String, DataType>;
        fn expecting(&self, formatter: &mut std::fmt::Formatter) -> std::fmt::Result {
            formatter.write_str("a map of unique typed variable declarations")
        }
        fn visit_map<A: serde::de::MapAccess<'de>>(
            self,
            mut access: A,
        ) -> Result<Self::Value, A::Error> {
            let mut variables = std::collections::BTreeMap::new();
            while let Some((name, data_type)) = access.next_entry::<String, DataType>()? {
                if variables.insert(name, data_type).is_some() {
                    return Err(serde::de::Error::custom(
                        "duplicate typed variable declaration",
                    ));
                }
                if variables.len() > 1024 {
                    return Err(serde::de::Error::custom(
                        "procedure exceeds 1024 typed variable declarations",
                    ));
                }
            }
            Ok(variables)
        }
    }
    deserializer.deserialize_map(Variables)
}

impl Project {
    /// Loads and validates one bounded JSON project, canonicalizing identifiers to ASCII uppercase.
    pub fn from_json(input: &str) -> Result<Self, String> {
        if input.len() > MAX_PROJECT_BYTES {
            return Err(format!(
                "project JSON exceeds the {MAX_PROJECT_BYTES}-byte limit"
            ));
        }

        let mut project: Self = serde_json::from_str(input)
            .map_err(|error| format!("invalid project JSON: {error}"))?;
        project.normalize_and_validate()?;
        Ok(project)
    }

    /// Serializes a validated canonical project. Unknown or invalid data is never silently dropped.
    pub fn to_json(&self) -> Result<String, String> {
        let project = self.canonicalized()?;
        let output = serde_json::to_string(&project)
            .map_err(|error| format!("could not serialize project: {error}"))?;
        if output.len() > MAX_PROJECT_BYTES {
            return Err(format!(
                "serialized project exceeds the {MAX_PROJECT_BYTES}-byte limit"
            ));
        }
        Ok(output)
    }

    /// Parses each procedure body using the compiler frontend without rewriting source text.
    pub fn parse(&self) -> Result<Vec<crate::frontend::Procedure>, String> {
        let project = self.canonicalized()?;
        let mut parsed = Vec::with_capacity(project.procedures.len());
        for procedure in project.procedures {
            let body = crate::frontend::parse_body(&procedure.source)
                .map_err(|error| format!("procedure {}: {error}", procedure.name))?;
            parsed.push(crate::frontend::Procedure {
                name: procedure.name,
                params: procedure.params,
                body,
            });
        }
        Ok(parsed)
    }

    fn canonicalized(&self) -> Result<Self, String> {
        let mut project = self.clone();
        project.normalize_and_validate()?;
        Ok(project)
    }

    fn normalize_and_validate(&mut self) -> Result<(), String> {
        if self.format_version != FORMAT_VERSION && self.format_version != 2 {
            return Err(format!(
                "unsupported project format_version {}; expected 1 or 2",
                self.format_version
            ));
        }
        if let Some(max) = self.date_two_digit_year_max
            && (self.format_version != 2 || !(99..=9999).contains(&max))
        {
            return Err("date_two_digit_year_max requires format 2 and a value in 99..9999".into());
        }
        if self.procedures.len() > MAX_PROCEDURES {
            return Err(format!(
                "project has {} procedures; limit is {MAX_PROCEDURES}",
                self.procedures.len()
            ));
        }

        normalize_identifier(&mut self.entry, "entry")?;
        let mut total_text_bytes = self.entry.len();
        if !self.record_types.is_empty() && self.format_version != 2 {
            return Err("record types require format_version 2".into());
        }
        if self.record_types.len() > 128 {
            return Err("project exceeds 128 record schemas".into());
        }
        let mut schema_names = std::collections::HashSet::new();
        for schema in &mut self.record_types {
            normalize_identifier(&mut schema.name, "record schema")?;
            add_bounded_bytes(&mut total_text_bytes, schema.name.len())?;
            if !schema_names.insert(schema.name.clone()) {
                return Err("duplicate record schema".into());
            }
            if schema.fields.is_empty() || schema.fields.len() > 1024 {
                return Err("record schema needs 1..1024 supported scalar fields".into());
            }
            let mut fields = std::collections::BTreeMap::new();
            for (mut name, mut data_type) in std::mem::take(&mut schema.fields) {
                normalize_identifier(&mut name, "record field")?;
                add_bounded_bytes(&mut total_text_bytes, name.len())?;
                if let DataType::Record { schema } | DataType::Collection { schema } =
                    &mut data_type
                {
                    normalize_identifier(schema, "field schema reference")?;
                }
                validate_data_type(&data_type)?;
                if fields.insert(name, data_type).is_some() {
                    return Err("duplicate record field".into());
                }
            }
            schema.fields = fields;
        }
        validate_record_graph(&self.record_types)?;
        let mut procedure_names = std::collections::HashSet::with_capacity(self.procedures.len());

        for procedure in &mut self.procedures {
            normalize_identifier(&mut procedure.name, "procedure name")?;
            if !procedure_names.insert(procedure.name.clone()) {
                return Err(format!(
                    "duplicate procedure name '{}' (names are case-insensitive)",
                    procedure.name
                ));
            }
            add_bounded_bytes(&mut total_text_bytes, procedure.name.len())?;
            add_bounded_bytes(&mut total_text_bytes, procedure.source.len())?;
            if let Some(data_type) = &procedure.return_type {
                validate_data_type(data_type)?;
            }
            if self.format_version == 1
                && (!procedure.variables.is_empty() || procedure.return_type.is_some())
            {
                return Err("typed variable declarations require format_version 2".into());
            }
            if procedure.variables.len() > 1024 {
                return Err("procedure exceeds 1024 typed variable declarations".into());
            }
            let mut variables = std::collections::BTreeMap::new();
            for (mut name, mut data_type) in std::mem::take(&mut procedure.variables) {
                if let DataType::Record { schema } | DataType::Collection { schema } =
                    &mut data_type
                {
                    normalize_identifier(schema, "record schema reference")?;
                    if !schema_names.contains(schema) {
                        return Err(format!("unknown record schema {schema}"));
                    }
                }
                normalize_identifier(&mut name, "variable name")?;
                add_bounded_bytes(&mut total_text_bytes, name.len())?;
                validate_data_type(&data_type)?;
                if variables.insert(name, data_type).is_some() {
                    return Err("duplicate typed variable ignoring ASCII case".into());
                }
            }
            procedure.variables = variables;

            if procedure.params.len() > MAX_PARAMETERS {
                return Err(format!(
                    "procedure '{}' has {} parameters; limit is {MAX_PARAMETERS}",
                    procedure.name,
                    procedure.params.len()
                ));
            }
            let mut parameter_names =
                std::collections::HashSet::with_capacity(procedure.params.len());
            for parameter in &mut procedure.params {
                let (mode, name) = parse_parameter(parameter)?;
                if parameter.contains(':') && self.format_version != 2 {
                    return Err("parameter modes require format_version 2".into());
                }
                let mut name = name.to_owned();
                normalize_identifier(&mut name, "parameter name")?;
                if !parameter_names.insert(name.clone()) {
                    return Err(format!(
                        "procedure '{}' has duplicate parameter '{}' (names are case-insensitive)",
                        procedure.name, name
                    ));
                }
                *parameter = match mode {
                    ParamMode::In => format!("IN:{name}"),
                    ParamMode::Out => format!("OUT:{name}"),
                    ParamMode::InOut => name,
                };
                add_bounded_bytes(&mut total_text_bytes, parameter.len())?;
            }
        }

        if !procedure_names.contains(&self.entry) {
            return Err(format!(
                "entry procedure '{}' is not declared in procedures",
                self.entry
            ));
        }
        Ok(())
    }
}

/// GeneXus `Parm` modes. A parameter is written `Name`, `in:Name`, `out:Name` or
/// `inout:Name`; the canonical form drops `inout:` (the GeneXus default) and uppercases.
/// Native KBTeste probes (plan 129): `in` is copied in only; `out` starts empty in the
/// callee and is copied back; `inout` is copied in and back, but only when the argument
/// is a variable (a literal or expression is passed by value). Copy-back runs in
/// parameter order, so one variable passed to several modes ends with the last write.
/// Format 1 rejects explicit modes and keeps its by-value calls.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParamMode {
    In,
    Out,
    InOut,
}

/// Splits a parameter entry into its mode and name without validating the name.
pub fn parse_parameter(parameter: &str) -> Result<(ParamMode, &str), String> {
    let Some((prefix, name)) = parameter.split_once(':') else {
        return Ok((ParamMode::InOut, parameter));
    };
    let mode = match prefix.to_ascii_lowercase().as_str() {
        "in" => ParamMode::In,
        "out" => ParamMode::Out,
        "inout" => ParamMode::InOut,
        _ => {
            return Err(format!(
                "unknown parameter mode '{prefix}'; use in, out or inout"
            ));
        }
    };
    Ok((mode, name))
}

pub(crate) fn validate_data_type(data_type: &DataType) -> Result<(), String> {
    match data_type {
        DataType::Boolean {} | DataType::Date {} | DataType::DateTime {} => Ok(()),
        DataType::Record { schema } | DataType::Collection { schema } => {
            if !is_identifier(schema) {
                return Err("record schema must be an ASCII identifier".into());
            }
            Ok(())
        }
        DataType::Numeric { precision, scale } => {
            crate::types::NumericType::new(*precision, *scale).map(|_| ())
        }
        DataType::Character { length } | DataType::Varchar { length } => {
            if !(1..=65535).contains(length) {
                return Err("text declaration length must be 1..65535".into());
            }
            Ok(())
        }
    }
}

fn validate_record_graph(record_types: &[RecordType]) -> Result<(), String> {
    use std::collections::BTreeMap;
    let schemas: BTreeMap<&str, &RecordType> =
        record_types.iter().map(|s| (s.name.as_str(), s)).collect();
    fn visit<'a>(
        name: &'a str,
        schemas: &BTreeMap<&'a str, &'a RecordType>,
        states: &mut BTreeMap<&'a str, u8>,
    ) -> Result<(), String> {
        match states.get(name) {
            Some(1) => return Err("recursive record/collection schemas are not admitted".into()),
            Some(2) => return Ok(()),
            _ => {}
        }
        let schema = schemas
            .get(name)
            .ok_or_else(|| format!("unknown record schema {name}"))?;
        states.insert(name, 1);
        for data_type in schema.fields.values() {
            if let DataType::Record { schema } | DataType::Collection { schema } = data_type {
                visit(schema, schemas, states)?;
            }
        }
        states.insert(name, 2);
        Ok(())
    }
    let mut states = BTreeMap::new();
    for name in schemas.keys() {
        visit(name, &schemas, &mut states)?;
    }
    Ok(())
}

fn normalize_identifier(identifier: &mut str, field: &str) -> Result<(), String> {
    if !is_identifier(identifier) {
        return Err(format!(
            "{field} must be an ASCII identifier of 1 to {MAX_IDENTIFIER_BYTES} characters (letter or underscore first; then letters, digits, or underscores)"
        ));
    }
    identifier.make_ascii_uppercase();
    Ok(())
}

pub(crate) fn is_identifier(value: &str) -> bool {
    let bytes = value.as_bytes();
    if bytes.is_empty() || bytes.len() > MAX_IDENTIFIER_BYTES {
        return false;
    }
    let first = bytes[0];
    if !(first.is_ascii_alphabetic() || first == b'_') {
        return false;
    }
    bytes[1..]
        .iter()
        .all(|byte| byte.is_ascii_alphanumeric() || *byte == b'_')
}

fn add_bounded_bytes(total: &mut usize, amount: usize) -> Result<(), String> {
    *total = total
        .checked_add(amount)
        .ok_or_else(|| "project text size overflow".to_owned())?;
    if *total > MAX_PROJECT_BYTES {
        return Err(format!(
            "project text exceeds the {MAX_PROJECT_BYTES}-byte limit"
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{MAX_PARAMETERS, MAX_PROCEDURES, MAX_PROJECT_BYTES, Project};

    const DEMO: &str = include_str!("../fixtures/demo.json");

    fn project_json(entry: &str, procedures: &str) -> String {
        format!(r#"{{"format_version":1,"entry":"{entry}","procedures":{procedures}}}"#)
    }

    #[test]
    fn loads_demo_and_canonicalizes_identifiers_without_changing_source() {
        let project = Project::from_json(DEMO).expect("demo fixture should be valid");
        assert_eq!(project.entry, "MAIN");
        assert_eq!(project.procedures[0].name, "MAIN");
        assert_eq!(project.procedures[1].name, "DOUBLE");
        assert_eq!(project.procedures[1].params, ["NUMBER"]);
        assert!(project.procedures[0].source.contains("double(&number)"));
    }

    #[test]
    fn rejects_malformed_json_and_unknown_fields() {
        assert!(
            Project::from_json("{")
                .unwrap_err()
                .contains("invalid project JSON")
        );
        let unknown = r#"{"format_version":1,"entry":"main","procedures":[],"extra":true}"#;
        assert!(
            Project::from_json(unknown)
                .unwrap_err()
                .contains("unknown field")
        );
        let nested_unknown = project_json(
            "main",
            r#"[{"name":"main","params":[],"source":"Return 0","extra":1}]"#,
        );
        assert!(
            Project::from_json(&nested_unknown)
                .unwrap_err()
                .contains("unknown field")
        );
    }

    #[test]
    fn rejects_unknown_format_versions() {
        for version in [0, 3, u32::MAX] {
            let input = format!(
                r#"{{"format_version":{version},"entry":"main","procedures":[{{"name":"main","params":[],"source":"Return 0"}}]}}"#
            );
            assert!(
                Project::from_json(&input)
                    .unwrap_err()
                    .contains("unsupported project format_version")
            );
        }
    }

    #[test]
    fn validates_identifiers_entry_and_case_insensitive_uniqueness() {
        let invalid = project_json(
            "main",
            r#"[{"name":"bad-name","params":[],"source":"Return 0"}]"#,
        );
        assert!(
            Project::from_json(&invalid)
                .unwrap_err()
                .contains("procedure name")
        );

        let duplicate_procedures = project_json(
            "main",
            r#"[{"name":"main","params":[],"source":"Return 0"},{"name":"MAIN","params":[],"source":"Return 1"}]"#,
        );
        assert!(
            Project::from_json(&duplicate_procedures)
                .unwrap_err()
                .contains("duplicate procedure name")
        );

        let duplicate_parameters = project_json(
            "main",
            r#"[{"name":"main","params":["value","VALUE"],"source":"Return 0"}]"#,
        );
        assert!(
            Project::from_json(&duplicate_parameters)
                .unwrap_err()
                .contains("duplicate parameter")
        );

        let missing_entry = project_json(
            "other",
            r#"[{"name":"main","params":[],"source":"Return 0"}]"#,
        );
        assert!(
            Project::from_json(&missing_entry)
                .unwrap_err()
                .contains("entry procedure")
        );

        let too_long = "a".repeat(129);
        let invalid_length = project_json(
            &too_long,
            r#"[{"name":"main","params":[],"source":"Return 0"}]"#,
        );
        assert!(Project::from_json(&invalid_length).is_err());

        let non_ascii = project_json(
            "máin",
            r#"[{"name":"máin","params":[],"source":"Return 0"}]"#,
        );
        assert!(Project::from_json(&non_ascii).is_err());
    }

    #[test]
    fn enforces_procedure_parameter_and_project_size_limits() {
        let procedures = (0..=MAX_PROCEDURES)
            .map(|index| format!(r#"{{"name":"p{index}","params":[],"source":""}}"#))
            .collect::<Vec<_>>()
            .join(",");
        let too_many_procedures = project_json("p0", &format!("[{procedures}]"));
        assert!(
            Project::from_json(&too_many_procedures)
                .unwrap_err()
                .contains("limit is 1024")
        );

        let parameters = (0..=MAX_PARAMETERS)
            .map(|index| format!(r#""v{index}""#))
            .collect::<Vec<_>>()
            .join(",");
        let too_many_parameters = project_json(
            "main",
            &format!(r#"[{{"name":"main","params":[{parameters}],"source":""}}]"#),
        );
        assert!(
            Project::from_json(&too_many_parameters)
                .unwrap_err()
                .contains("parameters; limit is 1024")
        );

        let padded = format!(
            "{}{}",
            DEMO.trim_end(),
            " ".repeat(MAX_PROJECT_BYTES - DEMO.trim_end().len())
        );
        assert_eq!(padded.len(), MAX_PROJECT_BYTES);
        assert!(Project::from_json(&padded).is_ok());
        assert!(
            Project::from_json(&(padded + " "))
                .unwrap_err()
                .contains("exceeds the 1048576-byte limit")
        );
    }

    #[test]
    fn serialization_enforces_the_project_size_limit_after_json_escaping() {
        let project = Project {
            format_version: 1,
            date_two_digit_year_max: None,
            record_types: Vec::new(),
            entry: "main".into(),
            procedures: vec![super::ProcedureSource {
                name: "main".into(),
                params: vec![],
                variables: Default::default(),
                return_type: None,
                source: "\0".repeat(180_000),
            }],
        };
        assert!(
            project
                .to_json()
                .unwrap_err()
                .contains("serialized project exceeds the 1048576-byte limit")
        );
    }

    #[test]
    fn round_trip_is_canonical_and_preserves_source_exactly() {
        let input = r#"{"format_version":1,"entry":"Main","procedures":[{"name":"mAiN","params":[],"source":"value = 4\r\nReturn value"},{"name":"DoUble","params":["nUmBeR"],"source":"Return number * 2"}]}"#;
        let project = Project::from_json(input).expect("valid project");
        let original_source = project.procedures[0].source.clone();
        let encoded = project.to_json().expect("valid serialization");
        assert!(encoded.contains("\"entry\":\"MAIN\""));
        let decoded = Project::from_json(&encoded).expect("round trip");
        assert_eq!(decoded, project);
        assert_eq!(decoded.procedures[0].source, original_source);
    }

    #[test]
    fn parses_procedure_bodies_and_returns_canonical_metadata() {
        let project = Project::from_json(DEMO).expect("demo fixture should be valid");
        let procedures = project.parse().expect("demo body should parse");
        assert_eq!(procedures[0].name, "MAIN");
        assert_eq!(procedures[1].name, "DOUBLE");
        assert_eq!(procedures[1].params, ["NUMBER"]);
    }
}
