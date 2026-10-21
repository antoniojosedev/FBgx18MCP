//! Hydration of one active object: its component parts, references and synthetic parts.

use super::*;

impl UdmReader {
    /// The text of one component part. A composition that names a version with no
    /// `EntityVersion` row is an inconsistent model and fails the read: it is
    /// never reported as an empty or absent part. An empty payload is an empty part.
    pub(super) fn component_text(
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

    pub(super) fn hydrate_object(
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
