//! Facts read from the stored Knowledge Base: name, guids, attributes, subtypes, tables, translations and properties.

use super::live_rows::{decode_hex_value, parse_live_int, parse_live_rows};
use super::model_pin::{
    attribute_facts_sql, design_system_name_sql, language_names_sql, stored_model_properties_sql,
    subtype_groups_sql, table_keys_sql,
};
use super::*;

impl UdmClient {
    /// The name of this Knowledge Base: the `Name` of its root entity (EntityType 9, EntityId 1), at the
    /// latest stored version. The root is not a model object, so it is read by its entity id. `None` when no
    /// version of it is stored or it has no `Name`.
    pub fn knowledge_base_name(&self) -> Result<Option<String>, String> {
        const KB_ENTITY_TYPE: i32 = 9;
        const KB_ENTITY_ID: i32 = 1;
        let version = if self.is_live() {
            let cached = self
                .bulk
                .read()
                .map_err(|e| format!("Bulk coverage lock poisoned: {e}"))?
                .root_version;
            if let Some(known) = cached {
                match known {
                    Some(version) => version,
                    None => return Ok(None),
                }
            } else {
                let sql = format!(
                    "SET NOCOUNT ON; SELECT EntityVersionId FROM EntityVersion WHERE EntityTypeId = {KB_ENTITY_TYPE} AND EntityId = {KB_ENTITY_ID} ORDER BY EntityVersionId DESC"
                );
                let raw = self.execute_live_sql(&sql)?;
                let rows = parse_live_rows(&raw, 1)?;
                let latest = match rows.first() {
                    Some(row) => Some(parse_live_int(row[0], "EntityVersionId")?),
                    None => None,
                };
                self.bulk
                    .write()
                    .map_err(|e| format!("Bulk coverage lock poisoned: {e}"))?
                    .root_version = Some(latest);
                match latest {
                    Some(version) => version,
                    None => return Ok(None),
                }
            }
        } else {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            match store
                .entity_versions
                .iter()
                .filter(|ev| ev.entity_type_id == KB_ENTITY_TYPE && ev.entity_id == KB_ENTITY_ID)
                .map(|ev| ev.entity_version_id)
                .max()
            {
                Some(version) => version,
                None => return Ok(None),
            }
        };
        let Some(rec) = self.get_entity_version(KB_ENTITY_TYPE, KB_ENTITY_ID, version)? else {
            return Ok(None);
        };
        let Some(raw) = rec.properties else {
            return Ok(None);
        };
        crate::udm::stored_variables::named_property(&raw, "Name")
    }

    /// The entity GUID of every Image, SDT, SDT level and Domain of the Knowledge Base, by the reference a stored
    /// part uses for it: the GUID of its entity type, `-`, and its entity id (`9fb193d9-…-74` ->
    /// `ef73725b-…`). An entity GUID is the object's identity across Knowledge Bases (a module's
    /// objects keep theirs when the module is imported); its entity id is local. An in-memory client
    /// has none.
    pub fn object_guids(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT LOWER(CONVERT(VARCHAR(36), g.EntityGuid)), e.EntityId, LOWER(CONVERT(VARCHAR(36), e.EntityGuid)) FROM Entity e JOIN EntityType t ON t.EntityTypeId = e.EntityTypeId AND t.EntityTypeName IN ('Image', 'SDT', 'SDTLevelEntity', 'Domain') JOIN Entity g ON g.EntityTypeId = 1 AND g.EntityId = t.EntityTypeId";
        let raw = self.execute_live_sql(sql)?;
        let mut guids = BTreeMap::new();
        for row in parse_live_rows(&raw, 3)? {
            let id = parse_live_int(row[1], "EntityId")?;
            guids.insert(format!("{}-{id}", row[0].trim()), row[2].trim().to_string());
        }
        Ok(guids)
    }

    /// The Attributes of this client's Design model, by declared name: the entity id and whether the version
    /// the model holds stores a title of its own (`Title`, `Description`, `ContextualTitle` or `ColumnTitle`),
    /// which replaces the title GeneXus derives from the name. An in-memory client has none.
    pub fn attribute_facts(&self) -> Result<BTreeMap<String, AttributeFacts>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let raw = self.execute_live_sql(&attribute_facts_sql(self.design_model_id()))?;
        let mut facts = BTreeMap::new();
        for row in parse_live_rows(&raw, 4)? {
            let id = parse_live_int(row[0], "EntityId")?;
            let id =
                u32::try_from(id).map_err(|_| format!("Attribute entity id {id} is negative"))?;
            let declared = parse_live_int(row[2], "DATALENGTH(EntityVersionProperties)")?;
            let declared = usize::try_from(declared)
                .map_err(|_| format!("Attribute {id} properties length {declared} is negative"))?;
            let bytes = decode_hex_value(row[3].trim(), declared)?;
            let text = String::from_utf8(bytes)
                .map_err(|e| format!("Attribute {id} properties are not UTF-8: {e}"))?;
            let custom_title =
                attribute_has_own_title(&text).map_err(|e| format!("Attribute {id}: {e}"))?;
            facts.insert(
                row[1].trim().to_string(),
                AttributeFacts { id, custom_title },
            );
        }
        Ok(facts)
    }

    /// The description of the subtype group each attribute belongs to, by attribute name: the version of
    /// every `Group` and attribute this client's Design model holds, and the cross-references that model
    /// stores. An attribute in more than one group is left out. An in-memory client has none.
    pub fn subtype_groups(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let raw = self.execute_live_sql(&subtype_groups_sql(self.design_model_id()))?;
        let mut seen: BTreeMap<String, Vec<String>> = BTreeMap::new();
        for row in parse_live_rows(&raw, 3)? {
            let declared = parse_live_int(row[1], "DATALENGTH(EntityVersionDescription)")?;
            let declared = usize::try_from(declared)
                .map_err(|_| format!("group description length {declared} is negative"))?;
            let bytes = decode_hex_value(row[2].trim(), declared)?;
            let description = String::from_utf8(bytes)
                .map_err(|e| format!("a group description is not UTF-8: {e}"))?;
            let groups = seen.entry(row[0].trim().to_string()).or_default();
            if !groups.contains(&description) {
                groups.push(description);
            }
        }
        Ok(seen
            .into_iter()
            .filter_map(|(name, groups)| match groups.as_slice() {
                [only] => Some((name, only.clone())),
                _ => None,
            })
            .collect())
    }

    /// The `TablePK` of each Table of this client's Design model, by name. A table the model stores
    /// with two different keys is left out. An in-memory client has none.
    pub fn table_keys(&self) -> Result<BTreeMap<String, u32>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let raw = self.execute_live_sql(&table_keys_sql(self.design_model_id()))?;
        let mut seen: BTreeMap<String, Vec<u32>> = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let key = parse_live_int(row[1], "TablePK")?;
            let key = u32::try_from(key).map_err(|_| format!("TablePK {key} is negative"))?;
            seen.entry(row[0].trim().to_string()).or_default().push(key);
        }
        Ok(seen
            .into_iter()
            .filter_map(|(name, keys)| match keys.as_slice() {
                [only] => Some((name, *only)),
                _ => None,
            })
            .collect())
    }

    /// The design-system classes of the Knowledge Base, by the reference a stored layout uses for a
    /// class: the GUID of the `DesignSystemClass` entity type, `-`, and the class entity id
    /// (`d52f9833-…-443` -> `ContainerFluid`). The latest version of each class names it. An
    /// in-memory client has none.
    pub fn design_system_classes(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT LOWER(CONVERT(VARCHAR(36), g.EntityGuid)), v.EntityId, v.EntityVersionName FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'DesignSystemClass' JOIN Entity g ON g.EntityTypeId = 1 AND g.EntityId = t.EntityTypeId WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
        let mut classes = BTreeMap::new();
        for row in parse_live_rows(&raw, 3)? {
            let id = parse_live_int(row[1], "EntityId")?;
            classes.insert(format!("{}-{id}", row[0].trim()), row[2].trim().to_string());
        }
        Ok(classes)
    }

    /// The Knowledge Base's `TranslationMessage` entities (the `GXM_*` web messages): entity id, key
    /// (`FullName`, the latest version's name) and `MessageType` (`Internal` or `User`), in id order. The KB
    /// stores no text for them; the SDK reads it from the install (see `crate::csharp::messages`). An
    /// in-memory client has none.
    pub fn translation_messages(&self) -> Result<Vec<(i64, String, String)>, String> {
        if !self.is_live() {
            return Ok(Vec::new());
        }
        let sql = "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName, CASE WHEN v.EntityVersionProperties LIKE '%<Name>MessageType</Name><Value>Internal</Value>%' THEN 'Internal' WHEN v.EntityVersionProperties LIKE '%<Name>MessageType</Name><Value>User</Value>%' THEN 'User' ELSE 'Unknown' END FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'TranslationMessage' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId) ORDER BY v.EntityId";
        let raw = self.execute_live_sql(sql)?;
        parse_live_rows(&raw, 3)?
            .into_iter()
            .map(|row| {
                let id = parse_live_int(row[0], "EntityId")?;
                Ok((
                    i64::from(id),
                    row[1].trim().to_string(),
                    row[2].trim().to_string(),
                ))
            })
            .collect()
    }

    /// The names of the `Language` objects of this client's Design model (the version it holds of each),
    /// sorted. An in-memory client has none.
    pub fn language_names(&self) -> Result<Vec<String>, String> {
        if !self.is_live() {
            return Ok(Vec::new());
        }
        let raw = self.execute_live_sql(&language_names_sql(self.design_model_id()))?;
        let mut names: Vec<String> = parse_live_rows(&raw, 2)?
            .into_iter()
            .map(|row| row[1].trim().to_string())
            .collect();
        names.sort();
        Ok(names)
    }

    /// The stored values of the named model properties (`Property: <name>` entities), by name, from this
    /// client's Design model and the environments below it, for every entity that stores one. A property the
    /// model never stored is absent: GeneXus then uses the generator default. Two different stored values for
    /// one name are an error, not a choice.
    pub fn stored_model_properties(
        &self,
        names: &[&str],
    ) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() || names.is_empty() {
            return Ok(BTreeMap::new());
        }
        for name in names {
            if !name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_') {
                return Err(format!("invalid model property name '{name}'"));
            }
        }
        let list = names
            .iter()
            .map(|name| format!("'Property: {name}'"))
            .collect::<Vec<_>>()
            .join(", ");
        let sql = stored_model_properties_sql(self.design_model_id(), &list);
        let raw = self.execute_live_sql(&sql)?;
        let mut values = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let name = row[0].trim().to_string();
            let value = row[1].trim().to_string();
            if let Some(previous) = values.insert(name.clone(), value.clone())
                && previous != value
            {
                return Err(format!(
                    "model property {name} is stored with different values ('{previous}', '{value}')"
                ));
            }
        }
        Ok(values)
    }
}

impl UdmClient {
    /// The name of the theme (DesignSystem) the web generator uses: the `Theme` property the generating
    /// environment of this client's Design model stores, else the one it inherits from the Design model. `None` when no model stores one:
    /// GeneXus then uses a built-in default the engine does not resolve. Environments that would generate
    /// with different themes are an error, since the engine cannot tell which one builds.
    pub fn default_web_theme(&self) -> Result<Option<String>, String> {
        if !self.is_live() {
            return Ok(None);
        }
        const MARK: &str = "<Name>Theme</Name><Value>";
        let props = "CAST(v.EntityVersionProperties AS NVARCHAR(MAX))";
        let start = format!("CHARINDEX('{MARK}', {props})");
        let sql = format!(
            "SET NOCOUNT ON; SELECT v.EntityId, ISNULL(m.ModelParentId, 0), CASE WHEN {start} > 0 THEN SUBSTRING({props}, {start} + {len}, CHARINDEX('</Value>', {props}, {start}) - {start} - {len}) ELSE '' END FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Model' JOIN UdmModel m ON m.ModelId = v.EntityId WHERE (m.ModelId = {model} OR m.ModelParentId = {model}) AND v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)",
            len = MARK.len(),
            model = self.design_model_id()
        );
        let raw = self.execute_live_sql(&sql)?;
        let rows = parse_live_rows(&raw, 3)?
            .into_iter()
            .map(|row| {
                Ok(ModelThemeRow {
                    model_id: parse_live_int(row[0], "EntityId")?,
                    parent_id: parse_live_int(row[1], "ModelParentId")?,
                    theme: row[2].trim().to_string(),
                })
            })
            .collect::<Result<Vec<_>, String>>()?;
        let Some(reference) = effective_theme_reference(&rows)? else {
            return Ok(None);
        };
        let (type_guid, entity_id) = parse_theme_reference(&reference)?;
        // `type_guid` is validated hex and dashes, `entity_id` a number: neither can break out of the literal.
        let sql = design_system_name_sql(self.design_model_id(), &type_guid, entity_id);
        let raw = self.execute_live_sql(&sql)?;
        match parse_live_rows(&raw, 1)?.as_slice() {
            [row] if !row[0].trim().is_empty() => Ok(Some(row[0].trim().to_string())),
            _ => Err(format!(
                "the stored theme '{reference}' names no DesignSystem of this Knowledge Base"
            )),
        }
    }
}

/// One `Udm.Types.Model` at its latest version: its id, its parent (0 for the Design model) and the
/// `Theme` reference it stores (empty when it inherits).
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct ModelThemeRow {
    pub model_id: i32,
    pub parent_id: i32,
    pub theme: String,
}

/// The theme reference the generating environments use. Each environment (a model with a parent) uses
/// the theme it stores, else its parent's; with no environment, the Design model's. They must agree.
pub(crate) fn effective_theme_reference(rows: &[ModelThemeRow]) -> Result<Option<String>, String> {
    let stored = |id: i32| {
        rows.iter()
            .find(|row| row.model_id == id)
            .map(|row| row.theme.as_str())
            .filter(|theme| !theme.is_empty())
    };
    let effective = |row: &ModelThemeRow| -> Option<String> {
        let mut current = Some(row);
        let mut seen = 0;
        while let Some(model) = current {
            if !model.theme.is_empty() {
                return Some(model.theme.clone());
            }
            seen += 1;
            if model.parent_id == 0 || seen > rows.len() {
                return None;
            }
            current = rows
                .iter()
                .find(|parent| parent.model_id == model.parent_id);
        }
        None
    };
    let environments: Vec<&ModelThemeRow> = rows.iter().filter(|row| row.parent_id != 0).collect();
    if environments.is_empty() {
        return Ok(rows
            .iter()
            .find_map(|row| stored(row.model_id).map(str::to_string)));
    }
    let mut themes: Vec<Option<String>> = environments.iter().map(|row| effective(row)).collect();
    themes.sort();
    themes.dedup();
    match themes.as_slice() {
        [only] => Ok(only.clone()),
        _ => Err(format!(
            "the environments generate with different themes ({}); pass --web-theme <name>",
            themes
                .iter()
                .map(|theme| theme.as_deref().unwrap_or("the GeneXus default"))
                .collect::<Vec<_>>()
                .join(", ")
        )),
    }
}

/// Splits a stored theme reference, `<DesignSystem type GUID>-<entity id>`, into its lowercase GUID and id.
pub(crate) fn parse_theme_reference(reference: &str) -> Result<(String, i64), String> {
    let invalid =
        || format!("the stored theme '{reference}' is not a <type GUID>-<entity id> reference");
    let (guid, id) = reference.rsplit_once('-').ok_or_else(invalid)?;
    let id: i64 = id.parse().map_err(|_| invalid())?;
    let groups: Vec<&str> = guid.split('-').collect();
    let shaped = groups.iter().map(|group| group.len()).eq([8, 4, 4, 4, 12])
        && groups
            .iter()
            .all(|group| group.bytes().all(|b| b.is_ascii_hexdigit()));
    if !shaped || id < 0 {
        return Err(invalid());
    }
    Ok((guid.to_ascii_lowercase(), id))
}

/// What a transaction emitter needs to know of one Attribute (see [`UdmClient::attribute_facts`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct AttributeFacts {
    /// The Attribute's entity id (`A<id><name>` in generated code).
    pub id: u32,
    /// The Attribute stores a title of its own, so its title is not the one derived from its name.
    pub custom_title: bool,
}

/// Whether stored Attribute properties set a title of their own. Empty properties set none.
fn attribute_has_own_title(properties: &str) -> Result<bool, String> {
    if properties.trim().is_empty() {
        return Ok(false);
    }
    let document = roxmltree::Document::parse(properties)
        .map_err(|e| format!("its properties are not XML: {e}"))?;
    Ok(document
        .descendants()
        .filter(|node| node.has_tag_name("Property"))
        .filter_map(|property| {
            property
                .children()
                .find(|c| c.has_tag_name("Name"))
                .and_then(|c| c.text())
        })
        .any(|name| {
            matches!(
                name,
                "Title" | "Description" | "ContextualTitle" | "ColumnTitle"
            )
        }))
}

#[cfg(test)]
mod attribute_title_tests {
    use super::attribute_has_own_title;

    #[test]
    fn only_a_stored_title_property_counts_as_an_own_title() {
        let named = "<Properties><Property><Name>Name</Name><Value>LiveMoveAttributeRulesId</Value></Property></Properties>";
        assert!(!attribute_has_own_title(named).unwrap());
        assert!(!attribute_has_own_title("").unwrap());
        let titled = "<Properties><Property><Name>Name</Name><Value>A</Value></Property><Property><Name>ContextualTitle</Name><Value>Code</Value></Property></Properties>";
        assert!(attribute_has_own_title(titled).unwrap());
        assert!(attribute_has_own_title("<Properties>").is_err());
    }
}

#[cfg(test)]
mod web_theme_tests {
    use super::{ModelThemeRow, effective_theme_reference, parse_theme_reference};

    /// Models as `(id, parent, stored theme)`.
    fn models(spec: &[(i32, i32, &str)]) -> Vec<ModelThemeRow> {
        spec.iter()
            .map(|&(model_id, parent_id, theme)| ModelThemeRow {
                model_id,
                parent_id,
                theme: theme.into(),
            })
            .collect()
    }

    #[test]
    fn an_environment_inherits_the_design_model_theme_unless_it_stores_its_own() {
        // KBTeste: the Design model stores the theme, the .NET environment inherits it.
        let kbteste = models(&[(1, 0, "78b3fa0e-174c-4b2b-8716-718167a428b5-6"), (2, 1, "")]);
        assert_eq!(
            effective_theme_reference(&kbteste).unwrap().as_deref(),
            Some("78b3fa0e-174c-4b2b-8716-718167a428b5-6")
        );
        let own = models(&[(1, 0, "g-6"), (2, 1, "g-9")]);
        assert_eq!(
            effective_theme_reference(&own).unwrap().as_deref(),
            Some("g-9")
        );
        // Two environments that generate with different themes: the engine cannot tell which one builds.
        let split = models(&[(1, 0, "g-6"), (2, 1, ""), (3, 1, "g-9")]);
        assert!(
            effective_theme_reference(&split)
                .unwrap_err()
                .contains("--web-theme")
        );
        let agreeing = models(&[(1, 0, "g-6"), (2, 1, ""), (3, 1, "g-6")]);
        assert_eq!(
            effective_theme_reference(&agreeing).unwrap().as_deref(),
            Some("g-6")
        );
        // Nothing stored: GeneXus falls back to a built-in default the engine does not resolve.
        assert_eq!(
            effective_theme_reference(&models(&[(1, 0, ""), (2, 1, "")])).unwrap(),
            None
        );
        assert_eq!(effective_theme_reference(&models(&[])).unwrap(), None);
        // A design model alone (no environment yet) still names the theme.
        assert_eq!(
            effective_theme_reference(&models(&[(1, 0, "g-6")]))
                .unwrap()
                .as_deref(),
            Some("g-6")
        );
    }

    #[test]
    fn a_theme_reference_is_a_type_guid_and_an_entity_id() {
        assert_eq!(
            parse_theme_reference("78B3FA0E-174c-4b2b-8716-718167a428b5-6").unwrap(),
            ("78b3fa0e-174c-4b2b-8716-718167a428b5".to_string(), 6)
        );
        for bad in [
            "",
            "6",
            "78b3fa0e-174c-4b2b-8716-718167a428b5",
            "78b3fa0e-174c-4b2b-8716-718167a428b5-x",
            "'; DROP TABLE x;--1-6",
            "78b3fa0e-174c-4b2b-8716-718167a428b5--6",
        ] {
            assert!(parse_theme_reference(bad).is_err(), "{bad}");
        }
    }
}

impl UdmClient {
    /// Every `UdmModel` row with its parent and the `KBVersion` property its latest stored version holds,
    /// for [`crate::udm::version_scope::select_design_model`]. An in-memory client stores no topology and
    /// answers with the default Design model alone.
    pub fn model_topology(
        &self,
    ) -> Result<Vec<crate::udm::version_scope::ModelTopologyRow>, String> {
        use crate::udm::version_scope::ModelTopologyRow;
        if !self.is_live() {
            return Ok(vec![ModelTopologyRow {
                model_id: self.design_model_id(),
                parent_id: 0,
                kb_version: None,
            }]);
        }
        // Hex and declared length preserve the complete XML across sqlcmd row delimiters and
        // encoding. Parse the XML, not adjacent tag spellings: an unreadable version is not trunk.
        let sql = "SET NOCOUNT ON; SELECT m.ModelId, m.ModelParentId, ISNULL(DATALENGTH(v.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(v.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM UdmModel m LEFT JOIN EntityType t ON t.EntityTypeName = 'Udm.Types.Model' LEFT JOIN EntityVersion v ON v.EntityTypeId = t.EntityTypeId AND v.EntityId = m.ModelId AND v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = t.EntityTypeId AND l.EntityId = m.ModelId) ORDER BY m.ModelId";
        let raw = self.execute_live_sql(sql)?;
        parse_live_rows(&raw, 4)?
            .into_iter()
            .map(|row| {
                let model_id = parse_live_int(row[0], "ModelId")?;
                let declared = parse_live_int(row[2], "DATALENGTH(EntityVersionProperties)")?;
                let declared = usize::try_from(declared)
                    .map_err(|_| format!("model {model_id} properties length is negative"))?;
                let bytes = decode_hex_value(row[3].trim(), declared)?;
                let properties = String::from_utf8(bytes).map_err(|error| {
                    format!("model {model_id} properties are not UTF-8: {error}")
                })?;
                let kb_version = crate::udm::version_scope::version_from_properties(&properties)
                    .map_err(|error| format!("model {model_id}: {error}"))?;
                Ok(ModelTopologyRow {
                    model_id,
                    parent_id: parse_live_int(row[1], "ModelParentId")?,
                    kb_version,
                })
            })
            .collect()
    }
}
