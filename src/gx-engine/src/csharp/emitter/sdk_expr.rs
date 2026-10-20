//! The SDK's own form of a Procedure expression, for a class written with the SDK's variable names
//! (see `ProcedureClass::var_ids`).
//!
//! Every rule below is read from the SDK's output for the KBTeste Procedures (NetModel/Web), which
//! the rebuild compares byte for byte. An expression none of these rules covers is written by the
//! portable printer (`CSharpEmitter::emit_expr`): it still compiles, but it is not the SDK's text.
//!
//! - A call is `Name( a, b)`: a space after `(`, `, ` between arguments, none before `)`
//!   (`StringUtil.Substring( AV8C10, 1, 3)`, arusttextfuncprobe.cs).
//! - Arithmetic and concatenation have no spaces (`"nv_var=["+AV8N5+"]"`, `AV8I+1`), and a division
//!   casts its divisor (`AV9P52/ (decimal)(3)`, arustdisplayprobe.cs).
//! - An integer literal of ten digits or more is a `long` (`1234567890L`).
//! - A comparison of integers is `a == b`; of a decimal with an integer literal,
//!   `( a == Convert.ToDecimal( 1 ))`; of texts, `StringUtil.StrCmp(a, b) == 0`. Inside `&&` and `||`
//!   each comparison is `( a == b )` (adcm37961700.cs, arustedge7476154929200.cs).

use super::CSharpEmitter;
use crate::csharp::types::to_csharp_type;
use crate::frontend::{BinOp, Expr};
use crate::model::DataType;

/// The C# value class of an expression, as the SDK's casts and comparisons depend on it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum Class {
    Integer,
    Decimal,
    Text,
    Date,
    DateTime,
    Boolean,
    Other,
}

fn class_of(data_type: &DataType) -> Class {
    match data_type {
        DataType::Numeric { .. } => {
            if to_csharp_type(data_type) == "decimal" {
                Class::Decimal
            } else {
                Class::Integer
            }
        }
        DataType::Character { .. } | DataType::Varchar { .. } | DataType::LongVarchar { .. } => {
            Class::Text
        }
        DataType::Date {} => Class::Date,
        DataType::DateTime {} => Class::DateTime,
        DataType::Boolean {} => Class::Boolean,
        _ => Class::Other,
    }
}

fn comparison(op: BinOp) -> Option<&'static str> {
    Some(match op {
        BinOp::Equal => "==",
        BinOp::NotEqual => "!=",
        BinOp::Less => "<",
        BinOp::LessEqual => "<=",
        BinOp::Greater => ">",
        BinOp::GreaterEqual => ">=",
        _ => return None,
    })
}

/// `Name( a, b)`.
fn call(name: &str, args: &[String]) -> String {
    format!("{name}( {})", args.join(", "))
}

/// An integer literal, or its negation (`-42`): the SDK assigns either bare.
fn is_integer_literal(expr: &Expr) -> bool {
    match expr {
        Expr::Paren(inner) => is_integer_literal(inner),
        Expr::Integer(_) => true,
        Expr::Negate(inner) => matches!(inner.as_ref(), Expr::Integer(n) if *n >= 0),
        _ => false,
    }
}

/// The C# integer type of an integer literal, by its digits: 0 `short`, 1 `int`, 2 `long`.
fn literal_rank(expr: &Expr) -> u8 {
    match expr {
        Expr::Integer(n) => match n.unsigned_abs().to_string().len() {
            0..=4 => 0,
            5..=9 => 1,
            _ => 2,
        },
        Expr::Negate(inner) | Expr::Paren(inner) => literal_rank(inner),
        _ => 2,
    }
}

fn type_rank(cs_type: &str) -> u8 {
    match cs_type {
        "short" => 0,
        "int" => 1,
        _ => 2,
    }
}

/// Whether an integer literal is written `long` (`1234567890L`): ten digits or more.
fn is_long_literal(expr: &Expr) -> bool {
    match expr {
        Expr::Integer(n) => n.unsigned_abs().to_string().len() >= 10,
        Expr::Negate(inner) => is_long_literal(inner),
        _ => false,
    }
}

impl CSharpEmitter<'_> {
    /// Whether this emitter writes the SDK's form.
    pub(super) fn sdk_form(&self) -> bool {
        self.var_ids.is_some()
    }

    /// The value class of an expression, when this printer can tell it.
    pub(super) fn sdk_class(&self, expr: &Expr) -> Class {
        match expr {
            Expr::Paren(inner) => self.sdk_class(inner),
            Expr::Integer(_) => Class::Integer,
            Expr::Decimal(_) => Class::Decimal,
            Expr::Text(_) => Class::Text,
            Expr::Boolean(_) => Class::Boolean,
            Expr::TextLength(_) => Class::Integer,
            Expr::Negate(inner) => self.sdk_class(inner),
            Expr::Not(_) => Class::Boolean,
            Expr::Binary(left, op, right) => {
                if comparison(*op).is_some() || matches!(op, BinOp::And | BinOp::Or) {
                    return Class::Boolean;
                }
                let (l, r) = (self.sdk_class(left), self.sdk_class(right));
                match op {
                    BinOp::Add if l == Class::Text || r == Class::Text => Class::Text,
                    BinOp::Divide => Class::Decimal,
                    _ if l == Class::Decimal || r == Class::Decimal => Class::Decimal,
                    _ if l == Class::Integer && r == Class::Integer => Class::Integer,
                    _ => Class::Other,
                }
            }
            Expr::DateMethod(method, _) => match method.to_ascii_uppercase().as_str() {
                "DAY" | "MONTH" | "YEAR" | "HOUR" | "MINUTE" | "SEC" | "SECOND" | "DOW" => {
                    Class::Integer
                }
                "ISEMPTY" => Class::Boolean,
                _ => Class::Other,
            },
            Expr::Call(name, _) => match name.to_ascii_uppercase().as_str() {
                "TRIM" | "LTRIM" | "RTRIM" | "STR" | "SUBSTR" | "UPPER" | "LOWER"
                | "STRREPLACE" | "PADL" | "PADR" | "CONCAT" => Class::Text,
                "LEN" | "YEAR" | "MONTH" | "DAY" => Class::Integer,
                "YMDTOD" => Class::Date,
                "YMDHMSTOT" => Class::DateTime,
                "TOSTRING" => Class::Text,
                _ => Class::Other,
            },
            _ => self
                .value_type(expr)
                .as_ref()
                .map_or(Class::Other, class_of),
        }
    }

    /// An expression in the SDK's form, or `None` when no rule here covers it.
    pub(super) fn sdk_value(&self, expr: &Expr) -> Option<String> {
        Some(match expr {
            Expr::Integer(n) => {
                if n.unsigned_abs().to_string().len() >= 10 {
                    format!("{n}L")
                } else {
                    n.to_string()
                }
            }
            Expr::Decimal(_) | Expr::Text(_) | Expr::Boolean(_) | Expr::Variable(_) => {
                self.emit_expr_portable(expr)
            }
            Expr::Member(..) => self.emit_expr_portable(expr),
            Expr::Paren(inner) => format!("({})", self.sdk_value(inner)?),
            // An item index is bare when it is an integer, rounded when it is a decimal
            // (`Item((int)(Math.Round(AV10I, 18, MidpointRounding.ToEven)))`, artm91046200.cs).
            Expr::CollectionItem(col, idx) => {
                let collection = self.emit_expr(col);
                let index = match self.sdk_class(idx) {
                    Class::Integer => self.sdk_value(idx)?,
                    Class::Decimal => format!(
                        "(int)(Math.Round({}, 18, MidpointRounding.ToEven))",
                        self.sdk_value(idx)?
                    ),
                    _ => return None,
                };
                match self.resolve(expr) {
                    Ok(super::SdtMemberType::Class { class, .. }) => {
                        format!("(({class}){collection}.Item({index}))")
                    }
                    _ => return None,
                }
            }
            // A negated sum or negation is parenthesised (`-(-7)`, arustedge7476154929200.cs); a value is not
            // (`-AV8First.gxTpr_Amount`).
            Expr::Negate(inner) => match inner.as_ref() {
                Expr::Negate(_) | Expr::Binary(..) => format!("-({})", self.sdk_value(inner)?),
                Expr::Integer(n) if *n < 0 => format!("-({})", self.sdk_value(inner)?),
                _ => format!("-{}", self.sdk_value(inner)?),
            },
            Expr::TextLength(inner) => call("StringUtil.Len", &[self.sdk_value(inner)?]),
            Expr::Binary(left, BinOp::Add, right)
                if self.sdk_class(left) == Class::Text || self.sdk_class(right) == Class::Text =>
            {
                format!(
                    "{}+{}",
                    self.sdk_concat_operand(left)?,
                    self.sdk_concat_operand(right)?
                )
            }
            Expr::Binary(left, op, right)
                if matches!(self.sdk_class(left), Class::Date | Class::DateTime)
                    || matches!(self.sdk_class(right), Class::Date | Class::DateTime) =>
            {
                // Only a Date plus an integer literal is evidenced: `DateTimeUtil.DAdd(AV12D,+((int)(1)))`.
                match (op, right.as_ref(), self.sdk_class(left)) {
                    (BinOp::Add, Expr::Integer(n), Class::Date) => {
                        format!("DateTimeUtil.DAdd({},+((int)({n})))", self.sdk_value(left)?)
                    }
                    _ => return None,
                }
            }
            Expr::Binary(left, op, right) => {
                let l = self.sdk_operand(left)?;
                let r = self.sdk_operand(right)?;
                match op {
                    BinOp::Add => format!("{l}+{r}"),
                    BinOp::Subtract => format!("{l}-{r}"),
                    BinOp::Multiply => {
                        format!("{}*{}", self.sdk_factor(left)?, self.sdk_factor(right)?)
                    }
                    BinOp::Divide => format!("{}/ (decimal)({r})", self.sdk_factor(left)?),
                    _ => return None,
                }
            }
            Expr::DateMethod(method, target) => {
                let t = self.sdk_value(target)?;
                match method.to_ascii_uppercase().as_str() {
                    "DAY" => call("DateTimeUtil.Day", &[t]),
                    "MONTH" => call("DateTimeUtil.Month", &[t]),
                    "YEAR" => call("DateTimeUtil.Year", &[t]),
                    "HOUR" => call("DateTimeUtil.Hour", &[t]),
                    "MINUTE" => call("DateTimeUtil.Minute", &[t]),
                    "SEC" | "SECOND" => call("DateTimeUtil.Second", &[t]),
                    "ISEMPTY"
                        if matches!(self.sdk_class(target), Class::Date | Class::DateTime) =>
                    {
                        format!("(DateTime.MinValue=={t})")
                    }
                    _ => return None,
                }
            }
            Expr::Call(name, args) => {
                let arg = |i: usize| self.sdk_value(&args[i]);
                match (name.to_ascii_uppercase().as_str(), args.len()) {
                    ("TRIM", 1) => call("StringUtil.Trim", &[arg(0)?]),
                    ("LTRIM", 1) => call("StringUtil.LTrim", &[arg(0)?]),
                    ("RTRIM", 1) => call("StringUtil.RTrim", &[arg(0)?]),
                    ("UPPER", 1) => call("StringUtil.Upper", &[arg(0)?]),
                    ("LOWER", 1) => call("StringUtil.Lower", &[arg(0)?]),
                    ("LEN", 1) => call("StringUtil.Len", &[arg(0)?]),
                    ("SUBSTR", 3) => call("StringUtil.Substring", &[arg(0)?, arg(1)?, arg(2)?]),
                    ("STRREPLACE", 3) => {
                        call("StringUtil.StringReplace", &[arg(0)?, arg(1)?, arg(2)?])
                    }
                    // `Str(x)` is `Str(x, 10, 0)`, `Str(x, n)` is `Str(x, n, 0)`
                    // (arustdisplaysyntaxprobe.cs: `StringUtil.Str( (decimal)(AV8N5), 10, 0)`).
                    ("STR", 1..=3) => call(
                        "StringUtil.Str",
                        &[
                            self.sdk_as_decimal(&args[0])?,
                            if args.len() > 1 { arg(1)? } else { "10".into() },
                            if args.len() > 2 { arg(2)? } else { "0".into() },
                        ],
                    ),
                    ("YMDTOD", 3) => call("context.localUtil.YMDToD", &[arg(0)?, arg(1)?, arg(2)?]),
                    ("YMDHMSTOT", 6) => call(
                        "context.localUtil.YMDHMSToT",
                        &[arg(0)?, arg(1)?, arg(2)?, arg(3)?, arg(4)?, arg(5)?],
                    ),
                    ("TOSTRING", 1) => self.sdk_to_string(&args[0])?,
                    ("YEAR", 1) => call("DateTimeUtil.Year", &[arg(0)?]),
                    ("MONTH", 1) => call("DateTimeUtil.Month", &[arg(0)?]),
                    ("DAY", 1) => call("DateTimeUtil.Day", &[arg(0)?]),
                    // `iif(c, a, b)`: `((DateTime.MinValue==AV8Stamp) ? "true" : "false")` (aodm42412500.cs).
                    ("IIF", 3) => format!(
                        "({} ? {} : {})",
                        self.sdk_value(&args[0])
                            .or_else(|| self.sdk_condition(&args[0]))?,
                        arg(1)?,
                        arg(2)?
                    ),
                    _ if !crate::csharp::calls::is_builtin_name(name) => {
                        self.emit_call_expression(name, args)
                    }
                    _ => return None,
                }
            }
            _ => return None,
        })
    }

    /// An operand of `+` or `-`, as the source wrote it (its parentheses are `Expr::Paren`).
    fn sdk_operand(&self, expr: &Expr) -> Option<String> {
        self.sdk_value(expr)
    }

    /// An operand of a text concatenation: its parentheses dropped (`"ne_var_add=["+AV8N5+0+"]"`), a
    /// boolean as `true`/`false` (`((DateTime.MinValue==AV8Stamp) ? "true" : "false")`, aodm42412500.cs).
    fn sdk_concat_operand(&self, expr: &Expr) -> Option<String> {
        match expr {
            Expr::Paren(inner) => self.sdk_concat_operand(inner),
            _ if self.sdk_class(expr) == Class::Boolean => Some(format!(
                "({} ? \"true\" : \"false\")",
                self.sdk_value(expr)?
            )),
            _ => self.sdk_value(expr),
        }
    }

    /// An operand of `*` or `/`: a sum is parenthesised, so that the C# keeps the source's meaning.
    fn sdk_factor(&self, expr: &Expr) -> Option<String> {
        let text = self.sdk_value(expr)?;
        Some(match expr {
            Expr::Binary(_, BinOp::Add | BinOp::Subtract, _) => format!("({text})"),
            _ => text,
        })
    }

    /// A numeric value as `Str` takes it: a decimal bare, anything else cast (`StringUtil.Str(
    /// (decimal)(AV8N5), 5, 0)`, `StringUtil.Str( AV8Hits, 5, 2)`).
    fn sdk_as_decimal(&self, expr: &Expr) -> Option<String> {
        let text = self.sdk_value(expr)?;
        Some(if self.sdk_class(expr) == Class::Decimal {
            text
        } else {
            format!("(decimal)({text})")
        })
    }

    /// `ToString()`: `Str` with the value's precision for a number, the KB's date formats for a date.
    fn sdk_to_string(&self, operand: &Expr) -> Option<String> {
        let text = self.sdk_value(operand)?;
        if self.sdk_class(operand) == Class::Boolean {
            return Some(call("StringUtil.BoolToStr", &[text]));
        }
        Some(match self.value_type(operand)? {
            DataType::Date {} => call(
                "context.localUtil.DToC",
                &[text, "1".into(), "\"/\"".into()],
            ),
            DataType::DateTime {} => call(
                "context.localUtil.TToC",
                &[
                    text,
                    "8".into(),
                    "5".into(),
                    "1".into(),
                    "2".into(),
                    "\"/\"".into(),
                    "\":\"".into(),
                    "\" \"".into(),
                ],
            ),
            DataType::Numeric { precision, scale } => call(
                "StringUtil.Str",
                &[
                    self.sdk_as_decimal(operand)?,
                    precision.to_string(),
                    scale.to_string(),
                ],
            ),
            _ => return None,
        })
    }

    /// The right side of an assignment to a variable of `target`: the SDK casts a computed number to
    /// the target's type, and writes a literal or a value of the same type bare (`AV8N5 = 42;`,
    /// `AV9P52 = (decimal)(7);`, `AV8I = (int)(AV8I+1);`).
    pub(super) fn sdk_assigned(&self, target: &DataType, expr: &Expr) -> Option<String> {
        if matches!(target, DataType::Boolean {}) {
            if let Expr::Binary(left, op @ (BinOp::And | BinOp::Or), right) = expr {
                // `AV9Flag = (bool)(GXt_boolean3||GXt_boolean4);` (arusttypedcallprobe.cs).
                let joiner = if *op == BinOp::And { "&&" } else { "||" };
                return Some(format!(
                    "(bool)({}{joiner}{})",
                    self.sdk_value(left)?,
                    self.sdk_value(right)?
                ));
            }
            if let Expr::Binary(left, op, right) = expr {
                if let Some(symbol) = comparison(*op) {
                    // `(bool)((AV8Value>Convert.ToDecimal(0)))` (rustcallpositiveprobe.cs).
                    let l = self.sdk_value(left)?;
                    let r = self.sdk_value(right)?;
                    let r = if self.sdk_class(left) == Class::Decimal && is_integer_literal(right) {
                        format!("Convert.ToDecimal({r})")
                    } else {
                        r
                    };
                    return Some(format!("(bool)(({l}{symbol}{r}))"));
                }
            }
        }
        let text = self.sdk_value(expr)?;
        if !matches!(target, DataType::Numeric { .. }) {
            return Some(text);
        }
        let cs_type = to_csharp_type(target);
        let target_class = class_of(target);
        let bare = match expr {
            // Zero is assigned bare to a decimal too (`AV9Hits = 0;`, adcs37961700.cs).
            Expr::Integer(0) => true,
            // A literal is bare unless its own type is wider than the target's: up to four digits it is a
            // `short`, up to nine an `int`, then a `long` (`AV21V4_0 = (short)(10000);`,
            // `AV32V9_0 = (int)(1000000000L);`, `AV20V3_0 = 1000;`; arustnumericrenderprobe.cs).
            _ if is_integer_literal(expr) => {
                target_class == Class::Integer && literal_rank(expr) <= type_rank(&cs_type)
            }
            Expr::Decimal(_) => target_class == Class::Decimal,
            Expr::Negate(inner) if matches!(inner.as_ref(), Expr::Decimal(_)) => {
                target_class == Class::Decimal
            }

            Expr::Variable(_) | Expr::Member(..) => self
                .value_type(expr)
                .is_some_and(|source| to_csharp_type(&source) == cs_type),
            _ => false,
        };
        Some(if bare {
            text
        } else {
            format!("({cs_type})({text})")
        })
    }

    /// A condition in the SDK's form (the text inside `if ( … )`), or `None`.
    pub(super) fn sdk_condition(&self, expr: &Expr) -> Option<String> {
        match expr {
            Expr::Paren(inner) => self.sdk_condition(inner),
            Expr::Binary(left, op @ (BinOp::And | BinOp::Or), right) => {
                let joiner = if *op == BinOp::And { "&&" } else { "||" };
                Some(format!(
                    "{} {joiner} {}",
                    self.sdk_logical_operand(left)?,
                    self.sdk_logical_operand(right)?
                ))
            }
            Expr::Binary(left, op, right) if comparison(*op).is_some() => {
                self.sdk_comparison(left, *op, right, false)
            }
            Expr::Not(inner) => Some(format!("! {}", self.sdk_logical_operand(inner)?)),
            _ if self.sdk_class(expr) == Class::Boolean => self.sdk_value(expr),
            _ => None,
        }
    }

    /// An operand of `&&`, `||` or `!`: a comparison wrapped as `( a == b )`.
    fn sdk_logical_operand(&self, expr: &Expr) -> Option<String> {
        match expr {
            // A parenthesised condition is written `( … )` around its operand form:
            // `( ( 1 == 1 ) || ( 1 == 2 ) ) && ( 2 == 3 )`, `! ( ( 1 == 2 ) )` (arustedge7476154929200.cs).
            Expr::Paren(inner) => match inner.as_ref() {
                Expr::Binary(_, BinOp::And | BinOp::Or, _) => {
                    Some(format!("( {} )", self.sdk_condition(inner)?))
                }
                _ => Some(format!("( {} )", self.sdk_logical_operand(inner)?)),
            },
            Expr::Binary(left, op, right) if comparison(*op).is_some() => {
                self.sdk_comparison(left, *op, right, true)
            }
            Expr::Binary(_, BinOp::And | BinOp::Or, _) => self.sdk_condition(expr),
            Expr::Not(_) => self.sdk_condition(expr),
            _ => self.sdk_value(expr),
        }
    }

    fn sdk_comparison(
        &self,
        left: &Expr,
        op: BinOp,
        right: &Expr,
        wrapped: bool,
    ) -> Option<String> {
        let symbol = comparison(op)?;
        let (lc, rc) = (self.sdk_class(left), self.sdk_class(right));
        let l = match left {
            // A division compared is written `10 /  ( decimal )( AV12x )` (arustedge7476154929200.cs).
            Expr::Binary(dividend, BinOp::Divide, divisor) => format!(
                "{} /  ( decimal )( {} )",
                self.sdk_value(dividend)?,
                self.sdk_value(divisor)?
            ),
            _ => self.sdk_value(left)?,
        };
        let r = self.sdk_value(right)?;
        if lc == Class::Text || rc == Class::Text {
            return Some(format!("StringUtil.StrCmp({l}, {r}) {symbol} 0"));
        }
        if lc == Class::Decimal && is_integer_literal(right) {
            return Some(format!("( {l} {symbol} Convert.ToDecimal( {r} ))"));
        }
        let numeric = |c: Class| matches!(c, Class::Integer | Class::Decimal | Class::Other);
        if numeric(lc) && numeric(rc) {
            return Some(if wrapped {
                format!("( {l} {symbol} {r} )")
            } else {
                format!("{l} {symbol} {r}")
            });
        }
        None
    }
}

impl CSharpEmitter<'_> {
    /// A numeric message: `StringUtil.LTrimStr( (decimal)((double)(21*2)), 2, 0)`, and without the
    /// `double` for a `long` (`(decimal)(1234567890L+5)`; arustdisplayprobe.cs, arustdisplayprobe2.cs).
    pub(super) fn sdk_numeric_message(
        &self,
        expr: &Expr,
        precision: u32,
        scale: u32,
    ) -> Option<String> {
        let text = self.sdk_value(expr)?;
        let cast = if long_operand(expr) {
            format!("(decimal)({text})")
        } else {
            format!("(decimal)((double)({text}))")
        };
        Some(format!(
            "StringUtil.LTrimStr( {cast}, {precision}, {scale})"
        ))
    }
}

fn long_operand(expr: &Expr) -> bool {
    match expr {
        Expr::Binary(left, _, right) => long_operand(left) || long_operand(right),
        Expr::Negate(inner) => long_operand(inner),
        _ => is_long_literal(expr),
    }
}
