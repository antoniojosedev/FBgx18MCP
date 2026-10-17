//! Direct Knowledge Base Reader and Catalog Hydration for GeneXus UDM storage.
//!
//! Reads active Knowledge Base objects from `ModelEntityVersion`, unpacks their component
//! parts from `EntityVersionComposition` and `EntityVersion` (Source 100, Rules 102,
//! Events 97, Conditions 89, Variables 105, Structure 104, WebForm 107), and hydrates
//! portable `Catalog` and `KbObject` structures without loading the legacy COM STA SDK.

#![forbid(unsafe_code)]

use crate::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use crate::udm::client::{ModelEntityVersionRecord, UdmClient};
use crate::udm::decoder::{UdmPartKind, decode_blob, extract_token_data_list};
use std::collections::{BTreeMap, HashSet};
use std::sync::Arc;

/// Default Design Model ID in GeneXus Knowledge Bases.
pub const DEFAULT_DESIGN_MODEL_ID: i32 = 1;

/// Universal Data Model reader hydrating objects directly into the `gx-engine` catalog.
#[derive(Debug, Clone)]
pub struct UdmReader {
    client: Arc<UdmClient>,
    model_id: i32,
}

impl UdmReader {
    /// Creates a reader using the default design model (ModelId = 1).
    pub fn new(client: Arc<UdmClient>) -> Self {
        Self {
            client,
            model_id: DEFAULT_DESIGN_MODEL_ID,
        }
    }

    /// Creates a reader targeting a specific model ID.
    pub fn with_model_id(client: Arc<UdmClient>, model_id: i32) -> Self {
        Self { client, model_id }
    }

    /// Creates a reader from a value client.
    pub fn from_client(client: UdmClient) -> Self {
        Self::new(Arc::new(client))
    }

    /// Returns the target model ID.
    pub fn model_id(&self) -> i32 {
        self.model_id
    }

    /// Returns a reference to the underlying database client.
    pub fn client(&self) -> &UdmClient {
        &self.client
    }

    /// Reads all active objects in the model and hydrates them into a validated `Catalog`.
    pub fn read_catalog(&self) -> Result<Catalog, String> {
        let objects = self.read_all_objects()?;

        // Build object map and set of known object IDs to filter dangling references
        let known_ids: HashSet<ObjectId> = objects.iter().map(|o| o.id.clone()).collect();

        let mut objects_map = BTreeMap::new();
        for mut obj in objects {
            // Discard references to objects not present in the current catalog
            obj.references.retain(|r| known_ids.contains(&r.target));
            objects_map.insert(obj.id.clone(), obj);
        }

        let catalog = Catalog::from_objects_map(objects_map);
        // Ensure catalog passes strict normalization and validation
        catalog
            .to_json()
            .map_err(|e| format!("Catalog validation failed: {e}"))?;
        Ok(catalog)
    }

    /// Reads all active objects in the model as `KbObject` instances.
    pub fn read_all_objects(&self) -> Result<Vec<KbObject>, String> {
        let active_records = self.client.get_active_objects(self.model_id)?;
        let mut objects = Vec::with_capacity(active_records.len());

        for record in &active_records {
            let obj = self.hydrate_object(record, &active_records)?;
            objects.push(obj);
        }

        Ok(objects)
    }

    /// Reads a specific object by its entity type ID and entity ID.
    pub fn read_object(&self, entity_type_id: i32, entity_id: i32) -> Result<KbObject, String> {
        let active_records = self.client.get_active_objects(self.model_id)?;
        let record = active_records
            .iter()
            .find(|r| r.entity_type_id == entity_type_id && r.entity_id == entity_id)
            .ok_or_else(|| {
                format!(
                    "Active object not found for ModelId {}, EntityTypeId {entity_type_id}, EntityId {entity_id}",
                    self.model_id
                )
            })?;

        self.hydrate_object(record, &active_records)
    }

    /// Reads an active object by its name (case-insensitive).
    pub fn read_object_by_name(&self, name: &str) -> Result<Option<KbObject>, String> {
        let active_records = self.client.get_active_objects(self.model_id)?;
        if let Some(record) = active_records
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(name))
        {
            let obj = self.hydrate_object(record, &active_records)?;
            Ok(Some(obj))
        } else {
            Ok(None)
        }
    }

    /// Reads and decodes a specific component part of an object version.
    pub fn read_part(
        &self,
        compound_type_id: i32,
        compound_id: i32,
        compound_ver_id: i32,
        part_kind: UdmPartKind,
    ) -> Result<Option<String>, String> {
        let comps = self
            .client
            .get_compositions(compound_type_id, compound_id, compound_ver_id)?;

        let target_part_id = part_kind.id();
        if let Some(comp) = comps
            .iter()
            .find(|c| c.component_entity_type_id == target_part_id)
        {
            if let Some(ev) = self.client.get_entity_version(
                comp.component_entity_type_id,
                comp.component_entity_id,
                comp.component_entity_version_id,
            )? {
                if !ev.data.is_empty() {
                    let decoded = decode_blob(&ev.data)?;
                    let source =
                        if part_kind.is_tokenized_source() && decoded.contains("<TokenDataList") {
                            extract_token_data_list(&decoded).unwrap_or(decoded)
                        } else {
                            decoded
                        };
                    return Ok(Some(source));
                }
            }
        }

        Ok(None)
    }

    fn hydrate_object(
        &self,
        record: &ModelEntityVersionRecord,
        all_active: &[ModelEntityVersionRecord],
    ) -> Result<KbObject, String> {
        let obj_id = ObjectId::new(&record.name)
            .unwrap_or_else(|_| ObjectId::new(format!("obj-{}", record.entity_id)).unwrap());

        let kind = map_entity_type_id_to_kind(record.entity_type_id);
        let mut object = KbObject::new(obj_id, &record.name, kind);

        // Hydrate component parts: Source (100), Rules (102), Events (97), Conditions (89),
        // Variables (105), Structure (104), WebForm (107)
        let compositions = self.client.get_compositions(
            record.entity_type_id,
            record.entity_id,
            record.entity_version_id,
        )?;

        for comp in compositions {
            let part_kind = UdmPartKind::from_id(comp.component_entity_type_id);
            if let Some(ev) = self.client.get_entity_version(
                comp.component_entity_type_id,
                comp.component_entity_id,
                comp.component_entity_version_id,
            )? {
                if !ev.data.is_empty() {
                    let decoded_text = decode_blob(&ev.data)?;
                    let source_text = if part_kind.is_tokenized_source()
                        && decoded_text.contains("<TokenDataList")
                    {
                        extract_token_data_list(&decoded_text).unwrap_or(decoded_text)
                    } else {
                        decoded_text
                    };

                    object.source_parts.push(SourcePart {
                        name: part_kind.name().to_string(),
                        source: source_text,
                    });
                }
            }
        }

        // Hydrate cross-references from ModelCrossReference
        let xr_records = self.client.get_cross_references(
            self.model_id,
            record.entity_type_id,
            record.entity_id,
        )?;

        for xr in xr_records {
            if let Some(target_record) = all_active.iter().find(|r| {
                r.entity_type_id == xr.to_entity_type_id && r.entity_id == xr.to_entity_id
            }) {
                if let Ok(target_id) = ObjectId::new(&target_record.name) {
                    let ref_kind = match xr.reference_type {
                        1 => ReferenceKind::Calls,
                        2 => ReferenceKind::Uses,
                        3 => ReferenceKind::BasedOn,
                        4 => ReferenceKind::Contains,
                        _ => ReferenceKind::Uses,
                    };

                    object.references.push(Reference {
                        kind: ref_kind,
                        target: target_id,
                    });
                }
            }
        }

        Ok(object)
    }
}

/// Maps a GeneXus entity type integer ID to `ObjectKind`.
pub fn map_entity_type_id_to_kind(id: i32) -> ObjectKind {
    match id {
        1 | 38 => ObjectKind::Procedure,
        2 | 43 => ObjectKind::Transaction,
        4 | 27 => ObjectKind::Attribute,
        5 | 33 => ObjectKind::Domain,
        7 | 41 => ObjectKind::Table,
        8 | 31 => ObjectKind::DataView,
        12 | 13 | 47 => ObjectKind::WebPanel,
        29 | 40 => ObjectKind::StructuredDataType,
        30 | 37 => ObjectKind::DataSelector,
        45 | 57 => ObjectKind::DesignSystem,
        _ => ObjectKind::Procedure,
    }
}

/// Maps an `ObjectKind` to its standard GeneXus entity type integer ID.
pub fn map_kind_to_entity_type_id(kind: ObjectKind) -> i32 {
    match kind {
        ObjectKind::Procedure => 1,
        ObjectKind::Transaction => 2,
        ObjectKind::Attribute => 4,
        ObjectKind::Domain => 5,
        ObjectKind::Table => 7,
        ObjectKind::DataView => 8,
        ObjectKind::WebPanel => 12,
        ObjectKind::StructuredDataType => 29,
        ObjectKind::DataProvider => 33,
        ObjectKind::DataSelector => 37,
        ObjectKind::Module => 38,
        ObjectKind::DesignSystem => 45,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::udm::client::*;
    use crate::udm::decoder::{build_token_data_list, encode_blob};

    #[test]
    fn read_procedure_with_parts_and_references() {
        let client = Arc::new(UdmClient::new_in_memory());

        // Target called procedure
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 1, // Procedure
                entity_id: 200,
                entity_version_id: 1,
                name: "PChildProcess".to_string(),
            })
            .unwrap();

        // Main caller procedure
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 1,
                entity_id: 100,
                entity_version_id: 1,
                name: "PMainCaller".to_string(),
            })
            .unwrap();

        // Source part (Type 100)
        let source_code = "For Each\n    Call(PChildProcess)\nEndFor";
        let source_xml = build_token_data_list(source_code);
        let source_blob = encode_blob(&source_xml, true).unwrap();

        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 100, // Source Part
                entity_id: 1001,
                entity_version_id: 1,
                data: source_blob,
                properties: None,
            })
            .unwrap();

        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 1,
                compound_entity_id: 100,
                compound_entity_version_id: 1,
                component_entity_type_id: 100,
                component_entity_id: 1001,
                component_entity_version_id: 1,
            })
            .unwrap();

        // Rules part (Type 102)
        let rules_code = "NoAccept(&UserId);";
        let rules_xml = build_token_data_list(rules_code);
        let rules_blob = encode_blob(&rules_xml, true).unwrap();

        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 102, // Rules Part
                entity_id: 1002,
                entity_version_id: 1,
                data: rules_blob,
                properties: None,
            })
            .unwrap();

        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 1,
                compound_entity_id: 100,
                compound_entity_version_id: 1,
                component_entity_type_id: 102,
                component_entity_id: 1002,
                component_entity_version_id: 1,
            })
            .unwrap();

        // Cross reference: PMainCaller calls PChildProcess
        client
            .insert_cross_reference(ModelCrossReferenceRecord {
                model_id: 1,
                from_entity_type_id: 1,
                from_entity_id: 100,
                to_entity_type_id: 1,
                to_entity_id: 200,
                reference_type: 1, // Calls
            })
            .unwrap();

        let reader = UdmReader::new(client);
        let catalog = reader.read_catalog().expect("read catalog");

        let obj = catalog
            .get(&ObjectId::new("pmaincaller").unwrap())
            .expect("find pmaincaller");

        assert_eq!(obj.name, "PMainCaller");
        assert_eq!(obj.kind, ObjectKind::Procedure);
        assert_eq!(obj.source_parts.len(), 2);

        let src = obj
            .source_parts
            .iter()
            .find(|p| p.name == "SOURCE")
            .unwrap();
        assert_eq!(src.source, source_code);

        let rules = obj.source_parts.iter().find(|p| p.name == "RULES").unwrap();
        assert_eq!(rules.source, rules_code);

        assert_eq!(obj.references.len(), 1);
        assert_eq!(obj.references[0].kind, ReferenceKind::Calls);
        assert_eq!(
            obj.references[0].target,
            ObjectId::new("pchildprocess").unwrap()
        );
    }

    #[test]
    fn read_part_selectively() {
        let client = Arc::new(UdmClient::new_in_memory());

        let code = "Event 'Click'\n    Msg('Hello')\nEndEvent";
        let xml = build_token_data_list(code);
        let blob = encode_blob(&xml, true).unwrap();

        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 97, // Events
                entity_id: 501,
                entity_version_id: 1,
                data: blob,
                properties: None,
            })
            .unwrap();

        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 12, // WebPanel
                compound_entity_id: 50,
                compound_entity_version_id: 1,
                component_entity_type_id: 97,
                component_entity_id: 501,
                component_entity_version_id: 1,
            })
            .unwrap();

        let reader = UdmReader::new(client);
        let events = reader
            .read_part(12, 50, 1, UdmPartKind::Events)
            .expect("read events part")
            .expect("part should exist");

        assert_eq!(events, code);

        let non_existent = reader
            .read_part(12, 50, 1, UdmPartKind::WebForm)
            .expect("read webform part");
        assert!(non_existent.is_none());
    }
}
