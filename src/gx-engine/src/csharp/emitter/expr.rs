//! The portable printer of expressions and conditions.

use super::literal_shapes::integer_literals_only;
use super::*;

impl<'a> CSharpEmitter<'a> {
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
    pub(super) fn emit_condition(&self, expr: &Expr) -> String {
        if self.sdk_form() {
            if let Some(text) = self.sdk_condition(expr) {
                return text;
            }
        }
        self.emit_expr_portable(expr)
    }

    /// Emits a C# expression in the portable form.
    pub(super) fn emit_expr_portable(&self, expr: &Expr) -> String {
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
}
