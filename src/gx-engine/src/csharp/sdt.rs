//! GxUserType C# class generator for Structured Data Types (SDTs).

use crate::{
    catalog::{KbObject, ObjectKind},
    csharp::types::{clean_ident, to_csharp_type},
    model::DataType,
};
use std::collections::BTreeMap;
use std::fmt::Write;

/// Represents an emittable GeneXus C# SDT class (`GxUserType`).
#[derive(Debug, Clone)]
pub struct SdtClass {
    pub name: String,
    pub namespace: String,
    pub fields: Vec<(String, DataType)>,
}

impl SdtClass {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            namespace: "GeneXus.Programs".to_string(),
            fields: Vec::new(),
        }
    }

    pub fn with_namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = namespace.into();
        self
    }

    pub fn add_field(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.fields.push((name.into(), data_type));
        self
    }

    pub fn from_kb_object(object: &KbObject) -> Result<Self, String> {
        if object.kind != ObjectKind::StructuredDataType {
            return Err(format!(
                "expected StructuredDataType, found {:?}",
                object.kind
            ));
        }

        let mut sdt = Self::new(&object.name);

        for part in &object.source_parts {
            if part.name.eq_ignore_ascii_case("Fields")
                || part.name.eq_ignore_ascii_case("STRUCTURE")
            {
                if let Ok(fields) = serde_json::from_str::<BTreeMap<String, DataType>>(&part.source)
                {
                    for (k, v) in fields {
                        sdt.fields.push((k, v));
                    }
                } else if let Ok(val) = serde_json::from_str::<serde_json::Value>(&part.source) {
                    if let Some(map) = val.as_object() {
                        for (k, v) in map {
                            let dt = match v.get("kind").and_then(|s| s.as_str()) {
                                Some("numeric") => {
                                    let p =
                                        v.get("precision").and_then(|n| n.as_u64()).unwrap_or(10)
                                            as u8;
                                    let s =
                                        v.get("scale").and_then(|n| n.as_u64()).unwrap_or(0) as u8;
                                    DataType::Numeric {
                                        precision: p,
                                        scale: s,
                                    }
                                }
                                Some("boolean") => DataType::Boolean {},
                                Some("date") => DataType::Date {},
                                Some("datetime") => DataType::DateTime {},
                                _ => DataType::Character { length: 50 },
                            };
                            sdt.fields.push((k.clone(), dt));
                        }
                    }
                }
            }
        }

        Ok(sdt)
    }

    pub fn emit(&self) -> String {
        let mut out = String::new();
        let _ = writeln!(out, "using System;");
        let _ = writeln!(out, "using System.Collections;");
        let _ = writeln!(out, "using GeneXus.Utils;");
        let _ = writeln!(out, "using GeneXus.Application;");
        let _ = writeln!(out, "using System.Xml.Serialization;");
        let _ = writeln!(out, "using System.Runtime.Serialization;");
        let _ = writeln!(out);
        let _ = writeln!(out, "namespace {} {{", self.namespace);
        let _ = writeln!(out, "   [Serializable]");
        let _ = writeln!(out, "   public class Sdt{} : GxUserType", self.name);
        let _ = writeln!(out, "   {{");
        let _ = writeln!(out, "      public Sdt{}( ) {{ }}", self.name);
        let _ = writeln!(out);
        let _ = writeln!(out, "      public Sdt{}( IGxContext context )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         this.context = context;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        for (field_name, dt) in &self.fields {
            let cs_type = to_csharp_type(dt);
            let clean = clean_ident(field_name);
            let _ = writeln!(out, "      public {cs_type} gxTpr_{clean} {{ get; set; }}");
        }

        let _ = writeln!(out, "   }}");
        let _ = writeln!(out, "}}");
        out
    }
}
