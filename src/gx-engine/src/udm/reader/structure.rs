//! Structure sources of SDTs, Transactions and Data Providers, and the root/nested SDT level indexes.

use super::*;

impl UdmReader {
    pub(super) fn data_provider_output(
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
    pub(super) fn sdt_xml_namespace(&self, record: &ModelEntityVersionRecord) -> Option<String> {
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

    pub(super) fn held_sdt_structure(
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
    pub(super) fn held_transaction_structure(
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
    pub(super) fn domain_type(
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
    pub(super) fn structure_node_rows(
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
    pub(super) fn root_collections(
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

    pub(super) fn compute_root_collections(
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
    pub(super) fn sdt_structure_source(
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
    pub(super) fn nested_level(
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

    pub(super) fn compute_nested_levels(
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
