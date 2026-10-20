//! C# syntax emitter for expressions and statements.

use crate::{
    csharp::{
        calls::{
            CallParam, CallSignatures, CallTarget, is_builtin_name, procedure_calls, temp_cs_type,
            temp_type,
        },
        sdt::{SdtIndex, SdtMemberType, sdt_item, sdt_member, sdt_type},
        types::{to_csharp_attr_name, to_csharp_type, to_csharp_var_name},
    },
    frontend::{BinOp, Expr, Stmt},
    model::{DataType, ParamMode},
};
use std::cell::RefCell;
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write;

mod sdk_expr;

/// The call-site temporaries of one procedure, numbered as the SDK numbers them. A counter shared by every
/// type gives each new temporary its number. A temporary a statement has finished with goes back to a stack
/// for its type, and the next call of that type takes the most recently released one. Evidence:
/// arusttypedcallprobe.cs lines 70-97 (`GXt_boolean3` follows `GXt_decimal1` and `GXt_decimal2`, and the
/// third call of a statement takes `GXt_decimal2` and then `GXt_decimal1` before the new `GXt_decimal5`),
/// and apoa97435900.cs lines 71-75 (the outer call's temporary is planned before the inner call's).
#[derive(Default)]
struct TempPool {
    counter: u32,
    free: BTreeMap<&'static str, Vec<u32>>,
    declared: BTreeSet<(&'static str, u32)>,
}

impl TempPool {
    /// Takes a temporary of `kind`: the most recently released one, or a new number.
    fn alloc(&mut self, kind: &'static str) -> (String, (&'static str, u32)) {
        let number = match self.free.get_mut(kind).and_then(|stack| stack.pop()) {
            Some(number) => number,
            None => {
                self.counter += 1;
                self.counter
            }
        };
        self.declared.insert((kind, number));
        (format!("GXt_{kind}{number}"), (kind, number))
    }

    /// Returns the temporaries of a finished statement, in the order they were taken.
    fn release(&mut self, allocated: &[(&'static str, u32)]) {
        for (kind, number) in allocated {
            self.free.entry(kind).or_default().push(*number);
        }
    }

    /// Every temporary this procedure uses, as (C# type, name), in a stable order.
    fn declarations(&self) -> Vec<(&'static str, String)> {
        self.declared
            .iter()
            .map(|(kind, number)| (temp_cs_type(kind), format!("GXt_{kind}{number}")))
            .collect()
    }
}

/// The Procedure calls of `expr` that are not inside another Procedure call's arguments, in source order.
/// A nested call is planned by the call that encloses it.
fn top_level_calls(expr: &Expr) -> Vec<(&str, &[Expr])> {
    let mut found = Vec::new();
    collect_top_level(expr, &mut found);
    found
}

fn collect_top_level<'e>(expr: &'e Expr, found: &mut Vec<(&'e str, &'e [Expr])>) {
    match expr {
        Expr::Paren(inner) => collect_top_level(inner, found),
        Expr::Call(name, args) => {
            if is_builtin_name(name) {
                for arg in args {
                    collect_top_level(arg, found);
                }
            } else {
                found.push((name.as_str(), args.as_slice()));
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
        | Expr::Negate(inner) => collect_top_level(inner, found),
        Expr::CollectionItem(a, b) | Expr::Binary(a, _, b) | Expr::DateShift(_, a, b) => {
            collect_top_level(a, found);
            collect_top_level(b, found);
        }
    }
}

/// The assignment whose Procedure calls are planned: its target variable, the C# type of the target, the
/// indentation of its lines and the Procedures of the build.
struct AssignPlan<'p> {
    cs_var: &'p str,
    target: &'p str,
    indent: &'p str,
    calls: &'p CallSignatures,
}

/// The C# family of a declared type. Two types of different families have no implicit conversion in C#
/// and no conversion this build evidences from the SDK, so an assignment across families is refused.
/// `Date` and `DateTime` are one family: both are `DateTime`.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ValueFamily {
    Numeric,
    Text,
    DateTime,
    Boolean,
}

fn family_of(data_type: &DataType) -> Option<ValueFamily> {
    match data_type {
        DataType::Numeric { .. } => Some(ValueFamily::Numeric),
        DataType::Character { .. } | DataType::Varchar { .. } | DataType::LongVarchar { .. } => {
            Some(ValueFamily::Text)
        }
        DataType::Date {} | DataType::DateTime {} => Some(ValueFamily::DateTime),
        DataType::Boolean {} => Some(ValueFamily::Boolean),
        DataType::Record { .. }
        | DataType::Collection { .. }
        | DataType::BaseCollection { .. }
        | DataType::Guid {}
        | DataType::Image {}
        | DataType::Geospatial {} => None,
    }
}

/// A declared type whose C# form is evidenced for a declaration and an SDT member, but whose
/// expression forms (assignment, comparison, helper calls) are not. A statement or expression that
/// reads or writes such a value is refused by name rather than emitted as an uncompilable C# line.
fn has_no_expression_form(data_type: &DataType) -> bool {
    matches!(
        data_type,
        DataType::Guid {} | DataType::Image {} | DataType::Geospatial {}
    )
}

/// The GeneXus kind of a declared type, lower case, for refusal messages.
fn kind_name(data_type: &DataType) -> &'static str {
    match data_type {
        DataType::Boolean {} => "boolean",
        DataType::Character { .. } => "character",
        DataType::Varchar { .. } => "varchar",
        DataType::Date {} => "date",
        DataType::DateTime {} => "datetime",
        DataType::Numeric { .. } => "numeric",
        DataType::LongVarchar { .. } => "longvarchar",
        DataType::Guid {} => "guid",
        DataType::Image {} => "image",
        DataType::Geospatial {} => "geospatial",
        DataType::Record { .. } => "record",
        DataType::Collection { .. } | DataType::BaseCollection { .. } => "collection",
    }
}

/// Whether a statement that writes a member, attribute, collection or JSON value holds a Procedure call.
/// Those statements have no evidenced SDK form for a call, so such a call is refused.
fn statement_has_procedure_call(stmt: &Stmt) -> bool {
    let has = |expr: &Expr| !procedure_calls(expr).is_empty();
    match stmt {
        Stmt::AssignField(target, _, value) => has(target) || has(value),
        Stmt::AssignAttribute(_, value) => has(value),
        Stmt::CollectionAdd(target, value, extra) => {
            has(target) || has(value) || extra.as_ref().is_some_and(has)
        }
        Stmt::CollectionRemove(target, value) | Stmt::FromJson(target, value) => {
            has(target) || has(value)
        }
        _ => false,
    }
}

/// Configurable C# code generator and indentation manager.
pub struct CSharpEmitter<'a> {
    indent_level: usize,
    indent_unit: String,
    vars: Option<&'a BTreeMap<String, DataType>>,
    /// The stored id of each variable, by name. With an id a variable is written as the SDK names its
    /// field (`AV8Hits`); without one, as `AV_Hits`.
    var_ids: Option<&'a BTreeMap<String, u32>>,
    /// The Procedure's subroutines in source order, for the SDK's `S111` names (listprograms.cs).
    subroutines: Vec<String>,
    /// Whether a subroutine body is being written: a `Return` there leaves through `returnInSub`.
    pub(crate) in_subroutine: std::cell::Cell<bool>,
    sdts: Option<SdtScope<'a>>,
    /// The Procedures this build emits, for call sites. `None` writes calls as they were (unit and
    /// WebPanel paths); a Procedure build always sets it.
    calls: Option<&'a CallSignatures>,
    /// Constructs this emitter could not write in the SDK's form, by name. A build refuses on any.
    refusals: RefCell<Vec<String>>,
    /// The temporary each Procedure call of the statement being emitted writes to, keyed by the address of
    /// the call's name (unique per call site). Set while the statement is planned, then cleared.
    call_temps: RefCell<BTreeMap<usize, String>>,
    /// The call-site temporaries of this class, numbered as the SDK numbers them (see `TempPool`).
    temps: RefCell<TempPool>,
    /// The class names (upper-cased) of the Procedures this class calls. A build refuses a caller whose
    /// callee it does not generate, so it needs to know these.
    callees: RefCell<BTreeSet<String>>,
}

/// The SDTs a Procedure resolves its member accesses against. Without a scope, member access is written
/// as it was (WebPanel code, which is not a compile unit).
struct SdtScope<'a> {
    index: &'a SdtIndex,
}

impl Default for CSharpEmitter<'_> {
    fn default() -> Self {
        Self::new()
    }
}

impl<'a> CSharpEmitter<'a> {
    /// Creates a new emitter using the classic GeneXus 3-space indentation.
    pub fn new() -> Self {
        Self::bare(None, None, "   ".to_string())
    }

    /// Creates an emitter with a variable context for case-insensitive identifier preservation.
    pub fn with_vars(vars: &'a BTreeMap<String, DataType>) -> Self {
        Self::bare(Some(vars), None, "   ".to_string())
    }

    /// Creates an emitter that resolves SDT member accesses of the variables in `vars` against `index`.
    /// An access that does not resolve is refused (see [`CSharpEmitter::refusals`]).
    pub fn with_sdts(vars: &'a BTreeMap<String, DataType>, index: &'a SdtIndex) -> Self {
        Self::bare(Some(vars), Some(SdtScope { index }), "   ".to_string())
    }

    /// Creates an emitter with custom indentation unit.
    pub fn with_indent_unit(unit: impl Into<String>) -> Self {
        Self::bare(None, None, unit.into())
    }

    fn bare(
        vars: Option<&'a BTreeMap<String, DataType>>,
        sdts: Option<SdtScope<'a>>,
        indent_unit: String,
    ) -> Self {
        Self {
            indent_level: 0,
            indent_unit,
            vars,
            var_ids: None,
            subroutines: Vec::new(),
            in_subroutine: std::cell::Cell::new(false),
            sdts,
            calls: None,
            refusals: RefCell::new(Vec::new()),
            call_temps: RefCell::new(BTreeMap::new()),
            temps: RefCell::new(TempPool::default()),
            callees: RefCell::new(BTreeSet::new()),
        }
    }

    /// Sets the stored id of each variable, so that variables are written with the SDK's field names.
    pub fn with_var_ids(mut self, ids: &'a BTreeMap<String, u32>) -> Self {
        self.var_ids = Some(ids);
        self
    }

    /// Sets the Procedure's subroutines, in source order.
    pub fn with_subroutines(mut self, names: Vec<String>) -> Self {
        self.subroutines = names;
        self
    }

    /// The SDK's method of a subroutine: `S111` for the first (listprograms.cs), `S1<n>1` after it.
    pub fn subroutine_method(&self, name: &str) -> String {
        let index = self
            .subroutines
            .iter()
            .position(|sub| sub.eq_ignore_ascii_case(name))
            .unwrap_or(self.subroutines.len());
        format!("S1{}1", index + 1)
    }

    /// The SDK's counter of the `n`th For-In loop (`AV9GXV1`), when its id is known.
    fn for_in_counter(&self, n: usize) -> Option<String> {
        let name = format!("GXV{n}");
        self.var_ids
            .and_then(|ids| ids.get(&name))
            .map(|id| format!("AV{id}{name}"))
    }

    /// The C# field of a variable: `AV<Id><Name>` when its stored id is known, else `AV_<Name>`.
    pub fn var_cs(&self, name: &str) -> String {
        sdk_var_name(self.var_ids, name)
    }

    /// Sets the Procedures this build emits, so that every call site is written in the SDK's form or
    /// refused by name.
    pub fn with_calls(mut self, calls: &'a CallSignatures) -> Self {
        self.calls = Some(calls);
        self
    }

    /// Everything this emitter refused, by name: unresolved member accesses and call shapes with no SDK
    /// form. Empty when everything was written in a form the SDK output shows.
    pub fn refusals(&self) -> Vec<String> {
        self.refusals.borrow().clone()
    }

    /// The call-site temporaries this class uses, as (C# type, name), in a stable order.
    pub fn temp_declarations(&self) -> Vec<(&'static str, String)> {
        self.temps.borrow().declarations()
    }

    /// The Procedures this class calls, by upper-cased class name.
    pub fn callees(&self) -> BTreeSet<String> {
        self.callees.borrow().clone()
    }

    /// The Procedure a call names, when this build generates it. A call to any other Procedure is refused
    /// by name, not written: its class would not exist, and csc would fail on the caller.
    fn callee<'c>(&self, calls: &'c CallSignatures, name: &str) -> Option<&'c CallTarget> {
        match calls.get(name) {
            Some(target) => {
                self.callees
                    .borrow_mut()
                    .insert(target.class_name.to_ascii_uppercase());
                Some(target)
            }
            None => {
                self.fail(format!(
                    "needs Procedure '{name}': this build does not generate it, so a call to it cannot be written"
                ));
                None
            }
        }
    }

    fn fail(&self, message: String) {
        let mut refusals = self.refusals.borrow_mut();
        if !refusals.contains(&message) {
            refusals.push(message);
        }
    }

    /// The declared type of a variable of this emitter, matched case-insensitively.
    fn declared(&self, name: &str) -> Option<&'a DataType> {
        let vars = self.vars?;
        let lookup = name.trim_start_matches('&');
        vars.iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(lookup) || k.eq_ignore_ascii_case(name))
            .map(|(_, dt)| dt)
    }

    /// What an SDT-valued expression is: a declared SDT variable, a member of one, or the item of a
    /// collection member. Anything else is an error naming what it is not.
    fn resolve(&self, expr: &Expr) -> Result<SdtMemberType<'a>, String> {
        let scope = self
            .sdts
            .as_ref()
            .ok_or_else(|| "no SDT scope is set".to_string())?;
        match expr {
            Expr::Variable(name) => match self.declared(name) {
                Some(dt) if matches!(dt, DataType::Record { .. }) => {
                    sdt_type(scope.index, &to_csharp_type(dt))
                }
                Some(DataType::BaseCollection { schema, .. }) => {
                    let item_type = to_csharp_type(&DataType::Record {
                        schema: schema.clone(),
                    });
                    let class = scope.index.class(&item_type).ok_or_else(|| {
                        format!("collection item SDT '{item_type}' is not emitted by this build")
                    })?;
                    Ok(SdtMemberType::Collection {
                        item: class.class_name(),
                        children: &class.nodes,
                    })
                }
                Some(_) => Err(format!("'{name}' is not an SDT variable")),
                None => Err(format!("'{name}' is not a declared variable")),
            },
            Expr::Member(target, field) => {
                let parent = self.resolve(target)?;
                sdt_member(&parent, field).map(|(_, member)| member)
            }
            Expr::CollectionItem(col, _) => {
                let parent = self.resolve(col)?;
                sdt_item(&parent)
            }
            _ => Err("the receiver is not an SDT variable or a member of one".to_string()),
        }
    }

    /// Records a refusal when a variable of a type with no expression form is read or written.
    fn refuse_if_no_expression_form(&self, name: &str, data_type: &DataType) {
        if has_no_expression_form(data_type) {
            self.fail(format!(
                "'{name}' is a {}: no expression form for this type is evidenced in this build",
                kind_name(data_type)
            ));
        }
    }

    /// `receiver.member` for a member access. With an SDT scope the member is the SDK's `gxTpr_` form
    /// of the stored member; an access that does not resolve is recorded and written as it was.
    fn member_target(&self, target: &Expr, field: &str) -> String {
        let receiver = self.emit_expr(target);
        if self.sdts.is_none() {
            return format!("{receiver}.{field}");
        }
        match self
            .resolve(target)
            .and_then(|parent| sdt_member(&parent, field))
        {
            Ok((member, SdtMemberType::Scalar(data_type))) => {
                self.refuse_if_no_expression_form(field, &data_type);
                format!("{receiver}.{member}")
            }
            Ok((member, _)) => format!("{receiver}.{member}"),
            Err(reason) => {
                self.fail(format!("member '{field}': {reason}"));
                format!("{receiver}.{field}")
            }
        }
    }

    /// The declared type of a variable, or of an SDT scalar member, when it resolves to one.
    fn value_type(&self, operand: &Expr) -> Option<DataType> {
        match operand {
            Expr::Paren(inner) => self.value_type(inner),
            Expr::Variable(name) => self.declared(name).cloned(),
            Expr::Member(..) => match self.resolve(operand) {
                Ok(SdtMemberType::Scalar(data_type)) => Some(data_type),
                _ => None,
            },
            // `Length()` is written as `StringUtil.Len`, and the SDK writes its ToString as
            // `StringUtil.Str( (decimal)(StringUtil.Len( AV8C4)), 10, 0)` (arusttextfuncsemantics.cs).
            Expr::TextLength(_) => Some(DataType::Numeric {
                precision: 10,
                scale: 0,
            }),
            // `Year()` as the SDK writes its ToString: `StringUtil.Trim( StringUtil.Str( (decimal)(DateTimeUtil.Year(
            // AV14D)), 10, 0))` (arustparmmainprobe.cs line 83).
            Expr::DateMethod(name, _) if name == "YEAR" => Some(DataType::Numeric {
                precision: 10,
                scale: 0,
            }),
            _ => None,
        }
    }

    /// Whether an expression is text by its shape: a text literal, a text-typed value, or a `+` with a text side.
    fn is_text_expr(&self, expr: &Expr) -> bool {
        match expr {
            Expr::Paren(inner) => self.is_text_expr(inner),
            Expr::Text(_) => true,
            Expr::Binary(left, BinOp::Add, right) => {
                self.is_text_expr(left) || self.is_text_expr(right)
            }
            Expr::Variable(_) | Expr::Member(..) => matches!(
                self.value_type(expr),
                Some(DataType::Character { .. } | DataType::Varchar { .. })
            ),
            _ => false,
        }
    }

    /// Whether an expression is numeric by its shape. A numeric value cannot be a `StatusMessage` argument.
    fn is_numeric_value(&self, expr: &Expr) -> bool {
        match expr {
            Expr::Paren(inner) => self.is_numeric_value(inner),
            Expr::Integer(_) | Expr::Decimal(_) | Expr::TextLength(_) => true,
            Expr::DateMethod(name, _) => matches!(
                name.to_ascii_uppercase().as_str(),
                "YEAR" | "MONTH" | "DAY" | "HOUR" | "MINUTE" | "SEC" | "SECOND" | "DOW"
            ),
            Expr::Variable(_) | Expr::Member(..) => {
                matches!(self.value_type(expr), Some(DataType::Numeric { .. }))
            }
            Expr::Negate(inner) => self.is_numeric_value(inner),
            Expr::Binary(left, op, right) => match op {
                BinOp::Subtract | BinOp::Multiply | BinOp::Divide => true,
                BinOp::Add => self.is_numeric_value(left) && self.is_numeric_value(right),
                _ => false,
            },
            _ => false,
        }
    }

    /// The kind and family of a value whose type this build can prove: a declared variable, an SDT member
    /// or a literal. Any other expression has no proven type and is not checked here.
    fn source_kind(&self, expr: &Expr) -> Option<(String, ValueFamily)> {
        match expr {
            Expr::Paren(inner) => self.source_kind(inner),
            Expr::Integer(_) | Expr::Decimal(_) => {
                Some(("numeric literal".to_string(), ValueFamily::Numeric))
            }
            Expr::Text(_) => Some(("text literal".to_string(), ValueFamily::Text)),
            Expr::Boolean(_) => Some(("boolean literal".to_string(), ValueFamily::Boolean)),
            Expr::Variable(_) | Expr::Member(..) => {
                let data_type = self.value_type(expr)?;
                let family = family_of(&data_type)?;
                Some((kind_name(&data_type).to_string(), family))
            }
            _ => None,
        }
    }

    /// `ToString()` of a numeric operand is written as `Str` with the operand's own precision and
    /// decimals. The SDK output for N(5,0) is `StringUtil.Str( (decimal)(AV8I), 5, 0)` and for N(5,2)
    /// `StringUtil.Str( AV8Hits, 5, 2)`; this writes the same call with the cast the `Str` form uses.
    /// Any other operand has no evidenced text form, so it is refused by name.
    fn to_string_of(&self, operand: &Expr) -> String {
        let text = self.emit_expr(operand);
        // `IsEmpty()` is a boolean; its text is `StringUtil.BoolToStr( (DateTime.MinValue==AV10D))` (rustouttypesprobe.cs
        // line 87).
        if matches!(operand, Expr::DateMethod(name, _) if name.eq_ignore_ascii_case("ISEMPTY")) {
            return format!("StringUtil.BoolToStr({text})");
        }
        match self.value_type(operand) {
            // Date and DateTime use the SDK's localUtil formatters with the arguments the SDK writes for this
            // KB: `DToC( AV12D, 1, "/")` (arustdisplayprobe.cs line 101) and `TToC( AV13DT, 8, 5, 1, 2, "/", ":", " ")`
            // (line 119). These are the KB's own date and time format settings as the SDK received them. They
            // are not read from the KB yet, so they hold for the KBs whose SDK output was checked (KBTeste).
            Some(DataType::Date {}) => format!("context.localUtil.DToC({text}, 1, \"/\")"),
            Some(DataType::DateTime {}) => {
                format!("context.localUtil.TToC({text}, 8, 5, 1, 2, \"/\", \":\", \" \")")
            }
            Some(DataType::Numeric { precision, scale }) => {
                format!("StringUtil.Str((decimal)({text}), {precision}, {scale})")
            }
            Some(_) => {
                self.fail(format!(
                    "ToString() of '{text}' is mapped for numeric values only"
                ));
                format!("{text}.ToString()")
            }
            None => {
                self.fail(format!(
                    "ToString() of '{text}': its type is not one this build resolves"
                ));
                format!("{text}.ToString()")
            }
        }
    }

    /// The receiver of a collection method (`Add`, `RemoveItem`, `Clear`). With an SDT scope it must
    /// resolve to a collection member.
    fn collection_target(&self, col: &Expr, method: &str) -> String {
        let receiver = self.emit_expr(col);
        if self.sdts.is_some() {
            match self.resolve(col) {
                Ok(SdtMemberType::Collection { .. }) => {}
                Ok(_) => self.fail(format!("{method}(): the receiver is not a collection")),
                Err(reason) => self.fail(format!("{method}(): {reason}")),
            }
        }
        receiver
    }

    /// SDT collections require their declared item class, not their containing SDT.
    fn check_collection_item(&self, collection: &Expr, value: &Expr) {
        let Ok(SdtMemberType::Collection { item, .. }) = self.resolve(collection) else {
            return;
        };
        if let Expr::CloneRecord(inner) = value {
            self.check_collection_item(collection, inner);
            return;
        }
        let actual = match value {
            Expr::Variable(name) => match self.declared(name) {
                Some(data_type @ DataType::Record { .. }) => Some(to_csharp_type(data_type)),
                _ => None,
            },
            _ => match self.resolve(value) {
                Ok(SdtMemberType::Class { class, .. }) => Some(class),
                _ => None,
            },
        };
        match actual {
            Some(class) if class.eq_ignore_ascii_case(&item) => {}
            Some(class) => {
                self.fail(format!("Add(): expected item type '{item}', got '{class}'"));
            }
            None => self.fail(format!(
                "Add(): expected item type '{item}'; the argument does not resolve to that SDT class"
            )),
        }
    }

    /// Current indentation string.
    pub fn indent(&self) -> String {
        self.indent_unit.repeat(self.indent_level)
    }

    /// Executes closure with one deeper indentation level.
    pub fn indented<F, R>(&mut self, f: F) -> R
    where
        F: FnOnce(&mut Self) -> R,
    {
        self.indent_level += 1;
        let res = f(self);
        self.indent_level -= 1;
        res
    }

    /// Emits a C# expression string: the SDK's form when this emitter writes it and a rule covers the
    /// expression (see `sdk_expr`), else the portable form.
    pub fn emit_expr(&self, expr: &Expr) -> String {
        if self.sdk_form() {
            if let Some(text) = self.sdk_value(expr) {
                return text;
            }
        }
        self.emit_expr_portable(expr)
    }

    /// A condition: the SDK's form when it covers it, else the portable expression.
    fn emit_condition(&self, expr: &Expr) -> String {
        if self.sdk_form() {
            if let Some(text) = self.sdk_condition(expr) {
                return text;
            }
        }
        self.emit_expr_portable(expr)
    }

    /// Emits a C# expression in the portable form.
    fn emit_expr_portable(&self, expr: &Expr) -> String {
        match expr {
            Expr::Paren(inner) => format!("({})", self.emit_expr(inner)),
            Expr::Integer(val) => val.to_string(),
            Expr::Decimal(s) => {
                if s.ends_with('m') || s.ends_with('M') {
                    s.clone()
                } else {
                    format!("{s}m")
                }
            }
            // The SDK writes a text's characters raw (a tab, `\xE1`; artm41852400.cs), escaping only the
            // quote, the backslash and line ends. A character cp1252 has no byte for keeps its escape.
            Expr::Text(s) if self.sdk_form() => {
                let mut out = String::with_capacity(s.len() + 2);
                out.push('"');
                for ch in s.chars() {
                    match ch {
                        '\\' => out.push_str("\\\\"),
                        '"' => out.push_str("\\\""),
                        '\n' => out.push_str("\\n"),
                        '\r' => out.push_str("\\r"),
                        c if c.is_ascii() || crate::csharp::literals::has_cp1252_byte(c) => {
                            out.push(c)
                        }
                        c if (c as u32) <= 0xFFFF => {
                            let _ = write!(out, "\\u{:04X}", c as u32);
                        }
                        c => {
                            let _ = write!(out, "\\U{:08X}", c as u32);
                        }
                    }
                }
                out.push('"');
                out
            }
            Expr::Text(s) => {
                let mut out = String::with_capacity(s.len() + 2);
                out.push('"');
                // Each stored character is written as its own escape. The value the SDK's compiled literal
                // holds depends on the whole file, so `csharp::literals::resolve_sdk_literals` settles it when
                // the file is written.
                for ch in s.chars() {
                    match ch {
                        '\\' => out.push_str("\\\\"),
                        '"' => out.push_str("\\\""),
                        '\n' => out.push_str("\\n"),
                        '\r' => out.push_str("\\r"),
                        '\t' => out.push_str("\\t"),
                        c if c.is_ascii() => out.push(c),
                        c if (c as u32) <= 0xFFFF => {
                            let _ = write!(out, "\\u{:04X}", c as u32);
                        }
                        c => {
                            let _ = write!(out, "\\U{:08X}", c as u32);
                        }
                    }
                }
                out.push('"');
                out
            }
            Expr::TextLength(inner) => format!("StringUtil.Len({})", self.emit_expr(inner)),
            Expr::Boolean(b) => {
                if *b {
                    "true".to_string()
                } else {
                    "false".to_string()
                }
            }
            Expr::EmptyDate => "DateTime.MinValue".to_string(),
            Expr::Variable(name) => {
                if let Some(vars) = self.vars {
                    let lookup = name.trim_start_matches('&');
                    if let Some((k, dt)) = vars.iter().find(|(k, _)| {
                        k.eq_ignore_ascii_case(lookup) || k.eq_ignore_ascii_case(name)
                    }) {
                        self.refuse_if_no_expression_form(k, dt);
                        return self.var_cs(k);
                    }
                }
                self.var_cs(name)
            }
            Expr::Attribute(name) => to_csharp_attr_name(name),
            Expr::Member(target, field) => self.member_target(target, field),
            Expr::CloneRecord(inner) => {
                let receiver = self.emit_expr(inner);
                if self.sdts.is_none() {
                    return format!("{receiver}.Clone()");
                }
                // The SDK casts the clone to the SDT class `(SdtX)(AV8First.Clone())`, and a
                // collection member to its collection class `(GXBaseCollection<Item>)(X.Rows.Clone())`.
                match self.resolve(inner) {
                    Ok(SdtMemberType::Class { class, .. }) => {
                        format!("({class})({receiver}.Clone())")
                    }
                    Ok(SdtMemberType::Collection { item, .. }) => {
                        format!("(GXBaseCollection<{item}>)({receiver}.Clone())")
                    }
                    Ok(_) => {
                        self.fail(
                            "Clone(): the receiver is neither an SDT nor a collection".to_string(),
                        );
                        format!("{receiver}.Clone()")
                    }
                    Err(reason) => {
                        self.fail(format!("Clone(): {reason}"));
                        format!("{receiver}.Clone()")
                    }
                }
            }
            Expr::CollectionItem(col, idx) => {
                let collection = self.emit_expr(col);
                let index = self.emit_expr(idx);
                if self.sdts.is_none() {
                    return format!("{collection}.Item((int)({index}))");
                }
                // The SDK casts the item to its class: `((SdtX_RowsItem)AV8First.gxTpr_Rows.Item(1))`.
                match self.resolve(expr) {
                    Ok(SdtMemberType::Class { class, .. }) => {
                        format!("(({class}){collection}.Item((int)({index})))")
                    }
                    Ok(_) => {
                        self.fail("Item(n): the result is not an SDT item".to_string());
                        format!("{collection}.Item((int)({index}))")
                    }
                    Err(reason) => {
                        self.fail(format!("Item(n): {reason}"));
                        format!("{collection}.Item((int)({index}))")
                    }
                }
            }
            Expr::DateMethod(method, target) => match method.to_ascii_uppercase().as_str() {
                "DAY" => format!("DateTimeUtil.Day({})", self.emit_expr(target)),
                "MONTH" => format!("DateTimeUtil.Month({})", self.emit_expr(target)),
                "YEAR" => format!("DateTimeUtil.Year({})", self.emit_expr(target)),
                "HOUR" => format!("DateTimeUtil.Hour({})", self.emit_expr(target)),
                "MINUTE" => format!("DateTimeUtil.Minute({})", self.emit_expr(target)),
                "SEC" | "SECOND" => format!("DateTimeUtil.Second({})", self.emit_expr(target)),
                // `IsEmpty()` of a date or datetime is the SDK's comparison with `DateTime.MinValue`: `(DateTime.MinValue==AV8Stamp)`
                // (aodm42412500.cs line 71; the Date form in rustouttypesprobe.cs line 87). Any other target is not evidenced.
                "ISEMPTY" => match self.value_type(target) {
                    Some(DataType::Date {} | DataType::DateTime {}) => {
                        format!("(DateTime.MinValue=={})", self.emit_expr(target))
                    }
                    _ => {
                        self.fail("IsEmpty() of a value that is not a date or datetime: the SDK form is not evidenced".to_string());
                        String::new()
                    }
                },
                "DOW" => format!("DateTimeUtil.Dow({})", self.emit_expr(target)),
                "AGE" => format!("DateTimeUtil.Age(context, {})", self.emit_expr(target)),
                _ => format!("DateTimeUtil.{method}({})", self.emit_expr(target)),
            },
            Expr::DateShift(method, target, amount) => match method.to_ascii_uppercase().as_str() {
                "ADDDAYS" | "DADD" => format!(
                    "DateTimeUtil.DAdd({}, (int)({}))",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
                "ADDMTH" => format!(
                    "DateTimeUtil.AddMth({}, (int)({}))",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
                "ADDYR" => format!(
                    "DateTimeUtil.AddYr({}, (int)({}))",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
                "TADD" => format!(
                    "DateTimeUtil.TAdd({}, (int)({}))",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
                "DDIFF" => format!(
                    "DateTimeUtil.DDiff({}, {})",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
                "TDIFF" => format!(
                    "DateTimeUtil.TDiff({}, {})",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
                _ => format!(
                    "DateTimeUtil.{method}({}, {})",
                    self.emit_expr(target),
                    self.emit_expr(amount)
                ),
            },
            Expr::Not(inner) => format!("!({})", self.emit_expr(inner)),
            Expr::Negate(inner) => format!("-({})", self.emit_expr(inner)),
            Expr::Binary(left, op, right) => {
                // Date arithmetic: the SDK writes `&D + 1` as `DateTimeUtil.DAdd(AV12D,+((int)(1)))` (arustdisplayprobe.cs
                // line 124). Only that form is evidenced; any other date arithmetic is refused rather than written as C#
                // arithmetic on DateTime, which does not compile.
                let date_operand = |e: &Expr| {
                    matches!(
                        self.value_type(e),
                        Some(DataType::Date {} | DataType::DateTime {})
                    )
                };
                let arithmetic = matches!(
                    op,
                    BinOp::Add | BinOp::Subtract | BinOp::Multiply | BinOp::Divide
                );
                // `"text" + &D` is a concatenation, not arithmetic: the SDK writes it as raw `"dv_min=["+AV12D+"]"`
                // (arustdisplayprobe.cs line 109), which compiles.
                let concatenation = matches!(op, BinOp::Add)
                    && (self.is_text_expr(left) || self.is_text_expr(right));
                if arithmetic && !concatenation && (date_operand(left) || date_operand(right)) {
                    if let (BinOp::Add, Expr::Integer(n), true) = (
                        op,
                        right.as_ref(),
                        matches!(self.value_type(left), Some(DataType::Date {})),
                    ) {
                        return format!(
                            "DateTimeUtil.DAdd({}, +((int)({n})))",
                            self.emit_expr(left)
                        );
                    }
                    self.fail("date arithmetic other than a Date plus an integer literal: the SDK form is not evidenced".to_string());
                    return String::new();
                }
                let left_s = self.emit_expr(left);
                let right_s = self.emit_expr(right);
                // GeneXus division is decimal. Between two integer literals C# divides as int (`7 / 3` is 2), so
                // the divisor is written as decimal, as the SDK writes it (`7/ (decimal)(3)`, arustdisplayprobe2.cs
                // line 89). Other arithmetic on literals keeps the SDK's form (`(21 * 2)`, arustdisplayprobe.cs line 77).
                let literal_arithmetic =
                    integer_literals_only(left) && integer_literals_only(right);
                match op {
                    BinOp::And => format!("({left_s} && {right_s})"),
                    BinOp::Or => format!("({left_s} || {right_s})"),
                    BinOp::Divide if literal_arithmetic => {
                        format!("({left_s} / (decimal)({right_s}))")
                    }
                    BinOp::Add => format!("({left_s} + {right_s})"),
                    BinOp::Subtract => format!("({left_s} - {right_s})"),
                    BinOp::Multiply => format!("({left_s} * {right_s})"),
                    BinOp::Divide => format!("({left_s} / {right_s})"),
                    BinOp::Equal => format!("({left_s} == {right_s})"),
                    BinOp::NotEqual => format!("({left_s} != {right_s})"),
                    BinOp::Less => format!("({left_s} < {right_s})"),
                    BinOp::LessEqual => format!("({left_s} <= {right_s})"),
                    BinOp::Greater => format!("({left_s} > {right_s})"),
                    BinOp::GreaterEqual => format!("({left_s} >= {right_s})"),
                }
            }
            Expr::Call(name, args) => match name.to_ascii_uppercase().as_str() {
                "TRIM" if !args.is_empty() => {
                    format!("StringUtil.Trim({})", self.emit_expr(&args[0]))
                }
                "LTRIM" if !args.is_empty() => {
                    format!("StringUtil.LTrim({})", self.emit_expr(&args[0]))
                }
                "RTRIM" if !args.is_empty() => {
                    format!("StringUtil.RTrim({})", self.emit_expr(&args[0]))
                }
                "STR" if !args.is_empty() => {
                    let n = self.emit_expr(&args[0]);
                    let len = if args.len() > 1 {
                        self.emit_expr(&args[1])
                    } else {
                        "10".to_string()
                    };
                    let dec = if args.len() > 2 {
                        self.emit_expr(&args[2])
                    } else {
                        "0".to_string()
                    };
                    format!("StringUtil.Str((decimal)({n}), {len}, {dec})")
                }
                "TOSTRING" if args.len() == 1 && self.sdts.is_some() => self.to_string_of(&args[0]),
                "LEN" if !args.is_empty() => {
                    format!("StringUtil.Len({})", self.emit_expr(&args[0]))
                }
                "SUBSTR" if args.len() >= 2 => {
                    let s = self.emit_expr(&args[0]);
                    let start = self.emit_expr(&args[1]);
                    let len = if args.len() > 2 {
                        self.emit_expr(&args[2])
                    } else {
                        "-1".to_string()
                    };
                    format!("StringUtil.Substring({s}, {start}, {len})")
                }
                "UPPER" if !args.is_empty() => {
                    format!("StringUtil.Upper({})", self.emit_expr(&args[0]))
                }
                "LOWER" if !args.is_empty() => {
                    format!("StringUtil.Lower({})", self.emit_expr(&args[0]))
                }
                "PADL" if args.len() >= 2 => {
                    let s = self.emit_expr(&args[0]);
                    let len = self.emit_expr(&args[1]);
                    let fill = if args.len() > 2 {
                        self.emit_expr(&args[2])
                    } else {
                        "\" \"".to_string()
                    };
                    format!("StringUtil.PadL({s}, (int)({len}), {fill})")
                }
                "PADR" if args.len() >= 2 => {
                    let s = self.emit_expr(&args[0]);
                    let len = self.emit_expr(&args[1]);
                    let fill = if args.len() > 2 {
                        self.emit_expr(&args[2])
                    } else {
                        "\" \"".to_string()
                    };
                    format!("StringUtil.PadR({s}, (int)({len}), {fill})")
                }
                "CONCAT" if args.len() >= 2 => {
                    let a = self.emit_expr(&args[0]);
                    let b = self.emit_expr(&args[1]);
                    format!("StringUtil.Concat({a}, {b}, \"\")")
                }
                "STARTSWITH" if args.len() >= 2 => {
                    format!(
                        "StringUtil.StartsWith({}, {})",
                        self.emit_expr(&args[0]),
                        self.emit_expr(&args[1])
                    )
                }
                "ENDSWITH" if args.len() >= 2 => {
                    format!(
                        "StringUtil.EndsWith({}, {})",
                        self.emit_expr(&args[0]),
                        self.emit_expr(&args[1])
                    )
                }
                "CONTAINS" | "STRSEARCH" if args.len() >= 2 => {
                    format!(
                        "(StringUtil.StringSearch({}, {}) > 0)",
                        self.emit_expr(&args[0]),
                        self.emit_expr(&args[1])
                    )
                }
                "INT" if !args.is_empty() => {
                    format!(
                        "(long)(Math.Truncate((decimal)({})))",
                        self.emit_expr(&args[0])
                    )
                }
                "ROUND" if !args.is_empty() => {
                    let n = self.emit_expr(&args[0]);
                    let d = if args.len() > 1 {
                        self.emit_expr(&args[1])
                    } else {
                        "0".to_string()
                    };
                    format!("(decimal)Math.Round((decimal)({n}), (int)({d}))")
                }
                "TRUNC" if !args.is_empty() => {
                    format!("decimal.Truncate((decimal)({}))", self.emit_expr(&args[0]))
                }
                "VAL" if !args.is_empty() => {
                    format!("NumberUtil.Val({}, \".\")", self.emit_expr(&args[0]))
                }
                "IIF" if args.len() >= 3 => {
                    let cond = self.emit_expr(&args[0]);
                    let t = self.emit_expr(&args[1]);
                    let f = self.emit_expr(&args[2]);
                    format!("(({cond}) ? ({t}) : ({f}))")
                }
                "ISEMPTY" if !args.is_empty() => {
                    format!("string.IsNullOrEmpty({})", self.emit_expr(&args[0]))
                }
                "NULLVALUE" => "\"\"".to_string(),
                "YMDTOD" if args.len() >= 3 => {
                    let y = self.emit_expr(&args[0]);
                    let m = self.emit_expr(&args[1]);
                    let d = self.emit_expr(&args[2]);
                    // The SDK calls these two on the context's instance utility (`context.localUtil`).
                    format!("context.localUtil.YMDToD((int)({y}), (int)({m}), (int)({d}))")
                }
                "YMDHMSTOT" if args.len() >= 6 => {
                    let y = self.emit_expr(&args[0]);
                    let m = self.emit_expr(&args[1]);
                    let d = self.emit_expr(&args[2]);
                    let h = self.emit_expr(&args[3]);
                    let mi = self.emit_expr(&args[4]);
                    let s = self.emit_expr(&args[5]);
                    format!(
                        "context.localUtil.YMDHMSToT((int)({y}), (int)({m}), (int)({d}), (int)({h}), (int)({mi}), (int)({s}))"
                    )
                }
                "NOW" | "SERVERNOW" => "DateTimeUtil.Now(context)".to_string(),
                "TODAY" | "SERVERDATE" => "DateTimeUtil.Today(context)".to_string(),
                "TOJSON" if !args.is_empty() => {
                    format!("{}.ToJSonString(false)", self.emit_expr(&args[0]))
                }
                "TOXML" if !args.is_empty() => {
                    format!(
                        "{}.ToXml(false, true, \"\", \"\")",
                        self.emit_expr(&args[0])
                    )
                }
                "NEW" => {
                    if let Some(Expr::Variable(s)) = args.first() {
                        let clean = s.trim_start_matches('&');
                        format!("new Sdt{clean}(context)")
                    } else {
                        "new Object()".to_string()
                    }
                }
                // The SDK writes `DateTimeUtil.Year( AV8Stamp)` (aodm42412500.cs) and
                // `StringUtil.StringReplace( "banana", "na", "X")` (arusttextfuncprobe.cs).
                "YEAR" if args.len() == 1 => {
                    format!("DateTimeUtil.Year({})", self.emit_expr(&args[0]))
                }
                "STRREPLACE" if args.len() == 3 => {
                    let source = self.emit_expr(&args[0]);
                    let find = self.emit_expr(&args[1]);
                    let replace = self.emit_expr(&args[2]);
                    format!("StringUtil.StringReplace({source}, {find}, {replace})")
                }
                _ if crate::frontend::is_typed_builtin(&name.to_ascii_uppercase()) => {
                    // The parser accepts this built-in but this emitter writes no C# for it. Writing it
                    // as a call falls through to `new NAME(context).executeUdp(...)`, which names a class
                    // that does not exist, so it is refused by name instead.
                    self.fail(format!(
                        "built-in {}() is accepted by the parser but this build writes no C# for it",
                        name.to_ascii_uppercase()
                    ));
                    String::new()
                }
                _ => self.emit_call_expression(name, args),
            },
        }
    }

    /// Emits a C# statement string with indentation and trailing newline.
    pub fn emit_stmt(&mut self, stmt: &Stmt, vars: &BTreeMap<String, DataType>) -> String {
        let indent = self.indent();
        if self.calls.is_some() && statement_has_procedure_call(stmt) {
            self.fail(
                "a Procedure call in a member, attribute, collection or JSON statement has no SDK form in this build"
                    .to_string(),
            );
            return String::new();
        }
        match stmt {
            Stmt::Assign(var, expr) => {
                let lookup_key = var.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(var)
                });
                let cs_var = if let Some((k, _)) = matched_var {
                    self.var_cs(k)
                } else {
                    self.var_cs(var)
                };
                if let Some((k, dt)) = matched_var {
                    if has_no_expression_form(dt) {
                        self.fail(format!(
                            "assignment to '{k}': a {} variable has no assignment form evidenced in this build",
                            kind_name(dt)
                        ));
                        return String::new();
                    }
                }
                // NEW() uses the same declared record/collection constructor as initialization.
                if let (Expr::Call(name, args), Some((_, data_type))) = (expr, matched_var) {
                    if name.eq_ignore_ascii_case("NEW") && args.is_empty() {
                        if let Some(allocation) = super::procedure::allocation_expression(data_type)
                        {
                            return format!("{indent}{cs_var} = {allocation};\n");
                        }
                    }
                }
                if let Some(calls) = self.calls {
                    if !procedure_calls(expr).is_empty() {
                        return match matched_var {
                            Some((_, dt))
                                if !matches!(
                                    dt,
                                    DataType::Record { .. } | DataType::Collection { .. }
                                ) =>
                            {
                                self.emit_assignment_with_call(&cs_var, dt, expr, &indent, calls)
                            }
                            _ => {
                                self.fail(format!(
                                    "assignment to '{var}' with a Procedure call: the target is not a scalar variable"
                                ));
                                String::new()
                            }
                        };
                    }
                }
                // C# has no implicit conversion between a number, a text, a date-time or a boolean, and
                // this build evidences none of these conversions from the SDK. The assignment would not
                // compile, so it is refused by name; only a proven source type is checked.
                if let Some((_, target)) = matched_var {
                    if let (Some(target_family), Some((source, source_family))) =
                        (family_of(target), self.source_kind(expr))
                    {
                        if target_family != source_family {
                            self.fail(format!(
                                "assignment to '{lookup_key}': a {source} value cannot be assigned to a {} variable; no conversion between these types is evidenced in this build",
                                kind_name(target)
                            ));
                            return String::new();
                        }
                    }
                }
                if self.sdk_form() {
                    if let Some((_, dt)) = matched_var {
                        if let Some(text) = self.sdk_assigned(dt, expr) {
                            return format!("{indent}{cs_var} = {text};\n");
                        }
                    }
                }
                let expr_str = self.emit_expr(expr);
                if let Some((_, dt)) = matched_var {
                    if matches!(dt, DataType::Numeric { .. }) {
                        let cs_type = to_csharp_type(dt);
                        return format!("{indent}{cs_var} = ({cs_type})({expr_str});\n");
                    }
                }
                format!("{indent}{cs_var} = {expr_str};\n")
            }
            Stmt::AssignField(target, field, expr) => {
                // The same family rule as a variable: a member's type comes from its SDT, and a value of
                // another family has no evidenced conversion, so it is refused by name.
                let member = Expr::Member(target.clone(), field.clone());
                if let (Some(member_type), Some((source, source_family))) =
                    (self.value_type(&member), self.source_kind(expr))
                {
                    if let Some(member_family) = family_of(&member_type) {
                        if member_family != source_family {
                            self.fail(format!(
                                "assignment to member '{field}': a {source} value cannot be assigned to a {} member; no conversion between these types is evidenced in this build",
                                kind_name(&member_type)
                            ));
                            return String::new();
                        }
                    }
                }
                let t = self.member_target(target, field);
                let e = self.emit_expr(expr);
                format!("{indent}{t} = {e};\n")
            }
            Stmt::CollectionAdd(col, item, opt_pos) => {
                let c = self.collection_target(col, "Add");
                self.check_collection_item(col, item);
                let i = self.emit_expr(item);
                if let Some(pos) = opt_pos {
                    let p = self.emit_expr(pos);
                    format!("{indent}{c}.Add({i}, {p});\n")
                } else {
                    // The SDK appends without a position as `X.gxTpr_Rows.Add(AV9Row, 0);`: 31 member
                    // appends and 1 variable append in the KBTeste NetModel output (`artdx765000a.cs`).
                    format!("{indent}{c}.Add({i}, 0);\n")
                }
            }
            Stmt::CollectionRemove(col, index) => {
                let c = self.collection_target(col, "Remove");
                let idx = self.emit_expr(index);
                format!("{indent}{c}.RemoveItem((int)({idx}));\n")
            }
            Stmt::CollectionClear(col) => {
                let c = self.collection_target(col, "Clear");
                format!("{indent}{c}.Clear();\n")
            }
            Stmt::FromJson(target, expr) => {
                let t = self.emit_expr(target);
                let e = self.emit_expr(expr);
                format!("{indent}{t}.FromJSonString({e}, null);\n")
            }
            Stmt::FromXml(from_xml) => {
                let t = self.emit_expr(&from_xml.target);
                let x = self.emit_expr(&from_xml.xml);
                if let Some(root) = &from_xml.root {
                    let r = self.emit_expr(root);
                    format!("{indent}{t}.FromXml({x}, {r});\n")
                } else {
                    format!("{indent}{t}.FromXml({x});\n")
                }
            }
            Stmt::Print(expr) | Stmt::MsgStatus(expr) => {
                // `StatusMessage` takes a string. A numeric message is written by the SDK as `StringUtil.LTrimStr(...)`
                // with the expression's own precision and scale (arustdisplayprobe.cs line 77), which is not evidenced
                // for these expressions, so it is refused rather than written as an int argument.
                if self.is_numeric_value(expr) {
                    match literal_message_format(expr) {
                        Some((precision, scale)) if self.sdk_form() => {
                            match self.sdk_numeric_message(expr, precision, scale) {
                                Some(text) => format!("{indent}context.StatusMessage( {text} );\n"),
                                None => {
                                    let e = self.emit_expr(expr);
                                    format!(
                                        "{indent}context.StatusMessage( StringUtil.LTrimStr( {e}, {precision}, {scale}) );\n"
                                    )
                                }
                            }
                        }
                        Some((precision, scale)) => {
                            let e = self.emit_expr(expr);
                            format!(
                                "{indent}context.StatusMessage( StringUtil.LTrimStr( {e}, {precision}, {scale}) );\n"
                            )
                        }
                        None => {
                            self.fail("message of a numeric value: the SDK writes StringUtil.LTrimStr with the expression's precision and scale, which is not evidenced for this expression".to_string());
                            String::new()
                        }
                    }
                } else {
                    let e = self.emit_expr(expr);
                    format!("{indent}context.StatusMessage( {e} );\n")
                }
            }
            Stmt::Return(expr) => {
                let e = self.emit_expr(expr);
                format!("{indent}AV_Result = {e};\n{indent}cleanup();\n{indent}return;\n")
            }
            Stmt::ReturnVoid if self.sdk_form() && self.in_subroutine.get() => {
                format!("{indent}returnInSub = true;\n{indent}if (true) return;\n")
            }
            Stmt::ReturnVoid if self.sdk_form() => {
                // The SDK's return from a Procedure body (afrq12224800.cs).
                format!("{indent}cleanup();\n{indent}if (true) return;\n")
            }
            Stmt::ReturnVoid => {
                format!("{indent}cleanup();\n{indent}return;\n")
            }
            Stmt::If(cond, then_branch, else_branch) if self.var_ids.is_some() => {
                // The SDK writes a Do Case, and an Else holding only an If, as `else if` (adcm37961700.cs).
                let mut out = String::new();
                let mut keyword = "if";
                let (mut cond, mut then_branch, mut else_branch) = (cond, then_branch, else_branch);
                loop {
                    let c = self.emit_condition(cond);
                    out.push_str(&format!("{indent}{keyword} ( {c} )\n{indent}{{\n"));
                    self.indented(|this| {
                        for s in then_branch {
                            out.push_str(&this.emit_stmt(s, vars));
                        }
                    });
                    out.push_str(&format!("{indent}}}\n"));
                    match else_branch.as_slice() {
                        [] => break,
                        [Stmt::If(c, t, e)] => {
                            keyword = "else if";
                            (cond, then_branch, else_branch) = (c, t, e);
                        }
                        rest => {
                            out.push_str(&format!("{indent}else\n{indent}{{\n"));
                            self.indented(|this| {
                                for s in rest {
                                    out.push_str(&this.emit_stmt(s, vars));
                                }
                            });
                            out.push_str(&format!("{indent}}}\n"));
                            break;
                        }
                    }
                }
                out
            }
            Stmt::If(cond, then_branch, else_branch) => {
                let c = self.emit_expr(cond);
                let mut out = format!("{indent}if ( {c} )\n{indent}{{\n");
                self.indented(|this| {
                    for s in then_branch {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                if !else_branch.is_empty() {
                    out.push_str(&format!("{indent}else\n{indent}{{\n"));
                    self.indented(|this| {
                        for s in else_branch {
                            out.push_str(&this.emit_stmt(s, vars));
                        }
                    });
                    out.push_str(&format!("{indent}}}\n"));
                }
                out
            }
            Stmt::While(cond, body) => {
                let c = self.emit_condition(cond);
                let mut out = format!("{indent}while ( {c} )\n{indent}{{\n");
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::For(var, start, end, stride, body) => {
                let lookup_key = var.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(var)
                });
                // The loop is `for (x = a; x <= b; x++)`, which needs a numeric iterator. The typed backend
                // refuses a non-numeric iterator for the same reason; the SDK never writes a loop over one.
                if let Some((name, data_type)) = matched_var {
                    if family_of(data_type) != Some(ValueFamily::Numeric) {
                        self.fail(format!(
                            "For '{name}': the iterator is a {} variable; this build writes For only over a numeric variable",
                            kind_name(data_type)
                        ));
                        return String::new();
                    }
                }
                let cs_var = if let Some((k, _)) = matched_var {
                    self.var_cs(k)
                } else {
                    self.var_cs(var)
                };
                // A start value that calls a Procedure is assigned before the loop, as the SDK does
                // (`GXt_int1 = AV_I; execute(...); AV_I = GXt_int1; while (AV_I <= 3)`).
                let hoisted = match self.calls {
                    Some(_) if !procedure_calls(start).is_empty() => {
                        Some(self.emit_stmt(&Stmt::Assign(var.clone(), start.clone()), vars))
                    }
                    _ => None,
                };
                let init = if hoisted.is_some() {
                    String::new()
                } else {
                    format!("{cs_var} = {}", self.emit_expr(start))
                };
                let e = self.emit_expr(end);
                let cmp = if *stride >= 0 { "<=" } else { ">=" };
                if self.var_ids.is_some() {
                    // The SDK's loop: the start assigned, a `while`, and the step added at the end of the
                    // body (`AV8I = (int)(AV8I+1);`, `AV8I+-1` downward; afrc12224800.cs, adcs37961700.cs).
                    let mut out = hoisted.unwrap_or_else(|| {
                        self.emit_stmt(&Stmt::Assign(var.clone(), start.clone()), vars)
                    });
                    out.push_str(&format!(
                        "{indent}while ( {cs_var} {cmp} {e} )\n{indent}{{\n"
                    ));
                    self.indented(|this| {
                        for s in body {
                            out.push_str(&this.emit_stmt(s, vars));
                        }
                    });
                    let cs_type =
                        matched_var.map_or("int".to_string(), |(_, dt)| to_csharp_type(dt));
                    out.push_str(&format!(
                        "{indent}   {cs_var} = ({cs_type})({cs_var}+{stride});\n{indent}}}\n"
                    ));
                    return out;
                }
                let step = if *stride == 1 {
                    format!("{cs_var}++")
                } else if *stride == -1 {
                    format!("{cs_var}--")
                } else if *stride >= 0 {
                    format!("{cs_var} += {stride}")
                } else {
                    format!("{cs_var} -= {}", -stride)
                };
                let mut out = hoisted.unwrap_or_default();
                out.push_str(&format!(
                    "{indent}for ( {init}; {cs_var} {cmp} {e}; {step} )\n{indent}{{\n"
                ));
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::ForIn(item, col, body) if self.sdk_form() && self.for_in_counter(1).is_some() => {
                // The SDK walks a collection by index (general/ui/sidebaritemsdp.cs): `AV9GXV1 = 1;`,
                // `while ( AV9GXV1 <= AV5wwProgramNames.Count )`, the item read by `Item`, the step last.
                let counter = self.for_in_counter(1).unwrap_or_default();
                let c = self.emit_expr(col);
                let item_type = vars
                    .iter()
                    .find(|(k, _)| k.eq_ignore_ascii_case(item.trim_start_matches('&')))
                    .map(|(_, dt)| to_csharp_type(dt))
                    .unwrap_or_default();
                let cs_item = self.var_cs(item);
                let mut out = format!(
                    "{indent}{counter} = 1;\n{indent}while ( {counter} <= {c}.Count )\n{indent}{{\n{indent}   {cs_item} = (({item_type}){c}.Item({counter}));\n"
                );
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!(
                    "{indent}   {counter} = (int)({counter}+1);\n{indent}}}\n"
                ));
                out
            }
            Stmt::ForIn(item, col, body) => {
                let lookup_key = item.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(item)
                });
                let cs_item = if let Some((k, _)) = matched_var {
                    self.var_cs(k)
                } else {
                    self.var_cs(item)
                };
                let c = self.emit_expr(col);
                let mut out = format!("{indent}foreach ( var {cs_item} in {c} )\n{indent}{{\n");
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::ForEach(for_each_stmt) => {
                let mut out = format!("{indent}/* GeneXus For Each block */\n");
                for s in &for_each_stmt.body {
                    out.push_str(&self.emit_stmt(s, vars));
                }
                out
            }
            Stmt::AssignAttribute(attr, expr) => {
                let cs_attr = to_csharp_attr_name(attr);
                let e = self.emit_expr(expr);
                format!("{indent}{cs_attr} = {e};\n")
            }
            Stmt::Delete => format!("{indent}/* delete current row */\n"),
            Stmt::New(new_stmt) => {
                let mut out = format!("{indent}/* new record */\n");
                for s in &new_stmt.body {
                    out.push_str(&self.emit_stmt(s, vars));
                }
                out
            }
            Stmt::Exit => format!("{indent}break;\n"),
            Stmt::Call(Expr::Call(name, args)) => {
                if let Some(calls) = self.calls {
                    return self.emit_statement_call(name, args, &indent, calls);
                }
                let args_str = args
                    .iter()
                    .map(|a| self.emit_expr(a))
                    .collect::<Vec<_>>()
                    .join(", ");
                let call_args = if args_str.is_empty() {
                    String::new()
                } else {
                    args_str
                };
                format!("{indent}new {name}(context).execute({call_args});\n")
            }
            Stmt::Call(other) => {
                let e = self.emit_expr(other);
                format!("{indent}{e};\n")
            }
            Stmt::DoSub(name) if self.sdk_form() => {
                // listprograms.cs: the call, then the leave when the subroutine returned.
                let method = self.subroutine_method(name);
                let leave = if self.in_subroutine.get() {
                    format!("{indent}   returnInSub = true;\n")
                } else {
                    format!("{indent}   cleanup();\n")
                };
                format!(
                    "{indent}/* Execute user subroutine: '{}' */\n{indent}{method} ();\n{indent}if ( returnInSub )\n{indent}{{\n{leave}{indent}   if (true) return;\n{indent}}}\n",
                    name.to_ascii_uppercase()
                )
            }
            Stmt::DoSub(name) => {
                let clean = crate::csharp::types::clean_ident(name);
                format!("{indent}Sub_{clean}();\n")
            }
            Stmt::Sub(_name, _body) => {
                // Handled as separate member method
                String::new()
            }
            Stmt::Commit => format!("{indent}context.CommitDataStores();\n"),
            Stmt::Rollback => format!("{indent}context.RollbackDataStores();\n"),
        }
    }
}

// ---- Procedure call sites ----------------------------------------------------------------------
// Each form below is the one the SDK output for KBTeste shows (see `csharp::calls` for the evidence).
// A call that does not match a form is refused by name, and the Procedure is not written.

impl<'a> CSharpEmitter<'a> {
    /// A Procedure call used as a value: `executeUdp`, which returns the Procedure's one output. Its
    /// arguments are the non-output parameters, in order, as the callee's `executeUdp` declares them.
    pub(super) fn emit_call_expression(&self, name: &str, args: &[Expr]) -> String {
        if !is_builtin_name(name) {
            if let Some(temp) = self.call_temps.borrow().get(&(name.as_ptr() as usize)) {
                // The statement's plan has already written this call's result to `temp`.
                return temp.clone();
            }
        }
        let Some(calls) = self.calls else {
            // No signature table: the call is written as it was (unit and WebPanel paths).
            let args_str = args
                .iter()
                .map(|a| self.emit_expr(a))
                .collect::<Vec<_>>()
                .join(", ");
            return format!("new {name}(context).executeUdp({args_str})");
        };
        if is_builtin_name(name) {
            self.fail(format!(
                "built-in {}() is accepted by the parser but this build writes no C# for it",
                name.to_ascii_uppercase()
            ));
            return String::new();
        }
        let Some(callee) = self.callee(calls, name) else {
            return String::new();
        };
        let params = &callee.params;
        let class = &callee.qualified_class();
        let outputs = params.iter().filter(|p| p.mode == ParamMode::Out).count();
        if outputs != 1 {
            self.fail(format!(
                "call to '{name}' in an expression: it must return exactly one value, and it has {outputs} output parameters"
            ));
            return String::new();
        }
        let input_params: Vec<&CallParam> =
            params.iter().filter(|p| p.mode != ParamMode::Out).collect();
        if args.len() != input_params.len() {
            self.fail(format!(
                "call to '{name}': {} argument(s) for {} input parameter(s)",
                args.len(),
                input_params.len()
            ));
            return String::new();
        }
        let mut parts = Vec::with_capacity(args.len());
        for (arg, param) in args.iter().zip(&input_params) {
            match param.mode {
                ParamMode::In => match self.input_argument(name, arg, &param.data_type, true) {
                    Some(text) => parts.push(text),
                    None => return String::new(),
                },
                _ => {
                    if !matches!(arg, Expr::Variable(_)) {
                        self.fail(format!(
                            "call to '{name}' in an expression: an in-out argument that is not a variable has no temporary the SDK output shows"
                        ));
                        return String::new();
                    }
                    parts.push(format!("ref {}", self.emit_expr(arg)));
                }
            }
        }
        if self.sdk_form() {
            return format!(
                "new {class}(context).executeUdp( {})",
                sdk_arguments(&parts)
            );
        }
        format!("new {class}(context).executeUdp({})", parts.join(", "))
    }

    /// An input (`in`) argument. A variable is written as it is; a literal is written as the SDK writes
    /// it for its parameter (`(decimal)(99)` for an integer, `1.235m` for a decimal). A nested Procedure
    /// call is written only inside an expression (the SDK's `executeUdp(new X(context).executeUdp(...))`).
    /// Any other form is refused.
    fn input_argument(
        &self,
        name: &str,
        arg: &Expr,
        data_type: &DataType,
        nested_calls: bool,
    ) -> Option<String> {
        let decimal = to_csharp_type(data_type) == "decimal";
        match arg {
            Expr::Variable(_) => Some(self.emit_expr(arg)),
            Expr::Integer(value) if decimal => Some(format!("(decimal)({value})")),
            Expr::Decimal(_) if decimal => Some(self.emit_expr(arg)),
            Expr::Call(callee, _) if nested_calls && !is_builtin_name(callee) => {
                Some(self.emit_expr(arg))
            }
            _ => {
                self.fail(format!(
                    "call to '{name}': an input argument of this form has no SDK form in this build"
                ));
                None
            }
        }
    }

    /// A Procedure call as a statement: `execute(...)` with each argument in its parameter's mode. A
    /// variable takes its keyword (`out AV_B`, `ref AV_C`). A literal or arithmetic expression for an
    /// in-out parameter goes through the one decimal temporary (`GXt_decimal1 = (decimal)(7);`).
    fn emit_statement_call(
        &self,
        name: &str,
        args: &[Expr],
        indent: &str,
        calls: &CallSignatures,
    ) -> String {
        let mut allocated = Vec::new();
        let out = self.plan_statement_call(name, args, indent, calls, &mut allocated);
        self.temps.borrow_mut().release(&allocated);
        out
    }

    fn plan_statement_call(
        &self,
        name: &str,
        args: &[Expr],
        indent: &str,
        calls: &CallSignatures,
        allocated: &mut Vec<(&'static str, u32)>,
    ) -> String {
        let Some(callee) = self.callee(calls, name) else {
            return String::new();
        };
        let params = &callee.params;
        let class = &callee.qualified_class();
        if args.len() != params.len() {
            self.fail(format!(
                "call to '{name}': {} argument(s) for {} parameter(s)",
                args.len(),
                params.len()
            ));
            return String::new();
        }
        let mut before = String::new();
        let mut temp_in_use = false;
        let mut parts = Vec::with_capacity(params.len());
        for (arg, param) in args.iter().zip(params) {
            if !procedure_calls(arg).is_empty() {
                self.fail(format!(
                    "call to '{name}': a Procedure call inside an argument has no SDK form in this build"
                ));
                return String::new();
            }
            match param.mode {
                ParamMode::In => match self.input_argument(name, arg, &param.data_type, false) {
                    Some(text) => parts.push(text),
                    None => return String::new(),
                },
                ParamMode::Out | ParamMode::InOut => {
                    if matches!(arg, Expr::Variable(_)) {
                        let keyword = if param.mode == ParamMode::Out {
                            "out"
                        } else {
                            "ref"
                        };
                        parts.push(format!("{keyword} {}", self.emit_expr(arg)));
                        continue;
                    }
                    if param.mode == ParamMode::Out {
                        self.fail(format!(
                            "call to '{name}': an output argument must be a variable"
                        ));
                        return String::new();
                    }
                    let arithmetic = matches!(
                        arg,
                        Expr::Binary(
                            _,
                            BinOp::Add | BinOp::Subtract | BinOp::Multiply | BinOp::Divide,
                            _
                        )
                    );
                    let evidenced = matches!(arg, Expr::Integer(_)) || arithmetic;
                    if temp_type(&param.data_type) != Some("decimal") || !evidenced {
                        self.fail(format!(
                            "call to '{name}': an in-out argument of this form has no temporary the SDK output shows"
                        ));
                        return String::new();
                    }
                    if temp_in_use {
                        self.fail(format!(
                            "call to '{name}': two temporaries in one statement; this build emits one per statement, and the SDK's sibling and nested copy-in shapes are not emitted yet"
                        ));
                        return String::new();
                    }
                    temp_in_use = true;
                    let (temp, slot) = self.temps.borrow_mut().alloc("decimal");
                    allocated.push(slot);
                    before.push_str(&format!(
                        "{indent}{temp} = (decimal)({});\n",
                        self.emit_expr(arg)
                    ));
                    parts.push(format!("ref {temp}"));
                }
            }
        }
        if self.sdk_form() {
            return format!(
                "{before}{indent}new {class}(context ).execute( {}) ;\n",
                sdk_arguments(&parts)
            );
        }
        format!(
            "{before}{indent}new {class}(context).execute({});\n",
            parts.join(", ")
        )
    }

    /// An assignment whose value holds Procedure calls: the SDK's copy-in, calls and copy-out. Each call takes
    /// a temporary and copies the target into it, the calls its arguments need come before it, and the target
    /// takes the value back (`GXt_decimal1 = AV8Amount; ... AV8Amount = GXt_decimal1;`). A call nested in a
    /// larger value reads its temporary in its place (`AV8Amount = (decimal)(GXt_decimal1+GXt_decimal2)`).
    fn emit_assignment_with_call(
        &self,
        cs_var: &str,
        data_type: &DataType,
        expr: &Expr,
        indent: &str,
        calls: &CallSignatures,
    ) -> String {
        let target = to_csharp_type(data_type);
        let plan = AssignPlan {
            cs_var,
            target: &target,
            indent,
            calls,
        };
        let mut allocated = Vec::new();
        let out = self.plan_assignment_with_call(&plan, expr, &mut allocated);
        self.temps.borrow_mut().release(&allocated);
        self.call_temps.borrow_mut().clear();
        out
    }

    fn plan_assignment_with_call(
        &self,
        plan: &AssignPlan<'_>,
        expr: &Expr,
        allocated: &mut Vec<(&'static str, u32)>,
    ) -> String {
        let whole_value = matches!(expr, Expr::Call(name, _) if !is_builtin_name(name));
        let mut lines = String::new();
        let mut roots = Vec::new();
        for (name, args) in top_level_calls(expr) {
            match self.plan_call(plan, name, args, whole_value, &mut lines, allocated) {
                Some(temp) => roots.push(temp),
                None => return String::new(),
            }
        }
        if whole_value {
            // The whole value is the call's own temporary, with no cast: `AV9PairResult = GXt_decimal1;`.
            return format!("{lines}{}{} = {};\n", plan.indent, plan.cs_var, roots[0]);
        }
        if self.sdk_form() {
            if let Expr::Binary(left, op @ (BinOp::And | BinOp::Or), right) = expr {
                // `AV9Flag = (bool)(GXt_boolean3||GXt_boolean4);` (arusttypedcallprobe.cs).
                let joiner = if *op == BinOp::And { "&&" } else { "||" };
                return format!(
                    "{lines}{}{} = ({})({}{joiner}{});\n",
                    plan.indent,
                    plan.cs_var,
                    plan.target,
                    self.emit_expr(left),
                    self.emit_expr(right)
                );
            }
        }
        let value = self.emit_expr(expr);
        format!(
            "{lines}{}{} = ({})({value});\n",
            plan.indent, plan.cs_var, plan.target
        )
    }

    /// Plans one Procedure call of an assignment and returns the temporary its result is written to. The
    /// copy-in of that temporary comes first, then the calls its arguments need, then the call itself
    /// (arusttypedcallprobe.cs lines 70-74 and 91-97; apoa97435900.cs lines 71-75).
    fn plan_call(
        &self,
        plan: &AssignPlan<'_>,
        name: &str,
        args: &[Expr],
        whole_value: bool,
        lines: &mut String,
        allocated: &mut Vec<(&'static str, u32)>,
    ) -> Option<String> {
        let callee = self.callee(plan.calls, name)?;
        let params = &callee.params;
        let class = &callee.qualified_class();
        let outputs: Vec<usize> = params
            .iter()
            .enumerate()
            .filter(|(_, p)| p.mode == ParamMode::Out)
            .map(|(i, _)| i)
            .collect();
        if outputs.len() != 1 {
            self.fail(format!(
                "call to '{name}' in an assignment: it must return exactly one value, and it has {} output parameters",
                outputs.len()
            ));
            return None;
        }
        let out_at = outputs[0];
        let out_type = to_csharp_type(&params[out_at].data_type);
        if whole_value && out_type != plan.target {
            self.fail(format!(
                "call to '{name}' in an assignment to '{}': its result is {out_type}, not {}",
                plan.cs_var, plan.target
            ));
            return None;
        }
        let Some(kind) = temp_type(&params[out_at].data_type) else {
            self.fail(format!(
                "call to '{name}' in an assignment to '{}': a {out_type} result has no temporary the SDK output shows",
                plan.cs_var
            ));
            return None;
        };
        let input_count = params.iter().filter(|p| p.mode != ParamMode::Out).count();
        if args.len() != input_count {
            self.fail(format!(
                "call to '{name}': {} argument(s) for {input_count} input parameter(s)",
                args.len()
            ));
            return None;
        }
        // The copy-in is the target itself when the temporary has the target's type. Otherwise a numeric
        // temporary starts at `0` (`GXt_decimal2 = 0;`, arusttypedcallprobe.cs line 79). No other initial
        // value of a temporary is evidenced, so a boolean temporary of another type is refused.
        let copy_in = if temp_cs_type(kind) == plan.target {
            plan.cs_var.to_string()
        } else if kind == "boolean" {
            self.fail(format!(
                "call to '{name}' in an assignment to '{}': a boolean temporary for a {} target has no evidenced initial value",
                plan.cs_var, plan.target
            ));
            return None;
        } else {
            "0".to_string()
        };
        let (temp, slot) = self.temps.borrow_mut().alloc(kind);
        allocated.push(slot);
        let _ = writeln!(lines, "{}{temp} = {copy_in};", plan.indent);
        self.call_temps
            .borrow_mut()
            .insert(name.as_ptr() as usize, temp.clone());
        let mut parts = Vec::with_capacity(params.len());
        let mut inputs = args.iter();
        for (index, param) in params.iter().enumerate() {
            if index == out_at {
                parts.push(format!("out {temp}"));
                continue;
            }
            let Some(arg) = inputs.next() else {
                self.fail(format!("call to '{name}': argument count changed"));
                return None;
            };
            match param.mode {
                ParamMode::In => {
                    for (nested, nested_args) in top_level_calls(arg) {
                        self.plan_call(plan, nested, nested_args, false, lines, allocated)?;
                    }
                    parts.push(self.input_argument(name, arg, &param.data_type, true)?);
                }
                _ => {
                    if !matches!(arg, Expr::Variable(_)) {
                        self.fail(format!(
                            "call to '{name}' in an assignment: an in-out argument that is not a variable needs a second temporary, which is not evidenced"
                        ));
                        return None;
                    }
                    parts.push(format!("ref {}", self.emit_expr(arg)));
                }
            }
        }
        if self.sdk_form() {
            let _ = writeln!(
                lines,
                "{}new {class}(context ).execute( {}) ;",
                plan.indent,
                sdk_arguments(&parts)
            );
        } else {
            let _ = writeln!(
                lines,
                "{}new {class}(context).execute({});",
                plan.indent,
                parts.join(", ")
            );
        }
        Some(temp)
    }
}

/// The C# field of a variable: `AV<Id><Name>`, the SDK's name (`AV8Hits`, dch37961700.cs), when the
/// stored id of the variable is known, with the name as declared; else `AV_<Name>`.
pub(crate) fn sdk_var_name(ids: Option<&BTreeMap<String, u32>>, name: &str) -> String {
    let bare = name.trim_start_matches('&');
    // The SDK's own variables keep their names (`Gxm2rootcol`, `GXt_objcol_...1`; sidebaritemsdp.cs).
    if ids.is_some() && (bare.starts_with("Gxm") || bare.starts_with("GXt_")) {
        return bare.to_string();
    }
    if let Some((declared, id)) =
        ids.and_then(|ids| ids.iter().find(|(k, _)| k.eq_ignore_ascii_case(bare)))
    {
        // The standard &Page and &Line are the runtime's fields (`Gx_page`, afrc12224800.cs); a variable
        // named `Gx...` keeps its name (`GxObject`, general/security/isauthorized.cs).
        if declared.starts_with("Gx") || declared.starts_with("GXt_") {
            return declared.clone();
        }
        return match declared.to_ascii_lowercase().as_str() {
            "page" if *id <= 7 => "Gx_page".to_string(),
            "line" if *id <= 7 => "Gx_line".to_string(),
            _ => format!("AV{id}{declared}"),
        };
    }
    to_csharp_var_name(name)
}

/// The SDK's call arguments: each value after a space, its mode before it (`ref  AV9Calls`,
/// `  (decimal)(1)`; arustcallpairprobe.cs, afrj12224800.cs).
fn sdk_arguments(parts: &[String]) -> String {
    parts
        .iter()
        .map(|part| match part.split_once(' ') {
            Some((keyword @ ("ref" | "out"), value)) => format!("{keyword}  {value}"),
            _ => format!(" {part}"),
        })
        .collect::<Vec<_>>()
        .join(", ")
}

/// Whether an expression is built from integer literals only, with `+`, `-`, `*` or `/`.
fn integer_literals_only(expr: &Expr) -> bool {
    match expr {
        Expr::Paren(inner) => integer_literals_only(inner),
        Expr::Integer(_) => true,
        Expr::Binary(
            left,
            BinOp::Add | BinOp::Subtract | BinOp::Multiply | BinOp::Divide,
            right,
        ) => integer_literals_only(left) && integer_literals_only(right),
        _ => false,
    }
}

/// The `(precision, scale)` the SDK gives a bare numeric message of an integer expression, where the
/// message is written with `StringUtil.LTrimStr`. Three SDK samples fix it, and only these shapes are
/// written: integer literals combined by `+`, `-` or `*` take the digit count of the widest literal and
/// scale 0 (`21*2` gives 2,0 in arustdisplayprobe.cs line 77; `1234567890+5` gives 10,0), and a division
/// of integer literals takes 15,5 (`7/3` in arustdisplayprobe2.cs). Any other numeric message is refused.
/// The rule is inferred from these samples, not from GeneXus documentation.
fn literal_message_format(expr: &Expr) -> Option<(u32, u32)> {
    fn digits(expr: &Expr) -> Option<u32> {
        match expr {
            Expr::Paren(inner) => digits(inner),
            Expr::Integer(value) => Some(value.unsigned_abs().to_string().len() as u32),
            Expr::Binary(left, BinOp::Add | BinOp::Subtract | BinOp::Multiply, right) => {
                Some(digits(left)?.max(digits(right)?))
            }
            _ => None,
        }
    }
    match expr {
        Expr::Paren(inner) => literal_message_format(inner),
        Expr::Binary(_, BinOp::Divide, _) if integer_literals_only(expr) => Some((15, 5)),
        Expr::Binary(_, BinOp::Add | BinOp::Subtract | BinOp::Multiply, _) => {
            digits(expr).map(|precision| (precision, 0))
        }
        _ => None,
    }
}
