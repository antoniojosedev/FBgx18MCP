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

    /// Creates an empty record with identical schema, fields and sub-structure.
    pub fn empty_clone(&self) -> Self {
        let mut empty_fields = BTreeMap::new();
        for (&name, field) in self.fields.borrow().iter() {
            let empty_field = match field {
                Field::Numeric(_) => Field::Numeric("0".parse::<Decimal>().expect("0")),
                Field::Text(_) => Field::Text(Text::new("").expect("empty text")),
                Field::Boolean(_) => Field::Boolean(false),
                Field::Date(_) => Field::Date(Date::empty()),
                Field::DateTime(_) => Field::DateTime(DateTime::empty()),
                Field::Record(r) => Field::Record(r.empty_clone()),
                Field::Collection(c) => Field::Collection(c.empty_clone()),
            };
            empty_fields.insert(name, empty_field);
        }
        Self {
            schema: self.schema,
            fields: Rc::new(RefCell::new(empty_fields)),
        }
    }

    /// Serializes this record to compact JSON string matching GeneXus GxUserType.ToJSonString().
    pub fn to_json(&self) -> String {
        let fields = self.fields.borrow();
        let mut out = String::from("{");
        let mut first = true;
        for (name, field) in fields.iter() {
            if !first {
                out.push(',');
            }
            first = false;
            out.push('"');
            out.push_str(name);
            out.push_str("\":");
            match field {
                Field::Numeric(d) => out.push_str(&d.to_string()),
                Field::Text(t) => {
                    out.push('"');
                    out.push_str(&escape_json(t.as_str()));
                    out.push('"');
                }
                Field::Boolean(b) => out.push_str(if *b { "true" } else { "false" }),
                Field::Date(d) => {
                    if d.is_empty() {
                        out.push_str("\"0000-00-00\"");
                    } else {
                        out.push_str(&format!(
                            "\"{:04}-{:02}-{:02}\"",
                            d.year(),
                            d.month(),
                            d.day()
                        ));
                    }
                }
                Field::DateTime(dt) => {
                    if dt.is_empty() {
                        out.push_str("\"0000-00-00T00:00:00\"");
                    } else {
                        out.push_str(&format!(
                            "\"{:04}-{:02}-{:02}T{:02}:{:02}:{:02}\"",
                            dt.year(),
                            dt.month(),
                            dt.day(),
                            dt.hour(),
                            dt.minute(),
                            dt.second()
                        ));
                    }
                }
                Field::Record(r) => out.push_str(&r.to_json()),
                Field::Collection(c) => out.push_str(&c.to_json()),
            }
        }
        out.push('}');
        out
    }

    pub fn to_json_string(&self) -> String {
        self.to_json()
    }

    /// Deserializes JSON string into this record matching GeneXus GxUserType.FromJSonString().
    pub fn from_json(&self, json_str: &str) -> bool {
        let Some(json) = parse_json(json_str) else {
            return false;
        };
        match json {
            SimpleJson::Object(entries) => self.apply_json_object(&entries),
            _ => false,
        }
    }

    pub fn from_json_string(&self, json_str: &str) -> bool {
        self.from_json(json_str)
    }

    /// Serializes this record to formatted XML matching GeneXus GxUserType.ToXml().
    pub fn to_xml(&self, root_name: Option<&str>) -> String {
        let root = root_name.filter(|s| !s.is_empty()).unwrap_or(self.schema);
        let fields_xml = self.serialize_xml_fields(1);
        format!("<{root}>\r\n{fields_xml}</{root}>")
    }

    pub fn serialize_xml_fields(&self, depth: usize) -> String {
        let indent = "\t".repeat(depth);
        let fields = self.fields.borrow();
        let mut out = String::new();
        for (name, field) in fields.iter() {
            match field {
                Field::Numeric(d) => {
                    out.push_str(&format!("{indent}<{name}>{d}</{name}>\r\n"));
                }
                Field::Text(t) => {
                    if t.as_str().is_empty() {
                        out.push_str(&format!("{indent}<{name} />\r\n"));
                    } else {
                        out.push_str(&format!(
                            "{indent}<{name}>{}</{name}>\r\n",
                            escape_xml(t.as_str())
                        ));
                    }
                }
                Field::Boolean(b) => {
                    out.push_str(&format!(
                        "{indent}<{name}>{}</{name}>\r\n",
                        if *b { "true" } else { "false" }
                    ));
                }
                Field::Date(d) => {
                    if d.is_empty() {
                        out.push_str(&format!("{indent}<{name}>0001-01-01T00:00:00</{name}>\r\n"));
                    } else {
                        out.push_str(&format!(
                            "{indent}<{name}>{:04}-{:02}-{:02}T00:00:00</{name}>\r\n",
                            d.year(),
                            d.month(),
                            d.day()
                        ));
                    }
                }
                Field::DateTime(dt) => {
                    if dt.is_empty() {
                        out.push_str(&format!("{indent}<{name}>0001-01-01T00:00:00</{name}>\r\n"));
                    } else {
                        out.push_str(&format!(
                            "{indent}<{name}>{:04}-{:02}-{:02}T{:02}:{:02}:{:02}</{name}>\r\n",
                            dt.year(),
                            dt.month(),
                            dt.day(),
                            dt.hour(),
                            dt.minute(),
                            dt.second()
                        ));
                    }
                }
                Field::Record(r) => {
                    out.push_str(&format!("{indent}<{name}>\r\n"));
                    out.push_str(&r.serialize_xml_fields(depth + 1));
                    out.push_str(&format!("{indent}</{name}>\r\n"));
                }
                Field::Collection(c) => {
                    if c.is_empty() {
                        out.push_str(&format!("{indent}<{name} />\r\n"));
                    } else {
                        out.push_str(&format!("{indent}<{name}>\r\n"));
                        out.push_str(&c.serialize_xml_items(depth + 1));
                        out.push_str(&format!("{indent}</{name}>\r\n"));
                    }
                }
            }
        }
        out
    }

    /// Deserializes XML string into this record matching GeneXus GxUserType.FromXml().
    pub fn from_xml(&self, xml_str: &str, root_name: Option<&str>) -> bool {
        let Some(root) = parse_xml(xml_str) else {
            return false;
        };
        if let Some(expected_root) = root_name.filter(|s| !s.is_empty())
            && !root.tag.eq_ignore_ascii_case(expected_root)
        {
            return false;
        }
        let empty = self.empty_clone();
        *self.fields.borrow_mut() = empty.fields.borrow().clone();
        self.apply_xml_element(&root);
        true
    }

    fn apply_xml_element(&self, elem: &SimpleXmlElement) {
        for child in &elem.children {
            let mut fields = self.fields.borrow_mut();
            let matching_key = fields
                .keys()
                .copied()
                .find(|k| k.eq_ignore_ascii_case(&child.tag));
            let Some(key) = matching_key else {
                continue;
            };
            let Some(field) = fields.get_mut(key) else {
                continue;
            };
            match field {
                Field::Numeric(d) => {
                    let text = child.text.trim();
                    if let Ok(parsed) = text.parse::<Decimal>() {
                        *d = parsed;
                    }
                }
                Field::Text(t) => {
                    if let Ok(new_t) = Text::new(&child.text) {
                        *t = new_t;
                    }
                }
                Field::Boolean(b) => {
                    let text = child.text.trim();
                    *b = text.eq_ignore_ascii_case("true") || text == "1";
                }
                Field::Date(d) => {
                    let text = child.text.trim();
                    if !text.is_empty()
                        && !text.starts_with("0001-01-01")
                        && !text.starts_with("0000-00-00")
                        && text.len() >= 10
                        && let (Ok(y), Ok(m), Ok(day)) = (
                            text[0..4].parse::<i64>(),
                            text[5..7].parse::<i64>(),
                            text[8..10].parse::<i64>(),
                        )
                        && let Ok(parsed_date) = Date::ymd(y, m, day)
                    {
                        *d = parsed_date;
                    }
                }
                Field::DateTime(dt) => {
                    let text = child.text.trim();
                    if !text.is_empty()
                        && !text.starts_with("0001-01-01")
                        && !text.starts_with("0000-00-00")
                        && text.len() >= 10
                    {
                        let y = text[0..4].parse::<i64>().unwrap_or(0);
                        let m = text[5..7].parse::<i64>().unwrap_or(0);
                        let day = text[8..10].parse::<i64>().unwrap_or(0);
                        let mut hour = 0;
                        let mut min = 0;
                        let mut sec = 0;
                        if text.len() >= 19 && text.as_bytes()[10] == b'T' {
                            hour = text[11..13].parse::<i64>().unwrap_or(0);
                            min = text[14..16].parse::<i64>().unwrap_or(0);
                            sec = text[17..19].parse::<i64>().unwrap_or(0);
                        }
                        if let Ok(parsed_dt) =
                            DateTime::from_parts([y, m, day, hour, min, sec], None)
                        {
                            *dt = parsed_dt;
                        }
                    }
                }
                Field::Record(r) => {
                    r.apply_xml_element(child);
                }
                Field::Collection(c) => {
                    c.apply_xml_element(child);
                }
            }
        }
    }

    fn apply_json_object(&self, entries: &[(String, SimpleJson)]) -> bool {
        let mut fields = self.fields.borrow_mut();
        for (key, val) in entries {
            if matches!(val, SimpleJson::Null) {
                continue;
            }
            let found_key = fields.keys().find(|k| k.eq_ignore_ascii_case(key)).copied();
            let Some(f_name) = found_key else {
                continue;
            };
            let field = fields.get_mut(f_name).unwrap();
            match (field, val) {
                (Field::Numeric(target), SimpleJson::Number(num_str)) => {
                    if let Ok(d) = num_str.parse::<Decimal>() {
                        *target = d;
                    }
                }
                (Field::Numeric(target), SimpleJson::String(num_str)) => {
                    if let Ok(d) = num_str.parse::<Decimal>() {
                        *target = d;
                    }
                }
                (Field::Text(target), SimpleJson::String(s)) => {
                    if let Ok(t) = Text::new(s) {
                        *target = t;
                    }
                }
                (Field::Text(target), SimpleJson::Number(s)) => {
                    if let Ok(t) = Text::new(s) {
                        *target = t;
                    }
                }
                (Field::Text(target), SimpleJson::Bool(b)) => {
                    let s = if *b { "true" } else { "false" };
                    if let Ok(t) = Text::new(s) {
                        *target = t;
                    }
                }
                (Field::Boolean(target), SimpleJson::Bool(b)) => {
                    *target = *b;
                }
                (Field::Boolean(target), SimpleJson::String(s)) => {
                    *target = s.eq_ignore_ascii_case("true") || s == "1";
                }
                (Field::Date(target), SimpleJson::String(s)) => {
                    if s == "0000-00-00" || s.is_empty() {
                        *target = Date::empty();
                    } else if let Ok(d) = parse_iso_date(s) {
                        *target = d;
                    }
                }
                (Field::DateTime(target), SimpleJson::String(s)) => {
                    if s.starts_with("0000-00-00") || s.is_empty() {
                        *target = DateTime::empty();
                    } else if let Ok(dt) = parse_iso_datetime(s) {
                        *target = dt;
                    }
                }
                (Field::Record(target), SimpleJson::Object(sub_entries)) => {
                    target.apply_json_object(sub_entries);
                }
                (Field::Collection(target), SimpleJson::Array(items)) => {
                    target.apply_json_array(items);
                }
                _ => {}
            }
        }
        true
    }
}

/// An assignable collection reference. Cloning a `Collection` aliases its list.
#[derive(Clone)]
pub struct Collection {
    item_schema: &'static str,
    prototype: Rc<RefCell<Option<Record>>>,
    items: Rc<RefCell<Vec<Record>>>,
}

impl Collection {
    pub fn new(item_schema: &'static str) -> Self {
        Self {
            item_schema,
            prototype: Rc::new(RefCell::new(None)),
            items: Rc::new(RefCell::new(Vec::new())),
        }
    }

    pub fn with_prototype(item_schema: &'static str, prototype: Record) -> Self {
        Self {
            item_schema,
            prototype: Rc::new(RefCell::new(Some(prototype.empty_clone()))),
            items: Rc::new(RefCell::new(Vec::new())),
        }
    }

    pub fn empty_clone(&self) -> Self {
        Self {
            item_schema: self.item_schema,
            prototype: Rc::new(RefCell::new(
                self.prototype.borrow().as_ref().map(|p| p.empty_clone()),
            )),
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
        if self.prototype.borrow().is_none() {
            *self.prototype.borrow_mut() = Some(record.empty_clone());
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
            prototype: self.prototype.clone(),
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
        if self.prototype.borrow().is_none() {
            *self.prototype.borrow_mut() = Some(record.empty_clone());
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

    /// Serializes this collection to compact JSON array string matching GeneXus GXBaseCollection.ToJSonString().
    pub fn to_json(&self) -> String {
        let items = self.items.borrow();
        let mut out = String::from("[");
        for (i, item) in items.iter().enumerate() {
            if i > 0 {
                out.push(',');
            }
            out.push_str(&item.to_json());
        }
        out.push(']');
        out
    }

    pub fn to_json_string(&self) -> String {
        self.to_json()
    }

    /// Deserializes JSON array into this collection matching GeneXus GXBaseCollection.FromJSonString().
    pub fn from_json(&self, json_str: &str) -> bool {
        let Some(json) = parse_json(json_str) else {
            return false;
        };
        match json {
            SimpleJson::Array(items) => self.apply_json_array(&items),
            _ => false,
        }
    }

    pub fn from_json_string(&self, json_str: &str) -> bool {
        self.from_json(json_str)
    }

    /// Serializes this collection to formatted XML matching GeneXus GXBaseCollection.ToXml().
    pub fn to_xml(&self, root_name: Option<&str>) -> String {
        let root = root_name
            .filter(|s| !s.is_empty())
            .unwrap_or(self.item_schema);
        if self.is_empty() {
            format!("<{root} />")
        } else {
            let items_xml = self.serialize_xml_items(1);
            format!("<{root}>\r\n{items_xml}</{root}>")
        }
    }

    pub fn serialize_xml_items(&self, depth: usize) -> String {
        let indent = "\t".repeat(depth);
        let items = self.items.borrow();
        let mut out = String::new();
        for item in items.iter() {
            let item_tag = item.schema;
            out.push_str(&format!("{indent}<{item_tag}>\r\n"));
            out.push_str(&item.serialize_xml_fields(depth + 1));
            out.push_str(&format!("{indent}</{item_tag}>\r\n"));
        }
        out
    }

    /// Deserializes XML into this collection matching GeneXus GXBaseCollection.FromXml().
    pub fn from_xml(&self, xml_str: &str, root_name: Option<&str>) -> bool {
        let Some(root) = parse_xml(xml_str) else {
            return false;
        };
        if let Some(expected_root) = root_name.filter(|s| !s.is_empty())
            && !root.tag.eq_ignore_ascii_case(expected_root)
        {
            return false;
        }
        self.clear();
        self.apply_xml_element(&root);
        true
    }

    fn apply_xml_element(&self, elem: &SimpleXmlElement) {
        self.clear();
        let proto = self.prototype.borrow().clone();
        for child in &elem.children {
            if let Some(ref p) = proto {
                let rec = p.empty_clone();
                rec.apply_xml_element(child);
                self.items.borrow_mut().push(rec);
            }
        }
    }

    fn apply_json_array(&self, items: &[SimpleJson]) -> bool {
        self.clear();
        let proto = self.prototype.borrow().clone();
        for item in items {
            if let SimpleJson::Object(entries) = item
                && let Some(ref p) = proto
            {
                let rec = p.empty_clone();
                rec.apply_json_object(entries);
                self.items.borrow_mut().push(rec);
            }
        }
        true
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

fn escape_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{08}' => out.push_str("\\b"),
            '\u{0C}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 => {
                use std::fmt::Write;
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out
}

fn escape_xml(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            _ => out.push(c),
        }
    }
    out
}

fn unescape_xml(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '&' {
            let mut entity = String::new();
            while let Some(&next) = chars.peek() {
                chars.next();
                if next == ';' {
                    break;
                }
                entity.push(next);
            }
            match entity.as_str() {
                "lt" => out.push('<'),
                "gt" => out.push('>'),
                "amp" => out.push('&'),
                "quot" => out.push('"'),
                "apos" => out.push('\''),
                _ => {
                    out.push('&');
                    out.push_str(&entity);
                    out.push(';');
                }
            }
        } else {
            out.push(c);
        }
    }
    out
}

#[derive(Debug, PartialEq, Clone)]
pub struct SimpleXmlElement {
    pub tag: String,
    pub text: String,
    pub children: Vec<SimpleXmlElement>,
}

struct XmlParser<'a> {
    input: &'a str,
    cursor: usize,
}

impl<'a> XmlParser<'a> {
    fn new(input: &'a str) -> Self {
        Self { input, cursor: 0 }
    }

    fn skip_whitespace_and_misc(&mut self) {
        while self.cursor < self.input.len() {
            let remaining = &self.input[self.cursor..];
            if remaining.starts_with("<!--") {
                if let Some(pos) = remaining.find("-->") {
                    self.cursor += pos + 3;
                    continue;
                } else {
                    self.cursor = self.input.len();
                    return;
                }
            }
            if remaining.starts_with("<?") {
                if let Some(pos) = remaining.find("?>") {
                    self.cursor += pos + 2;
                    continue;
                } else {
                    self.cursor = self.input.len();
                    return;
                }
            }
            let c = remaining.chars().next().unwrap();
            if c.is_whitespace() {
                self.cursor += c.len_utf8();
            } else {
                break;
            }
        }
    }

    fn parse_element(&mut self) -> Option<SimpleXmlElement> {
        self.skip_whitespace_and_misc();
        if self.cursor >= self.input.len() {
            return None;
        }
        if !self.input[self.cursor..].starts_with('<') {
            return None;
        }
        self.cursor += 1; // skip '<'

        // Read tag name
        let start_tag = self.cursor;
        while self.cursor < self.input.len() {
            let b = self.input.as_bytes()[self.cursor];
            if b.is_ascii_whitespace() || b == b'/' || b == b'>' {
                break;
            }
            self.cursor += 1;
        }
        if self.cursor == start_tag {
            return None;
        }
        let tag = self.input[start_tag..self.cursor].to_string();

        // Skip attributes until '>' or '/>'
        let mut self_closing = false;
        while self.cursor < self.input.len() {
            let b = self.input.as_bytes()[self.cursor];
            if b == b'>' {
                self.cursor += 1;
                break;
            }
            if self.input[self.cursor..].starts_with("/>") {
                self.cursor += 2;
                self_closing = true;
                break;
            }
            if b == b'"' || b == b'\'' {
                let quote = b;
                self.cursor += 1;
                while self.cursor < self.input.len() && self.input.as_bytes()[self.cursor] != quote
                {
                    self.cursor += 1;
                }
                if self.cursor < self.input.len() {
                    self.cursor += 1;
                }
                continue;
            }
            self.cursor += 1;
        }

        if self_closing {
            return Some(SimpleXmlElement {
                tag,
                text: String::new(),
                children: Vec::new(),
            });
        }

        let mut children = Vec::new();
        let mut text = String::new();

        loop {
            self.skip_whitespace_and_misc();
            if self.cursor >= self.input.len() {
                return None; // unclosed tag
            }
            if self.input[self.cursor..].starts_with("</") {
                self.cursor += 2;
                let close_start = self.cursor;
                while self.cursor < self.input.len() {
                    let b = self.input.as_bytes()[self.cursor];
                    if b.is_ascii_whitespace() || b == b'>' {
                        break;
                    }
                    self.cursor += 1;
                }
                let close_tag = &self.input[close_start..self.cursor];
                while self.cursor < self.input.len() && self.input.as_bytes()[self.cursor] != b'>' {
                    self.cursor += 1;
                }
                if self.cursor < self.input.len() {
                    self.cursor += 1; // skip '>'
                }
                if !close_tag.eq_ignore_ascii_case(&tag) {
                    return None; // tag mismatch
                }
                break;
            } else if self.input[self.cursor..].starts_with('<') {
                let child = self.parse_element()?;
                children.push(child);
            } else {
                let text_start = self.cursor;
                while self.cursor < self.input.len() && self.input.as_bytes()[self.cursor] != b'<' {
                    self.cursor += 1;
                }
                let raw_text = &self.input[text_start..self.cursor];
                text.push_str(&unescape_xml(raw_text));
            }
        }

        Some(SimpleXmlElement {
            tag,
            text: text.trim().to_string(),
            children,
        })
    }
}

pub fn parse_xml(input: &str) -> Option<SimpleXmlElement> {
    let mut parser = XmlParser::new(input);
    let elem = parser.parse_element()?;
    parser.skip_whitespace_and_misc();
    Some(elem)
}

#[derive(Debug, PartialEq, Clone)]
enum SimpleJson {
    Object(Vec<(String, SimpleJson)>),
    Array(Vec<SimpleJson>),
    String(String),
    Number(String),
    Bool(bool),
    Null,
}

struct JsonParser<'a> {
    chars: std::str::Chars<'a>,
    peeked: Option<char>,
}

impl<'a> JsonParser<'a> {
    fn new(input: &'a str) -> Self {
        let mut p = Self {
            chars: input.chars(),
            peeked: None,
        };
        p.bump();
        p
    }

    fn bump(&mut self) -> Option<char> {
        let prev = self.peeked;
        self.peeked = self.chars.next();
        prev
    }

    fn skip_whitespace(&mut self) {
        while let Some(c) = self.peeked {
            if c.is_whitespace() {
                self.bump();
            } else {
                break;
            }
        }
    }

    fn parse_value(&mut self) -> Option<SimpleJson> {
        self.skip_whitespace();
        match self.peeked? {
            '{' => self.parse_object(),
            '[' => self.parse_array(),
            '"' => self.parse_string().map(SimpleJson::String),
            't' | 'f' => self.parse_bool().map(SimpleJson::Bool),
            'n' => self.parse_null(),
            '-' | '0'..='9' => self.parse_number().map(SimpleJson::Number),
            _ => None,
        }
    }

    fn parse_object(&mut self) -> Option<SimpleJson> {
        self.bump();
        self.skip_whitespace();
        let mut members = Vec::new();
        if self.peeked == Some('}') {
            self.bump();
            return Some(SimpleJson::Object(members));
        }
        loop {
            self.skip_whitespace();
            let key = self.parse_string()?;
            self.skip_whitespace();
            if self.bump()? != ':' {
                return None;
            }
            let val = self.parse_value()?;
            members.push((key, val));
            self.skip_whitespace();
            match self.peeked? {
                ',' => {
                    self.bump();
                }
                '}' => {
                    self.bump();
                    return Some(SimpleJson::Object(members));
                }
                _ => return None,
            }
        }
    }

    fn parse_array(&mut self) -> Option<SimpleJson> {
        self.bump();
        self.skip_whitespace();
        let mut items = Vec::new();
        if self.peeked == Some(']') {
            self.bump();
            return Some(SimpleJson::Array(items));
        }
        loop {
            let val = self.parse_value()?;
            items.push(val);
            self.skip_whitespace();
            match self.peeked? {
                ',' => {
                    self.bump();
                }
                ']' => {
                    self.bump();
                    return Some(SimpleJson::Array(items));
                }
                _ => return None,
            }
        }
    }

    fn parse_string(&mut self) -> Option<String> {
        if self.bump()? != '"' {
            return None;
        }
        let mut s = String::new();
        loop {
            let c = self.bump()?;
            match c {
                '"' => return Some(s),
                '\\' => {
                    let esc = self.bump()?;
                    match esc {
                        '"' => s.push('"'),
                        '\\' => s.push('\\'),
                        '/' => s.push('/'),
                        'b' => s.push('\u{08}'),
                        'f' => s.push('\u{0C}'),
                        'n' => s.push('\n'),
                        'r' => s.push('\r'),
                        't' => s.push('\t'),
                        'u' => {
                            let mut hex = String::new();
                            for _ in 0..4 {
                                hex.push(self.bump()?);
                            }
                            let codepoint = u32::from_str_radix(&hex, 16).ok()?;
                            s.push(char::from_u32(codepoint)?);
                        }
                        _ => return None,
                    }
                }
                other => s.push(other),
            }
        }
    }

    fn parse_number(&mut self) -> Option<String> {
        let mut num = String::new();
        if self.peeked == Some('-') {
            num.push(self.bump()?);
        }
        while let Some(c) = self.peeked {
            if c.is_ascii_digit() || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-' {
                num.push(self.bump()?);
            } else {
                break;
            }
        }
        if num.is_empty() { None } else { Some(num) }
    }

    fn parse_bool(&mut self) -> Option<bool> {
        if self.peeked == Some('t') {
            for expected in "true".chars() {
                if self.bump()? != expected {
                    return None;
                }
            }
            Some(true)
        } else if self.peeked == Some('f') {
            for expected in "false".chars() {
                if self.bump()? != expected {
                    return None;
                }
            }
            Some(false)
        } else {
            None
        }
    }

    fn parse_null(&mut self) -> Option<SimpleJson> {
        for expected in "null".chars() {
            if self.bump()? != expected {
                return None;
            }
        }
        Some(SimpleJson::Null)
    }
}

fn parse_json(input: &str) -> Option<SimpleJson> {
    let mut parser = JsonParser::new(input);
    let val = parser.parse_value()?;
    parser.skip_whitespace();
    if parser.peeked.is_some() {
        return None;
    }
    Some(val)
}

fn parse_iso_date(s: &str) -> Result<Date, String> {
    let parts: Vec<&str> = s.split('-').collect();
    if parts.len() != 3 {
        return Err("invalid date format".into());
    }
    let y = parts[0].parse::<i64>().map_err(|e| e.to_string())?;
    let m = parts[1].parse::<i64>().map_err(|e| e.to_string())?;
    let d = parts[2].parse::<i64>().map_err(|e| e.to_string())?;
    Date::ymd(y, m, d)
}

fn parse_iso_datetime(s: &str) -> Result<DateTime, String> {
    let date_time: Vec<&str> = s.split('T').collect();
    if date_time.is_empty() {
        return Err("invalid datetime format".into());
    }
    let d = parse_iso_date(date_time[0])?;
    if date_time.len() == 1 {
        return DateTime::from_parts([d.year(), d.month(), d.day(), 0, 0, 0], None);
    }
    let time_parts: Vec<&str> = date_time[1].split(':').collect();
    if time_parts.len() < 2 {
        return Err("invalid time format".into());
    }
    let hour = time_parts[0].parse::<i64>().map_err(|e| e.to_string())?;
    let min = time_parts[1].parse::<i64>().map_err(|e| e.to_string())?;
    let sec = if time_parts.len() >= 3 {
        let sec_str = time_parts[2].split('.').next().unwrap_or("0");
        sec_str.parse::<i64>().unwrap_or(0)
    } else {
        0
    };
    DateTime::from_parts([d.year(), d.month(), d.day(), hour, min, sec], None)
}
