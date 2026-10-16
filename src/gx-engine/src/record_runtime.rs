// Dependency-free runtime values for shallow GeneXus records and collections.
//
// Record and collection assignment aliases storage. `shallow_clone` copies only
// the outer record map or collection list; nested records and collection items
// retain their aliases, matching the measured native behavior.
use crate::{date::Date, datetime::DateTime, text::Text, types::Decimal};
use std::{cell::RefCell, collections::BTreeMap, rc::Rc};

const MAX_RECORD_FIELDS: usize = 1024;
const MAX_COLLECTION_ITEMS: usize = 100_000;

#[derive(Clone)]
pub enum Field {
    Numeric(Decimal),
    Text(Text),
    Boolean(bool),
    Date(Date),
    DateTime(DateTime),
    Record(Record),
    Collection(Collection),
}

/// An assignable record reference. Cloning a `Record` aliases its field map.
#[derive(Clone)]
pub struct Record {
    schema: &'static str,
    fields: Rc<RefCell<BTreeMap<&'static str, Field>>>,
}

impl Record {
    pub fn new(
        schema: &'static str,
        fields: BTreeMap<&'static str, Field>,
    ) -> Result<Self, String> {
        if !(1..=MAX_RECORD_FIELDS).contains(&fields.len()) {
            return Err(format!(
                "record must contain 1..={MAX_RECORD_FIELDS} fields"
            ));
        }
        Ok(Self {
            schema,
            fields: Rc::new(RefCell::new(fields)),
        })
    }

    pub fn schema(&self) -> &'static str {
        self.schema
    }

    pub fn get(&self, name: &str) -> Result<Field, String> {
        let field = self.fields.borrow().get(name).cloned();
        field.ok_or_else(|| format!("record schema `{}` has no field `{name}`", self.schema))
    }

    pub fn set(&self, name: &str, value: Field) -> Result<(), String> {
        let mut fields = self.fields.borrow_mut();
        let Some(existing) = fields.get_mut(name) else {
            return Err(format!(
                "record schema `{}` has no field `{name}`",
                self.schema
            ));
        };
        ensure_compatible(name, existing, &value)?;
        *existing = value;
        Ok(())
    }

    /// Copies this record's field map while preserving nested reference aliases.
    pub fn shallow_clone(&self) -> Self {
        let fields = self.fields.borrow().clone();
        Self {
            schema: self.schema,
            fields: Rc::new(RefCell::new(fields)),
        }
    }
}

/// An assignable collection reference. Cloning a `Collection` aliases its list.
#[derive(Clone)]
pub struct Collection {
    item_schema: &'static str,
    items: Rc<RefCell<Vec<Record>>>,
}

impl Collection {
    pub fn new(item_schema: &'static str) -> Self {
        Self {
            item_schema,
            items: Rc::new(RefCell::new(Vec::new())),
        }
    }

    pub fn item_schema(&self) -> &'static str {
        self.item_schema
    }

    pub fn add(&self, record: Record) -> Result<(), String> {
        if record.schema() != self.item_schema {
            return Err(format!(
                "collection expects item schema `{}`, got `{}`",
                self.item_schema,
                record.schema()
            ));
        }
        let mut items = self.items.borrow_mut();
        if items.len() >= MAX_COLLECTION_ITEMS {
            return Err(format!(
                "collection exceeds {MAX_COLLECTION_ITEMS} item resource limit"
            ));
        }
        items.push(record);
        Ok(())
    }

    /// Returns an aliased record at a checked, one-based index.
    pub fn item(&self, index: i64) -> Result<Record, String> {
        if index <= 0 {
            return Err(format!("collection index must be positive, got {index}"));
        }
        let offset = usize::try_from(index - 1)
            .map_err(|_| format!("collection index is outside the supported range: {index}"))?;
        let item = self.items.borrow().get(offset).cloned();
        item.ok_or_else(|| format!("collection index {index} is out of bounds"))
    }

    pub fn len(&self) -> usize {
        self.items.borrow().len()
    }

    pub fn is_empty(&self) -> bool {
        self.items.borrow().is_empty()
    }

    /// Copies the list while preserving aliases to each record in the list.
    pub fn shallow_clone(&self) -> Self {
        let items = self.items.borrow().clone();
        Self {
            item_schema: self.item_schema,
            items: Rc::new(RefCell::new(items)),
        }
    }

    pub fn insert(&self, index: i64, record: Record) -> Result<(), String> {
        if record.schema() != self.item_schema {
            return Err(format!(
                "collection expects item schema `{}`, got `{}`",
                self.item_schema,
                record.schema()
            ));
        }
        let mut items = self.items.borrow_mut();
        if items.len() >= MAX_COLLECTION_ITEMS {
            return Err(format!(
                "collection exceeds {MAX_COLLECTION_ITEMS} item resource limit"
            ));
        }
        if index == 0 {
            items.push(record);
            return Ok(());
        }
        if index < 0 || (index as usize) > items.len() + 1 {
            return Err(format!("collection insert index out of range: {index}"));
        }
        let pos = (index - 1) as usize;
        items.insert(pos, record);
        Ok(())
    }

    pub fn remove(&self, index: i64) -> Result<(), String> {
        let mut items = self.items.borrow_mut();
        if index > 0 && (index as usize) <= items.len() {
            items.remove((index - 1) as usize);
        }
        Ok(())
    }

    pub fn clear(&self) {
        self.items.borrow_mut().clear();
    }

    pub fn index_of(&self, record: &Record) -> i64 {
        let items = self.items.borrow();
        for (i, it) in items.iter().enumerate() {
            if Rc::ptr_eq(&it.fields, &record.fields) {
                return (i + 1) as i64;
            }
        }
        0
    }
}

fn ensure_compatible(name: &str, current: &Field, replacement: &Field) -> Result<(), String> {
    match (current, replacement) {
        (Field::Numeric(_), Field::Numeric(_))
        | (Field::Text(_), Field::Text(_))
        | (Field::Boolean(_), Field::Boolean(_))
        | (Field::Date(_), Field::Date(_))
        | (Field::DateTime(_), Field::DateTime(_)) => Ok(()),
        (Field::Record(expected), Field::Record(actual))
            if expected.schema() == actual.schema() =>
        {
            Ok(())
        }
        (Field::Collection(expected), Field::Collection(actual))
            if expected.item_schema() == actual.item_schema() =>
        {
            Ok(())
        }
        (Field::Record(expected), Field::Record(actual)) => Err(format!(
            "record field `{name}` expects schema `{}`, got `{}`",
            expected.schema(),
            actual.schema()
        )),
        (Field::Collection(expected), Field::Collection(actual)) => Err(format!(
            "collection field `{name}` expects item schema `{}`, got `{}`",
            expected.item_schema(),
            actual.item_schema()
        )),
        (expected, actual) => Err(format!(
            "field `{name}` type mismatch: expected {}, got {}",
            field_kind(expected),
            field_kind(actual)
        )),
    }
}

fn field_kind(field: &Field) -> &'static str {
    match field {
        Field::Numeric(_) => "Numeric",
        Field::Text(_) => "Text",
        Field::Boolean(_) => "Boolean",
        Field::Date(_) => "Date",
        Field::DateTime(_) => "DateTime",
        Field::Record(_) => "Record",
        Field::Collection(_) => "Collection",
    }
}
