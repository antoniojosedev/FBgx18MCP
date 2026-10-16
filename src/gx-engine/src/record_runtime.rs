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
