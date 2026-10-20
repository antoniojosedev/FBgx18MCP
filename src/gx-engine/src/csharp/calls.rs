//! Procedure call sites: the parameter modes a caller writes and the temporaries the SDK uses.
//!
//! Every rule here is read from the C# GeneXus generates for KBTeste (`NetModel/Web/a*.cs`) and from
//! the callee signatures (`execute( decimal aP0_A, out decimal aP1_B, ref decimal aP2_C )`). A call
//! shape the SDK output does not show is refused by name by the emitter, not written from a guess.

use crate::{
    csharp::types::to_csharp_type,
    frontend::{Expr, is_typed_builtin},
    model::{DataType, ParamMode},
};
use std::collections::BTreeMap;

/// One parameter of a Procedure as its callers see it.
#[derive(Debug, Clone, PartialEq)]
pub struct CallParam {
    pub mode: ParamMode,
    pub data_type: DataType,
}

/// A Procedure a call site can name: the class it declares (the name a call must use, since C# is
/// case-sensitive) and its parameters as its callers see them.
#[derive(Debug, Clone, PartialEq)]
pub struct CallTarget {
    pub class_name: String,
    /// The namespace the class is declared in (`GeneXus.Programs` under the root module).
    pub namespace: String,
    pub params: Vec<CallParam>,
}

impl CallTarget {
    /// The class as a call site names it: bare under the root module, else namespace-qualified, as the
    /// SDK writes a call to a Procedure held by a module (`new GeneXus.Programs.general.ui.x(context )`).
    pub fn qualified_class(&self) -> String {
        if self.namespace.is_empty() || self.namespace == crate::csharp::types::ROOT_NAMESPACE {
            self.class_name.clone()
        } else {
            format!("{}.{}", self.namespace, self.class_name)
        }
    }
}

/// The Procedures of this build, keyed by the upper-cased class name.
#[derive(Debug, Default, Clone)]
pub struct CallSignatures {
    by_name: BTreeMap<String, CallTarget>,
}

impl CallSignatures {
    /// Creates an empty table.
    pub fn new() -> Self {
        Self::default()
    }

    /// Records a Procedure of this build. A second one with the same name (in any case) is refused: a
    /// call could then bind to either one.
    pub fn insert(&mut self, target: CallTarget) -> Result<(), String> {
        let key = target.class_name.to_ascii_uppercase();
        if self.by_name.contains_key(&key) {
            return Err(format!(
                "two Procedures share the name '{}'; a call could bind to either",
                target.class_name
            ));
        }
        self.by_name.insert(key, target);
        Ok(())
    }

    /// The Procedure `name`, in any case, or `None` when this build does not emit it.
    pub fn get(&self, name: &str) -> Option<&CallTarget> {
        self.by_name.get(&name.to_ascii_uppercase())
    }
}

/// The temporary kind (the SDK's name for it, `GXt_<kind><n>`) for a value of `data_type`, when the SDK
/// output shows its form: `decimal` (`GXt_decimal1`), `int` (`GXt_int1`) and `boolean` (`GXt_boolean3`,
/// arusttypedcallprobe.cs line 76). Every other type is not evidenced.
pub fn temp_type(data_type: &DataType) -> Option<&'static str> {
    match to_csharp_type(data_type).as_str() {
        "decimal" => Some("decimal"),
        "int" => Some("int"),
        "bool" => Some("boolean"),
        _ => None,
    }
}

/// The C# type a temporary of `kind` is declared with.
pub fn temp_cs_type(kind: &str) -> &'static str {
    match kind {
        "boolean" => "bool",
        "int" => "int",
        _ => "decimal",
    }
}

/// Names the emitter maps itself, or that the parser reads as built-ins. A call to any of them is not
/// a Procedure call, whatever its arguments.
pub fn is_builtin_name(name: &str) -> bool {
    let upper = name.to_ascii_uppercase();
    is_typed_builtin(&upper)
        || matches!(
            upper.as_str(),
            "IIF" | "ISEMPTY" | "NOW" | "NULLVALUE" | "TODAY" | "TOJSON" | "TOXML"
        )
}

/// Every Procedure call inside `expr`, in source order: the name and the argument expressions.
pub fn procedure_calls(expr: &Expr) -> Vec<(&str, &[Expr])> {
    let mut found = Vec::new();
    collect(expr, &mut found);
    found
}

fn collect<'e>(expr: &'e Expr, found: &mut Vec<(&'e str, &'e [Expr])>) {
    match expr {
        Expr::Paren(inner) => collect(inner, found),
        Expr::Call(name, args) => {
            if !is_builtin_name(name) {
                found.push((name.as_str(), args.as_slice()));
            }
            for arg in args {
                collect(arg, found);
            }
        }
        Expr::Integer(_)
        | Expr::Decimal(_)
        | Expr::Text(_)
        | Expr::Boolean(_)
        | Expr::EmptyDate
        | Expr::Variable(_)
        | Expr::Attribute(_) => {}
        Expr::TextLength(inner)
        | Expr::Member(inner, _)
        | Expr::CloneRecord(inner)
        | Expr::DateMethod(_, inner)
        | Expr::Not(inner)
        | Expr::Negate(inner) => collect(inner, found),
        Expr::CollectionItem(a, b) | Expr::Binary(a, _, b) | Expr::DateShift(_, a, b) => {
            collect(a, found);
            collect(b, found);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builtins_are_not_procedure_calls_including_the_emitter_only_ones() {
        for name in ["Trim", "ISEMPTY", "NOW", "TODAY", "NULLVALUE", "TOJSON"] {
            assert!(is_builtin_name(name), "{name} is a built-in");
        }
        assert!(!is_builtin_name("POB97435900"));
    }

    #[test]
    fn a_numeric_temporary_is_decimal_or_int_and_nothing_else() {
        assert_eq!(
            temp_type(&DataType::Numeric {
                precision: 5,
                scale: 2
            }),
            Some("decimal")
        );
        assert_eq!(
            temp_type(&DataType::Numeric {
                precision: 5,
                scale: 0
            }),
            Some("int")
        );
        // Not evidenced by the SDK output: short, long, string. A Boolean temporary is `bool GXt_boolean3`
        // in arusttypedcallprobe.cs.
        assert_eq!(
            temp_type(&DataType::Numeric {
                precision: 4,
                scale: 0
            }),
            None
        );
        assert_eq!(temp_type(&DataType::Boolean {}), Some("boolean"));
        assert_eq!(temp_type(&DataType::Character { length: 3 }), None);
    }

    #[test]
    fn the_same_procedure_name_in_another_case_is_refused() {
        let mut table = CallSignatures::new();
        let target = |class: &str| CallTarget {
            class_name: class.to_string(),
            namespace: String::new(),
            params: Vec::new(),
        };
        table.insert(target("Pob")).unwrap();
        assert!(table.insert(target("POB")).is_err());
        assert!(table.get("pob").is_some());
    }

    #[test]
    fn a_call_gets_the_class_name_the_callee_declares_not_the_name_as_written() {
        // The parser upper-cases a call's name; the callee's class keeps the case it is declared with.
        let mut table = CallSignatures::new();
        table
            .insert(CallTarget {
                class_name: "PCalculaDesconto".to_string(),
                namespace: String::new(),
                params: Vec::new(),
            })
            .unwrap();
        assert_eq!(
            table.get("PCALCULADESCONTO").unwrap().class_name,
            "PCalculaDesconto"
        );
    }
}
