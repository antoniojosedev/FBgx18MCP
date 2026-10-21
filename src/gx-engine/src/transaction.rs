//! Portable Transaction structure, levels, primary keys, and attribute definitions.
//!
//! Represents GeneXus Transactions with hierarchical levels, primary keys,
//! foreign keys, attribute references, and Business Component (BC) semantics
//! conforming to Plan 130 Track B.

use crate::model::DataType;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

/// Maximum allowed depth for nested Transaction levels.
pub const MAX_LEVEL_DEPTH: usize = 16;

/// A single attribute reference within a Transaction level.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TransactionAttributeRef {
    pub name: String,
    #[serde(default)]
    pub is_key: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub data_type: Option<DataType>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub formula: Option<String>,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_nullable: bool,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_foreign_key: bool,
}

/// A hierarchical level within a Transaction.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TransactionLevel {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default)]
    pub attributes: Vec<TransactionAttributeRef>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub levels: Vec<TransactionLevel>,
}

/// Transaction rules: defaults, validations, serials, and error messages.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum TransactionRule {
    Default {
        attribute: String,
        value: String,
    },
    Error {
        message: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        condition: Option<String>,
    },
    Msg {
        message: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        condition: Option<String>,
    },
    Serial {
        attribute: String,
        step: u64,
    },
    NoAccept {
        attribute: String,
    },
    Prompt {
        object: String,
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        attributes: Vec<String>,
    },
}

/// The portable representation of a Transaction's structure part.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TransactionStructure {
    pub root: TransactionLevel,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_business_component: bool,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub rules: Vec<TransactionRule>,
    /// The key attributes whose `SuperTypeKey` names the key of another Transaction. The physical schema
    /// derives a foreign key from each of them. The web class checks the reference only for the
    /// key-only shape `csharp::transaction_form` matches.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub key_references: Vec<KeyReference>,
}

/// A key attribute of a Transaction that references the key of another Transaction (`SuperTypeKey`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct KeyReference {
    /// The name of the key attribute of the Transaction that stores the reference.
    pub attribute: String,
    /// The name of the referenced attribute, whose table's whole primary key the foreign key targets.
    pub references: String,
}

impl TransactionStructure {
    pub fn new(root: TransactionLevel) -> Self {
        Self {
            root,
            is_business_component: false,
            rules: Vec::new(),
            key_references: Vec::new(),
        }
    }

    pub fn with_business_component(mut self, enabled: bool) -> Self {
        self.is_business_component = enabled;
        self
    }

    pub fn with_rule(mut self, rule: TransactionRule) -> Self {
        self.rules.push(rule);
        self
    }

    /// Validates the structure: bounded recursion, unique level and attribute
    /// names, required key attribute on all levels, data type validity,
    /// rule target validity, and multi-level key consistency.
    pub fn validate(&self) -> Result<(), String> {
        let mut seen_levels = BTreeSet::new();
        self.root.validate(0, true, &mut seen_levels)?;

        let all_attrs = self.all_attributes();
        let attr_set: BTreeSet<String> = all_attrs.iter().map(|a| a.to_ascii_uppercase()).collect();

        for rule in &self.rules {
            match rule {
                TransactionRule::Default { attribute, .. }
                | TransactionRule::Serial { attribute, .. }
                | TransactionRule::NoAccept { attribute } => {
                    let key = attribute.trim().to_ascii_uppercase();
                    if !attr_set.contains(&key) {
                        return Err(format!(
                            "Transaction rule references unknown attribute '{attribute}'"
                        ));
                    }
                }
                TransactionRule::Error { message, .. } | TransactionRule::Msg { message, .. } => {
                    if message.trim().is_empty() {
                        return Err("Transaction error/msg rule cannot have empty message".into());
                    }
                }
                TransactionRule::Prompt { object, .. } => {
                    if object.trim().is_empty() {
                        return Err("Transaction prompt rule requires object name".into());
                    }
                }
            }
        }

        for (index, reference) in self.key_references.iter().enumerate() {
            let owner = self
                .root
                .attributes
                .iter()
                .find(|a| a.name.eq_ignore_ascii_case(&reference.attribute))
                .ok_or_else(|| {
                    format!(
                        "key reference on '{}', which is not an attribute of the root level",
                        reference.attribute
                    )
                })?;
            if !owner.is_key {
                return Err(format!(
                    "key reference on '{}', which is not a key attribute",
                    reference.attribute
                ));
            }
            if reference.references.trim().is_empty() {
                return Err(format!(
                    "key reference on '{}' names no attribute",
                    reference.attribute
                ));
            }
            if reference
                .references
                .eq_ignore_ascii_case(&reference.attribute)
            {
                return Err(format!(
                    "key '{}' references its own attribute",
                    reference.attribute
                ));
            }
            if self.key_references[..index]
                .iter()
                .any(|earlier| earlier.attribute.eq_ignore_ascii_case(&reference.attribute))
            {
                return Err(format!(
                    "key '{}' has two key references",
                    reference.attribute
                ));
            }
        }

        self.validate_multilevel_keys(&self.root, 0)?;

        Ok(())
    }

    fn validate_multilevel_keys(
        &self,
        level: &TransactionLevel,
        depth: usize,
    ) -> Result<(), String> {
        if depth > 0 {
            let pk = level.primary_key();
            if pk.is_empty() {
                return Err(format!(
                    "Nested level '{}' must define at least one local key attribute",
                    level.name
                ));
            }
        }
        for child in &level.levels {
            self.validate_multilevel_keys(child, depth + 1)?;
        }
        Ok(())
    }

    /// Returns the primary key attribute names of the root level in order.
    pub fn root_primary_key(&self) -> Vec<String> {
        self.root.primary_key()
    }

    /// Returns all attribute names in the transaction across all levels.
    pub fn all_attributes(&self) -> Vec<String> {
        let mut result = Vec::new();
        self.root.collect_attributes(&mut result);
        result
    }

    /// Finds a level by name (case-insensitive).
    pub fn find_level(&self, name: &str) -> Option<&TransactionLevel> {
        let needle = name.to_ascii_uppercase();
        self.root.find_level(&needle)
    }

    /// Finds an attribute across all levels by name (case-insensitive).
    pub fn find_attribute(&self, name: &str) -> Option<&TransactionAttributeRef> {
        let needle = name.to_ascii_uppercase();
        self.root.find_attribute(&needle)
    }
}

impl TransactionLevel {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            description: None,
            attributes: Vec::new(),
            levels: Vec::new(),
        }
    }

    /// Returns the primary key attribute names of this level in order.
    pub fn primary_key(&self) -> Vec<String> {
        self.attributes
            .iter()
            .filter(|a| a.is_key)
            .map(|a| a.name.clone())
            .collect()
    }

    /// Returns references to all foreign key attributes in this level.
    pub fn foreign_keys(&self) -> Vec<&TransactionAttributeRef> {
        self.attributes
            .iter()
            .filter(|a| a.is_foreign_key)
            .collect()
    }

    /// Finds an attribute in this level by name (case-insensitive).
    pub fn find_attribute(&self, needle_uppercase: &str) -> Option<&TransactionAttributeRef> {
        for a in &self.attributes {
            if a.name.to_ascii_uppercase() == needle_uppercase {
                return Some(a);
            }
        }
        for child in &self.levels {
            if let Some(found) = child.find_attribute(needle_uppercase) {
                return Some(found);
            }
        }
        None
    }

    /// Finds a level in this subtree by name (case-insensitive).
    pub fn find_level(&self, needle_uppercase: &str) -> Option<&TransactionLevel> {
        if self.name.to_ascii_uppercase() == needle_uppercase {
            return Some(self);
        }
        for child in &self.levels {
            if let Some(found) = child.find_level(needle_uppercase) {
                return Some(found);
            }
        }
        None
    }

    fn validate(
        &self,
        depth: usize,
        _is_root: bool,
        seen_levels: &mut BTreeSet<String>,
    ) -> Result<(), String> {
        if depth > MAX_LEVEL_DEPTH {
            return Err(format!(
                "Transaction level nesting exceeds maximum depth of {MAX_LEVEL_DEPTH}"
            ));
        }
        let normalized = self.name.trim();
        if normalized.is_empty() {
            return Err("Transaction level name cannot be empty".into());
        }
        if normalized.len() > 128 {
            return Err(format!(
                "Transaction level name '{normalized}' exceeds 128 bytes"
            ));
        }
        if !normalized
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_')
        {
            return Err(format!(
                "Transaction level name '{normalized}' contains invalid characters"
            ));
        }
        let key = normalized.to_ascii_uppercase();
        if !seen_levels.insert(key) {
            return Err(format!(
                "duplicate Transaction level name '{normalized}' (names are case-insensitive)"
            ));
        }
        if self.attributes.is_empty() {
            return Err(format!(
                "Transaction level '{normalized}' must contain at least one attribute"
            ));
        }
        let mut seen_attrs = BTreeSet::new();
        let mut has_key = false;
        for attr in &self.attributes {
            let attr_name = attr.name.trim();
            if attr_name.is_empty() {
                return Err(format!(
                    "Transaction level '{normalized}' contains attribute with empty name"
                ));
            }
            if attr_name.len() > 128 {
                return Err(format!(
                    "Transaction attribute name '{attr_name}' exceeds 128 bytes"
                ));
            }
            if !attr_name
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '_')
            {
                return Err(format!(
                    "Transaction attribute name '{attr_name}' contains invalid characters"
                ));
            }
            let attr_key = attr_name.to_ascii_uppercase();
            if !seen_attrs.insert(attr_key) {
                return Err(format!(
                    "duplicate attribute '{attr_name}' in Transaction level '{normalized}'"
                ));
            }
            if attr.is_key {
                has_key = true;
            }
            if let Some(ref dt) = attr.data_type {
                crate::model::validate_data_type(dt)?;
                crate::model::require_attribute_form(dt)?;
            }
        }
        if !has_key {
            return Err(format!(
                "Transaction level '{normalized}' must have at least one key attribute"
            ));
        }
        for child in &self.levels {
            child.validate(depth + 1, false, seen_levels)?;
        }
        Ok(())
    }

    fn collect_attributes(&self, result: &mut Vec<String>) {
        for attr in &self.attributes {
            result.push(attr.name.clone());
        }
        for child in &self.levels {
            child.collect_attributes(result);
        }
    }
}

// ---------------------------------------------------------------------------
// Business Component (BC) Execution Model & Operations
// ---------------------------------------------------------------------------

/// Mode of a Business Component operation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BcMode {
    Insert,
    Update,
    Delete,
    Display,
}

/// Status / validation message generated during a Business Component operation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BcMessage {
    pub id: String,
    pub description: String,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_error: bool,
}

impl BcMessage {
    pub fn error(id: impl Into<String>, desc: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            description: desc.into(),
            is_error: true,
        }
    }

    pub fn info(id: impl Into<String>, desc: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            description: desc.into(),
            is_error: false,
        }
    }
}

/// A row within a nested level of a Business Component.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct BcLevelRow {
    pub values: BTreeMap<String, String>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub children: BTreeMap<String, Vec<BcLevelRow>>,
}

impl BcLevelRow {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn set(&mut self, attr: &str, val: impl Into<String>) -> &mut Self {
        self.values.insert(attr.to_string(), val.into());
        self
    }

    pub fn get(&self, attr: &str) -> Option<&str> {
        self.values.get(attr).map(|s| s.as_str())
    }

    pub fn add_child(&mut self, level_name: &str, row: BcLevelRow) {
        self.children
            .entry(level_name.to_string())
            .or_default()
            .push(row);
    }

    pub fn get_children(&self, level_name: &str) -> &[BcLevelRow] {
        self.children
            .get(level_name)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }
}

/// Record in the in-memory Business Component data store.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BcRecord {
    pub key: String,
    pub values: BTreeMap<String, String>,
    pub children: BTreeMap<String, Vec<BcLevelRow>>,
}

/// In-memory storage table simulating the database repository for Business Components.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct BcStore {
    pub records: BTreeMap<String, BcRecord>,
}

impl BcStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn contains(&self, key: &str) -> bool {
        self.records.contains_key(key)
    }

    pub fn get(&self, key: &str) -> Option<&BcRecord> {
        self.records.get(key)
    }

    pub fn insert(
        &mut self,
        key: impl Into<String>,
        values: BTreeMap<String, String>,
        children: BTreeMap<String, Vec<BcLevelRow>>,
    ) {
        let k = key.into();
        self.records.insert(
            k.clone(),
            BcRecord {
                key: k,
                values,
                children,
            },
        );
    }

    pub fn remove(&mut self, key: &str) -> Option<BcRecord> {
        self.records.remove(key)
    }

    pub fn count(&self) -> usize {
        self.records.len()
    }
}

/// A Business Component runtime instance representing an in-memory entity
/// with multi-level data, validations, defaults, and standard operations
/// (`Load`, `Save`, `Delete`, `Success`, `Fail`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BusinessComponent {
    pub structure: TransactionStructure,
    pub mode: BcMode,
    pub values: BTreeMap<String, String>,
    pub children: BTreeMap<String, Vec<BcLevelRow>>,
    pub messages: Vec<BcMessage>,
}

impl BusinessComponent {
    /// Creates a new Business Component instance initialized in Insert mode.
    pub fn new(structure: TransactionStructure) -> Self {
        Self {
            structure,
            mode: BcMode::Insert,
            values: BTreeMap::new(),
            children: BTreeMap::new(),
            messages: Vec::new(),
        }
    }

    /// Sets an attribute value on the root level.
    pub fn set_value(&mut self, attr: &str, val: impl Into<String>) -> &mut Self {
        self.values.insert(attr.to_string(), val.into());
        self
    }

    /// Gets an attribute value from the root level.
    pub fn get_value(&self, attr: &str) -> Option<&str> {
        self.values.get(attr).map(|s| s.as_str())
    }

    /// Adds a child row to a nested level.
    pub fn add_child(&mut self, level_name: &str, row: BcLevelRow) {
        self.children
            .entry(level_name.to_string())
            .or_default()
            .push(row);
    }

    /// Gets the rows of a nested level.
    pub fn get_children(&self, level_name: &str) -> &[BcLevelRow] {
        self.children
            .get(level_name)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }

    /// Gets mutable rows of a nested level.
    pub fn get_children_mut(&mut self, level_name: &str) -> &mut Vec<BcLevelRow> {
        self.children.entry(level_name.to_string()).or_default()
    }

    /// Computes the primary key string from the root primary key attributes.
    pub fn primary_key_value(&self) -> String {
        let pk_attrs = self.structure.root_primary_key();
        let parts: Vec<&str> = pk_attrs
            .iter()
            .filter_map(|attr| self.get_value(attr))
            .collect();
        if parts.len() == pk_attrs.len() {
            parts.join(";")
        } else {
            String::new()
        }
    }

    /// Applies default rules to unset or empty attributes on root and child rows.
    pub fn apply_defaults(&mut self) {
        for rule in &self.structure.rules {
            if let TransactionRule::Default { attribute, value } = rule {
                // Apply to root if attribute exists in root
                if self
                    .structure
                    .root
                    .attributes
                    .iter()
                    .any(|a| a.name.eq_ignore_ascii_case(attribute))
                {
                    if !self.values.contains_key(attribute)
                        || self.values[attribute].trim().is_empty()
                    {
                        self.values.insert(attribute.clone(), value.clone());
                    }
                }
                // Apply to child rows if attribute exists in child level
                for (level_name, rows) in &mut self.children {
                    if let Some(lvl) = self.structure.find_level(level_name) {
                        if lvl
                            .attributes
                            .iter()
                            .any(|a| a.name.eq_ignore_ascii_case(attribute))
                        {
                            for row in rows.iter_mut() {
                                if !row.values.contains_key(attribute)
                                    || row.values[attribute].trim().is_empty()
                                {
                                    row.values.insert(attribute.clone(), value.clone());
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// Performs automatic validation across all levels:
    /// - Required primary key attributes
    /// - Non-nullable constraints
    /// - Duplicate child row primary keys
    ///
    /// Returns true if valid, false if errors were recorded.
    pub fn validate(&mut self) -> bool {
        self.apply_defaults();
        let mut valid = true;

        // 1. Root level primary keys
        for pk in self.structure.root_primary_key() {
            match self.values.get(&pk) {
                Some(val) if !val.trim().is_empty() => {}
                _ => {
                    self.messages.push(BcMessage::error(
                        "GXM003",
                        format!("Root primary key attribute '{pk}' cannot be empty"),
                    ));
                    valid = false;
                }
            }
        }

        // 2. Root non-nullable attributes without formulas
        for attr in &self.structure.root.attributes {
            if !attr.is_key && !attr.is_nullable && attr.formula.is_none() {
                match self.values.get(&attr.name) {
                    Some(val) if !val.trim().is_empty() => {}
                    _ => {
                        self.messages.push(BcMessage::error(
                            "GXM004",
                            format!("Attribute '{}' cannot be empty", attr.name),
                        ));
                        valid = false;
                    }
                }
            }
        }

        // 3. Child levels validation
        for child_level in &self.structure.root.levels {
            if !validate_child_level(child_level, &self.children, &mut self.messages) {
                valid = false;
            }
        }

        valid
    }

    /// BC `Load` operation: loads record by primary key string from `store`.
    /// Sets mode to `Update` and returns true on success, or records an error
    /// and returns false if not found.
    pub fn load(&mut self, store: &BcStore, key: &str) -> bool {
        self.messages.clear();
        if let Some(rec) = store.get(key) {
            self.values = rec.values.clone();
            self.children = rec.children.clone();
            self.mode = BcMode::Update;
            true
        } else {
            self.messages.push(BcMessage::error(
                "GXM001",
                format!("Record with key '{key}' not found"),
            ));
            false
        }
    }

    /// BC `Save` operation: validates record and persists to `store`.
    /// Sets mode to `Update` and returns true on success, or records errors
    /// and returns false on failure.
    pub fn save(&mut self, store: &mut BcStore) -> bool {
        self.messages.clear();
        if !self.validate() {
            return false;
        }

        let key = self.primary_key_value();
        if key.is_empty() {
            self.messages.push(BcMessage::error(
                "GXM003",
                "Primary key value is required to save",
            ));
            return false;
        }

        if self.mode == BcMode::Insert && store.contains(&key) {
            self.messages.push(BcMessage::error(
                "GXM002",
                format!("Record with key '{key}' already exists"),
            ));
            return false;
        }

        store.insert(key, self.values.clone(), self.children.clone());
        self.mode = BcMode::Update;
        true
    }

    /// BC `Delete` operation: removes record from `store`.
    /// Sets mode to `Delete` and returns true on success, or records an error
    /// and returns false on failure.
    pub fn delete(&mut self, store: &mut BcStore) -> bool {
        self.messages.clear();
        let key = self.primary_key_value();
        if key.is_empty() || !store.contains(&key) {
            self.messages.push(BcMessage::error(
                "GXM001",
                format!("Record with key '{key}' not found to delete"),
            ));
            return false;
        }

        store.remove(&key);
        self.mode = BcMode::Delete;
        true
    }

    /// BC `Success` method: returns true if last operation had zero errors.
    pub fn success(&self) -> bool {
        !self.fail()
    }

    /// BC `Fail` method: returns true if last operation produced any error.
    pub fn fail(&self) -> bool {
        self.messages.iter().any(|m| m.is_error)
    }

    /// Returns all messages generated by the last operation.
    pub fn messages(&self) -> &[BcMessage] {
        &self.messages
    }

    /// Returns only the error messages generated by the last operation.
    pub fn errors(&self) -> Vec<&BcMessage> {
        self.messages.iter().filter(|m| m.is_error).collect()
    }
}

fn validate_child_level(
    level: &TransactionLevel,
    children_map: &BTreeMap<String, Vec<BcLevelRow>>,
    messages: &mut Vec<BcMessage>,
) -> bool {
    let mut valid = true;
    let rows = match children_map.get(&level.name) {
        Some(r) => r,
        None => return true,
    };

    let local_pks = level.primary_key();
    let mut seen_keys = BTreeSet::new();

    for (idx, row) in rows.iter().enumerate() {
        // Check local primary key on row
        let mut key_parts = Vec::new();
        for pk in &local_pks {
            match row.values.get(pk) {
                Some(val) if !val.trim().is_empty() => {
                    key_parts.push(val.as_str());
                }
                _ => {
                    messages.push(BcMessage::error(
                        "GXM005",
                        format!(
                            "Level '{}' row #{} missing key attribute '{pk}'",
                            level.name,
                            idx + 1
                        ),
                    ));
                    valid = false;
                }
            }
        }

        // Check uniqueness among sibling rows
        if key_parts.len() == local_pks.len() {
            let composite = key_parts.join(";");
            if !seen_keys.insert(composite.clone()) {
                messages.push(BcMessage::error(
                    "GXM006",
                    format!(
                        "Duplicate key '{composite}' in level '{}' row #{}",
                        level.name,
                        idx + 1
                    ),
                ));
                valid = false;
            }
        }

        // Non-nullable checks
        for attr in &level.attributes {
            if !attr.is_key && !attr.is_nullable && attr.formula.is_none() {
                match row.values.get(&attr.name) {
                    Some(val) if !val.trim().is_empty() => {}
                    _ => {
                        messages.push(BcMessage::error(
                            "GXM004",
                            format!(
                                "Level '{}' row #{} attribute '{}' cannot be empty",
                                level.name,
                                idx + 1,
                                attr.name
                            ),
                        ));
                        valid = false;
                    }
                }
            }
        }

        // Recurse for nested sub-levels
        for sub_level in &level.levels {
            if !validate_child_level(sub_level, &row.children, messages) {
                valid = false;
            }
        }
    }

    valid
}
