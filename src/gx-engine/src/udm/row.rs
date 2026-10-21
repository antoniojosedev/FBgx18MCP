//! Typed values and row abstraction for GeneXus UDM storage.

#![forbid(unsafe_code)]

use std::fmt;

/// A typed value in a UDM database row.
#[derive(Debug, Clone, PartialEq)]
pub enum UdmValue {
    Null,
    Int(i64),
    String(String),
    Binary(Vec<u8>),
    Bool(bool),
}

impl UdmValue {
    pub fn as_str(&self) -> Option<&str> {
        match self {
            Self::String(s) => Some(s),
            _ => None,
        }
    }

    pub fn as_i64(&self) -> Option<i64> {
        match self {
            Self::Int(n) => Some(*n),
            _ => None,
        }
    }

    pub fn as_i32(&self) -> Option<i32> {
        self.as_i64().and_then(|n| i32::try_from(n).ok())
    }

    pub fn as_bytes(&self) -> Option<&[u8]> {
        match self {
            Self::Binary(b) => Some(b.as_slice()),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Self::Bool(b) => Some(*b),
            Self::Int(n) => Some(*n != 0),
            _ => None,
        }
    }

    pub fn is_null(&self) -> bool {
        matches!(self, Self::Null)
    }
}

impl fmt::Display for UdmValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Null => write!(f, "NULL"),
            Self::Int(n) => write!(f, "{n}"),
            Self::String(s) => write!(f, "{s}"),
            Self::Binary(b) => write!(f, "<binary {} bytes>", b.len()),
            Self::Bool(b) => write!(f, "{b}"),
        }
    }
}

/// A row returned from a UDM query, providing column-name and index-based access.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct UdmRow {
    columns: Vec<String>,
    values: Vec<UdmValue>,
}

impl UdmRow {
    pub fn new(columns: Vec<String>, values: Vec<UdmValue>) -> Self {
        Self { columns, values }
    }

    pub fn get(&self, col_name: &str) -> Option<&UdmValue> {
        self.columns
            .iter()
            .position(|c| c.eq_ignore_ascii_case(col_name))
            .and_then(|idx| self.values.get(idx))
    }

    pub fn get_by_index(&self, index: usize) -> Option<&UdmValue> {
        self.values.get(index)
    }

    pub fn get_str(&self, col_name: &str) -> Option<&str> {
        self.get(col_name).and_then(UdmValue::as_str)
    }

    pub fn get_i32(&self, col_name: &str) -> Option<i32> {
        self.get(col_name).and_then(UdmValue::as_i32)
    }

    pub fn get_i64(&self, col_name: &str) -> Option<i64> {
        self.get(col_name).and_then(UdmValue::as_i64)
    }

    pub fn get_bytes(&self, col_name: &str) -> Option<&[u8]> {
        self.get(col_name).and_then(UdmValue::as_bytes)
    }

    pub fn get_bool(&self, col_name: &str) -> Option<bool> {
        self.get(col_name).and_then(UdmValue::as_bool)
    }

    pub fn columns(&self) -> &[String] {
        &self.columns
    }

    pub fn values(&self) -> &[UdmValue] {
        &self.values
    }

    pub fn len(&self) -> usize {
        self.values.len()
    }

    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}
