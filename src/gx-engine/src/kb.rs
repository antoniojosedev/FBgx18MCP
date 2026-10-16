//! Bridge from the independent portable object catalog to the admitted compiler.
//! Native KB decoding, general Rules/Events and provider fallback are absent.
//! A standalone Parm rule can supply the portable Procedure interface.
use crate::{
    catalog::{Catalog, ObjectId, ObjectKind, ReferenceKind},
    model::{DataType, Project},
};
use serde::Deserialize;
use std::collections::{BTreeMap, BTreeSet, VecDeque};

#[derive(Default, Deserialize)]
#[serde(deny_unknown_fields)]
struct Signature {
    #[serde(default)]
    params: Vec<String>,
    #[serde(default)]
    return_type: Option<DataType>,
}

/// Converts the reachable Procedure/Calls graph and explicit Uses SDT schemas. Unsupported
/// referenced kinds/parts fail before compilation; the catalog is never mutated.
pub fn project_from_catalog(
    catalog: &Catalog,
    entry: &ObjectId,
    date_two_digit_year_max: Option<u16>,
) -> Result<Project, String> {
    let entry_object = catalog
        .get(entry)
        .ok_or("catalog entry identity was not found")?;
    if entry_object.kind != ObjectKind::Procedure {
        return Err("catalog compiler entry must be a Procedure".into());
    }
    let mut queue = VecDeque::from([entry.clone()]);
    let mut seen = BTreeSet::new();
    let mut procedures = Vec::new();
    let mut record_types = Vec::new();
    let mut direct_calls = BTreeMap::new();
    while let Some(id) = queue.pop_front() {
        if !seen.insert(id.clone()) {
            continue;
        }
        if seen.len() > 1152 {
            return Err("catalog compile exceeds 1024 procedures plus 128 record schemas".into());
        }
        let object = catalog.get(&id).ok_or("catalog reference was not found")?;
        if object.kind == ObjectKind::StructuredDataType {
            if object.source_parts.len() != 1 || object.source_parts[0].name != "FIELDS" {
                return Err("portable SDT requires only a Fields part".into());
            }
            let mut used_schemas = BTreeSet::new();
            for reference in &object.references {
                let target = catalog
                    .get(&reference.target)
                    .ok_or("catalog reference was not found")?;
                if reference.kind != ReferenceKind::Uses
                    || target.kind != ObjectKind::StructuredDataType
                {
                    return Err("portable SDT dependencies must be Uses references to SDTs".into());
                }
                used_schemas.insert(target.name.to_ascii_uppercase());
                queue.push_back(reference.target.clone());
            }
            let fields: std::collections::BTreeMap<String, DataType> =
                serde_json::from_str(&object.source_parts[0].source)
                    .map_err(|e| format!("invalid portable Fields part: {e}"))?;
            for data_type in fields.values() {
                require_explicit_schema_use(data_type, &used_schemas, &object.name)?;
            }
            // Keep raw field JSON so Project can reject duplicate declarations.
            record_types.push(format!(
                "{{\"name\":{},\"fields\":{}}}",
                serde_json::to_string(&object.name).map_err(|e| e.to_string())?,
                object.source_parts[0].source
            ));
            if record_types.len() > 128 {
                return Err("catalog compile exceeds 128 record schemas".into());
            }
            continue;
        }
        if object.kind != ObjectKind::Procedure {
            return Err(format!(
                "catalog compiler does not support referenced kind {:?}",
                object.kind
            ));
        }
        let mut used_schemas = BTreeSet::new();
        let mut referenced_procedures = BTreeSet::new();
        for reference in &object.references {
            let target = catalog
                .get(&reference.target)
                .ok_or("catalog reference was not found")?;
            if !matches!(
                (&reference.kind, target.kind),
                (ReferenceKind::Calls, ObjectKind::Procedure)
                    | (ReferenceKind::Uses, ObjectKind::StructuredDataType)
            ) {
                return Err("catalog compiler requires Calls to Procedures or Uses to SDTs".into());
            }
            if reference.kind == ReferenceKind::Uses {
                used_schemas.insert(target.name.to_ascii_uppercase());
            } else {
                referenced_procedures.insert(target.name.to_ascii_uppercase());
            }
            queue.push_back(reference.target.clone());
        }
        direct_calls.insert(object.name.to_ascii_uppercase(), referenced_procedures);
        let mut source = None;
        let mut variables = "{}";
        let mut signature = Signature::default();
        let mut has_signature = false;
        let mut rules = None;
        for part in &object.source_parts {
            match part.name.as_str() {
                "SOURCE" => source = Some(part.source.as_str()),
                "VARIABLES" => variables = &part.source,
                "SIGNATURE" => {
                    has_signature = true;
                    signature = serde_json::from_str(&part.source)
                        .map_err(|e| format!("invalid portable Signature part: {e}"))?
                }
                "RULES" => rules = Some(part.source.as_str()),
                name => return Err(format!("unsupported catalog compiler source part {name}")),
            }
        }
        if let Some(rules) = rules {
            if has_signature {
                return Err("Procedure cannot supply both Rules and Signature parts".into());
            }
            signature.params = crate::rules::parameters(rules)
                .map_err(|e| format!("Procedure {}: {e}", object.name))?;
        }
        let source =
            source.ok_or_else(|| format!("Procedure {} has no Source part", object.name))?;
        let variable_types: std::collections::BTreeMap<String, DataType> =
            serde_json::from_str(variables)
                .map_err(|e| format!("invalid portable Variables part: {e}"))?;
        for data_type in variable_types.values() {
            require_explicit_schema_use(data_type, &used_schemas, &object.name)?;
        }
        if let Some(data_type) = signature.return_type.as_ref() {
            require_explicit_schema_use(data_type, &used_schemas, &object.name)?;
        }
        // Preserve raw Variables JSON until Project's duplicate-name rejecting
        // deserializer sees it; serde_json::Value would silently collapse duplicates.
        procedures.push(format!(
            "{{\"name\":{},\"params\":{},\"variables\":{},\"return_type\":{},\"source\":{}}}",
            serde_json::to_string(&object.name).map_err(|e| e.to_string())?,
            serde_json::to_string(&signature.params).map_err(|e| e.to_string())?,
            variables,
            serde_json::to_string(&signature.return_type).map_err(|e| e.to_string())?,
            serde_json::to_string(source).map_err(|e| e.to_string())?
        ));
        if procedures.len() > 1024 {
            return Err("catalog compile exceeds 1024 procedures".into());
        }
    }
    let project = Project::from_json(&format!(
        "{{\"format_version\":2,\"date_two_digit_year_max\":{},\"entry\":{},\"procedures\":[{}],\"record_types\":[{}]}}",
        serde_json::to_string(&date_two_digit_year_max).map_err(|e| e.to_string())?,
        serde_json::to_string(&entry_object.name).map_err(|e| e.to_string())?,
        procedures.join(","),
        record_types.join(",")
    ))?;
    for procedure in project.parse()? {
        let declared = direct_calls
            .get(&procedure.name)
            .ok_or("project procedure is missing its catalog Calls references")?;
        for callee in crate::dependencies::calls(&procedure.body) {
            let callee = callee.to_ascii_uppercase();
            if crate::frontend::is_typed_builtin(&callee) {
                continue;
            }
            if !declared.contains(&callee) {
                return Err(format!(
                    "Procedure {} calls {} without an explicit direct Calls reference",
                    procedure.name, callee
                ));
            }
        }
    }
    Ok(project)
}

fn require_explicit_schema_use(
    data_type: &DataType,
    used_schemas: &BTreeSet<String>,
    owner: &str,
) -> Result<(), String> {
    let schema = match data_type {
        DataType::Record { schema } | DataType::Collection { schema } => schema,
        _ => return Ok(()),
    };
    if used_schemas.contains(&schema.to_ascii_uppercase()) {
        return Ok(());
    }
    Err(format!(
        "{} in {} requires an explicit Uses reference to that SDT",
        schema, owner
    ))
}
