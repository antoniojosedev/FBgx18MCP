//! Bounded, owned structured values for the portable engine.
//!
//! `Clone`/`deep_clone` copy records and collections recursively. This is an
//! implementation-level value-copy operation, not a claim about GeneXus
//! assignment, reference, or `Clone` method semantics; those require native
//! characterization before compiler lowering can choose a mapping.
use crate::{date::Date, datetime::DateTime, text::Text, types::Decimal};
use serde::{Deserialize, Serialize};
use std::{collections::HashSet, str::FromStr};

pub const MAX_SCHEMA_JSON_BYTES: usize = 1024 * 1024;
pub const MAX_STRUCTURED_JSON_BYTES: usize = 1024 * 1024;
pub const MAX_FIELDS_PER_RECORD: usize = 4096;
pub const MAX_COLLECTION_ITEMS: usize = 65_536;
pub const MAX_STRUCTURED_DEPTH: usize = 32;
pub const MAX_STRUCTURED_NODES: usize = 65_536;
pub const MAX_STRUCTURED_TEXT_BYTES: usize = 1024 * 1024;
const MAX_IDENTIFIER_BYTES: usize = 128;
const FORMAT_VERSION: u32 = 1;

/// A closed, recursively composable schema. Collection element types are exact;
/// values with a different primitive declaration are rejected rather than coerced.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum StructuredType {
    Boolean {},
    Numeric { precision: u8, scale: u8 },
    Character { length: u32 },
    Varchar { length: u32 },
    Date {},
    DateTime {},
    Record { schema: Box<RecordSchema> },
    Collection { element: Box<StructuredType> },
}

impl StructuredType {
    pub fn record(schema: RecordSchema) -> Self {
        Self::Record {
            schema: Box::new(schema),
        }
    }

    pub fn collection(element: StructuredType) -> Self {
        Self::Collection {
            element: Box::new(element),
        }
    }

    pub fn validate(&self) -> Result<(), String> {
        let mut budget = SchemaBudget::default();
        validate_type(self, 0, &mut budget)
    }
}

/// A field is immutable after construction, so schemas cannot be mutated around
/// their identifier and type checks.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FieldSchema {
    name: String,
    field_type: StructuredType,
}

impl FieldSchema {
    pub fn new(name: impl Into<String>, field_type: StructuredType) -> Result<Self, String> {
        let mut field = Self {
            name: name.into(),
            field_type,
        };
        normalize_field(&mut field)?;
        let mut budget = SchemaBudget::default();
        validate_type(&field.field_type, 0, &mut budget)?;
        Ok(field)
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn field_type(&self) -> &StructuredType {
        &self.field_type
    }
}

/// Ordered schema: declaration order is preserved in serialization and runtime
/// field storage. Names are canonical ASCII uppercase identifiers.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecordSchema {
    name: String,
    fields: Vec<FieldSchema>,
}

impl RecordSchema {
    pub fn new(name: impl Into<String>, fields: Vec<FieldSchema>) -> Result<Self, String> {
        let mut schema = Self {
            name: name.into(),
            fields,
        };
        schema.normalize_and_validate()?;
        Ok(schema)
    }

    /// Parses only the portable schema format; this is not a GeneXus SDT file parser.
    pub fn from_json(input: &str) -> Result<Self, String> {
        if input.len() > MAX_SCHEMA_JSON_BYTES {
            return Err(format!(
                "schema JSON exceeds the {MAX_SCHEMA_JSON_BYTES}-byte limit"
            ));
        }
        let mut schema: Self = serde_json::from_str(input)
            .map_err(|error| format!("invalid structured schema JSON: {error}"))?;
        schema.normalize_and_validate()?;
        Ok(schema)
    }

    pub fn to_json(&self) -> Result<String, String> {
        self.validate()?;
        bounded_json(self, MAX_SCHEMA_JSON_BYTES, "schema")
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn fields(&self) -> &[FieldSchema] {
        &self.fields
    }

    pub fn field(&self, name: &str) -> Option<&FieldSchema> {
        let canonical = canonical_identifier(name).ok()?;
        self.fields.iter().find(|field| field.name == canonical)
    }

    pub fn validate(&self) -> Result<(), String> {
        let mut budget = SchemaBudget::default();
        validate_schema(self, 0, &mut budget)
    }

    fn normalize_and_validate(&mut self) -> Result<(), String> {
        self.name = canonical_identifier(&self.name)
            .map_err(|error| format!("record schema name {error}"))?;
        if self.fields.len() > MAX_FIELDS_PER_RECORD {
            return Err(format!(
                "record '{}' exceeds {MAX_FIELDS_PER_RECORD} fields",
                self.name
            ));
        }
        for field in &mut self.fields {
            normalize_field(field)?;
            normalize_type(&mut field.field_type)?;
        }
        self.validate()
    }
}

/// Runtime values own their nested records and collections. Primitive metadata
/// travels with each value so mismatched declarations fail closed.
#[derive(Debug, Clone)]
pub enum StructuredValue {
    Boolean(bool),
    Numeric {
        precision: u8,
        scale: u8,
        value: Decimal,
    },
    Character {
        length: u32,
        value: Text,
    },
    Varchar {
        length: u32,
        value: Text,
    },
    Date(Date),
    DateTime(DateTime),
    Record(Record),
    Collection(Collection),
}

impl StructuredValue {
    pub fn boolean(value: bool) -> Self {
        Self::Boolean(value)
    }

    pub fn numeric(precision: u8, scale: u8, value: Decimal) -> Result<Self, String> {
        let value = crate::types::NumericType::new(precision, scale)?.assign(value)?;
        Ok(Self::Numeric {
            precision,
            scale,
            value,
        })
    }

    pub fn character(length: u32, value: &str) -> Result<Self, String> {
        validate_text_length(length)?;
        Ok(Self::Character {
            length,
            value: Text::new(value)?,
        })
    }

    pub fn varchar(length: u32, value: &str) -> Result<Self, String> {
        validate_text_length(length)?;
        Ok(Self::Varchar {
            length,
            value: Text::new(value)?,
        })
    }

    pub fn date(value: Date) -> Self {
        Self::Date(value)
    }

    pub fn date_time(value: DateTime) -> Self {
        Self::DateTime(value)
    }

    pub fn record(value: Record) -> Self {
        Self::Record(value)
    }

    pub fn collection(value: Collection) -> Self {
        Self::Collection(value)
    }

    pub fn deep_clone(&self) -> Self {
        self.clone()
    }

    pub fn to_json(&self) -> Result<String, String> {
        let mut usage = Usage::default();
        validate_value_for_any_type(self, 0, &mut usage)?;
        bounded_json(
            &value_to_wire(self, 0)?,
            MAX_STRUCTURED_JSON_BYTES,
            "structured value",
        )
    }
}

/// Owned record whose Rust `Clone` recursively copies its members. Language-level
/// assignment/reference behavior is deliberately left to the compiler adapter.
#[derive(Debug, Clone)]
pub struct Record {
    schema: RecordSchema,
    values: Vec<StructuredValue>,
    usage: Usage,
}

impl Record {
    pub fn new(schema: RecordSchema) -> Result<Self, String> {
        schema.validate()?;
        let mut values = Vec::with_capacity(schema.fields.len());
        for field in &schema.fields {
            values.push(default_value(&field.field_type, 1)?);
        }
        let mut record = Self {
            schema,
            values,
            usage: Usage::default(),
        };
        record.usage = measure_record(&record)?;
        Ok(record)
    }

    pub fn schema(&self) -> &RecordSchema {
        &self.schema
    }

    pub fn get(&self, name: &str) -> Result<&StructuredValue, String> {
        let canonical =
            canonical_identifier(name).map_err(|error| format!("record member name {error}"))?;
        let index = self
            .schema
            .fields
            .iter()
            .position(|field| field.name == canonical)
            .ok_or_else(|| format!("record '{}' has no member '{canonical}'", self.schema.name))?;
        Ok(&self.values[index])
    }

    pub fn set(&mut self, name: &str, value: StructuredValue) -> Result<(), String> {
        let canonical =
            canonical_identifier(name).map_err(|error| format!("record member name {error}"))?;
        let index = self
            .schema
            .fields
            .iter()
            .position(|field| field.name == canonical)
            .ok_or_else(|| format!("record '{}' has no member '{canonical}'", self.schema.name))?;
        let field_type = &self.schema.fields[index].field_type;
        let old_usage = measure_value(field_type, &self.values[index], 1)?;
        let new_usage = measure_value(field_type, &value, 1)?;
        let next_usage = self.usage.replace(old_usage, new_usage)?;
        self.values[index] = value;
        self.usage = next_usage;
        Ok(())
    }

    /// Mutates one member transactionally; invalid nested edits are rolled back.
    pub fn update<F>(&mut self, name: &str, update: F) -> Result<(), String>
    where
        F: FnOnce(&mut StructuredValue) -> Result<(), String>,
    {
        let canonical =
            canonical_identifier(name).map_err(|error| format!("record member name {error}"))?;
        let index = self
            .schema
            .fields
            .iter()
            .position(|field| field.name == canonical)
            .ok_or_else(|| format!("record '{}' has no member '{canonical}'", self.schema.name))?;
        let previous = self.values[index].clone();
        let previous_usage = self.usage;
        if let Err(error) = update(&mut self.values[index]) {
            self.values[index] = previous;
            return Err(error);
        }
        match measure_record(self) {
            Ok(usage) => {
                self.usage = usage;
                Ok(())
            }
            Err(error) => {
                self.values[index] = previous;
                self.usage = previous_usage;
                Err(error)
            }
        }
    }

    pub fn values(&self) -> &[StructuredValue] {
        &self.values
    }

    pub fn deep_clone(&self) -> Self {
        self.clone()
    }

    pub fn validate(&self) -> Result<(), String> {
        self.schema.validate()?;
        if self.values.len() != self.schema.fields.len() {
            return Err("record value count does not match its schema".into());
        }
        if measure_record(self)? == self.usage {
            Ok(())
        } else {
            Err("record resource accounting is inconsistent".into())
        }
    }

    pub fn to_json(&self) -> Result<String, String> {
        self.validate()?;
        let fields = self
            .schema
            .fields
            .iter()
            .zip(&self.values)
            .map(|(field, value)| {
                Ok(serde_json::json!({
                    "name": field.name,
                    "value": value_to_wire(value, 1)?
                }))
            })
            .collect::<Result<Vec<_>, String>>()?;
        bounded_json(
            &serde_json::json!({
                "format_version": FORMAT_VERSION,
                "schema": self.schema,
                "fields": fields
            }),
            MAX_STRUCTURED_JSON_BYTES,
            "record",
        )
    }
}

/// Homogeneous owned collection. Its element schema is fixed at construction;
/// every insertion and replacement is checked before the value is retained.
#[derive(Debug, Clone)]
pub struct Collection {
    element_type: StructuredType,
    items: Vec<StructuredValue>,
    usage: Usage,
}

impl Collection {
    pub fn new(element_type: StructuredType) -> Result<Self, String> {
        element_type.validate()?;
        Ok(Self {
            element_type,
            items: Vec::new(),
            usage: Usage {
                nodes: 1,
                text_bytes: 0,
            },
        })
    }

    pub fn element_type(&self) -> &StructuredType {
        &self.element_type
    }

    pub fn len(&self) -> usize {
        self.items.len()
    }

    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    pub fn get(&self, index: usize) -> Result<&StructuredValue, String> {
        self.items
            .get(index)
            .ok_or_else(|| format!("collection index {index} is out of range"))
    }

    pub fn items(&self) -> &[StructuredValue] {
        &self.items
    }

    pub fn push(&mut self, value: StructuredValue) -> Result<(), String> {
        if self.items.len() >= MAX_COLLECTION_ITEMS {
            return Err(format!("collection exceeds {MAX_COLLECTION_ITEMS} items"));
        }
        let item_usage = measure_value(&self.element_type, &value, 1)?;
        let next_usage = self.usage.add(item_usage)?;
        self.items.push(value);
        self.usage = next_usage;
        Ok(())
    }

    pub fn set(&mut self, index: usize, value: StructuredValue) -> Result<(), String> {
        let current = self
            .items
            .get(index)
            .ok_or_else(|| format!("collection index {index} is out of range"))?;
        let old_usage = measure_value(&self.element_type, current, 1)?;
        let new_usage = measure_value(&self.element_type, &value, 1)?;
        let next_usage = self.usage.replace(old_usage, new_usage)?;
        self.items[index] = value;
        self.usage = next_usage;
        Ok(())
    }

    /// Mutates one item transactionally; invalid nested edits are rolled back.
    pub fn update<F>(&mut self, index: usize, update: F) -> Result<(), String>
    where
        F: FnOnce(&mut StructuredValue) -> Result<(), String>,
    {
        if index >= self.items.len() {
            return Err(format!("collection index {index} is out of range"));
        }
        let previous = self.items[index].clone();
        let previous_usage = self.usage;
        if let Err(error) = update(&mut self.items[index]) {
            self.items[index] = previous;
            return Err(error);
        }
        match measure_collection(self) {
            Ok(usage) => {
                self.usage = usage;
                Ok(())
            }
            Err(error) => {
                self.items[index] = previous;
                self.usage = previous_usage;
                Err(error)
            }
        }
    }

    pub fn deep_clone(&self) -> Self {
        self.clone()
    }

    pub fn validate(&self) -> Result<(), String> {
        self.element_type.validate()?;
        if self.items.len() > MAX_COLLECTION_ITEMS {
            return Err(format!("collection exceeds {MAX_COLLECTION_ITEMS} items"));
        }
        if measure_collection(self)? == self.usage {
            Ok(())
        } else {
            Err("collection resource accounting is inconsistent".into())
        }
    }

    pub fn to_json(&self) -> Result<String, String> {
        self.validate()?;
        let items = self
            .items
            .iter()
            .map(|item| value_to_wire(item, 1))
            .collect::<Result<Vec<_>, _>>()?;
        bounded_json(
            &serde_json::json!({
                "format_version": FORMAT_VERSION,
                "element_type": self.element_type,
                "items": items
            }),
            MAX_STRUCTURED_JSON_BYTES,
            "collection",
        )
    }
}

#[derive(Default)]
struct SchemaBudget {
    nodes: usize,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
struct Usage {
    nodes: usize,
    text_bytes: usize,
}

impl Usage {
    fn add(self, other: Self) -> Result<Self, String> {
        Self {
            nodes: self
                .nodes
                .checked_add(other.nodes)
                .ok_or_else(|| "structured value size overflow".to_owned())?,
            text_bytes: self
                .text_bytes
                .checked_add(other.text_bytes)
                .ok_or_else(|| "structured text size overflow".to_owned())?,
        }
        .check_limits()
    }

    fn replace(self, previous: Self, next: Self) -> Result<Self, String> {
        Self {
            nodes: self
                .nodes
                .checked_sub(previous.nodes)
                .and_then(|nodes| nodes.checked_add(next.nodes))
                .ok_or_else(|| "structured value size accounting error".to_owned())?,
            text_bytes: self
                .text_bytes
                .checked_sub(previous.text_bytes)
                .and_then(|bytes| bytes.checked_add(next.text_bytes))
                .ok_or_else(|| "structured text size accounting error".to_owned())?,
        }
        .check_limits()
    }

    fn check_limits(self) -> Result<Self, String> {
        if self.nodes > MAX_STRUCTURED_NODES {
            return Err(format!(
                "structured value exceeds {MAX_STRUCTURED_NODES} nodes"
            ));
        }
        if self.text_bytes > MAX_STRUCTURED_TEXT_BYTES {
            return Err(format!(
                "structured text exceeds {MAX_STRUCTURED_TEXT_BYTES} bytes"
            ));
        }
        Ok(self)
    }
}

fn normalize_field(field: &mut FieldSchema) -> Result<(), String> {
    field.name =
        canonical_identifier(&field.name).map_err(|error| format!("record field name {error}"))?;
    Ok(())
}

fn normalize_type(value_type: &mut StructuredType) -> Result<(), String> {
    match value_type {
        StructuredType::Record { schema } => schema.normalize_and_validate(),
        StructuredType::Collection { element } => normalize_type(element),
        _ => Ok(()),
    }
}

fn validate_schema(
    schema: &RecordSchema,
    depth: usize,
    budget: &mut SchemaBudget,
) -> Result<(), String> {
    check_depth(depth)?;
    count_schema_node(budget)?;
    canonical_identifier(&schema.name).map_err(|error| format!("record schema name {error}"))?;
    if schema.fields.len() > MAX_FIELDS_PER_RECORD {
        return Err(format!(
            "record '{}' exceeds {MAX_FIELDS_PER_RECORD} fields",
            schema.name
        ));
    }
    let mut names = HashSet::with_capacity(schema.fields.len());
    for field in &schema.fields {
        canonical_identifier(&field.name).map_err(|error| format!("record field name {error}"))?;
        if !names.insert(field.name.to_ascii_uppercase()) {
            return Err(format!(
                "record '{}' has duplicate field '{}' (names are case-insensitive)",
                schema.name, field.name
            ));
        }
        validate_type(&field.field_type, depth + 1, budget)?;
    }
    Ok(())
}

fn validate_type(
    value_type: &StructuredType,
    depth: usize,
    budget: &mut SchemaBudget,
) -> Result<(), String> {
    check_depth(depth)?;
    count_schema_node(budget)?;
    match value_type {
        StructuredType::Boolean {} | StructuredType::Date {} | StructuredType::DateTime {} => {
            Ok(())
        }
        StructuredType::Numeric { precision, scale } => {
            crate::types::NumericType::new(*precision, *scale).map(|_| ())
        }
        StructuredType::Character { length } | StructuredType::Varchar { length } => {
            validate_text_length(*length)
        }
        StructuredType::Record { schema } => validate_schema(schema, depth + 1, budget),
        StructuredType::Collection { element } => validate_type(element, depth + 1, budget),
    }
}

fn count_schema_node(budget: &mut SchemaBudget) -> Result<(), String> {
    budget.nodes = budget
        .nodes
        .checked_add(1)
        .ok_or_else(|| "structured schema size overflow".to_owned())?;
    if budget.nodes > MAX_STRUCTURED_NODES {
        return Err(format!(
            "structured schema exceeds {MAX_STRUCTURED_NODES} nodes"
        ));
    }
    Ok(())
}

fn canonical_identifier(value: &str) -> Result<String, String> {
    let bytes = value.as_bytes();
    let valid = !bytes.is_empty()
        && bytes.len() <= MAX_IDENTIFIER_BYTES
        && (bytes[0].is_ascii_alphabetic() || bytes[0] == b'_')
        && bytes[1..]
            .iter()
            .all(|byte| byte.is_ascii_alphanumeric() || *byte == b'_');
    if !valid {
        return Err(format!(
            "must be an ASCII identifier of 1 to {MAX_IDENTIFIER_BYTES} characters (letter or underscore first; then letters, digits, or underscores)"
        ));
    }
    Ok(value.to_ascii_uppercase())
}

fn validate_text_length(length: u32) -> Result<(), String> {
    if !(1..=65_535).contains(&length) {
        return Err("text declaration length must be 1..65535".into());
    }
    Ok(())
}

fn check_depth(depth: usize) -> Result<(), String> {
    if depth > MAX_STRUCTURED_DEPTH {
        return Err(format!(
            "structured value exceeds maximum depth {MAX_STRUCTURED_DEPTH}"
        ));
    }
    Ok(())
}

fn default_value(value_type: &StructuredType, depth: usize) -> Result<StructuredValue, String> {
    check_depth(depth)?;
    Ok(match value_type {
        StructuredType::Boolean {} => StructuredValue::Boolean(false),
        StructuredType::Numeric { precision, scale } => StructuredValue::Numeric {
            precision: *precision,
            scale: *scale,
            value: Decimal::from_str("0")
                .map_err(|error| format!("invalid Numeric zero: {error}"))?,
        },
        StructuredType::Character { length } => StructuredValue::Character {
            length: *length,
            value: Text::new("")?,
        },
        StructuredType::Varchar { length } => StructuredValue::Varchar {
            length: *length,
            value: Text::new("")?,
        },
        StructuredType::Date {} => StructuredValue::Date(Date::empty()),
        StructuredType::DateTime {} => StructuredValue::DateTime(DateTime::empty()),
        StructuredType::Record { schema } => {
            let mut values = Vec::with_capacity(schema.fields.len());
            for field in &schema.fields {
                values.push(default_value(&field.field_type, depth + 1)?);
            }
            let mut record = Record {
                schema: (**schema).clone(),
                values,
                usage: Usage::default(),
            };
            record.usage = measure_record(&record)?;
            StructuredValue::Record(record)
        }
        StructuredType::Collection { element } => StructuredValue::Collection(Collection {
            element_type: (**element).clone(),
            items: Vec::new(),
            usage: Usage {
                nodes: 1,
                text_bytes: 0,
            },
        }),
    })
}

fn measure_record(record: &Record) -> Result<Usage, String> {
    let mut usage = Usage::default();
    validate_record(record, 0, &mut usage)?;
    Ok(usage)
}

fn measure_collection(collection: &Collection) -> Result<Usage, String> {
    let mut usage = Usage::default();
    validate_collection(collection, 0, &mut usage)?;
    Ok(usage)
}

fn measure_value(
    expected: &StructuredType,
    value: &StructuredValue,
    depth: usize,
) -> Result<Usage, String> {
    let mut usage = Usage::default();
    validate_value(expected, value, depth, &mut usage)?;
    Ok(usage)
}

fn validate_record(record: &Record, depth: usize, usage: &mut Usage) -> Result<(), String> {
    check_depth(depth)?;
    add_usage_node(usage)?;
    validate_schema(&record.schema, depth + 1, &mut SchemaBudget::default())?;
    if record.values.len() != record.schema.fields.len() {
        return Err("record value count does not match its schema".into());
    }
    for (field, value) in record.schema.fields.iter().zip(&record.values) {
        validate_value(&field.field_type, value, depth + 1, usage)?;
    }
    Ok(())
}

fn validate_collection(
    collection: &Collection,
    depth: usize,
    usage: &mut Usage,
) -> Result<(), String> {
    check_depth(depth)?;
    add_usage_node(usage)?;
    collection.element_type.validate()?;
    if collection.items.len() > MAX_COLLECTION_ITEMS {
        return Err(format!("collection exceeds {MAX_COLLECTION_ITEMS} items"));
    }
    for value in &collection.items {
        validate_value(&collection.element_type, value, depth + 1, usage)?;
    }
    Ok(())
}

fn validate_value(
    expected: &StructuredType,
    value: &StructuredValue,
    depth: usize,
    usage: &mut Usage,
) -> Result<(), String> {
    check_depth(depth)?;
    add_usage_node(usage)?;
    let matches = match (expected, value) {
        (StructuredType::Boolean {}, StructuredValue::Boolean(_))
        | (StructuredType::Date {}, StructuredValue::Date(_))
        | (StructuredType::DateTime {}, StructuredValue::DateTime(_)) => true,
        (
            StructuredType::Numeric {
                precision: expected_precision,
                scale: expected_scale,
            },
            StructuredValue::Numeric {
                precision,
                scale,
                value,
            },
        ) => {
            if expected_precision != precision || expected_scale != scale {
                false
            } else {
                validate_numeric_storage(*precision, *scale, *value)?;
                true
            }
        }
        (
            StructuredType::Character { length: expected },
            StructuredValue::Character { length, value },
        )
        | (
            StructuredType::Varchar { length: expected },
            StructuredValue::Varchar { length, value },
        ) => {
            if expected != length {
                false
            } else {
                add_text_usage(usage, value.to_string().len())?;
                true
            }
        }
        (StructuredType::Record { schema }, StructuredValue::Record(record)) => {
            if schema.as_ref() != &record.schema {
                false
            } else {
                validate_record(record, depth + 1, usage)?;
                return Ok(());
            }
        }
        (StructuredType::Collection { element }, StructuredValue::Collection(collection)) => {
            if element.as_ref() != &collection.element_type {
                false
            } else {
                validate_collection(collection, depth + 1, usage)?;
                return Ok(());
            }
        }
        _ => false,
    };
    if matches {
        Ok(())
    } else {
        Err(format!(
            "structured value type does not match declared type {}",
            type_name(expected)
        ))
    }
}

fn validate_numeric_storage(precision: u8, scale: u8, value: Decimal) -> Result<(), String> {
    let numeric_type = crate::types::NumericType::new(precision, scale)?;
    if numeric_type.assign(value)? == value {
        Ok(())
    } else {
        Err("Numeric value is not normalized for its declaration".into())
    }
}

fn validate_value_for_any_type(
    value: &StructuredValue,
    depth: usize,
    usage: &mut Usage,
) -> Result<(), String> {
    let value_type = value_type(value)?;
    validate_value(&value_type, value, depth, usage)
}

fn value_type(value: &StructuredValue) -> Result<StructuredType, String> {
    Ok(match value {
        StructuredValue::Boolean(_) => StructuredType::Boolean {},
        StructuredValue::Numeric {
            precision,
            scale,
            value,
        } => {
            validate_numeric_storage(*precision, *scale, *value)?;
            StructuredType::Numeric {
                precision: *precision,
                scale: *scale,
            }
        }
        StructuredValue::Character { length, .. } => {
            validate_text_length(*length)?;
            StructuredType::Character { length: *length }
        }
        StructuredValue::Varchar { length, .. } => {
            validate_text_length(*length)?;
            StructuredType::Varchar { length: *length }
        }
        StructuredValue::Date(_) => StructuredType::Date {},
        StructuredValue::DateTime(_) => StructuredType::DateTime {},
        StructuredValue::Record(record) => StructuredType::record(record.schema.clone()),
        StructuredValue::Collection(collection) => {
            StructuredType::collection(collection.element_type.clone())
        }
    })
}

fn add_usage_node(usage: &mut Usage) -> Result<(), String> {
    usage.nodes = usage
        .nodes
        .checked_add(1)
        .ok_or_else(|| "structured value size overflow".to_owned())?;
    if usage.nodes > MAX_STRUCTURED_NODES {
        return Err(format!(
            "structured value exceeds {MAX_STRUCTURED_NODES} nodes"
        ));
    }
    Ok(())
}

fn add_text_usage(usage: &mut Usage, bytes: usize) -> Result<(), String> {
    usage.text_bytes = usage
        .text_bytes
        .checked_add(bytes)
        .ok_or_else(|| "structured text size overflow".to_owned())?;
    if usage.text_bytes > MAX_STRUCTURED_TEXT_BYTES {
        return Err(format!(
            "structured text exceeds {MAX_STRUCTURED_TEXT_BYTES} bytes"
        ));
    }
    Ok(())
}

fn type_name(value_type: &StructuredType) -> &'static str {
    match value_type {
        StructuredType::Boolean {} => "Boolean",
        StructuredType::Numeric { .. } => "Numeric",
        StructuredType::Character { .. } => "Character",
        StructuredType::Varchar { .. } => "Varchar",
        StructuredType::Date {} => "Date",
        StructuredType::DateTime {} => "DateTime",
        StructuredType::Record { .. } => "Record",
        StructuredType::Collection { .. } => "Collection",
    }
}

fn value_to_wire(value: &StructuredValue, depth: usize) -> Result<serde_json::Value, String> {
    check_depth(depth)?;
    Ok(match value {
        StructuredValue::Boolean(value) => serde_json::json!({
            "kind": "boolean",
            "value": value
        }),
        StructuredValue::Numeric {
            precision,
            scale,
            value,
        } => serde_json::json!({
            "kind": "numeric",
            "precision": precision,
            "scale": scale,
            "value": value.to_string()
        }),
        StructuredValue::Character { length, value } => serde_json::json!({
            "kind": "character",
            "length": length,
            "value": value.to_string()
        }),
        StructuredValue::Varchar { length, value } => serde_json::json!({
            "kind": "varchar",
            "length": length,
            "value": value.to_string()
        }),
        StructuredValue::Date(value) => serde_json::json!({
            "kind": "date",
            "value": if value.is_empty() {
                serde_json::Value::Null
            } else {
                serde_json::json!([value.year(), value.month(), value.day()])
            }
        }),
        StructuredValue::DateTime(value) => serde_json::json!({
            "kind": "date_time",
            "value": if value.is_empty() {
                serde_json::Value::Null
            } else {
                serde_json::json!([
                    value.year(), value.month(), value.day(),
                    value.hour(), value.minute(), value.second()
                ])
            }
        }),
        StructuredValue::Record(record) => {
            let fields = record
                .schema
                .fields
                .iter()
                .zip(&record.values)
                .map(|(field, field_value)| {
                    Ok(serde_json::json!({
                        "name": field.name,
                        "value": value_to_wire(field_value, depth + 1)?
                    }))
                })
                .collect::<Result<Vec<_>, String>>()?;
            serde_json::json!({
                "kind": "record",
                "schema": serde_json::to_value(&record.schema)
                    .map_err(|error| format!("could not serialize record schema: {error}"))?,
                "fields": fields
            })
        }
        StructuredValue::Collection(collection) => {
            let items = collection
                .items
                .iter()
                .map(|item| value_to_wire(item, depth + 1))
                .collect::<Result<Vec<_>, _>>()?;
            serde_json::json!({
                "kind": "collection",
                "element_type": serde_json::to_value(&collection.element_type)
                    .map_err(|error| format!("could not serialize collection type: {error}"))?,
                "items": items
            })
        }
    })
}

fn bounded_json<T: Serialize>(value: &T, limit: usize, label: &str) -> Result<String, String> {
    let output = serde_json::to_string(value)
        .map_err(|error| format!("could not serialize {label}: {error}"))?;
    if output.len() > limit {
        return Err(format!("serialized {label} exceeds the {limit}-byte limit"));
    }
    Ok(output)
}
