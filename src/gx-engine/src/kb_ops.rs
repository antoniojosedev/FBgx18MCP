//! Product-facing Knowledge Base operations served directly from UDM storage.
//!
//! This is the library backing the `kb-read` / `kb-query` / `kb-write` CLI
//! commands. The MCP Gateway's engine backend (`Engine.Backend=gxengine`)
//! invokes those commands for the `udm_storage_access` parity capability, which
//! registers `genexus_read`, `genexus_query` and `genexus_edit` as consumers
//! (`src/GxMcp.Gateway/EngineCapabilityRegistry.cs`).
//!
//! The admitted space is deliberately narrow and fail-closed: reads serve
//! existing objects and their stored parts, queries filter by exact object name,
//! object type, and recorded references, and writes replace one known part of an
//! existing object. Anything outside that space is an error the caller must
//! handle explicitly - never an approximation.
//!
//! Writes are refused outright against a live Knowledge Base database: the
//! writer's physical persistence is not implemented, so reporting success would
//! claim an edit that never reaches the KB. Live reads and queries go through
//! `sqlcmd` and fail closed when the database cannot be read.

#![forbid(unsafe_code)]

use crate::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use crate::udm::reader::{ObjectRow, UdmReader};
use crate::udm::writer::UdmWriter;
use crate::udm::{UdmClient, UdmPartKind};
use serde::Serialize;
use std::collections::HashSet;
use std::sync::Arc;

/// Query page size when the caller does not select one.
pub const DEFAULT_QUERY_LIMIT: usize = 50;
/// Hard cap on one query page, mirroring the Gateway's response budget.
pub const MAX_QUERY_LIMIT: usize = 200;

/// One decoded source part of an object.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct PartPayload {
    pub name: String,
    pub source: String,
}

/// Result of reading one object from the active model.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ReadResult {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
    pub parts: Vec<PartPayload>,
    /// Requested parts the object does not have (names as requested).
    pub missing_parts: Vec<String>,
    /// Every stored part name of the object, so a caller can recover from a
    /// missing part without a second invocation.
    pub available_parts: Vec<String>,
}

/// One object row of a query result.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct QueryObject {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
}

/// Result of one KB object query.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct QueryResult {
    pub objects: Vec<QueryObject>,
    /// Matches before the page limit was applied.
    pub total: usize,
    pub truncated: bool,
}

/// Result of one single-part write.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct WriteResult {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
    pub part: String,
    pub version_id: i32,
    pub created: bool,
    pub bytes: usize,
}

/// GeneXus-facing type name for an `ObjectKind` (what the MCP tools report).
pub fn type_name(kind: ObjectKind) -> &'static str {
    match kind {
        ObjectKind::Procedure => "Procedure",
        ObjectKind::Transaction => "Transaction",
        ObjectKind::WebPanel => "WebPanel",
        ObjectKind::StructuredDataType => "SDT",
        ObjectKind::DataProvider => "DataProvider",
        ObjectKind::DataSelector => "DataSelector",
        ObjectKind::Domain => "Domain",
        ObjectKind::Attribute => "Attribute",
        ObjectKind::Table => "Table",
        ObjectKind::Module => "Module",
        ObjectKind::DesignSystem => "DesignSystem",
        ObjectKind::DataView => "DataView",
    }
}

/// Parses a GeneXus-facing type name (case-insensitive) into an `ObjectKind`.
pub fn kind_from_type_name(name: &str) -> Option<ObjectKind> {
    match name.trim().to_ascii_uppercase().as_str() {
        "PROCEDURE" | "PROC" => Some(ObjectKind::Procedure),
        "TRANSACTION" | "TRN" => Some(ObjectKind::Transaction),
        "WEBPANEL" | "WEB_PANEL" => Some(ObjectKind::WebPanel),
        "SDT" | "STRUCTUREDDATATYPE" | "STRUCTURED_DATA_TYPE" => {
            Some(ObjectKind::StructuredDataType)
        }
        "DATAPROVIDER" => Some(ObjectKind::DataProvider),
        "DATASELECTOR" => Some(ObjectKind::DataSelector),
        "DOMAIN" => Some(ObjectKind::Domain),
        "ATTRIBUTE" => Some(ObjectKind::Attribute),
        "TABLE" => Some(ObjectKind::Table),
        "MODULE" => Some(ObjectKind::Module),
        "DESIGNSYSTEM" => Some(ObjectKind::DesignSystem),
        "DATAVIEW" => Some(ObjectKind::DataView),
        _ => None,
    }
}

/// Canonical, human-readable part name for a known part kind.
fn part_friendly_name(kind: &UdmPartKind) -> &'static str {
    match kind {
        UdmPartKind::Source => "Source",
        UdmPartKind::Rules => "Rules",
        UdmPartKind::Events => "Events",
        UdmPartKind::Conditions => "Conditions",
        UdmPartKind::Variables => "Variables",
        UdmPartKind::Structure => "Structure",
        UdmPartKind::WebForm => "WebForm",
        UdmPartKind::PatternInstance => "PatternInstance",
        UdmPartKind::Unknown(_) => "Unknown",
    }
}

/// The type GUID native storage writes between `StyleClass:` and a design-system class name.
const DESIGN_CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";
const STYLE_CLASS_PREFIX: &str = "StyleClass:";

/// The text `kb-read` returns for a stored source part: the SDK's projection of it.
///
/// Native storage names a design-system class `StyleClass:<type guid><Name>`; the SDK shows
/// `StyleClass:<Name>`. Only that wrapper is projected, and only in code: string literals (`"..."`,
/// `'...'`, which end with their line), `//` comments and `/* */` comments keep their stored text, as does
/// any other GUID, a wrapper with no name after it, and a keyword that ends a longer identifier. The
/// stored part itself is untouched - the code generators read it through `UdmReader` with the wrapper.
fn project_sdk_source(part_name: &str, source: &str) -> String {
    if !UdmPartKind::from_name(part_name).is_tokenized_source() {
        return source.to_string();
    }
    let bytes = source.as_bytes();
    let line_end = |from: usize| source[from..].find('\n').map_or(bytes.len(), |n| from + n);
    let mut out = String::with_capacity(source.len());
    let mut copied = 0;
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            quote @ (b'"' | b'\'') => {
                let mut end = i + 1;
                while end < bytes.len() && bytes[end] != b'\n' {
                    // Rules double-quoted literals consume escapes before testing the terminator.
                    if quote == b'"'
                        && bytes[end] == b'\\'
                        && bytes.get(end + 1).is_some_and(|&byte| byte != b'\n')
                    {
                        end += 2;
                    } else if bytes[end] == quote {
                        end += 1;
                        break;
                    } else {
                        end += 1;
                    }
                }
                i = end;
            }
            b'/' if bytes.get(i + 1) == Some(&b'/') => i = line_end(i),
            b'/' if bytes.get(i + 1) == Some(&b'*') => {
                i = source[i + 2..]
                    .find("*/")
                    .map_or(bytes.len(), |n| i + 2 + n + 2);
            }
            _ if is_design_class_wrapper(source, i) => {
                let guid_start = i + STYLE_CLASS_PREFIX.len();
                out.push_str(&source[copied..guid_start]);
                i = guid_start + DESIGN_CLASS_TYPE.len();
                copied = i;
            }
            _ => i += 1,
        }
    }
    out.push_str(&source[copied..]);
    out
}

/// Whether `StyleClass:<design class type guid>` starts at byte `at` of `source`, is followed by a class
/// name, and is not the tail of a longer identifier.
fn is_design_class_wrapper(source: &str, at: usize) -> bool {
    let is_identifier = |byte: u8| byte.is_ascii_alphanumeric() || byte == b'_';
    let guid_start = at + STYLE_CLASS_PREFIX.len();
    let guid_end = guid_start + DESIGN_CLASS_TYPE.len();
    let (Some(keyword), Some(guid)) =
        (source.get(at..guid_start), source.get(guid_start..guid_end))
    else {
        return false;
    };
    keyword.eq_ignore_ascii_case(STYLE_CLASS_PREFIX)
        && guid.eq_ignore_ascii_case(DESIGN_CLASS_TYPE)
        && !source.as_bytes()[..at]
            .last()
            .is_some_and(|&byte| is_identifier(byte))
        && source
            .as_bytes()
            .get(guid_end)
            .is_some_and(|&byte| is_identifier(byte))
}

fn shared_client(client: &UdmClient) -> Arc<UdmClient> {
    Arc::new(client.clone())
}

/// Reads one existing object, optionally restricted to requested part names
/// (case-insensitive). Only the requested parts are decoded; a requested part
/// the object does not have is reported in `missing_parts` instead of failing
/// the read.
pub fn read_object(
    client: &UdmClient,
    name: &str,
    parts: Option<&[String]>,
) -> Result<ReadResult, String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err("object name is required".to_string());
    }

    let reader = UdmReader::new(shared_client(client));
    let Some(loaded) = reader.load_object_by_name(trimmed, parts)? else {
        return Err(format!(
            "object '{trimmed}' was not found in the active model"
        ));
    };
    // A refused object is never read back as if it were complete (for example, an empty Procedure).
    if let Some(reason) = &loaded.object.refusal {
        return Err(format!(
            "object '{}' is refused by the reader: {reason}",
            loaded.object.name
        ));
    }

    let selected: Vec<PartPayload> = loaded
        .object
        .source_parts
        .iter()
        .map(|part| PartPayload {
            name: part.name.clone(),
            source: project_sdk_source(&part.name, &part.source),
        })
        .collect();

    let mut missing: Vec<String> = Vec::new();
    if let Some(requested) = parts {
        for want in requested {
            let want = want.trim();
            if want.is_empty() {
                continue;
            }
            let wanted_upper = want.to_ascii_uppercase();
            let stored = loaded
                .stored_parts
                .iter()
                .any(|part| part.to_ascii_uppercase() == wanted_upper);
            if !stored && !missing.iter().any(|m| m == want) {
                missing.push(want.to_string());
            }
        }
    }

    Ok(ReadResult {
        name: loaded.object.name.clone(),
        object_type: type_name(loaded.object.kind).to_string(),
        parts: selected,
        missing_parts: missing,
        available_parts: loaded.stored_parts,
    })
}

mod query_classification;

/// Queries active objects by exact name, type and/or recorded reference.
/// Object descriptors, WebPanel version properties and references are read — no stored part is
/// decoded. Results list Module objects first, then sort by ordinal name (the
/// SDK search order), and are paged by `limit`.
pub fn query_objects(
    client: &UdmClient,
    name: Option<&str>,
    object_type: Option<&str>,
    used_by: Option<&str>,
    limit: usize,
) -> Result<QueryResult, String> {
    let kind_filter = match object_type {
        None => None,
        Some(raw) if raw.trim().is_empty() => None,
        Some(raw) => {
            Some(kind_from_type_name(raw).ok_or_else(|| format!("unknown object type '{raw}'"))?)
        }
    };
    let name_filter = name.map(str::trim).filter(|value| !value.is_empty());
    let used_by_filter = used_by.map(str::trim).filter(|value| !value.is_empty());
    let limit = if limit == 0 {
        DEFAULT_QUERY_LIMIT
    } else {
        limit.min(MAX_QUERY_LIMIT)
    };

    let reader = UdmReader::new(shared_client(client));
    let rows = reader.list_objects()?;

    // `usedby:` resolves the named target once, then keeps the objects whose
    // cross-references point at it.
    let referencing: Option<HashSet<(i32, i32)>> = match used_by_filter {
        None => None,
        Some(target) => {
            let Some(target_row) = rows
                .iter()
                .find(|row| row.name.eq_ignore_ascii_case(target))
            else {
                return Ok(QueryResult {
                    objects: Vec::new(),
                    total: 0,
                    truncated: false,
                });
            };
            let references = reader.references_for_model()?;
            Some(
                references
                    .iter()
                    .filter(|reference| {
                        reference.to_entity_type_id == target_row.entity_type_id
                            && reference.to_entity_id == target_row.entity_id
                    })
                    .map(|reference| (reference.from_entity_type_id, reference.from_entity_id))
                    .collect(),
            )
        }
    };

    let mut matched: Vec<(&ObjectRow, &'static str)> = rows
        .iter()
        .filter(|row| {
            if let Some(wanted) = name_filter {
                if !row.name.eq_ignore_ascii_case(wanted) {
                    return false;
                }
            }
            if let Some(kind) = kind_filter {
                if row.kind != kind {
                    return false;
                }
            }
            if let Some(referencing) = &referencing {
                if !referencing.contains(&(row.entity_type_id, row.entity_id)) {
                    return false;
                }
            }
            true
        })
        .map(|row| {
            query_classification::query_object_type(client, row)
                .map(|object_type| (row, object_type))
        })
        .collect::<Result<_, _>>()?;
    if kind_filter == Some(ObjectKind::WebPanel) {
        matched.retain(|(_, object_type)| *object_type == "WebPanel");
    }

    // The SDK search orders a term-less query by score and then by ordinal name:
    // Module rows score 1000 and every other row 1. Paging with `limit` must
    // select the same page, so the engine sorts the same way.
    matched.sort_by(|(left, _), (right, _)| {
        (left.kind != ObjectKind::Module)
            .cmp(&(right.kind != ObjectKind::Module))
            .then_with(|| left.name.cmp(&right.name))
    });

    let total = matched.len();
    let truncated = total > limit;
    let objects = matched
        .into_iter()
        .take(limit)
        .map(|(object, object_type)| QueryObject {
            name: object.name.clone(),
            object_type: object_type.to_string(),
        })
        .collect();

    Ok(QueryResult {
        objects,
        total,
        truncated,
    })
}

/// Replaces one known part of an existing object (or creates the object when
/// `create` selects a kind and the name is free). Every stored part of the
/// object must map to a known part kind: a save re-encodes all parts, so an
/// unknown part is refused instead of being silently relocated. When
/// `expected_kind` is set, a mismatch is refused before any write.
pub fn write_object_part(
    client: &UdmClient,
    name: &str,
    part: &str,
    content: &str,
    create: Option<ObjectKind>,
    expected_kind: Option<ObjectKind>,
) -> Result<WriteResult, String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err("object name is required".to_string());
    }

    let part_kind = UdmPartKind::from_name(part);
    if matches!(part_kind, UdmPartKind::Unknown(_)) {
        return Err(format!(
            "part '{part}' is not a writable engine part (Source, Rules, Events, Conditions, Variables, Structure, WebForm, PatternInstance)"
        ));
    }

    // The engine writer mutates the in-process store only; a live Knowledge Base
    // database would keep its old version. Refusing here is what keeps a
    // reported write from silently not existing.
    if client.is_live() {
        return Err(format!(
            "object '{trimmed}' resolves to a live Knowledge Base database and engine UDM writes are not implemented for live storage; refusing a write that would not persist (the SDK path must serve this edit)"
        ));
    }

    let reader = UdmReader::new(shared_client(client));
    let writer = UdmWriter::new(shared_client(client));

    let existing = reader.read_object_by_name(trimmed)?;
    let created = existing.is_none();
    let mut object = match existing {
        Some(object) => object,
        None => {
            let kind = create
                .ok_or_else(|| format!("object '{trimmed}' was not found in the active model"))?;
            KbObject::new(ObjectId::new(trimmed)?, trimmed.to_string(), kind)
        }
    };

    if let Some(expected) = expected_kind {
        if object.kind != expected {
            return Err(format!(
                "object '{}' is a {}, not a {}",
                object.name,
                type_name(object.kind),
                type_name(expected)
            ));
        }
    }

    if let Some(unknown) = object.source_parts.iter().find(|stored| {
        matches!(
            UdmPartKind::from_name(&stored.name),
            UdmPartKind::Unknown(_)
        )
    }) {
        return Err(format!(
            "object '{}' has part '{}' outside engine coverage; refusing a partial save",
            object.name, unknown.name
        ));
    }

    let canonical = part_kind.name().to_string();
    match object
        .source_parts
        .iter_mut()
        .find(|stored| stored.name.eq_ignore_ascii_case(&canonical))
    {
        Some(stored) => stored.source = content.to_string(),
        None => object.source_parts.push(SourcePart {
            name: canonical,
            source: content.to_string(),
        }),
    }

    let identity = writer.save_object(&object)?;
    Ok(WriteResult {
        name: identity.name,
        object_type: type_name(object.kind).to_string(),
        part: part_friendly_name(&part_kind).to_string(),
        version_id: identity.version_id,
        created,
        bytes: content.len(),
    })
}
