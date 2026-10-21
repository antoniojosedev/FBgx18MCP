//! Direct Knowledge Base Reader and Catalog Hydration for GeneXus UDM storage.
//!
//! Reads active Knowledge Base objects from `ModelEntityVersion`, unpacks their component
//! parts from `EntityVersionComposition` and `EntityVersion`, and hydrates portable
//! `Catalog` and `KbObject` structures without loading the legacy COM STA SDK.
//!
//! Which entity type ids are objects and which are parts is decided by the
//! Knowledge Base's own `EntityType` table through [`EntityTypeResolver`]; no id is
//! hard-coded here, because GeneXus 16, 17 and 18 number them differently.

#![forbid(unsafe_code)]

use crate::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use crate::model::DataType;
use crate::udm::client::{
    EntityVersionCompositionRecord, ModelCrossReferenceRecord, ModelEntityVersionRecord, UdmClient,
};
use crate::udm::decoder::{UdmPartKind, decode_blob, extract_token_data_list};
use crate::udm::entity_types::EntityTypeResolver;
use crate::udm::library::library_members;
use crate::udm::stored_sdt;
use crate::udm::stored_transaction;
use crate::udm::stored_variables;
use crate::webpanel_settings::settings_from_properties;
use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::sync::{Arc, OnceLock};

mod hydrate;
mod resolve;
mod structure;
mod synthetic_parts;

use resolve::{
    Target, VariablesLookup, find_object, has_source_part, held_source, is_system_root, object_for,
    object_targets, refuse_without_parts, target_of,
};
use synthetic_parts::{enum_domain_row, webform_is_default};

pub use resolve::{NO_PARTS_REFUSAL, NO_SOURCE_REFUSAL};
pub use synthetic_parts::{DOMAIN_ENUM_PART, VARIABLE_IDS_PART, WEBFORM_DEFAULT_PART};

/// Default Design Model ID in GeneXus Knowledge Bases.
pub const DEFAULT_DESIGN_MODEL_ID: i32 = 1;

/// The catalog identity of the Knowledge Base root module. Its display name
/// (`Root Module`) is not a valid object identity, so the root gets this one.
pub const SYSTEM_ROOT_OBJECT_ID: &str = "root-module";

/// One active-object row of a model, read without decoding any stored part.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ObjectRow {
    pub name: String,
    pub kind: ObjectKind,
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub entity_version_id: i32,
}

/// One object with every stored part name and the decoded requested parts.
#[derive(Debug, Clone)]
pub struct LoadedObject {
    /// The object; `source_parts` holds only the parts that were requested and
    /// decoded (every served stored part when the request was `None`).
    pub object: KbObject,
    /// Every served stored part name of the object, in composition order.
    pub stored_parts: Vec<String>,
}

/// Universal Data Model reader hydrating objects directly into the `gx-engine` catalog.
#[derive(Debug, Clone)]
pub struct UdmReader {
    client: Arc<UdmClient>,
    model_id: i32,
    /// The nested SDT levels of this model, read on first use and kept for this reader. An SDT whose
    /// structure cannot be read contributes no levels; a read error of the model is kept as the error.
    nested_levels: OnceLock<Result<BTreeMap<i32, stored_variables::NestedLevel>, String>>,
    /// The stored `GenerateObject` value of each Procedure of this model, read once per reader.
    procedure_flags: OnceLock<Result<BTreeMap<i32, String>, String>>,
    main_flags: OnceLock<Result<BTreeMap<i32, bool>, String>>,
    /// The SDTs of this model whose root is a collection, read once per reader. The SDK has no class
    /// for such a root, so no SDT name resolves to one: a reference to it is refused, not emitted.
    root_collections: OnceLock<Result<BTreeMap<i32, stored_variables::CollectionRoot>, String>>,
}

/// Why a Procedure whose stored `GenerateObject` is False is not generated.
pub const GENERATE_OBJECT_FALSE: &str =
    "GenerateObject is False in the Knowledge Base, so the SDK does not generate this procedure";

impl UdmReader {
    /// Creates a reader of the client's Design model (`UdmClient::design_model_id`; ModelId 1 by default).
    pub fn new(client: Arc<UdmClient>) -> Self {
        let model_id = client.design_model_id();
        Self {
            client,
            model_id,
            nested_levels: OnceLock::new(),
            procedure_flags: OnceLock::new(),
            main_flags: OnceLock::new(),
            root_collections: OnceLock::new(),
        }
    }

    /// Creates a reader targeting a specific model ID.
    pub fn with_model_id(client: Arc<UdmClient>, model_id: i32) -> Self {
        Self {
            client,
            model_id,
            nested_levels: OnceLock::new(),
            procedure_flags: OnceLock::new(),
            main_flags: OnceLock::new(),
            root_collections: OnceLock::new(),
        }
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
            // Two objects with one id would overwrite each other in the map: refuse, never drop one.
            if objects_map.contains_key(&obj.id) {
                return Err(format!(
                    "two objects share the catalog id '{}'; refusing to drop one of them",
                    obj.id.as_str()
                ));
            }
            objects_map.insert(obj.id.clone(), obj);
        }

        let catalog = Catalog::from_objects_map(objects_map);
        // Ensure catalog passes strict normalization and validation
        catalog
            .to_json()
            .map_err(|e| format!("Catalog validation failed: {e}"))?;
        Ok(catalog)
    }

    /// The stored `GenerateObject` value of Procedure `entity_id` in this model, or `None` when the
    /// Knowledge Base holds no value for it. Read once per reader; a read error fails every caller.
    fn generate_flag(&self, entity_id: i32) -> Result<Option<String>, String> {
        match self
            .procedure_flags
            .get_or_init(|| self.read_procedure_flags())
        {
            Ok(flags) => Ok(flags.get(&entity_id).cloned()),
            Err(error) => Err(error.clone()),
        }
    }

    /// The stored `IsMain` of a Procedure, read once for the model.
    fn main_flag(&self, entity_id: i32) -> Result<Option<bool>, String> {
        match self.main_flags.get_or_init(|| {
            let resolver = self.client.entity_type_resolver()?;
            let procedure = resolver.object_type_id(ObjectKind::Procedure)?;
            self.client.main_flags(self.model_id, procedure)
        }) {
            Ok(flags) => Ok(flags.get(&entity_id).copied()),
            Err(error) => Err(error.clone()),
        }
    }

    fn read_procedure_flags(&self) -> Result<BTreeMap<i32, String>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let procedure = resolver.object_type_id(ObjectKind::Procedure)?;
        self.client.generate_flags(self.model_id, procedure)
    }

    /// Reads all active objects in the model as `KbObject` instances.
    pub fn read_all_objects(&self) -> Result<Vec<KbObject>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let active_records = self.client.get_active_objects(self.model_id)?;
        // Set-wise reads of this model's compositions and parts, so the per-object
        // hydration below answers from memory on a live client.
        self.client.prefetch_model_parts(self.model_id)?;
        // One model-wide cross-reference read fills the cache on a live client,
        // so per-object hydration does not issue one query per object.
        self.client.get_cross_references_for_model(self.model_id)?;
        let system_root = self.system_root(&resolver)?;
        let targets = object_targets(&active_records, &resolver, system_root);
        // GeneXus library membership comes from each row's holder (module or folder), read once per model.
        let holders = self.client.get_model_parents(self.model_id)?;
        let module_type = resolver.object_type_id(ObjectKind::Module).ok();
        let parents: BTreeMap<(i32, i32), (i32, i32)> = holders
            .iter()
            .filter(|row| row.parent_entity_type_id != 0)
            .map(|row| {
                (
                    (row.entity_type_id, row.entity_id),
                    (row.parent_entity_type_id, row.parent_entity_id),
                )
            })
            .collect();
        let module_names: BTreeMap<(i32, i32), String> = active_records
            .iter()
            .filter(|record| Some(record.entity_type_id) == module_type)
            .map(|record| {
                (
                    (record.entity_type_id, record.entity_id),
                    record.name.clone(),
                )
            })
            .collect();
        let library = library_members(&parents, &module_names, system_root);
        let mut objects = Vec::with_capacity(active_records.len());

        for record in &active_records {
            let Some(kind) = resolver.object_kind(record.entity_type_id) else {
                continue;
            };
            let is_root = is_system_root(record, system_root);
            let mut object = self.hydrate_object(record, kind, is_root, &resolver, &targets)?;
            object.library = library.contains(&(record.entity_type_id, record.entity_id));
            object.module_path = crate::udm::library::module_path(
                (record.entity_type_id, record.entity_id),
                &parents,
                &module_names,
                system_root,
            );
            objects.push(object);
        }

        Ok(objects)
    }

    /// Lists the active objects of the model without decoding any stored part.
    /// Only `ModelEntityVersion` rows are read, so a query never pays for part
    /// payloads and one undecodable part cannot fail a whole listing. Rows whose
    /// entity type is not a served object type are skipped: the model contains
    /// parts and KB-tree nodes as entities of their own.
    pub fn list_objects(&self) -> Result<Vec<ObjectRow>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let records = self.client.get_active_objects(self.model_id)?;
        Ok(records
            .iter()
            .filter_map(|record| {
                resolver
                    .object_kind(record.entity_type_id)
                    .map(|kind| ObjectRow {
                        name: record.name.clone(),
                        kind,
                        entity_type_id: record.entity_type_id,
                        entity_id: record.entity_id,
                        entity_version_id: record.entity_version_id,
                    })
            })
            .collect())
    }

    /// Returns every cross-reference of the model (one query on a live client).
    pub fn references_for_model(&self) -> Result<Vec<ModelCrossReferenceRecord>, String> {
        self.client.get_cross_references_for_model(self.model_id)
    }

    /// Loads one active object by name (case-insensitive), decoding only the
    /// requested parts. `requested` is matched against the canonical part names
    /// (`SOURCE`, `RULES`, â€¦) case-insensitively; `None` decodes every served
    /// stored part. Part names come from `EntityVersionComposition`, so an
    /// unrequested part is never fetched or decoded.
    pub fn load_object_by_name(
        &self,
        name: &str,
        requested: Option<&[String]>,
    ) -> Result<Option<LoadedObject>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let records = self.client.get_active_objects(self.model_id)?;
        let Some((record, kind)) = find_object(&records, &resolver, name)? else {
            return Ok(None);
        };
        let system_root = self.system_root(&resolver)?;
        let targets = object_targets(&records, &resolver, system_root);

        let requested_upper: Option<Vec<String>> = requested.map(|parts| {
            parts
                .iter()
                .map(|part| part.trim().to_ascii_uppercase())
                .filter(|part| !part.is_empty())
                .collect()
        });

        let is_root = is_system_root(record, system_root);
        let mut object = object_for(record, kind, target_of(&targets, record)?, is_root);

        let compositions = self.client.get_compositions(
            record.entity_type_id,
            record.entity_id,
            record.entity_version_id,
        )?;

        if kind == ObjectKind::Procedure {
            object.main = self.main_flag(record.entity_id)?;
            match self.generate_flag(record.entity_id)?.as_deref() {
                None | Some("True") => {}
                Some("False") => object.not_generated = Some(GENERATE_OBJECT_FALSE.into()),
                Some(other) => {
                    object.refusal.get_or_insert(format!(
                        "GenerateObject is stored as '{other}', which is neither True nor False"
                    ));
                }
            }
        }
        refuse_without_parts(
            &mut object,
            kind,
            compositions.len(),
            has_source_part(&compositions, &resolver),
        );
        let sdt_type = resolver.object_type_id(ObjectKind::StructuredDataType).ok();
        let mut stored_parts = Vec::with_capacity(compositions.len());
        let domain_type_id = resolver.object_type_id(ObjectKind::Domain).ok();
        let domain_of = |id: i32| domain_type_id.and_then(|ty| self.domain_type(ty, id, &targets));
        let level_of = |id: i32| self.nested_level(id, &targets, &resolver);
        let collection_of = |id: i32| {
            self.root_collections(&targets, &resolver)
                .map(|roots| roots.get(&id).cloned())
        };
        let lookup = VariablesLookup {
            sdt_type,
            domain_type: domain_type_id,
            targets: &targets,
            domain_of: &domain_of,
            level_of: &level_of,
            collection_of: &collection_of,
        };
        for comp in compositions {
            // Parts outside the served space (Documentation, Help, Layout, â€¦) are
            // neither decoded nor listed: they have no engine part kind.
            let Some(part_kind) = resolver.part_kind(comp.component_entity_type_id) else {
                continue;
            };
            let part_name = part_kind.name().to_string();
            stored_parts.push(part_name.clone());
            let wanted = match &requested_upper {
                None => true,
                Some(requested) => requested.iter().any(|want| want == &part_name),
            };
            if !wanted {
                continue;
            }
            let stored = self.component_text(&comp, part_kind)?;
            // The stored id of each variable names its SDK field (`AV<Id><Name>`); it is held as a
            // synthetic part beside the portable declarations, which carry no ids.
            let variable_ids = (matches!(kind, ObjectKind::Procedure | ObjectKind::DataProvider)
                && part_kind == UdmPartKind::Variables)
                .then(|| stored_variables::declaration_ids(&stored).ok())
                .flatten()
                .and_then(|ids| serde_json::to_string(&ids).ok());
            let held =
                if kind == ObjectKind::StructuredDataType && part_kind == UdmPartKind::Structure {
                    self.held_sdt_structure(
                        &mut object,
                        &comp,
                        stored,
                        &targets,
                        &resolver,
                        self.sdt_xml_namespace(record).as_deref(),
                    )
                } else if kind == ObjectKind::Transaction && part_kind == UdmPartKind::Structure {
                    self.held_transaction_structure(&mut object, stored, &targets, &resolver)
                } else {
                    held_source(&mut object, kind, part_kind, stored, &lookup)
                };
            if let Some(source) = held {
                object.source_parts.push(SourcePart {
                    name: part_name,
                    source,
                });
                if let Some(ids) = variable_ids {
                    object.source_parts.push(SourcePart {
                        name: VARIABLE_IDS_PART.to_string(),
                        source: ids,
                    });
                }
            }
        }

        Ok(Some(LoadedObject {
            object,
            stored_parts,
        }))
    }

    /// Reads a specific object by its entity type ID and entity ID.
    pub fn read_object(&self, entity_type_id: i32, entity_id: i32) -> Result<KbObject, String> {
        let resolver = self.client.entity_type_resolver()?;
        let kind = resolver.object_kind(entity_type_id).ok_or_else(|| {
            format!(
                "Entity type {entity_type_id} is not a served object type in this Knowledge Base (see docs/udm_storage_notes.md)"
            )
        })?;
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

        self.client.get_cross_references_for_model(self.model_id)?;
        let system_root = self.system_root(&resolver)?;
        let targets = object_targets(&active_records, &resolver, system_root);
        let is_root = is_system_root(record, system_root);
        self.hydrate_object(record, kind, is_root, &resolver, &targets)
    }

    /// Reads an active object by its name (case-insensitive). Names that resolve
    /// only to a non-object entity type (a part or KB-tree node) are not readable
    /// objects and are reported as not found.
    pub fn read_object_by_name(&self, name: &str) -> Result<Option<KbObject>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let active_records = self.client.get_active_objects(self.model_id)?;
        let Some((record, kind)) = find_object(&active_records, &resolver, name)? else {
            return Ok(None);
        };
        self.client.get_cross_references_for_model(self.model_id)?;
        let system_root = self.system_root(&resolver)?;
        let targets = object_targets(&active_records, &resolver, system_root);
        let is_root = is_system_root(record, system_root);
        self.hydrate_object(record, kind, is_root, &resolver, &targets)
            .map(Some)
    }

    /// Reads one object and every object its references reach, for a single-object closure. The walk
    /// stops at Modules and at the root module: a Module is namespace membership, never code. Each reached
    /// object is read once. Returns the entry's id and the objects read, or `None` when the KB has no such
    /// object.
    pub fn read_closure_by_name(
        &self,
        name: &str,
    ) -> Result<Option<(ObjectId, Vec<KbObject>)>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let active_records = self.client.get_active_objects(self.model_id)?;
        let Some((record, _)) = find_object(&active_records, &resolver, name)? else {
            return Ok(None);
        };
        self.client.get_cross_references_for_model(self.model_id)?;
        let system_root = self.system_root(&resolver)?;
        let targets = object_targets(&active_records, &resolver, system_root);
        let rows: BTreeMap<ObjectId, &ModelEntityVersionRecord> = active_records
            .iter()
            .filter_map(|row| {
                let target = targets.get(&(row.entity_type_id, row.entity_id))?;
                Some((target.id.clone(), row))
            })
            .collect();
        let entry = target_of(&targets, record)?;
        let mut read: BTreeMap<ObjectId, KbObject> = BTreeMap::new();
        let mut seen: BTreeSet<ObjectId> = BTreeSet::from([entry.clone()]);
        let mut wave = vec![entry.clone()];
        while !wave.is_empty() {
            // One prefetch per wave, keyed by this wave's objects only: their compositions and parts, and
            // their components, come in set-based queries, so the cost follows the closure, not the model.
            // A key the prefetch does not cover still reads live, so the walk is exact either way.
            let mut keys = Vec::with_capacity(wave.len());
            for id in &wave {
                let row = *rows.get(id).ok_or_else(|| {
                    format!("closure: object '{}' has no served row", id.as_str())
                })?;
                keys.push((row.entity_type_id, row.entity_id, row.entity_version_id));
            }
            self.client.prefetch_keys(&keys, &keys)?;
            let mut next = Vec::new();
            for id in wave {
                let row = *rows.get(&id).ok_or_else(|| {
                    format!("closure: object '{}' has no served row", id.as_str())
                })?;
                let kind = resolver.object_kind(row.entity_type_id).ok_or_else(|| {
                    format!(
                        "closure: object '{}' is not a served object type",
                        id.as_str()
                    )
                })?;
                let is_root = is_system_root(row, system_root);
                let object = self.hydrate_object(row, kind, is_root, &resolver, &targets)?;
                if kind != ObjectKind::Module && !is_root {
                    for reference in &object.references {
                        if seen.insert(reference.target.clone()) {
                            next.push(reference.target.clone());
                        }
                    }
                }
                read.insert(id, object);
            }
            wave = next;
        }
        Ok(Some((entry, read.into_values().collect())))
    }

    /// Reads and decodes the component part of one object version that has the
    /// given part kind. Returns `None` when the object has no such part.
    pub fn read_part(
        &self,
        compound_type_id: i32,
        compound_id: i32,
        compound_ver_id: i32,
        part_kind: UdmPartKind,
    ) -> Result<Option<String>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let comps = self
            .client
            .get_compositions(compound_type_id, compound_id, compound_ver_id)?;

        for comp in &comps {
            if resolver.part_kind(comp.component_entity_type_id) == Some(part_kind) {
                return self.component_text(comp, part_kind).map(Some);
            }
        }

        Ok(None)
    }

    /// The Knowledge Base root module of this model, identified by its owner data
    /// (see `UdmClient::system_root_module`). A vocabulary without a Module type has
    /// no root.
    fn system_root(&self, resolver: &EntityTypeResolver) -> Result<Option<(i32, i32)>, String> {
        match resolver.object_type_id(ObjectKind::Module) {
            Ok(module_type) => self.client.system_root_module(self.model_id, module_type),
            Err(_) => Ok(None),
        }
    }
}

#[cfg(test)]
mod tests;
