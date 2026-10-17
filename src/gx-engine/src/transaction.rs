//! Portable Transaction structure, levels, primary keys, and attribute definitions.
//!
//! Represents GeneXus Transactions with hierarchical levels, primary keys,
//! foreign keys, and attribute references, conforming to Plan 129 Matrix Row B2.

use crate::model::DataType;
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

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

/// The portable representation of a Transaction's structure part.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TransactionStructure {
    pub root: TransactionLevel,
}

impl TransactionStructure {
    pub fn new(root: TransactionLevel) -> Self {
        Self { root }
    }

    /// Validates the structure: bounded recursion, unique level and attribute
    /// names, required key attribute on all levels, and data type validity.
    pub fn validate(&self) -> Result<(), String> {
        let mut seen_levels = BTreeSet::new();
        self.root.validate(0, true, &mut seen_levels)
    }

    /// Returns the primary key attribute names of the root level in order.
    pub fn root_primary_key(&self) -> Vec<String> {
        self.root
            .attributes
            .iter()
            .filter(|a| a.is_key)
            .map(|a| a.name.clone())
            .collect()
    }

    /// Returns all attribute names in the transaction across all levels.
    pub fn all_attributes(&self) -> Vec<String> {
        let mut result = Vec::new();
        self.root.collect_attributes(&mut result);
        result
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
