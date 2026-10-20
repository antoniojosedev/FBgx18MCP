//! Bridge from the independent portable object catalog to the admitted compiler.
//! Native KB decoding, general Rules/Events and provider fallback are absent.
//! A standalone Parm rule can supply the portable Procedure interface.
use crate::{
    catalog::{Catalog, KbObject, ObjectId, ObjectKind, ReferenceKind},
    model::{DataType, Project},
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, VecDeque};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(untagged)]
pub(crate) enum RawCatalogType {
    Concrete(DataType),
    BasedOnShort {
        #[serde(rename = "based_on")]
        target: String,
    },
    BasedOnTagged {
        #[serde(rename = "kind")]
        kind: String,
        #[serde(rename = "target")]
        target: String,
    },
}

#[derive(Default, Deserialize)]
#[serde(deny_unknown_fields)]
struct Signature {
    #[serde(default)]
    params: Vec<String>,
    #[serde(default)]
    return_type: Option<RawCatalogType>,
}

fn deserialize_raw_variables<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<BTreeMap<String, RawCatalogType>, D::Error> {
    struct Variables;
    impl<'de> serde::de::Visitor<'de> for Variables {
        type Value = BTreeMap<String, RawCatalogType>;
        fn expecting(&self, formatter: &mut std::fmt::Formatter) -> std::fmt::Result {
            formatter.write_str("a map of unique variable declarations")
        }
        fn visit_map<A: serde::de::MapAccess<'de>>(
            self,
            mut access: A,
        ) -> Result<Self::Value, A::Error> {
            let mut variables = BTreeMap::new();
            while let Some((name, raw_type)) = access.next_entry::<String, RawCatalogType>()? {
                if variables.insert(name, raw_type).is_some() {
                    return Err(serde::de::Error::custom("duplicate variable declaration"));
                }
                if variables.len() > 1024 {
                    return Err(serde::de::Error::custom(
                        "exceeds 1024 variable declarations",
                    ));
                }
            }
            Ok(variables)
        }
    }
    deserializer.deserialize_map(Variables)
}

pub(crate) fn resolve_domain_or_attribute_data_type(
    catalog: &Catalog,
    target_name: &str,
    visited: &mut BTreeSet<String>,
    depth: usize,
) -> Result<DataType, String> {
    if depth > 16 {
        return Err("domain based_on chain exceeds maximum depth of 16".into());
    }
    let canonical = target_name.to_ascii_uppercase();
    if !visited.insert(canonical.clone()) {
        return Err(format!(
            "cycle detected in based_on reference: {target_name}"
        ));
    }
    let object = catalog
        .objects()
        .find(|obj| obj.name.eq_ignore_ascii_case(target_name))
        .ok_or_else(|| {
            format!(
                "referenced Domain or Attribute '{}' was not found in catalog",
                target_name
            )
        })?;
    if object.kind != ObjectKind::Domain && object.kind != ObjectKind::Attribute {
        return Err(format!(
            "referenced object '{}' is not a Domain or Attribute",
            target_name
        ));
    }
    let type_part = object
        .source_parts
        .iter()
        .find(|p| p.name == "TYPE")
        .ok_or_else(|| format!("{:?} '{}' has no Type part", object.kind, object.name))?;
    let raw_type: RawCatalogType = serde_json::from_str(&type_part.source)
        .map_err(|e| format!("invalid Type part in {}: {e}", object.name))?;
    match raw_type {
        RawCatalogType::Concrete(data_type) => {
            crate::model::validate_data_type(&data_type)?;
            Ok(data_type)
        }
        RawCatalogType::BasedOnShort { target } | RawCatalogType::BasedOnTagged { target, .. } => {
            let target_obj = catalog
                .objects()
                .find(|obj| obj.name.eq_ignore_ascii_case(&target))
                .ok_or_else(|| format!("chained based_on target '{}' was not found", target))?;
            if object.kind == ObjectKind::Domain && target_obj.kind != ObjectKind::Domain {
                return Err(format!(
                    "domain '{}' cannot be based on non-domain '{}'",
                    object.name, target
                ));
            }
            if !object
                .references
                .iter()
                .any(|r| r.kind == ReferenceKind::BasedOn && r.target == target_obj.id)
            {
                return Err(format!(
                    "{:?} '{}' is based on '{}' without an explicit BasedOn reference",
                    object.kind, object.name, target
                ));
            }
            resolve_domain_or_attribute_data_type(catalog, &target, visited, depth + 1)
        }
    }
}

fn resolve_catalog_variables(
    raw_vars: BTreeMap<String, RawCatalogType>,
    catalog: &Catalog,
    based_on_targets: &BTreeSet<String>,
    used_schemas: &BTreeSet<String>,
    owner: &str,
    is_field: bool,
) -> Result<BTreeMap<String, DataType>, String> {
    let mut resolved = BTreeMap::new();
    for (name, raw_type) in raw_vars {
        let concrete_type = match raw_type {
            RawCatalogType::Concrete(data_type) => {
                require_explicit_schema_use(&data_type, used_schemas, owner)?;
                data_type
            }
            RawCatalogType::BasedOnShort { target }
            | RawCatalogType::BasedOnTagged { target, .. } => {
                if !based_on_targets.contains(&target.to_ascii_uppercase()) {
                    let kind_desc = if is_field { "field" } else { "variable" };
                    return Err(format!(
                        "{} {} in {} is based on {} without an explicit direct BasedOn reference",
                        kind_desc, name, owner, target
                    ));
                }
                let concrete = resolve_domain_or_attribute_data_type(
                    catalog,
                    &target,
                    &mut BTreeSet::new(),
                    0,
                )?;
                require_explicit_schema_use(&concrete, used_schemas, owner)?;
                concrete
            }
        };
        resolved.insert(name, concrete_type);
    }
    Ok(resolved)
}

/// A request that targets a web UI or not-generated object, or reaches one, is refused by name
/// and is never reported as built. The kinds come from [`ObjectKind::not_compile_unit_reason`].
fn refuse_unbuildable(object: &KbObject) -> Result<(), String> {
    if let Some(reason) = &object.refusal {
        return Err(format!(
            "{:?} '{}' is refused by the reader ({reason}): a build or run that targets or needs it is refused",
            object.kind, object.name
        ));
    }
    match object.kind.not_compile_unit_reason() {
        Some(reason) => Err(format!(
            "{:?} '{}' is not a compile unit ({reason}): a build or run that targets or needs it is refused",
            object.kind, object.name
        )),
        None => Ok(()),
    }
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
    refuse_unbuildable(entry_object)?;
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
        refuse_unbuildable(object)?;
        if object.kind == ObjectKind::Domain || object.kind == ObjectKind::Attribute {
            if object.source_parts.len() != 1 || object.source_parts[0].name != "TYPE" {
                return Err(format!(
                    "portable {:?} requires only a Type part",
                    object.kind
                ));
            }
            for reference in &object.references {
                let target = catalog
                    .get(&reference.target)
                    .ok_or("catalog reference was not found")?;
                refuse_unbuildable(target)?;
                if reference.kind != ReferenceKind::BasedOn
                    || (target.kind != ObjectKind::Domain && target.kind != ObjectKind::Attribute)
                {
                    return Err(
                        "Domain/Attribute dependencies must be BasedOn references to Domains or Attributes"
                            .into(),
                    );
                }
                queue.push_back(reference.target.clone());
            }
            continue;
        }
        if object.kind == ObjectKind::StructuredDataType {
            if object.source_parts.len() != 1 || object.source_parts[0].name != "FIELDS" {
                return Err("portable SDT requires only a Fields part".into());
            }
            let mut used_schemas = BTreeSet::new();
            let mut based_on_targets = BTreeSet::new();
            for reference in &object.references {
                let target = catalog
                    .get(&reference.target)
                    .ok_or("catalog reference was not found")?;
                refuse_unbuildable(target)?;
                if !matches!(
                    (&reference.kind, target.kind),
                    (ReferenceKind::Uses, ObjectKind::StructuredDataType)
                        | (ReferenceKind::BasedOn, ObjectKind::Domain)
                        | (ReferenceKind::BasedOn, ObjectKind::Attribute)
                ) {
                    return Err(
                        "portable SDT dependencies must be Uses references to SDTs or BasedOn to Domains/Attributes"
                            .into(),
                    );
                }
                if reference.kind == ReferenceKind::Uses {
                    used_schemas.insert(target.name.to_ascii_uppercase());
                } else {
                    based_on_targets.insert(target.name.to_ascii_uppercase());
                }
                queue.push_back(reference.target.clone());
            }
            let raw_fields: std::collections::BTreeMap<String, RawCatalogType> =
                serde_json::from_str(&object.source_parts[0].source)
                    .map_err(|e| format!("invalid portable Fields part: {e}"))?;
            let has_based_on = raw_fields.values().any(|t| {
                matches!(
                    t,
                    RawCatalogType::BasedOnShort { .. } | RawCatalogType::BasedOnTagged { .. }
                )
            });
            let field_source = if has_based_on {
                let resolved = resolve_catalog_variables(
                    raw_fields,
                    catalog,
                    &based_on_targets,
                    &used_schemas,
                    &object.name,
                    true,
                )?;
                serde_json::to_string(&resolved).map_err(|e| e.to_string())?
            } else {
                for data_type in raw_fields.values() {
                    if let RawCatalogType::Concrete(dt) = data_type {
                        require_explicit_schema_use(dt, &used_schemas, &object.name)?;
                    }
                }
                object.source_parts[0].source.clone()
            };
            record_types.push(format!(
                "{{\"name\":{},\"fields\":{}}}",
                serde_json::to_string(&object.name).map_err(|e| e.to_string())?,
                field_source
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
        let mut based_on_targets = BTreeSet::new();
        for reference in &object.references {
            let target = catalog
                .get(&reference.target)
                .ok_or("catalog reference was not found")?;
            // A Calls edge to a Module is the procedure's folder membership, not a call: the KB stores it as a
            // cross-reference of link type 2 to a module, and the SDK places the procedure in that module's
            // namespace. A module is never compiled, so the edge is skipped (not queued). Every other edge
            // keeps its check.
            if reference.kind == ReferenceKind::Calls && target.kind == ObjectKind::Module {
                continue;
            }
            refuse_unbuildable(target)?;
            if !matches!(
                (&reference.kind, target.kind),
                (ReferenceKind::Calls, ObjectKind::Procedure)
                    | (ReferenceKind::Uses, ObjectKind::StructuredDataType)
                    | (ReferenceKind::BasedOn, ObjectKind::Domain)
                    | (ReferenceKind::BasedOn, ObjectKind::Attribute)
            ) {
                return Err(
                    "catalog compiler requires Calls to Procedures, Uses to SDTs, or BasedOn to Domains/Attributes"
                        .into(),
                );
            }
            if reference.kind == ReferenceKind::Uses {
                used_schemas.insert(target.name.to_ascii_uppercase());
            } else if reference.kind == ReferenceKind::Calls {
                referenced_procedures.insert(target.name.to_ascii_uppercase());
            } else if reference.kind == ReferenceKind::BasedOn {
                based_on_targets.insert(target.name.to_ascii_uppercase());
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
                // The SDK writes a CONDITIONS part for every Procedure. An empty one holds no condition, so it
                // changes nothing the portable build compiles; a non-empty one is refused by name.
                "CONDITIONS" if part.source.trim().is_empty() => {}
                "CONDITIONS" => {
                    return Err(format!(
                        "Procedure {} has a non-empty CONDITIONS part; the portable build does not compile conditions",
                        object.name
                    ));
                }
                name => return Err(format!("unsupported catalog compiler source part {name}")),
            }
        }
        if let Some(rules) = rules {
            if has_signature {
                return Err("Procedure cannot supply both Rules and Signature parts".into());
            }
            signature.params = crate::rules::extract_procedure_parameters(rules)
                .map_err(|e| format!("Procedure {}: {e}", object.name))?;
        }
        let source =
            source.ok_or_else(|| format!("Procedure {} has no Source part", object.name))?;
        let raw_variables: std::collections::BTreeMap<String, RawCatalogType> = {
            let mut deserializer = serde_json::Deserializer::from_str(variables);
            deserialize_raw_variables(&mut deserializer)
                .map_err(|e| format!("invalid portable Variables part: {e}"))?
        };
        let resolved_variables = resolve_catalog_variables(
            raw_variables,
            catalog,
            &based_on_targets,
            &used_schemas,
            &object.name,
            false,
        )?;
        let resolved_return_type = match signature.return_type {
            Some(RawCatalogType::Concrete(data_type)) => {
                require_explicit_schema_use(&data_type, &used_schemas, &object.name)?;
                Some(data_type)
            }
            Some(
                RawCatalogType::BasedOnShort { target }
                | RawCatalogType::BasedOnTagged { target, .. },
            ) => {
                if !based_on_targets.contains(&target.to_ascii_uppercase()) {
                    return Err(format!(
                        "return type in {} is based on {} without an explicit direct BasedOn reference",
                        object.name, target
                    ));
                }
                let concrete = resolve_domain_or_attribute_data_type(
                    catalog,
                    &target,
                    &mut BTreeSet::new(),
                    0,
                )?;
                require_explicit_schema_use(&concrete, &used_schemas, &object.name)?;
                Some(concrete)
            }
            None => None,
        };
        let variables_json =
            serde_json::to_string(&resolved_variables).map_err(|e| e.to_string())?;
        procedures.push(format!(
            "{{\"name\":{},\"params\":{},\"variables\":{},\"return_type\":{},\"source\":{}}}",
            serde_json::to_string(&object.name).map_err(|e| e.to_string())?,
            serde_json::to_string(&signature.params).map_err(|e| e.to_string())?,
            variables_json,
            serde_json::to_string(&resolved_return_type).map_err(|e| e.to_string())?,
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
        DataType::Record { schema }
        | DataType::Collection { schema }
        | DataType::BaseCollection { schema, .. } => schema,
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
