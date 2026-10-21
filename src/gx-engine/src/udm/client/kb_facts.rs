//! Facts read from the stored Knowledge Base: name, guids, attributes, subtypes, tables, translations and properties.

use super::live_rows::{decode_hex_value, parse_live_int, parse_live_rows};
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

    /// The Attributes of the Knowledge Base, by declared name: the entity id and whether the latest
    /// version stores a title of its own (`Title`, `Description`, `ContextualTitle` or `ColumnTitle`),
    /// which replaces the title GeneXus derives from the name. An in-memory client has none.
    pub fn attribute_facts(&self) -> Result<BTreeMap<String, AttributeFacts>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName, ISNULL(DATALENGTH(v.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(v.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Attribute' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
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

    /// The description of the subtype group each attribute belongs to, by attribute name: the
    /// latest version of every `Group` and the attributes its cross-references name, in any model.
    /// An attribute in more than one group is left out. An in-memory client has none.
    pub fn subtype_groups(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let latest = |alias: &str| {
            format!(
                "{alias}.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = {alias}.EntityTypeId AND l.EntityId = {alias}.EntityId)"
            )
        };
        let sql = format!(
            "SET NOCOUNT ON; SELECT DISTINCT a.EntityVersionName, ISNULL(DATALENGTH(g.EntityVersionDescription), 0), CONVERT(VARCHAR(MAX), CAST(g.EntityVersionDescription AS VARBINARY(MAX)), 2) FROM ModelCrossReference x JOIN EntityType gt ON gt.EntityTypeId = x.FromEntityTypeId AND gt.EntityTypeName = 'Group' JOIN EntityType at ON at.EntityTypeId = x.ToEntityTypeId AND at.EntityTypeName = 'Attribute' JOIN EntityVersion g ON g.EntityTypeId = x.FromEntityTypeId AND g.EntityId = x.FromEntityId JOIN EntityVersion a ON a.EntityTypeId = x.ToEntityTypeId AND a.EntityId = x.ToEntityId WHERE {} AND {}",
            latest("g"),
            latest("a")
        );
        let raw = self.execute_live_sql(&sql)?;
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

    /// The `TablePK` of each Table, by name, when every model stores the same one. A table the
    /// models disagree on is left out. An in-memory client has none.
    pub fn table_keys(&self) -> Result<BTreeMap<String, u32>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT DISTINCT v.EntityVersionName, m.TablePK FROM ModelEntityToTable m JOIN EntityType t ON t.EntityTypeId = m.EntityTypeId AND t.EntityTypeName = 'Table' JOIN EntityVersion v ON v.EntityTypeId = m.EntityTypeId AND v.EntityId = m.EntityId WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
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

    /// The names of the Knowledge Base's `Language` objects (the latest version of each), sorted. An
    /// in-memory client has none.
    pub fn language_names(&self) -> Result<Vec<String>, String> {
        if !self.is_live() {
            return Ok(Vec::new());
        }
        let sql = "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Language' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
        let mut names: Vec<String> = parse_live_rows(&raw, 2)?
            .into_iter()
            .map(|row| row[1].trim().to_string())
            .collect();
        names.sort();
        Ok(names)
    }

    /// The stored values of the named model properties (`Property: <name>` entities), by name, from every
    /// model and entity that stores one. A property the Knowledge Base never stored is absent: GeneXus then
    /// uses the generator default. Two different stored values for one name are an error, not a choice.
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
        let sql = format!(
            "SET NOCOUNT ON; SELECT SUBSTRING(p.EntityVersionName, 11, 200), ISNULL(m.ModelEntityPropertyValue, '') FROM ModelEntityProperty m JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName IN ({list}) JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property'"
        );
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
