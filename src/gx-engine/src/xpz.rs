//! Team Development and Knowledge Manager XPZ Transfer Engine (Row J3).
//!
//! Provides parsing, serialization, and conflict-resolution merging for GeneXus
//! XPZ (XML Export/Import Package) interchange archives.

use serde::{Deserialize, Serialize};
use std::collections::HashSet;

use crate::catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart};

/// Strategy for resolving conflicts during XPZ package import.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ImportStrategy {
    Overwrite,
    KeepExisting,
    FailOnConflict,
}

/// A serialized object entry inside an XPZ exchange package.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct XpzObject {
    pub guid: String,
    pub object_type: String,
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default)]
    pub parts: Vec<SourcePart>,
}

/// Metadata and object collection of an XPZ export package.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct XpzPackage {
    pub genexus_version: String,
    pub exported_at: String,
    pub objects: Vec<XpzObject>,
}

impl XpzPackage {
    /// Creates an XPZ package from a portable Catalog.
    pub fn from_catalog(catalog: &Catalog, gx_version: &str) -> Self {
        let mut objects = Vec::new();
        for obj in catalog.objects() {
            let guid = format!("{:016x}-{:08x}", obj.kind as u64, obj.name.len());
            let object_type = match obj.kind {
                ObjectKind::Procedure => "Procedure",
                ObjectKind::Transaction => "Transaction",
                ObjectKind::StructuredDataType => "StructuredDataType",
                ObjectKind::Domain => "Domain",
                ObjectKind::Attribute => "Attribute",
                ObjectKind::DataProvider => "DataProvider",
                ObjectKind::DataSelector => "DataSelector",
                ObjectKind::DesignSystem => "DesignSystem",
                ObjectKind::DataView => "DataView",
                ObjectKind::WebPanel => "WebPanel",
                ObjectKind::Module => "Module",
                ObjectKind::Table => "Table",
            };

            objects.push(XpzObject {
                guid,
                object_type: object_type.to_string(),
                name: obj.name.clone(),
                description: None,
                parts: obj.source_parts.clone(),
            });
        }

        Self {
            genexus_version: gx_version.to_string(),
            exported_at: "2026-10-17T12:00:00Z".to_string(),
            objects,
        }
    }

    /// Serializes the XPZ package into standard GeneXus ExportFile XML format.
    pub fn to_xml(&self) -> String {
        let mut xml = String::from("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        xml.push_str(&format!(
            "<ExportFile Generator=\"GeneXus\" Version=\"{}\" ExportedAt=\"{}\">\n",
            self.genexus_version, self.exported_at
        ));

        for obj in &self.objects {
            xml.push_str(&format!(
                "  <Object Guid=\"{}\" Type=\"{}\" Name=\"{}\">\n",
                obj.guid, obj.object_type, obj.name
            ));
            if let Some(desc) = &obj.description {
                xml.push_str(&format!(
                    "    <Description>{}</Description>\n",
                    escape_xml(desc)
                ));
            }
            for part in &obj.parts {
                xml.push_str(&format!(
                    "    <Part Name=\"{}\">\n      <Source><![CDATA[{}]]></Source>\n    </Part>\n",
                    part.name, part.source
                ));
            }
            xml.push_str("  </Object>\n");
        }

        xml.push_str("</ExportFile>\n");
        xml
    }

    /// Parses an XPZ package from ExportFile XML format.
    pub fn parse_xml(xml: &str) -> Result<Self, String> {
        let doc = roxmltree::Document::parse(xml).map_err(|e| format!("Corrupted XPZ XML: {e}"))?;

        let root = doc.root_element();
        if root.tag_name().name() != "ExportFile" {
            return Err("Expected <ExportFile> root element in XPZ".to_string());
        }

        let gx_version = root.attribute("Version").unwrap_or("18.0").to_string();
        let exported_at = root.attribute("ExportedAt").unwrap_or("").to_string();

        let mut objects = Vec::new();
        let mut seen_guids = HashSet::new();

        for obj_node in root.children().filter(|n| n.tag_name().name() == "Object") {
            let guid = obj_node.attribute("Guid").unwrap_or("").to_string();
            let object_type = obj_node.attribute("Type").unwrap_or("").to_string();
            let name = obj_node.attribute("Name").unwrap_or("").to_string();

            if name.is_empty() {
                return Err("XPZ Object missing Name attribute".to_string());
            }
            if !guid.is_empty() && !seen_guids.insert(guid.clone()) {
                return Err(format!("Duplicate Object GUID '{guid}' in XPZ"));
            }

            let mut description = None;
            let mut parts = Vec::new();

            for child in obj_node.children() {
                match child.tag_name().name() {
                    "Description" => {
                        description = child.text().map(|s| s.to_string());
                    }
                    "Part" => {
                        let part_name = child.attribute("Name").unwrap_or("Source").to_string();
                        let source_text = child
                            .children()
                            .find(|n| n.tag_name().name() == "Source")
                            .and_then(|n| n.text())
                            .unwrap_or("")
                            .to_string();
                        parts.push(SourcePart {
                            name: part_name,
                            source: source_text,
                        });
                    }
                    _ => {}
                }
            }

            objects.push(XpzObject {
                guid,
                object_type,
                name,
                description,
                parts,
            });
        }

        Ok(Self {
            genexus_version: gx_version,
            exported_at,
            objects,
        })
    }

    /// Merges objects from this package into a target Catalog according to the strategy.
    pub fn merge_into_catalog(
        &self,
        catalog: &mut Catalog,
        strategy: ImportStrategy,
    ) -> Result<usize, String> {
        let mut imported_count = 0;

        for xpz_obj in &self.objects {
            let kind = parse_object_kind(&xpz_obj.object_type)?;
            let obj_id = ObjectId::new(&xpz_obj.name)?;

            let exists = catalog.get(&obj_id).is_some();

            if exists {
                match strategy {
                    ImportStrategy::FailOnConflict => {
                        return Err(format!(
                            "Import conflict: Object '{:?}:{}' already exists in Catalog",
                            kind, xpz_obj.name
                        ));
                    }
                    ImportStrategy::KeepExisting => {
                        continue;
                    }
                    ImportStrategy::Overwrite => {}
                }
            }

            let mut kb_obj = KbObject::new(obj_id, &xpz_obj.name, kind);
            kb_obj.source_parts = xpz_obj.parts.clone();
            catalog.apply(catalog.revision(), &[Edit::Upsert(kb_obj)])?;
            imported_count += 1;
        }

        Ok(imported_count)
    }
}

fn parse_object_kind(s: &str) -> Result<ObjectKind, String> {
    match s.to_ascii_lowercase().as_str() {
        "procedure" => Ok(ObjectKind::Procedure),
        "transaction" => Ok(ObjectKind::Transaction),
        "structureddatatype" | "sdt" => Ok(ObjectKind::StructuredDataType),
        "domain" => Ok(ObjectKind::Domain),
        "attribute" => Ok(ObjectKind::Attribute),
        "dataprovider" => Ok(ObjectKind::DataProvider),
        "dataselector" => Ok(ObjectKind::DataSelector),
        "designsystem" | "dso" => Ok(ObjectKind::DesignSystem),
        "dataview" => Ok(ObjectKind::DataView),
        "webpanel" => Ok(ObjectKind::WebPanel),
        "module" => Ok(ObjectKind::Module),
        "table" => Ok(ObjectKind::Table),
        _ => Err(format!("Unsupported object type '{s}' in XPZ")),
    }
}

fn escape_xml(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
