//! C# syntax emitter for expressions and statements.

use crate::{
    csharp::types::{to_csharp_attr_name, to_csharp_type, to_csharp_var_name},
    frontend::{BinOp, Expr, Stmt},
    model::DataType,
};
use std::collections::BTreeMap;

/// Configurable C# code generator and indentation manager.
pub struct CSharpEmitter<'a> {
    indent_level: usize,
    indent_unit: String,
    vars: Option<&'a BTreeMap<String, DataType>>,
}

impl Default for CSharpEmitter<'_> {
    fn default() -> Self {
        Self::new()
    }
}

impl<'a> CSharpEmitter<'a> {
    /// Creates a new emitter using the classic GeneXus 3-space indentation.
    pub fn new() -> Self {
        Self {
            indent_level: 0,
            indent_unit: "   ".to_string(),
            vars: None,
        }
    }

    /// Creates an emitter with a variable context for case-insensitive identifier preservation.
    pub fn with_vars(vars: &'a BTreeMap<String, DataType>) -> Self {
        Self {
            indent_level: 0,
            indent_unit: "   ".to_string(),
            vars: Some(vars),
        }
    }

    /// Creates an emitter with custom indentation unit.
    pub fn with_indent_unit(unit: impl Into<String>) -> Self {
        Self {
            indent_level: 0,
            indent_unit: unit.into(),
            vars: None,
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

    /// Emits a C# expression string.
    pub fn emit_expr(&self, expr: &Expr) -> String {
        match expr {
            Expr::Integer(val) => val.to_string(),
            Expr::Decimal(s) => {
                if s.ends_with('m') || s.ends_with('M') {
                    s.clone()
                } else {
                    format!("{s}m")
                }
            }
            Expr::Text(s) => {
                let mut out = String::with_capacity(s.len() + 2);
                out.push('"');
                for ch in s.chars() {
                    match ch {
                        '\\' => out.push_str("\\\\"),
                        '"' => out.push_str("\\\""),
                        '\n' => out.push_str("\\n"),
                        '\r' => out.push_str("\\r"),
                        '\t' => out.push_str("\\t"),
                        c => out.push(c),
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
                    if let Some((k, _)) = vars.iter().find(|(k, _)| {
                        k.eq_ignore_ascii_case(lookup) || k.eq_ignore_ascii_case(name)
                    }) {
                        return to_csharp_var_name(k);
                    }
                }
                to_csharp_var_name(name)
            }
            Expr::Attribute(name) => to_csharp_attr_name(name),
            Expr::Member(target, field) => format!("{}.{}", self.emit_expr(target), field),
            Expr::CloneRecord(inner) => format!("{}.Clone()", self.emit_expr(inner)),
            Expr::CollectionItem(col, idx) => {
                format!(
                    "{}.Item((int)({}))",
                    self.emit_expr(col),
                    self.emit_expr(idx)
                )
            }
            Expr::DateMethod(method, target) => match method.to_ascii_uppercase().as_str() {
                "DAY" => format!("DateTimeUtil.Day({})", self.emit_expr(target)),
                "MONTH" => format!("DateTimeUtil.Month({})", self.emit_expr(target)),
                "YEAR" => format!("DateTimeUtil.Year({})", self.emit_expr(target)),
                "HOUR" => format!("DateTimeUtil.Hour({})", self.emit_expr(target)),
                "MINUTE" => format!("DateTimeUtil.Minute({})", self.emit_expr(target)),
                "SEC" => format!("DateTimeUtil.Second({})", self.emit_expr(target)),
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
                let left_s = self.emit_expr(left);
                let right_s = self.emit_expr(right);
                match op {
                    BinOp::And => format!("({left_s} && {right_s})"),
                    BinOp::Or => format!("({left_s} || {right_s})"),
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
                    format!("DateTimeUtil.YMDToD((int)({y}), (int)({m}), (int)({d}))")
                }
                "YMDHMSTOT" if args.len() >= 6 => {
                    let y = self.emit_expr(&args[0]);
                    let m = self.emit_expr(&args[1]);
                    let d = self.emit_expr(&args[2]);
                    let h = self.emit_expr(&args[3]);
                    let mi = self.emit_expr(&args[4]);
                    let s = self.emit_expr(&args[5]);
                    format!(
                        "DateTimeUtil.YMDHMSToT((int)({y}), (int)({m}), (int)({d}), (int)({h}), (int)({mi}), (int)({s}))"
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
                _ => {
                    let args_str = args
                        .iter()
                        .map(|a| self.emit_expr(a))
                        .collect::<Vec<_>>()
                        .join(", ");
                    format!("new {name}(context).executeUdp({args_str})")
                }
            },
        }
    }

    /// Emits a C# statement string with indentation and trailing newline.
    pub fn emit_stmt(&mut self, stmt: &Stmt, vars: &BTreeMap<String, DataType>) -> String {
        let indent = self.indent();
        match stmt {
            Stmt::Assign(var, expr) => {
                let lookup_key = var.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(var)
                });
                let cs_var = if let Some((k, _)) = matched_var {
                    to_csharp_var_name(k)
                } else {
                    to_csharp_var_name(var)
                };
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
                let t = self.emit_expr(target);
                let e = self.emit_expr(expr);
                format!("{indent}{t}.{field} = {e};\n")
            }
            Stmt::CollectionAdd(col, item, opt_pos) => {
                let c = self.emit_expr(col);
                let i = self.emit_expr(item);
                if let Some(pos) = opt_pos {
                    let p = self.emit_expr(pos);
                    format!("{indent}{c}.Add({i}, {p});\n")
                } else {
                    format!("{indent}{c}.Add({i});\n")
                }
            }
            Stmt::CollectionRemove(col, index) => {
                let c = self.emit_expr(col);
                let idx = self.emit_expr(index);
                format!("{indent}{c}.RemoveItem((int)({idx}));\n")
            }
            Stmt::CollectionClear(col) => {
                let c = self.emit_expr(col);
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
                let e = self.emit_expr(expr);
                format!("{indent}context.StatusMessage( {e} );\n")
            }
            Stmt::Return(expr) => {
                let e = self.emit_expr(expr);
                format!("{indent}AV_Result = {e};\n{indent}cleanup();\n{indent}return;\n")
            }
            Stmt::ReturnVoid => {
                format!("{indent}cleanup();\n{indent}return;\n")
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
                let c = self.emit_expr(cond);
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
                let cs_var = if let Some((k, _)) = matched_var {
                    to_csharp_var_name(k)
                } else {
                    to_csharp_var_name(var)
                };
                let s = self.emit_expr(start);
                let e = self.emit_expr(end);
                let cmp = if *stride >= 0 { "<=" } else { ">=" };
                let step = if *stride == 1 {
                    format!("{cs_var}++")
                } else if *stride == -1 {
                    format!("{cs_var}--")
                } else if *stride >= 0 {
                    format!("{cs_var} += {stride}")
                } else {
                    format!("{cs_var} -= {}", -stride)
                };
                let mut out = format!(
                    "{indent}for ( {cs_var} = {s}; {cs_var} {cmp} {e}; {step} )\n{indent}{{\n"
                );
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::ForIn(item, col, body) => {
                let lookup_key = item.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(item)
                });
                let cs_item = if let Some((k, _)) = matched_var {
                    to_csharp_var_name(k)
                } else {
                    to_csharp_var_name(item)
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
