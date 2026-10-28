//! Which Design model a native operation reads.
//!
//! A Knowledge Base with versions stores one Design model per version (the trunk, every frozen version and
//! every development branch), each a root `UdmModel` row (`ModelParentId = 0`) whose `KBVersion` property
//! names its version (`0` for the trunk). Environment models (`.NET`, ...) are children of the Design model
//! they generate. The GeneXus SDK serves the Design model of the version its session has active, so a native
//! read that picks a model on its own can silently answer from another version. The model is therefore
//! selected here: from the active version the caller names, or, when none is named, only from a
//! Knowledge Base that has a single Design model. Anything else is refused before any object is read.

use crate::udm::reader::DEFAULT_DESIGN_MODEL_ID;

/// The KB version the SDK session has active, as the host reports it: the id of its Design model and the id
/// of the version (`KBVersion.Id`; the trunk's Design model stores `KBVersion` 0).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ActiveVersion {
    pub design_model_id: i32,
    pub kb_version_id: i32,
}

/// One `UdmModel` row with the `KBVersion` property its latest version stores (`None` when it stores none).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelTopologyRow {
    pub model_id: i32,
    pub parent_id: i32,
    pub kb_version: Option<i32>,
}

/// What the selected model is used for. A build also reads the generating environment, which the engine
/// resolves only for the trunk Design model with every environment under it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScopeUse {
    Read,
    Build,
}

/// Why no model was selected. The message names the topology and what would make it selectable; the
/// refusal is raised before any object is read or any output is written.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VersionScopeRefusal(pub String);

impl std::fmt::Display for VersionScopeRefusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

/// The version stored in a model's properties. Missing is distinct from unreadable metadata:
/// substring extraction can misread a formatted or duplicated property as the trunk (version 0).
pub(crate) fn version_from_properties(raw: &str) -> Result<Option<i32>, String> {
    let Some(text) = property_scalars(raw, &["KBVersion"])?.remove("KBVersion") else {
        return Ok(None);
    };
    let id = text
        .trim()
        .parse::<i32>()
        .map_err(|_| "model property KBVersion is not an integer".to_string())?;
    if id < 0 {
        return Err("model property KBVersion is negative".into());
    }
    Ok(Some(id))
}

/// The text values of the `wanted` properties in a UDM `<Properties>` document. Every property's
/// structure and name are validated; a wanted property that repeats, lacks a value or holds
/// markup is refused. Other properties' values are not inspected.
pub(crate) fn property_scalars(
    raw: &str,
    wanted: &[&str],
) -> Result<std::collections::BTreeMap<String, String>, String> {
    let document = roxmltree::Document::parse(raw)
        .map_err(|error| format!("invalid model properties XML: {error}"))?;
    let root = document.root_element();
    if !root.has_tag_name("Properties") {
        return Err("model properties must have a Properties root".into());
    }
    let scalar = |node: roxmltree::Node<'_, '_>| -> Result<String, String> {
        let mut text = String::new();
        for child in node.children() {
            if !child.is_text() {
                return Err(format!(
                    "model property {} contains non-text content",
                    node.tag_name().name()
                ));
            }
            text.push_str(child.text().unwrap_or(""));
        }
        Ok(text)
    };
    let mut found = std::collections::BTreeMap::new();
    for property in root.children().filter(|node| node.is_element()) {
        if !property.has_tag_name("Property") {
            return Err("model properties contain an unsupported element".into());
        }
        let mut name = None;
        let mut value = None;
        for field in property.children().filter(|node| node.is_element()) {
            let slot = if field.has_tag_name("Name") {
                &mut name
            } else if field.has_tag_name("Value") {
                &mut value
            } else {
                return Err("model property contains an unsupported field".into());
            };
            if slot.replace(field).is_some() {
                return Err(format!(
                    "model property repeats {}",
                    field.tag_name().name()
                ));
            }
        }
        let name = scalar(name.ok_or("model property has no Name")?)?;
        if !wanted.contains(&name.as_str()) {
            continue;
        }
        let text = scalar(value.ok_or(format!("model property {name} has no Value"))?)?;
        if found.insert(name.clone(), text).is_some() {
            return Err(format!("model property {name} is repeated"));
        }
    }
    Ok(found)
}

/// The Design model `active` selects in `rows`, or the only Design model when `active` is `None`.
/// The trunk Design model may store no `KBVersion` property; it then counts as version 0.
pub fn select_design_model(
    rows: &[ModelTopologyRow],
    active: Option<ActiveVersion>,
    scope_use: ScopeUse,
) -> Result<i32, VersionScopeRefusal> {
    let refuse = |message: String| Err(VersionScopeRefusal(message));
    let designs: Vec<&ModelTopologyRow> = rows.iter().filter(|row| row.parent_id == 0).collect();
    if designs.is_empty() {
        return refuse(
            "the Knowledge Base stores no Design model (no UdmModel row without a parent)".into(),
        );
    }
    let version_of = |row: &ModelTopologyRow| row.kb_version.unwrap_or(0);
    for (index, design) in designs.iter().enumerate() {
        if let Some(other) = designs[index + 1..]
            .iter()
            .find(|other| version_of(other) == version_of(design))
        {
            return refuse(format!(
                "Design models {} and {} both belong to KB version {}: the version's model is ambiguous",
                design.model_id,
                other.model_id,
                version_of(design)
            ));
        }
    }
    let selected = match active {
        None if designs.len() == 1 => designs[0],
        None => {
            return refuse(format!(
                "the Knowledge Base has {} Design models (KB versions); the active version must be named with --design-model and --kb-version",
                designs.len()
            ));
        }
        Some(active) => {
            let Some(design) = designs
                .iter()
                .copied()
                .find(|design| design.model_id == active.design_model_id)
            else {
                return refuse(format!(
                    "model {} is not a Design model of this Knowledge Base",
                    active.design_model_id
                ));
            };
            if version_of(design) != active.kb_version_id {
                return refuse(format!(
                    "Design model {} belongs to KB version {}, not to the active version {}",
                    design.model_id,
                    version_of(design),
                    active.kb_version_id
                ));
            }
            design
        }
    };
    if scope_use == ScopeUse::Build {
        if designs.len() != 1 {
            return refuse(
                "native build facts are not yet pinned to a KB version; builds require a single Design model".into(),
            );
        }
        if version_of(selected) != 0 || selected.model_id != DEFAULT_DESIGN_MODEL_ID {
            return refuse(format!(
                "the active version's Design model is {} (KB version {}); native builds cover only the trunk Design model {}",
                selected.model_id,
                version_of(selected),
                DEFAULT_DESIGN_MODEL_ID
            ));
        }
        if let Some(environment) = rows
            .iter()
            .find(|row| row.parent_id != 0 && row.parent_id != selected.model_id)
        {
            return refuse(format!(
                "environment model {} generates Design model {}, not the trunk: native builds read the environments of the trunk only",
                environment.model_id, environment.parent_id
            ));
        }
    }
    Ok(selected.model_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(model_id: i32, parent_id: i32, kb_version: Option<i32>) -> ModelTopologyRow {
        ModelTopologyRow {
            model_id,
            parent_id,
            kb_version,
        }
    }

    /// The disposable clone's topology: trunk 1, .NET 2 under it, frozen 3, branch 4.
    fn versioned() -> Vec<ModelTopologyRow> {
        vec![
            row(1, 0, Some(0)),
            row(2, 1, None),
            row(3, 0, Some(1)),
            row(4, 0, Some(2)),
        ]
    }

    fn active(design_model_id: i32, kb_version_id: i32) -> Option<ActiveVersion> {
        Some(ActiveVersion {
            design_model_id,
            kb_version_id,
        })
    }

    #[test]
    fn model_version_uses_xml_structure_not_adjacent_tag_spelling() {
        for raw in [
            "<Properties><Property><Value>2</Value><Name>KBVersion</Name></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name>\n  <Value> 2 </Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Value>&#50;</Value></Property></Properties>",
        ] {
            assert_eq!(version_from_properties(raw).unwrap(), Some(2), "{raw}");
        }
        assert_eq!(version_from_properties("<Properties />").unwrap(), None);
    }

    #[test]
    fn unreadable_or_ambiguous_model_versions_never_become_trunk() {
        for raw in [
            "not XML",
            "<Other />",
            "<Properties><Property><Name>KBVersion</Name><Value>2</Value></Property><Property><Name>KBVersion</Name><Value>0</Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Value>0</Value><Value>2</Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Name>Other</Name><Value>0</Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion<!-- split --></Name><Value>2</Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Value>0<Extra>2</Extra></Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Value>0<!-- split -->2</Value></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Value /></Property></Properties>",
            "<Properties><Property><Name>KBVersion</Name><Value>-1</Value></Property></Properties>",
        ] {
            assert!(version_from_properties(raw).is_err(), "accepted {raw}");
        }
    }

    #[test]
    fn a_single_design_model_needs_no_named_version() {
        let rows = vec![row(1, 0, None), row(2, 1, None)];
        assert_eq!(select_design_model(&rows, None, ScopeUse::Read), Ok(1));
        assert_eq!(select_design_model(&rows, None, ScopeUse::Build), Ok(1));
    }

    #[test]
    fn versions_without_a_named_active_version_are_refused() {
        let refusal = select_design_model(&versioned(), None, ScopeUse::Read).unwrap_err();
        assert!(refusal.0.contains("3 Design models"), "{refusal}");
    }

    #[test]
    fn a_read_follows_the_named_active_version() {
        assert_eq!(
            select_design_model(&versioned(), active(4, 2), ScopeUse::Read),
            Ok(4)
        );
        assert_eq!(
            select_design_model(&versioned(), active(3, 1), ScopeUse::Read),
            Ok(3)
        );
        assert_eq!(
            select_design_model(&versioned(), active(1, 0), ScopeUse::Read),
            Ok(1)
        );
    }

    #[test]
    fn a_named_version_must_match_its_design_model() {
        let mismatch = select_design_model(&versioned(), active(4, 1), ScopeUse::Read).unwrap_err();
        assert!(mismatch.0.contains("belongs to KB version 2"), "{mismatch}");
        let environment =
            select_design_model(&versioned(), active(2, 0), ScopeUse::Read).unwrap_err();
        assert!(
            environment.0.contains("not a Design model"),
            "{environment}"
        );
        let missing = select_design_model(&versioned(), active(9, 0), ScopeUse::Read).unwrap_err();
        assert!(missing.0.contains("not a Design model"), "{missing}");
    }

    #[test]
    fn versioned_builds_are_refused_until_every_build_fact_is_model_pinned() {
        // Object parts are model-pinned, but attribute/table/message facts still use latest
        // global entity versions. A newer branch fact must never leak into a trunk build.
        let refusal = select_design_model(&versioned(), active(1, 0), ScopeUse::Build).unwrap_err();
        assert!(refusal.0.contains("build facts"), "{refusal}");
    }

    #[test]
    fn a_build_covers_only_the_trunk_with_its_own_environments() {
        let trunk = vec![row(1, 0, Some(0)), row(2, 1, None)];
        assert_eq!(
            select_design_model(&trunk, active(1, 0), ScopeUse::Build),
            Ok(1)
        );
        let branch_only = vec![row(4, 0, Some(2))];
        let branch = select_design_model(&branch_only, active(4, 2), ScopeUse::Build).unwrap_err();
        assert!(branch.0.contains("only the trunk"), "{branch}");
        let mut foreign = trunk;
        foreign.push(row(5, 4, None));
        let environment = select_design_model(&foreign, active(1, 0), ScopeUse::Build).unwrap_err();
        assert!(
            environment.0.contains("environment model 5"),
            "{environment}"
        );
        // A read of the trunk is not affected by another version's environment.
        assert_eq!(
            select_design_model(&foreign, active(1, 0), ScopeUse::Read),
            Ok(1)
        );
    }

    #[test]
    fn two_design_models_of_one_version_are_ambiguous() {
        let rows = vec![row(1, 0, None), row(3, 0, Some(0))];
        let refusal = select_design_model(&rows, active(1, 0), ScopeUse::Read).unwrap_err();
        assert!(refusal.0.contains("ambiguous"), "{refusal}");
    }

    #[test]
    fn a_knowledge_base_without_a_design_model_is_refused() {
        let refusal = select_design_model(&[row(2, 1, None)], None, ScopeUse::Read).unwrap_err();
        assert!(refusal.0.contains("no Design model"), "{refusal}");
    }

    #[test]
    fn a_trunk_stored_under_another_id_is_not_built() {
        let rows = vec![row(7, 0, Some(0)), row(8, 7, None)];
        assert_eq!(select_design_model(&rows, None, ScopeUse::Read), Ok(7));
        let refusal = select_design_model(&rows, None, ScopeUse::Build).unwrap_err();
        assert!(
            refusal.0.contains("only the trunk Design model 1"),
            "{refusal}"
        );
    }
}
