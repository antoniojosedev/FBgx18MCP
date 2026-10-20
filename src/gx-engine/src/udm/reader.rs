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
    /// Creates a reader using the default design model (ModelId = 1).
    pub fn new(client: Arc<UdmClient>) -> Self {
        Self {
            client,
            model_id: DEFAULT_DESIGN_MODEL_ID,
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

    /// The text of one component part. A composition that names a version with no
    /// `EntityVersion` row is an inconsistent model and fails the read: it is
    /// never reported as an empty or absent part. An empty payload is an empty part.
    fn component_text(
        &self,
        comp: &EntityVersionCompositionRecord,
        part_kind: UdmPartKind,
    ) -> Result<String, String> {
        let Some(ev) = self.client.get_entity_version(
            comp.component_entity_type_id,
            comp.component_entity_id,
            comp.component_entity_version_id,
        )?
        else {
            return Err(format!(
                "part {} ({}, {}) of its object has no EntityVersion row; the composition is inconsistent",
                part_kind.name(),
                comp.component_entity_type_id,
                comp.component_entity_id
            ));
        };
        if ev.data.is_empty() {
            return Ok(String::new());
        }
        let decoded = decode_blob(&ev.data)?;
        if part_kind.is_tokenized_source() && decoded.contains("<TokenDataList") {
            // A token list that does not extract is an error: returning the raw
            // XML as the part's source would present markup as GeneXus code.
            extract_token_data_list(&decoded)
        } else {
            Ok(decoded)
        }
    }

    fn hydrate_object(
        &self,
        record: &ModelEntityVersionRecord,
        kind: ObjectKind,
        is_root: bool,
        resolver: &EntityTypeResolver,
        targets: &BTreeMap<(i32, i32), Target>,
    ) -> Result<KbObject, String> {
        let mut object = object_for(record, kind, target_of(targets, record)?, is_root);

        // A Procedure the Knowledge Base stores as not generated is not read part by part: its parts
        // cannot make it an error, and the SDK builds nothing for it.
        if kind == ObjectKind::Procedure {
            object.main = self.main_flag(record.entity_id)?;
            match self.generate_flag(record.entity_id)?.as_deref() {
                None | Some("True") => {}
                Some("False") => {
                    object.not_generated = Some(GENERATE_OBJECT_FALSE.into());
                    return Ok(object);
                }
                Some(other) => {
                    object.refusal.get_or_insert(format!(
                        "GenerateObject is stored as '{other}', which is neither True nor False"
                    ));
                    return Ok(object);
                }
            }
        }

        // An enumerated Domain of the model (not a library interface) has a values class the SDK
        // writes (`gxdomain<name>.cs`). Its stored row is held as a synthetic part for that emitter.
        if kind == ObjectKind::Domain {
            if let Some(properties) = self
                .client
                .get_entity_version(
                    record.entity_type_id,
                    record.entity_id,
                    record.entity_version_id,
                )?
                .and_then(|version| version.properties)
            {
                match enum_domain_row(&properties) {
                    Ok(true) => object.source_parts.push(SourcePart {
                        name: DOMAIN_ENUM_PART.to_string(),
                        source: properties,
                    }),
                    Ok(false) => {}
                    Err(error) => {
                        object
                            .refusal
                            .get_or_insert(format!("{DOMAIN_ENUM_PART}: {error}"));
                    }
                }
            }
        }

        if kind == ObjectKind::DataProvider {
            match self.data_provider_output(record, resolver, targets) {
                Ok((part, output_id)) => {
                    object.source_parts.push(part);
                    object.references.push(Reference {
                        kind: ReferenceKind::Uses,
                        target: output_id,
                    });
                }
                Err(error) => {
                    object.refusal.get_or_insert(format!("OUTPUT: {error}"));
                }
            }
        }

        // Hydrate every served component part. A part outside the served space is
        // skipped, never emitted under a placeholder name: two skipped parts used
        // to become two `UNKNOWN` parts, and the catalog rejected the duplicate.
        let compositions = self.client.get_compositions(
            record.entity_type_id,
            record.entity_id,
            record.entity_version_id,
        )?;
        refuse_without_parts(
            &mut object,
            kind,
            compositions.len(),
            has_source_part(&compositions, resolver),
        );
        let sdt_type = resolver.object_type_id(ObjectKind::StructuredDataType).ok();
        let domain_type_id = resolver.object_type_id(ObjectKind::Domain).ok();
        let domain_of = |id: i32| domain_type_id.and_then(|ty| self.domain_type(ty, id, targets));
        let level_of = |id: i32| self.nested_level(id, targets, resolver);
        let collection_of = |id: i32| {
            self.root_collections(targets, resolver)
                .map(|roots| roots.get(&id).cloned())
        };
        let lookup = VariablesLookup {
            sdt_type,
            domain_type: domain_type_id,
            targets,
            domain_of: &domain_of,
            level_of: &level_of,
            collection_of: &collection_of,
        };

        // The XML namespace of an SDT's classes: its own `ExternalNamespace`, else the Knowledge Base's name.
        let sdt_xml_namespace = (kind == ObjectKind::StructuredDataType)
            .then(|| self.sdt_xml_namespace(record))
            .flatten();

        for comp in compositions {
            let Some(part_kind) = resolver.part_kind(comp.component_entity_type_id) else {
                continue;
            };
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
                        targets,
                        resolver,
                        sdt_xml_namespace.as_deref(),
                    )
                } else if kind == ObjectKind::Transaction && part_kind == UdmPartKind::Structure {
                    self.held_transaction_structure(&mut object, stored, targets, resolver)
                } else {
                    held_source(&mut object, kind, part_kind, stored, &lookup)
                };
            if let Some(source) = held {
                object.source_parts.push(SourcePart {
                    name: part_kind.name().to_string(),
                    source,
                });
                if let Some(ids) = variable_ids {
                    object.source_parts.push(SourcePart {
                        name: VARIABLE_IDS_PART.to_string(),
                        source: ids,
                    });
                }
            }
            // A Transaction's web form is generated from its structure when its `IsDefault`
            // property is set, whatever the stored layout holds. The flag is held as a synthetic
            // part; a flag that is not read holds no part, and the emitter refuses by name.
            if kind == ObjectKind::Transaction && part_kind == UdmPartKind::WebForm {
                if let Some(is_default) = self
                    .client
                    .get_entity_version(
                        comp.component_entity_type_id,
                        comp.component_entity_id,
                        comp.component_entity_version_id,
                    )?
                    .and_then(|version| version.properties)
                    .and_then(|properties| webform_is_default(&properties))
                {
                    object.source_parts.push(SourcePart {
                        name: WEBFORM_DEFAULT_PART.to_string(),
                        source: is_default.to_string(),
                    });
                }
            }
        }

        // Hydrate cross-references from ModelCrossReference. Only targets that are
        // objects of this model are references; a part is never a call target.
        let xr_records = self.client.get_cross_references(
            self.model_id,
            record.entity_type_id,
            record.entity_id,
        )?;

        for xr in xr_records {
            let Some(target) = targets.get(&(xr.to_entity_type_id, xr.to_entity_id)) else {
                // A part is never a call target, so a reference to a non-object row is not a fact about
                // this object. A reference to an object type whose row the model does not serve is
                // dangling: the object is refused by name, never built with the edge dropped.
                if resolver.object_kind(xr.to_entity_type_id).is_some() {
                    object.refusal.get_or_insert(format!(
                        "reference to object {} of type {} has no served row in the model (dangling)",
                        xr.to_entity_id, xr.to_entity_type_id
                    ));
                }
                continue;
            };
            let target_id = &target.id;
            let ref_kind = match xr.reference_type {
                1 => ReferenceKind::Calls,
                2 => ReferenceKind::Uses,
                3 => ReferenceKind::BasedOn,
                4 => ReferenceKind::Contains,
                _ => ReferenceKind::Uses,
            };
            // The catalog holds each (kind, target) once. The cross-reference table can
            // hold the same pair under two link types; that is one fact, not two.
            let reference = Reference {
                kind: ref_kind,
                target: target_id.clone(),
            };
            if !object.references.contains(&reference) {
                object.references.push(reference);
            }
        }

        // A WebPanel's own settings (Description, MasterPage, WEB_COMP) live in its version
        // properties, not in a part; a Transaction's declared name and description the same way. They are held as the synthetic settings part, so the emitter
        // decides from what the KB stores. A read error refuses the panel by name.
        if matches!(kind, ObjectKind::WebPanel | ObjectKind::Transaction) {
            let version = self.client.get_entity_version(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )?;
            match version {
                Some(version) => {
                    match settings_from_properties(record.entity_id, version.properties.as_deref())
                        .and_then(|settings| settings.to_part())
                    {
                        Ok(part) => object.source_parts.push(part),
                        Err(error) => {
                            object.refusal.get_or_insert(format!("PROPERTIES: {error}"));
                        }
                    }
                }
                None => {
                    object
                        .refusal
                        .get_or_insert("PROPERTIES: its active version record is missing".into());
                }
            }
        }

        Ok(object)
    }
}

impl UdmReader {
    fn data_provider_output(
        &self,
        record: &ModelEntityVersionRecord,
        resolver: &EntityTypeResolver,
        targets: &BTreeMap<(i32, i32), Target>,
    ) -> Result<(SourcePart, ObjectId), String> {
        use crate::csharp::data_provider::{DataProviderOutput, OUTPUT_PART, output_reference};
        let version = self
            .client
            .get_entity_version(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )?
            .ok_or("DataProvider active version is missing")?;
        let id = output_reference(version.properties.as_deref())?;
        let ty = resolver.object_type_id(ObjectKind::StructuredDataType)?;
        let target = targets
            .get(&(ty, id))
            .ok_or_else(|| format!("DataProvider output SDT {id} is not in this model"))?;
        let data_type = match self.root_collections(targets, resolver)?.get(&id) {
            Some(root) => DataType::BaseCollection {
                schema: format!("{}_{}", root.name, root.item),
                item: root.item.clone(),
                namespace: root
                    .namespace
                    .clone()
                    .ok_or("DataProvider output collection namespace is missing")?,
            },
            None => DataType::Record {
                schema: target.name.clone(),
            },
        };
        let output = DataProviderOutput {
            name: target.name.clone(),
            data_type,
        };
        Ok((
            SourcePart {
                name: OUTPUT_PART.into(),
                source: serde_json::to_string(&output).map_err(|e| e.to_string())?,
            },
            target.id.clone(),
        ))
    }

    /// Holds the decoded structure of an SDT in place of its raw `STRUCTURE` text. A refusal sets the
    /// object's refusal, naming the SDT and the node, and holds no part: the emitter never reads a
    /// structure it could not prove.
    /// The XML namespace the SDK writes on an SDT's classes: the SDT's stored `ExternalNamespace` when it
    /// stores a non-empty one, else the Knowledge Base's name; `None` when neither is read.
    fn sdt_xml_namespace(&self, record: &ModelEntityVersionRecord) -> Option<String> {
        let external = self
            .client
            .get_entity_version(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )
            .ok()
            .flatten()
            .and_then(|rec| rec.properties)
            .and_then(|raw| stored_variables::named_property(&raw, "ExternalNamespace").ok())
            .flatten()
            .filter(|namespace| !namespace.is_empty());
        external.or_else(|| self.client.knowledge_base_name().ok().flatten())
    }

    fn held_sdt_structure(
        &self,
        object: &mut KbObject,
        structure: &EntityVersionCompositionRecord,
        stored: String,
        targets: &BTreeMap<(i32, i32), Target>,
        resolver: &EntityTypeResolver,
        xml_namespace: Option<&str>,
    ) -> Option<String> {
        let root_collections = match self.root_collections(targets, resolver) {
            Ok(roots) => roots,
            Err(error) => {
                object.refusal.get_or_insert(format!("STRUCTURE: {error}"));
                return None;
            }
        };
        let sdt_type = resolver.object_type_id(ObjectKind::StructuredDataType).ok();
        let sdt_name = |id: i32| {
            if root_collections.contains_key(&id) {
                return None;
            }
            sdt_type
                .and_then(|ty| targets.get(&(ty, id)))
                .map(|target| target.name.clone())
        };
        let domain_type_id = resolver.object_type_id(ObjectKind::Domain).ok();
        let domain_type_of =
            |id: i32| domain_type_id.and_then(|ty| self.domain_type(ty, id, targets));
        let sdt = object.name.clone();
        let decoded = self
            .sdt_structure_source(structure, &stored, &sdt_name, &domain_type_of, &sdt)
            .and_then(|decoded| {
                let json = decoded.to_portable_json_in(xml_namespace)?;
                Ok((json, decoded.sdt_entity_ids, decoded.domain_entity_ids))
            });
        match decoded {
            Ok((json, sdt_ids, domain_ids)) => {
                for id in sdt_ids {
                    if let Some(target) = sdt_type.and_then(|ty| targets.get(&(ty, id))) {
                        let reference = Reference {
                            kind: ReferenceKind::Uses,
                            target: target.id.clone(),
                        };
                        if !object.references.contains(&reference) {
                            object.references.push(reference);
                        }
                    }
                }
                for id in domain_ids {
                    if let Some(target) = domain_type_id.and_then(|ty| targets.get(&(ty, id))) {
                        let reference = Reference {
                            kind: ReferenceKind::BasedOn,
                            target: target.id.clone(),
                        };
                        if !object.references.contains(&reference) {
                            object.references.push(reference);
                        }
                    }
                }
                Some(json)
            }
            Err(error) => {
                object.refusal.get_or_insert(format!("STRUCTURE: {error}"));
                None
            }
        }
    }

    /// Holds the portable structure of a Transaction in place of its raw `STRUCTURE` text. The stored form
    /// names each attribute by the entity id of its Attribute object, so the names are the model's targets.
    /// A refusal sets the object's refusal, naming the Transaction and the element, and holds no part.
    ///
    /// Only the native `Level` document is decoded. Any other well-formed text is held unchanged, as this
    /// reader always held it, and the physical-schema stage refuses it by name if it is not a Transaction
    /// structure.
    fn held_transaction_structure(
        &self,
        object: &mut KbObject,
        stored: String,
        targets: &BTreeMap<(i32, i32), Target>,
        resolver: &EntityTypeResolver,
    ) -> Option<String> {
        if !stored_transaction::is_native_level(&stored) {
            return Some(stored);
        }
        let attribute_type = resolver.object_type_id(ObjectKind::Attribute).ok();
        let attribute_name = |id: i32| {
            attribute_type
                .and_then(|ty| targets.get(&(ty, id)))
                .map(|target| target.name.clone())
        };
        // An attribute's row is read at the version the model serves, and decoded by the attribute allowlist.
        // Every other attribute the type or the link needs is read the same way; none falls back to an older
        // version, so a missing or unreadable row refuses the Transaction.
        let attribute_row_of = |id: i32| -> Result<stored_variables::AttributeRow, String> {
            let ty =
                attribute_type.ok_or("the Attribute object type is not in this Knowledge Base")?;
            let target = targets
                .get(&(ty, id))
                .ok_or("its Attribute object is not in the catalog")?;
            let row = self
                .client
                .get_entity_version(target.entity_type_id, id, target.entity_version_id)?
                .ok_or("its Attribute row is not stored at the composed version")?;
            let properties = row
                .properties
                .as_deref()
                .ok_or("its Attribute row stores no properties")?;
            stored_variables::attribute_row(properties)
        };
        // A key that stores no type of its own inherits the type of the key its SuperTypeKey names, through the
        // chain of untyped keys that leads to a stored type (`stored_variables::resolve_attribute_type`).
        let attribute_type_of = |id: i32| -> Result<DataType, String> {
            let row = attribute_row_of(id)?;
            stored_variables::resolve_attribute_type(id, &row, &attribute_row_of)
        };
        // The name of the key that a SuperTypeKey names, from the same served row. The physical schema derives
        // the foreign key from it; the reference is not checked by generated code.
        let attribute_super_key = |id: i32| -> Result<Option<String>, String> {
            let row = attribute_row_of(id)?;
            let Some(referenced) = row.super_type_key else {
                return Ok(None);
            };
            attribute_name(referenced).map(Some).ok_or_else(|| {
                format!(
                    "its SuperTypeKey references attribute {referenced}, which has no stored name"
                )
            })
        };
        let lookup = stored_transaction::Lookup {
            attribute_name: &attribute_name,
            attribute_type: &attribute_type_of,
            attribute_super_key: &attribute_super_key,
        };
        let transaction = object.name.clone();
        let decoded = stored_transaction::decode(&stored, &transaction, &lookup).and_then(|s| {
            serde_json::to_string(&s)
                .map_err(|e| format!("cannot write the Transaction structure: {e}"))
        });
        match decoded {
            Ok(json) => Some(json),
            Err(error) => {
                object.refusal.get_or_insert(format!("STRUCTURE: {error}"));
                None
            }
        }
    }

    /// The portable type of a domain: its own stored type row, decoded with the same code map as a
    /// declaration. The row is read with the Domain allowlist (`domain_property_map`), not the SDT
    /// node one: a Domain row carries a folder and enumeration properties the node list refuses.
    ///
    /// Only a typed domain has a type here (`stored_variables::domain_data_type`). An enumeration
    /// (no `ATTCUSTOMTYPE`) has none, so a based-on item of one is refused: its SDK length is not a
    /// stored value.
    fn domain_type(
        &self,
        domain_type_id: i32,
        id: i32,
        targets: &BTreeMap<(i32, i32), Target>,
    ) -> Option<DataType> {
        let target = targets.get(&(domain_type_id, id))?;
        let row = self
            .client
            .get_entity_version(target.entity_type_id, id, target.entity_version_id)
            .ok()??;
        stored_variables::domain_data_type(row.properties.as_deref()?)
    }

    /// The stored row of each level and item of one SDT structure, keyed by EntityId, each read at the
    /// version the structure composes. Levels and items have separate id spaces in the stored form, and
    /// the served XML names both by Id, so a collision between a level and an item is refused.
    fn structure_node_rows(
        &self,
        structure: &EntityVersionCompositionRecord,
        sdt: &str,
    ) -> Result<BTreeMap<i32, String>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let level_type = resolver.node_type_id("SDTLevelEntity")?;
        let item_type = resolver.node_type_id("SDTItemEntity")?;
        let nodes = self.client.get_compositions(
            structure.component_entity_type_id,
            structure.component_entity_id,
            structure.component_entity_version_id,
        )?;
        let mut node_rows: BTreeMap<i32, String> = BTreeMap::new();
        for node in nodes {
            let entity_type = node.component_entity_type_id;
            if entity_type != level_type && entity_type != item_type {
                continue;
            }
            let Some(row) = self.client.get_entity_version(
                entity_type,
                node.component_entity_id,
                node.component_entity_version_id,
            )?
            else {
                return Err(format!(
                    "node {} of SDT '{sdt}' has no EntityVersion row at its composed version",
                    node.component_entity_id
                ));
            };
            let properties = row.properties.ok_or_else(|| {
                format!(
                    "node {} of SDT '{sdt}' has no stored properties",
                    node.component_entity_id
                )
            })?;
            if node_rows
                .insert(node.component_entity_id, properties)
                .is_some()
            {
                return Err(format!(
                    "SDT '{sdt}' has two nodes with id {}; the served structure cannot name them apart",
                    node.component_entity_id
                ));
            }
        }
        Ok(node_rows)
    }

    /// The SDTs of this model whose root is a collection, read once per reader. Only the root of each
    /// structure is read (its item name and collection flag), so no nested level is decoded here.
    fn root_collections(
        &self,
        targets: &BTreeMap<(i32, i32), Target>,
        resolver: &EntityTypeResolver,
    ) -> Result<BTreeMap<i32, stored_variables::CollectionRoot>, String> {
        match self
            .root_collections
            .get_or_init(|| self.compute_root_collections(targets, resolver))
        {
            Ok(roots) => Ok(roots.clone()),
            Err(error) => Err(error.clone()),
        }
    }

    fn compute_root_collections(
        &self,
        targets: &BTreeMap<(i32, i32), Target>,
        resolver: &EntityTypeResolver,
    ) -> Result<BTreeMap<i32, stored_variables::CollectionRoot>, String> {
        let mut roots = BTreeMap::new();
        // The Knowledge Base's name, the XML namespace of an SDT that declares none. A name that cannot be
        // read is left unknown, and a variable of such an SDT is refused where it is used.
        let kb_name = self.client.knowledge_base_name().ok().flatten();
        let active = self.client.get_active_objects(self.model_id)?;
        for record in &active {
            if resolver.object_kind(record.entity_type_id) != Some(ObjectKind::StructuredDataType) {
                continue;
            }
            let Some(sdt) = targets
                .get(&(record.entity_type_id, record.entity_id))
                .map(|target| target.name.clone())
            else {
                continue;
            };
            let compositions = self.client.get_compositions(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )?;
            for comp in compositions {
                if resolver.part_kind(comp.component_entity_type_id) != Some(UdmPartKind::Structure)
                {
                    continue;
                }
                let stored = self.component_text(&comp, UdmPartKind::Structure)?;
                let verdict = self.structure_node_rows(&comp, &sdt).and_then(|node_rows| {
                    let node_properties = |id: i32| node_rows.get(&id).cloned();
                    stored_sdt::root_collection_item(&stored, &sdt, &node_properties)
                });
                // Only a root that reads as a collection is indexed. A structure that does not decode is
                // refused as an object when it is read on its own, and its members fail closed, as for any
                // SDT that does not build; it does not stop the Procedures that do not use it.
                if let Ok(Some(item)) = verdict {
                    // The SDT's own `ExternalNamespace` (SidebarItems: `GeneXusUnanimo`), else the KB's name.
                    let external = self
                        .client
                        .get_entity_version(
                            record.entity_type_id,
                            record.entity_id,
                            record.entity_version_id,
                        )
                        .ok()
                        .flatten()
                        .and_then(|rec| rec.properties)
                        .and_then(|raw| {
                            stored_variables::named_property(&raw, "ExternalNamespace").ok()
                        })
                        .flatten()
                        .filter(|namespace| !namespace.is_empty());
                    roots.insert(
                        record.entity_id,
                        stored_variables::CollectionRoot {
                            name: sdt.clone(),
                            item,
                            namespace: external.or_else(|| kb_name.clone()),
                        },
                    );
                }
            }
        }
        Ok(roots)
    }

    /// Decodes the stored structure of one SDT into the portable JSON the emitter reads.
    ///
    /// The served `STRUCTURE` text names each level and item by id; the composition of the
    /// `SDTStructure` part gives each of those nodes its exact version, and that version's row holds
    /// the node's stored type. Every node is read at its own composed version, never at a guessed
    /// one. Any node the decoder cannot prove refuses the SDT, naming the SDT and the node.
    fn sdt_structure_source(
        &self,
        structure: &EntityVersionCompositionRecord,
        stored: &str,
        sdt_name: &dyn Fn(i32) -> Option<String>,
        domain_type: &dyn Fn(i32) -> Option<DataType>,
        sdt: &str,
    ) -> Result<stored_sdt::SdtStructure, String> {
        let node_rows = self.structure_node_rows(structure, sdt)?;
        let node_properties = |id: i32| node_rows.get(&id).cloned();
        let lookup = stored_sdt::Lookup {
            node_properties: &node_properties,
            sdt_name,
            domain_type,
        };
        stored_sdt::decode(stored, sdt, &lookup)
    }

    /// The stored SDT level with the given entity id, from this model's SDT structures. The map is read
    /// once per reader, on the first Procedure variable that is typed by a nested level.
    fn nested_level(
        &self,
        id: i32,
        targets: &BTreeMap<(i32, i32), Target>,
        resolver: &EntityTypeResolver,
    ) -> Result<Option<stored_variables::NestedLevel>, String> {
        match self
            .nested_levels
            .get_or_init(|| self.compute_nested_levels(targets, resolver))
        {
            Ok(levels) => Ok(levels.get(&id).cloned()),
            Err(error) => Err(error.clone()),
        }
    }

    fn compute_nested_levels(
        &self,
        targets: &BTreeMap<(i32, i32), Target>,
        resolver: &EntityTypeResolver,
    ) -> Result<BTreeMap<i32, stored_variables::NestedLevel>, String> {
        let root_collections = self.root_collections(targets, resolver)?;
        let sdt_type = resolver.object_type_id(ObjectKind::StructuredDataType).ok();
        let domain_type_id = resolver.object_type_id(ObjectKind::Domain).ok();
        let sdt_name = |id: i32| {
            if root_collections.contains_key(&id) {
                return None;
            }
            sdt_type
                .and_then(|ty| targets.get(&(ty, id)))
                .map(|target| target.name.clone())
        };
        let domain_of = |id: i32| domain_type_id.and_then(|ty| self.domain_type(ty, id, targets));
        let active = self.client.get_active_objects(self.model_id)?;
        let mut levels = BTreeMap::new();
        for record in &active {
            if resolver.object_kind(record.entity_type_id) != Some(ObjectKind::StructuredDataType) {
                continue;
            }
            let Some(sdt) = targets
                .get(&(record.entity_type_id, record.entity_id))
                .map(|target| target.name.clone())
            else {
                continue;
            };
            let compositions = self.client.get_compositions(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )?;
            for comp in compositions {
                if resolver.part_kind(comp.component_entity_type_id) != Some(UdmPartKind::Structure)
                {
                    continue;
                }
                let stored = self.component_text(&comp, UdmPartKind::Structure)?;
                // An SDT whose structure does not decode is refused as an object when it is read on
                // its own. Its levels stay unmapped here, so a variable typed by one is refused by name.
                let Ok(decoded) =
                    self.sdt_structure_source(&comp, &stored, &sdt_name, &domain_of, &sdt)
                else {
                    continue;
                };
                for level in decoded.levels {
                    let entry = stored_variables::NestedLevel {
                        sdt_id: record.entity_id,
                        schema: level.schema,
                        dotted: level.dotted,
                        collection_item: level.collection_item,
                    };
                    if levels.insert(level.id, entry).is_some() {
                        return Err(format!(
                            "nested SDT level {} is held by two structures; refusing to choose one",
                            level.id
                        ));
                    }
                }
            }
        }
        Ok(levels)
    }
}

/// The refusal of a non-Procedure object whose active version has no parts. The engine reads only the active
/// version, and an empty object is refused rather than returned as if it were complete.
pub const NO_PARTS_REFUSAL: &str = "its active version has no parts; the engine reads only the active version and refuses an empty object rather than emit it";

/// The refusal of a Procedure whose active version has no SOURCE part among the composition rows the reader
/// reads. A Procedure is generated from its SOURCE part, so one without it is refused by name, never skipped.
pub const NO_SOURCE_REFUSAL: &str = "its active version has no SOURCE part among the parts the reader reads; the engine generates a Procedure only from its SOURCE and refuses it rather than emit it";

/// Whether the composition rows of an object's active version hold a SOURCE part the engine serves.
fn has_source_part(
    compositions: &[EntityVersionCompositionRecord],
    resolver: &EntityTypeResolver,
) -> bool {
    compositions
        .iter()
        .any(|c| resolver.part_kind(c.component_entity_type_id) == Some(UdmPartKind::Source))
}

/// Refuses an object whose active version lacks the part it is generated from, by name.
///
/// A Procedure is generated from its SOURCE part, so one without a SOURCE part is refused whatever other parts
/// it has. Any other kind with no parts is refused. A Module has no source of its own and is exempt (an unproven
/// assumption, unchanged here). A Procedure the Knowledge Base stores as `GenerateObject` False is returned before
/// this check, with its own reason (`object.not_generated`), and is not refused here. No object is marked not
/// generated from its parts: only the stored flag does that. Earlier versions are not read.
fn refuse_without_parts(object: &mut KbObject, kind: ObjectKind, parts: usize, has_source: bool) {
    if kind == ObjectKind::Module || object.refusal.is_some() || object.not_generated.is_some() {
        return;
    }
    if matches!(kind, ObjectKind::Procedure | ObjectKind::DataProvider) {
        if !has_source {
            object.refusal = Some(if kind == ObjectKind::Procedure {
                NO_SOURCE_REFUSAL.into()
            } else {
                "DataProvider active version has no SOURCE part; refusing an empty generator".into()
            });
        }
        return;
    }
    if parts == 0 {
        object.refusal = Some(NO_PARTS_REFUSAL.into());
    }
}

/// What a Procedure's VARIABLES decode reads from the Knowledge Base: the SDT and Domain type ids (to
/// name each SDT and Domain a declaration uses), the targets, and the lookup that gives a Domain its
/// stored type.
struct VariablesLookup<'a> {
    sdt_type: Option<i32>,
    domain_type: Option<i32>,
    targets: &'a BTreeMap<(i32, i32), Target>,
    domain_of: &'a dyn Fn(i32) -> Option<DataType>,
    level_of: &'a dyn Fn(i32) -> Result<Option<stored_variables::NestedLevel>, String>,
    /// Read collection metadata only when an SDT variable needs it; failures remain errors.
    collection_of: &'a dyn Fn(i32) -> Result<Option<stored_variables::CollectionRoot>, String>,
}

/// The source the catalog holds for one stored part. A Procedure's stored VARIABLES are decoded
/// into the portable declarations, and the SDTs they name become explicit `Uses` references. A
/// declaration the engine does not map refuses the object, naming the variable; the part is not
/// held, so no emitter can read it as if it were complete.
fn held_source(
    object: &mut KbObject,
    kind: ObjectKind,
    part_kind: UdmPartKind,
    stored: String,
    lookup: &VariablesLookup<'_>,
) -> Option<String> {
    if !matches!(kind, ObjectKind::Procedure | ObjectKind::DataProvider)
        || part_kind != UdmPartKind::Variables
    {
        return Some(stored);
    }
    let targets = lookup.targets;
    let sdt_name = |id: i32| {
        match (lookup.collection_of)(id) {
            Ok(None) => {}
            // An unreadable root is not a record. The fallible collection lookup below
            // propagates its error instead of accepting an unproven type.
            Ok(Some(_)) | Err(_) => return None,
        }
        lookup
            .sdt_type
            .and_then(|ty| targets.get(&(ty, id)))
            .map(|target| target.name.clone())
    };
    let decoded = match stored_variables::decode_with_collections(
        &stored,
        &sdt_name,
        lookup.collection_of,
        lookup.domain_of,
        lookup.level_of,
    ) {
        Ok(decoded) => decoded,
        Err(error) => {
            object.refusal.get_or_insert(format!("VARIABLES: {error}"));
            return None;
        }
    };
    for id in decoded.sdt_entity_ids {
        let Some(target) = lookup.sdt_type.and_then(|ty| targets.get(&(ty, id))) else {
            continue;
        };
        let reference = Reference {
            kind: ReferenceKind::Uses,
            target: target.id.clone(),
        };
        if !object.references.contains(&reference) {
            object.references.push(reference);
        }
    }
    // A based-on variable takes its type from its Domain, so the Domain is an explicit `BasedOn`
    // reference, as a based-on SDT item's is.
    for id in decoded.domain_entity_ids {
        let Some(target) = lookup.domain_type.and_then(|ty| targets.get(&(ty, id))) else {
            continue;
        };
        let reference = Reference {
            kind: ReferenceKind::BasedOn,
            target: target.id.clone(),
        };
        if !object.references.contains(&reference) {
            object.references.push(reference);
        }
    }
    match serde_json::to_string(&decoded.variables) {
        Ok(portable) => Some(portable),
        Err(error) => {
            object.refusal.get_or_insert(format!(
                "VARIABLES cannot be written in portable form: {error}"
            ));
            None
        }
    }
}

/// The object whose name matches `name` (case-insensitive), with its kind. Parts and KB-tree
/// nodes that share the name are not objects and never match. A name held by more than one
/// object is ambiguous: it is refused, never resolved to one of them. `Type:Name` (for example
/// `Transaction:Customer`) keeps only the objects of that type, so a Transaction and the Table it
/// defines, which share a name, are each readable; an unknown type is refused.
fn find_object<'a>(
    records: &'a [ModelEntityVersionRecord],
    resolver: &EntityTypeResolver,
    name: &str,
) -> Result<Option<(&'a ModelEntityVersionRecord, ObjectKind)>, String> {
    let (wanted_kind, name) = match name.split_once(':') {
        Some((type_name, bare)) => {
            let kind = crate::kb_ops::kind_from_type_name(type_name)
                .ok_or_else(|| format!("unknown object type '{type_name}' in '{name}'"))?;
            (Some(kind), bare)
        }
        None => (None, name),
    };
    let mut found: Vec<(&ModelEntityVersionRecord, ObjectKind)> = records
        .iter()
        .filter_map(|record| {
            resolver
                .object_kind(record.entity_type_id)
                .filter(|kind| wanted_kind.is_none_or(|wanted| wanted == *kind))
                .filter(|_| record.name.eq_ignore_ascii_case(name))
                .map(|kind| (record, kind))
        })
        .collect();
    match found.len() {
        0 => Ok(None),
        1 => Ok(found.pop()),
        count => Err(format!(
            "'{name}' is ambiguous: {count} objects of this model have that name; refusing to pick one (qualify it as Type:Name)"
        )),
    }
}

/// Whether `record` is the Knowledge Base root module identified by the model.
fn is_system_root(record: &ModelEntityVersionRecord, system_root: Option<(i32, i32)>) -> bool {
    system_root == Some((record.entity_type_id, record.entity_id))
}

/// The catalog id of every served object of the model, keyed by its entity type and id. The
/// target of a reference is the id the target object carries, so a reference never points at a
/// name the object was not given.
///
/// Ids are unique by construction. Two objects may share a name (GeneXus allows two Modules named
/// alike under different parents), so a name held by more than one object takes the entity
/// identity as a suffix. Names cannot contain `-`, so a suffixed id never equals a plain name.
fn object_targets(
    records: &[ModelEntityVersionRecord],
    resolver: &EntityTypeResolver,
    system_root: Option<(i32, i32)>,
) -> BTreeMap<(i32, i32), Target> {
    let served: Vec<&ModelEntityVersionRecord> = records
        .iter()
        .filter(|record| resolver.object_kind(record.entity_type_id).is_some())
        .collect();
    let mut name_counts: BTreeMap<String, usize> = BTreeMap::new();
    for record in &served {
        if !is_system_root(record, system_root) {
            *name_counts
                .entry(record.name.to_ascii_lowercase())
                .or_default() += 1;
        }
    }
    served
        .into_iter()
        .filter_map(|record| {
            let id = if is_system_root(record, system_root) {
                ObjectId::new(SYSTEM_ROOT_OBJECT_ID).ok()?
            } else {
                let shared = name_counts
                    .get(&record.name.to_ascii_lowercase())
                    .is_some_and(|count| *count > 1);
                object_id(record, shared)
            };
            Some((
                (record.entity_type_id, record.entity_id),
                Target {
                    id,
                    name: record.name.clone(),
                    entity_type_id: record.entity_type_id,
                    entity_version_id: record.entity_version_id,
                },
            ))
        })
        .collect()
}

/// One served row as the catalog sees it: its catalog id, and the name the model holds it under.
/// The name is what a reference to the row must carry (an SDT is named in its variables).
#[derive(Debug, Clone)]
struct Target {
    id: ObjectId,
    name: String,
    /// The entity type of the row, and its active version. A domain's stored type is read at this
    /// version, the one the model holds.
    entity_type_id: i32,
    entity_version_id: i32,
}

/// The catalog id of one model row, from the map `object_targets` built.
fn target_of(
    targets: &BTreeMap<(i32, i32), Target>,
    record: &ModelEntityVersionRecord,
) -> Result<ObjectId, String> {
    targets
        .get(&(record.entity_type_id, record.entity_id))
        .map(|target| target.id.clone())
        .ok_or_else(|| {
            format!(
                "object {} of type {} has no catalog id",
                record.entity_id, record.entity_type_id
            )
        })
}

/// The catalog object of one model row, under the id the target map gives it. The Knowledge Base
/// root module is flagged as such, which is the only way the catalog accepts its display name.
fn object_for(
    record: &ModelEntityVersionRecord,
    kind: ObjectKind,
    id: ObjectId,
    is_root: bool,
) -> KbObject {
    let mut object = KbObject::new(id, &record.name, kind);
    object.system_root = is_root;
    object
}

/// The id of one object row. A shared name takes the entity identity as a suffix. A name that is
/// not a valid identity falls back to the entity identity, which is unique as well.
fn object_id(record: &ModelEntityVersionRecord, shared_name: bool) -> ObjectId {
    let identity = format!("{}-{}", record.entity_type_id, record.entity_id);
    let base = if shared_name {
        format!("{}-{identity}", record.name)
    } else {
        record.name.clone()
    };
    ObjectId::new(&base).unwrap_or_else(|_| ObjectId::new(format!("obj-{identity}")).unwrap())
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
                entity_type_id: 38, // Procedure
                entity_id: 200,
                entity_version_id: 1,
                name: "PChildProcess".to_string(),
            })
            .unwrap();

        // Main caller procedure
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: 100,
                entity_version_id: 1,
                name: "PMainCaller".to_string(),
            })
            .unwrap();

        // Source part (Procedure, id 100 in GeneXus 18)
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
                compound_entity_type_id: 38,
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
                compound_entity_type_id: 38,
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
                from_entity_type_id: 38,
                from_entity_id: 100,
                to_entity_type_id: 38,
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
                compound_entity_type_id: 47, // WebPanel
                compound_entity_id: 50,
                compound_entity_version_id: 1,
                component_entity_type_id: 97,
                component_entity_id: 501,
                component_entity_version_id: 1,
            })
            .unwrap();

        let reader = UdmReader::new(client);
        let events = reader
            .read_part(47, 50, 1, UdmPartKind::Events)
            .expect("read events part")
            .expect("part should exist");

        assert_eq!(events, code);

        let non_existent = reader
            .read_part(47, 50, 1, UdmPartKind::WebForm)
            .expect("read webform part");
        assert!(non_existent.is_none());
    }

    /// A component part outside the served space (Documentation, 95) used to be
    /// emitted as `UNKNOWN`; two of them made the catalog refuse the object.
    #[test]
    fn hydrate_skips_parts_outside_the_served_space_instead_of_naming_them_unknown() {
        let client = Arc::new(UdmClient::new_in_memory());
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: 100,
                entity_version_id: 1,
                name: "PWithDocs".to_string(),
            })
            .unwrap();
        for (component_type, component_id) in [(95, 1), (98, 2), (100, 3)] {
            client
                .insert_entity_version(EntityVersionRecord {
                    entity_type_id: component_type,
                    entity_id: component_id,
                    entity_version_id: 1,
                    data: encode_blob(&build_token_data_list("x = 1"), true).unwrap(),
                    properties: None,
                })
                .unwrap();
            client
                .insert_composition(EntityVersionCompositionRecord {
                    compound_entity_type_id: 38,
                    compound_entity_id: 100,
                    compound_entity_version_id: 1,
                    component_entity_type_id: component_type,
                    component_entity_id: component_id,
                    component_entity_version_id: 1,
                })
                .unwrap();
        }

        let reader = UdmReader::new(client);
        let objects = reader.read_all_objects().expect("hydrate");
        let names: Vec<&str> = objects[0]
            .source_parts
            .iter()
            .map(|p| p.name.as_str())
            .collect();
        assert_eq!(names, vec!["SOURCE"]);
        assert!(reader.read_catalog().is_ok());
    }

    /// An empty stored payload is an empty part, not a missing one.
    #[test]
    fn empty_payload_is_served_as_an_empty_part() {
        let client = Arc::new(UdmClient::new_in_memory());
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: 100,
                entity_version_id: 1,
                name: "PEmptyRules".to_string(),
            })
            .unwrap();
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 102,
                entity_id: 7,
                entity_version_id: 1,
                data: Vec::new(),
                properties: None,
            })
            .unwrap();
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 38,
                compound_entity_id: 100,
                compound_entity_version_id: 1,
                component_entity_type_id: 102,
                component_entity_id: 7,
                component_entity_version_id: 1,
            })
            .unwrap();

        let reader = UdmReader::new(client);
        let loaded = reader
            .load_object_by_name("PEmptyRules", Some(&["RULES".to_string()]))
            .expect("load")
            .expect("object");
        assert_eq!(loaded.object.source_parts.len(), 1);
        assert_eq!(loaded.object.source_parts[0].name, "RULES");
        assert_eq!(loaded.object.source_parts[0].source, "");
        assert_eq!(loaded.stored_parts, vec!["RULES"]);
    }

    /// A composition whose version row is missing is an inconsistent model. It is
    /// refused, never reported as an empty or absent part.
    #[test]
    fn composition_without_an_entity_version_row_fails_the_read() {
        let client = Arc::new(UdmClient::new_in_memory());
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: 100,
                entity_version_id: 1,
                name: "PDangling".to_string(),
            })
            .unwrap();
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 38,
                compound_entity_id: 100,
                compound_entity_version_id: 1,
                component_entity_type_id: 100,
                component_entity_id: 9,
                component_entity_version_id: 1,
            })
            .unwrap();

        let reader = UdmReader::new(client);
        let error = reader.load_object_by_name("PDangling", None).unwrap_err();
        assert!(error.contains("no EntityVersion row"), "{error}");
    }

    /// `WWTrn353` (KBTeste) has two cross-reference rows to one target under the same
    /// reference type and different link types. The catalog allows one (kind, target) pair,
    /// so the reader must emit it once instead of refusing the object.
    #[test]
    fn repeated_cross_reference_pair_is_one_reference() {
        let client = Arc::new(UdmClient::new_in_memory());
        for (id, name) in [(100, "PCaller"), (200, "PCallee")] {
            client
                .insert_model_entity_version(ModelEntityVersionRecord {
                    model_id: 1,
                    entity_type_id: 38,
                    entity_id: id,
                    entity_version_id: 1,
                    name: name.to_string(),
                })
                .unwrap();
        }
        let row = ModelCrossReferenceRecord {
            model_id: 1,
            from_entity_type_id: 38,
            from_entity_id: 100,
            to_entity_type_id: 38,
            to_entity_id: 200,
            reference_type: 0,
        };
        client.insert_cross_reference(row.clone()).unwrap();
        client.insert_cross_reference(row).unwrap();

        let catalog = UdmReader::new(client).read_catalog().expect("catalog");
        let caller = catalog
            .get(&ObjectId::new("pcaller").unwrap())
            .expect("caller");
        assert_eq!(caller.references.len(), 1, "{:?}", caller.references);
    }

    /// Two objects that map to one catalog id would overwrite each other in the catalog.
    /// The read must refuse, never drop one of them.
    #[test]
    fn two_procedures_with_one_name_are_refused_not_dropped() {
        let client = Arc::new(UdmClient::new_in_memory());
        for id in [100, 101] {
            client
                .insert_model_entity_version(ModelEntityVersionRecord {
                    model_id: 1,
                    entity_type_id: 38,
                    entity_id: id,
                    entity_version_id: 1,
                    name: "PDup".to_string(),
                })
                .unwrap();
        }
        // Two Procedures of one name would be two classes of one name: the catalog refuses the
        // pair by name. Neither object is dropped.
        let error = UdmReader::new(client).read_catalog().unwrap_err();
        assert!(error.contains("duplicate object name 'PDup'"), "{error}");
    }

    /// A token list that does not extract must fail; the raw XML is never served as
    /// the part's source.
    #[test]
    fn malformed_token_list_is_an_error_not_raw_xml() {
        let client = Arc::new(UdmClient::new_in_memory());
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 38,
                entity_id: 100,
                entity_version_id: 1,
                name: "PBrokenToken".to_string(),
            })
            .unwrap();
        let broken = "<TokenDataList><Word>unclosed";
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 100,
                entity_id: 8,
                entity_version_id: 1,
                data: encode_blob(broken, true).unwrap(),
                properties: None,
            })
            .unwrap();
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 38,
                compound_entity_id: 100,
                compound_entity_version_id: 1,
                component_entity_type_id: 100,
                component_entity_id: 8,
                component_entity_version_id: 1,
            })
            .unwrap();

        let reader = UdmReader::new(client);
        assert!(reader.load_object_by_name("PBrokenToken", None).is_err());
    }

    #[test]
    fn a_procedure_with_no_source_part_is_refused_by_name_not_skipped() {
        // KBTeste Plan072Proc (38|15), GenerateObject True: its active version has no SOURCE part among the
        // composition rows the reader reads. The engine refuses it by name and never marks it not generated.
        let mut procedure = crate::catalog::KbObject::new(
            crate::catalog::ObjectId::new("plan072proc").unwrap(),
            "Plan072Proc",
            ObjectKind::Procedure,
        );
        refuse_without_parts(&mut procedure, ObjectKind::Procedure, 0, false);
        assert_eq!(procedure.refusal.as_deref(), Some(NO_SOURCE_REFUSAL));
        assert!(
            procedure.not_generated.is_none(),
            "{:?}",
            procedure.not_generated
        );
    }

    #[test]
    fn a_procedure_with_parts_but_no_source_part_is_refused_by_name() {
        let mut procedure = crate::catalog::KbObject::new(
            crate::catalog::ObjectId::new("rulesonly").unwrap(),
            "RulesOnly",
            ObjectKind::Procedure,
        );
        refuse_without_parts(&mut procedure, ObjectKind::Procedure, 2, false);
        assert_eq!(procedure.refusal.as_deref(), Some(NO_SOURCE_REFUSAL));
        assert!(procedure.not_generated.is_none());
    }

    #[test]
    fn a_procedure_with_a_source_part_is_not_refused_by_the_source_rule() {
        let mut procedure = crate::catalog::KbObject::new(
            crate::catalog::ObjectId::new("withsource").unwrap(),
            "WithSource",
            ObjectKind::Procedure,
        );
        refuse_without_parts(&mut procedure, ObjectKind::Procedure, 1, true);
        assert!(procedure.refusal.is_none(), "{:?}", procedure.refusal);
        assert!(procedure.not_generated.is_none());
    }

    #[test]
    fn a_module_with_no_parts_is_exempt() {
        let mut module = crate::catalog::KbObject::new(
            crate::catalog::ObjectId::new("folder").unwrap(),
            "Folder",
            ObjectKind::Module,
        );
        refuse_without_parts(&mut module, ObjectKind::Module, 0, false);
        assert!(module.refusal.is_none(), "{:?}", module.refusal);
    }

    #[test]
    fn a_generate_object_false_procedure_with_no_parts_stays_not_generated() {
        // The stored GenerateObject False decided the object before the parts were read: its reason stays,
        // and no refusal is added on top of it.
        let mut procedure = crate::catalog::KbObject::new(
            crate::catalog::ObjectId::new("plan072proc").unwrap(),
            "Plan072Proc",
            ObjectKind::Procedure,
        );
        procedure.not_generated = Some("GenerateObject is False".into());
        refuse_without_parts(&mut procedure, ObjectKind::Procedure, 0, false);
        assert_eq!(
            procedure.not_generated.as_deref(),
            Some("GenerateObject is False")
        );
        assert!(procedure.refusal.is_none(), "{:?}", procedure.refusal);
    }

    #[test]
    fn primitive_procedure_reads_do_not_scan_unrelated_sdt_roots() {
        let client = Arc::new(UdmClient::new_in_memory());
        let mut procedure = KbObject::new(
            ObjectId::new("PrimitiveOnly").unwrap(),
            "PrimitiveOnly",
            ObjectKind::Procedure,
        );
        procedure.source_parts = vec![
            SourcePart { name: "SOURCE".into(), source: "&Flag = True".into() },
            SourcePart { name: "VARIABLES".into(), source: "<Variables><Variable Name=\"Flag\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;15&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property></Properties></Variable></Variables>".into() },
        ];
        crate::udm::writer::UdmWriter::new(client.clone())
            .create_object(&procedure)
            .unwrap();
        for mode in 0..3 {
            let reader = UdmReader::new(client.clone());
            match mode {
                0 => {
                    assert!(
                        reader
                            .read_object_by_name("PrimitiveOnly")
                            .unwrap()
                            .unwrap()
                            .refusal
                            .is_none()
                    );
                }
                1 => {
                    reader
                        .load_object_by_name("PrimitiveOnly", None)
                        .unwrap()
                        .unwrap();
                }
                _ => {
                    reader
                        .read_closure_by_name("PrimitiveOnly")
                        .unwrap()
                        .unwrap();
                }
            }
            assert!(
                reader.root_collections.get().is_none(),
                "mode {mode} scanned every SDT for primitive variables"
            );
        }
    }

    #[test]
    fn a_structured_data_type_with_no_parts_stays_refused() {
        let mut sdt = crate::catalog::KbObject::new(
            crate::catalog::ObjectId::new("emptysdt").unwrap(),
            "EmptySdt",
            ObjectKind::StructuredDataType,
        );
        refuse_without_parts(&mut sdt, ObjectKind::StructuredDataType, 0, false);
        assert!(sdt.refusal.is_some());
        assert!(sdt.not_generated.is_none());
    }
}

/// The synthetic part holding the stored `Id` of each variable of a Procedure, by name, as JSON.
pub const VARIABLE_IDS_PART: &str = "VARIABLE_IDS";

/// The synthetic part holding a Transaction web form's `IsDefault` flag (`true` or `false`).
pub const WEBFORM_DEFAULT_PART: &str = "WEBFORM_DEFAULT";

/// The synthetic part holding the stored row (`<Properties>`) of an enumerated Domain of the model.
pub const DOMAIN_ENUM_PART: &str = "DOMAIN_ENUM";

/// Whether a stored Domain row is an enumeration of the model: it stores `IDEnumDefinedValues` and is not
/// a library interface (`ObjectIsInterface` True), whose values class the GeneXus runtime ships.
fn enum_domain_row(properties: &str) -> Result<bool, String> {
    if stored_variables::named_property(properties, "IDEnumDefinedValues")?.is_none() {
        return Ok(false);
    }
    Ok(
        stored_variables::named_property(properties, "ObjectIsInterface")?.as_deref()
            != Some("True"),
    )
}

/// The `IsDefault` property of stored web-form properties, when it is stored as `True` or `False`.
fn webform_is_default(properties: &str) -> Option<bool> {
    let document = roxmltree::Document::parse(properties).ok()?;
    let property = document.descendants().find(|node| {
        node.has_tag_name("Property")
            && node
                .children()
                .find(|c| c.has_tag_name("Name"))
                .and_then(|c| c.text())
                == Some("IsDefault")
    })?;
    match property
        .children()
        .find(|c| c.has_tag_name("Value"))
        .and_then(|c| c.text())
    {
        Some("True") => Some(true),
        Some("False") => Some(false),
        _ => None,
    }
}

#[cfg(test)]
mod webform_default_tests {
    use super::webform_is_default;

    #[test]
    fn the_stored_is_default_flag_is_read_and_anything_else_is_not() {
        let p = |v: &str| {
            format!(
                "<Properties><Property><Name>IsDefault</Name><Value>{v}</Value></Property></Properties>"
            )
        };
        assert_eq!(webform_is_default(&p("True")), Some(true));
        assert_eq!(webform_is_default(&p("False")), Some(false));
        assert_eq!(webform_is_default(&p("maybe")), None);
        assert_eq!(webform_is_default("<Properties/>"), None);
        assert_eq!(webform_is_default("not xml"), None);
    }
}

#[cfg(test)]
mod enum_domain_row_tests {
    use super::enum_domain_row;

    const VALUES: &str = "<Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Rows&lt;/Name&gt;&lt;Description&gt;Rows&lt;/Description&gt;&lt;Value&gt;10&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property>";

    fn row(extra: &str) -> String {
        format!(
            "<Properties><Property><Name>Name</Name><Value>Page</Value></Property>{extra}<Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"
        )
    }

    #[test]
    fn only_an_enumeration_of_the_model_has_a_values_class() {
        // KBTeste's General.Page: an enumeration with no interface flag.
        assert_eq!(enum_domain_row(&row(VALUES)), Ok(true));
        // A library enumeration (`ObjectIsInterface` True): the GeneXus runtime ships its class.
        let interface = format!(
            "{VALUES}<Property><Name>ObjectIsInterface</Name><Value>True</Value></Property>"
        );
        assert_eq!(enum_domain_row(&row(&interface)), Ok(false));
        // A Domain that is not an enumeration.
        assert_eq!(enum_domain_row(&row("")), Ok(false));
        // A repeated property is an error, never a guess.
        assert!(enum_domain_row(&row(&format!("{VALUES}{VALUES}"))).is_err());
    }
}
