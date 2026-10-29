//! Current-environment reads only when storage has exactly one candidate for the active version.

use super::UdmClient;
#[cfg(test)]
use super::live_rows::decode_utf16_le;
use crate::udm::version_scope::{ActiveVersion, ModelTopologyRow, ScopeUse, select_design_model};

#[derive(Debug, Clone, PartialEq, Eq)]
struct EnvironmentName {
    model_id: i32,
    name: String,
}

impl UdmClient {
    /// A fresh native environment name, without inferring the SDK's choice among multiple targets.
    /// Scope selection and environment enumeration share one private read-only transaction. This is
    /// deliberately not a build-environment selector and never persists an active environment.
    pub fn sole_environment_name(&self, active: Option<ActiveVersion>) -> Result<String, String> {
        self.with_read_view(|view| {
            let topology = view.model_topology()?;
            let model_id = environment_model(&topology, active)?;
            let names = view.environment_names(model_id)?;
            select_environment_name(model_id, &names)
        })
    }

    fn environment_names(&self, model_id: i32) -> Result<Vec<EnvironmentName>, String> {
        if !self.is_live() {
            return Err("the in-memory store has no environment-name metadata".into());
        }
        // The latest stored name of every model, like model_topology; absent or duplicated names for
        // this model never pick a default. Another model's unreadable name refuses the read too.
        Ok(self
            .entity_names("Udm.Types.Model")?
            .into_iter()
            .filter(|(entity_id, _)| *entity_id == model_id)
            .map(|(model_id, name)| EnvironmentName { model_id, name })
            .collect())
    }
}

fn environment_model(
    topology: &[ModelTopologyRow],
    active: Option<ActiveVersion>,
) -> Result<i32, String> {
    let design =
        select_design_model(topology, active, ScopeUse::Read).map_err(|refusal| refusal.0)?;
    let environments: Vec<_> = topology
        .iter()
        .filter(|row| row.parent_id == design)
        .collect();
    if environments.len() != 1 {
        return Err("current environment requires exactly one environment under the active Design model; SDK selection is required otherwise".into());
    }
    Ok(environments[0].model_id)
}

fn select_environment_name(model_id: i32, names: &[EnvironmentName]) -> Result<String, String> {
    let [row] = names else {
        return Err("environment name is missing or ambiguous in native storage".into());
    };
    if row.model_id != model_id
        || row.name.trim().is_empty()
        || row.name.chars().any(char::is_control)
    {
        return Err("environment name metadata does not identify the selected model".into());
    }
    Ok(row.name.clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn named(model_id: i32, name: &str) -> EnvironmentName {
        EnvironmentName {
            model_id,
            name: name.into(),
        }
    }

    fn model(model_id: i32, parent_id: i32, kb_version: Option<i32>) -> ModelTopologyRow {
        ModelTopologyRow {
            model_id,
            parent_id,
            kb_version,
        }
    }

    #[test]
    fn environment_selection_is_active_version_scoped_and_refuses_ambiguity() {
        let topology = [
            model(1, 0, Some(0)),
            model(2, 1, None),
            model(4, 0, Some(2)),
            model(5, 4, None),
        ];
        let active = Some(ActiveVersion {
            design_model_id: 4,
            kb_version_id: 2,
        });
        assert_eq!(environment_model(&topology, active).unwrap(), 5);
        assert!(environment_model(&topology, None).is_err());
        assert!(
            environment_model(
                &topology,
                Some(ActiveVersion {
                    design_model_id: 4,
                    kb_version_id: 0
                })
            )
            .is_err()
        );
        let mut duplicate = topology.to_vec();
        duplicate.push(model(6, 4, None));
        assert!(environment_model(&duplicate, active).is_err());
        assert!(environment_model(&[model(4, 0, Some(2))], active).is_err());
    }

    #[test]
    fn environment_name_preserves_stored_unicode_and_spacing() {
        let stored = " .NET ";
        assert_eq!(
            select_environment_name(2, &[named(2, stored)]).unwrap(),
            stored
        );
        let unicode = "Produ\u{e7}\u{e3}o \u{1f600}";
        let bytes: Vec<_> = unicode.encode_utf16().flat_map(u16::to_le_bytes).collect();
        assert_eq!(
            decode_utf16_le(&bytes, "environment name").unwrap(),
            unicode
        );
    }

    #[test]
    fn environment_name_refuses_absence_duplicates_and_wrong_model() {
        assert!(select_environment_name(2, &[]).is_err());
        assert!(select_environment_name(2, &[named(2, ".NET"), named(2, ".NET")]).is_err());
        for row in [named(3, ".NET"), named(2, " "), named(2, "a\nb")] {
            assert!(select_environment_name(2, &[row]).is_err());
        }
    }

    #[test]
    fn invalid_utf16_is_not_replaced_or_truncated() {
        assert!(decode_utf16_le(&[0], "environment name").is_err());
        assert!(decode_utf16_le(&[0, 0xd8], "environment name").is_err());
        assert_eq!(
            decode_utf16_le(&[0x3d, 0xd8, 0x00, 0xde], "environment name").unwrap(),
            "\u{1f600}"
        );
    }

    #[test]
    fn memory_without_environment_metadata_cannot_report_a_default() {
        assert!(
            UdmClient::new_in_memory()
                .sole_environment_name(None)
                .is_err()
        );
    }
}
