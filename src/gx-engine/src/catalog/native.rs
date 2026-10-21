//! Native input for legacy catalog commands. These commands have no SDK active-version argument,
//! so they may read only an unambiguous, single-Design-model KB, never silently default to trunk.

use std::{path::Path, sync::Arc};

use super::Catalog;
use crate::udm::{UdmClient, UdmReader, version_scope::ModelTopologyRow};

pub(super) fn load(path: &Path) -> Result<Catalog, String> {
    let client = UdmClient::connect_kb(path)?.with_persistent_reads();
    let topology = client.model_topology()?;
    read_catalog(client, &topology)
}

fn read_catalog(client: UdmClient, topology: &[ModelTopologyRow]) -> Result<Catalog, String> {
    let model = crate::udm::version_scope::select_design_model(
        topology,
        None,
        crate::udm::version_scope::ScopeUse::Read,
    )
    .map_err(|refusal| refusal.0)?;
    UdmReader::new(Arc::new(client.with_design_model_id(model))).read_catalog()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
        udm::UdmWriter,
    };

    #[test]
    fn native_catalog_commands_refuse_ambiguous_versions_before_reading_objects() {
        let topology = [
            ModelTopologyRow {
                model_id: 1,
                parent_id: 0,
                kb_version: Some(0),
            },
            ModelTopologyRow {
                model_id: 4,
                parent_id: 0,
                kb_version: Some(2),
            },
        ];
        let error = read_catalog(UdmClient::new_in_memory(), &topology).unwrap_err();
        assert!(error.contains("active version must be named"), "{error}");
    }

    #[test]
    fn native_catalog_commands_read_the_only_design_model_even_when_it_is_not_one() {
        let client = UdmClient::new_in_memory();
        let branch = client.clone().with_design_model_id(7);
        let mut object = KbObject::new(
            ObjectId::new("OnlyModelSeven").unwrap(),
            "OnlyModelSeven",
            ObjectKind::Procedure,
        );
        object.source_parts.push(SourcePart {
            name: "SOURCE".into(),
            source: "// model seven".into(),
        });
        UdmWriter::new(Arc::new(branch))
            .create_object(&object)
            .unwrap();
        let topology = [ModelTopologyRow {
            model_id: 7,
            parent_id: 0,
            kb_version: Some(0),
        }];
        let catalog = read_catalog(client, &topology).unwrap();
        let names: Vec<&str> = catalog
            .objects()
            .map(|object| object.name.as_str())
            .collect();
        assert_eq!(names, vec!["OnlyModelSeven"]);
    }
}
