//! DataSelector AST, parser, and query composition engine.
//! Supports `using DataSelectorName(&args...)` composition with `For Each` navigation.

use std::collections::BTreeMap;

/// A parameter inside a DataSelector definition.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Parameter {
    pub name: String,
    pub mode: crate::model::ParamMode,
}

impl Parameter {
    pub fn new(name: impl Into<String>, mode: crate::model::ParamMode) -> Self {
        Self {
            name: name.into(),
            mode,
        }
    }
}

pub type DataSelectorParameter = Parameter;

/// A GeneXus DataSelector object encapsulating parameters, orders, and filters.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataSelector {
    pub name: String,
    pub parameters: Vec<Parameter>,
    pub orders: Vec<crate::frontend::ForEachOrder>,
    pub wheres: Vec<crate::frontend::Expr>,
}

impl DataSelector {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            parameters: Vec::new(),
            orders: Vec::new(),
            wheres: Vec::new(),
        }
    }

    pub fn compose_with(
        &self,
        for_each: &crate::frontend::ForEachStmt,
        args: &[crate::frontend::Expr],
    ) -> Result<crate::frontend::ForEachStmt, String> {
        compose_data_selector(for_each, self, args)
    }
}

/// Holds the resulting `ForEachStmt` after composition with a DataSelector.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComposedForEach {
    pub for_each: crate::frontend::ForEachStmt,
}

impl ComposedForEach {
    pub fn new(for_each: crate::frontend::ForEachStmt) -> Self {
        Self { for_each }
    }

    pub fn compose(
        for_each: &crate::frontend::ForEachStmt,
        selector: &DataSelector,
        args: &[crate::frontend::Expr],
    ) -> Result<Self, String> {
        let stmt = compose_data_selector(for_each, selector, args)?;
        Ok(Self { for_each: stmt })
    }

    pub fn stmt(&self) -> &crate::frontend::ForEachStmt {
        &self.for_each
    }

    pub fn into_inner(self) -> crate::frontend::ForEachStmt {
        self.for_each
    }
}

impl std::ops::Deref for ComposedForEach {
    type Target = crate::frontend::ForEachStmt;

    fn deref(&self) -> &Self::Target {
        &self.for_each
    }
}

/// Recursively walks AST expressions and substitutes `Expr::Variable(var_name)`
/// if `var_name` matches a parameter in `param_map`.
pub fn substitute_parameters(
    expr: &crate::frontend::Expr,
    param_map: &BTreeMap<String, crate::frontend::Expr>,
) -> crate::frontend::Expr {
    use crate::frontend::Expr;
    match expr {
        Expr::Paren(inner) => Expr::Paren(Box::new(substitute_parameters(inner, param_map))),
        Expr::Variable(var_name) => {
            let key = var_name
                .strip_prefix('&')
                .unwrap_or(var_name)
                .to_ascii_uppercase();
            if let Some(replacement) = param_map.get(&key) {
                replacement.clone()
            } else {
                Expr::Variable(var_name.clone())
            }
        }
        Expr::Binary(left, op, right) => Expr::Binary(
            Box::new(substitute_parameters(left, param_map)),
            *op,
            Box::new(substitute_parameters(right, param_map)),
        ),
        Expr::Not(inner) => Expr::Not(Box::new(substitute_parameters(inner, param_map))),
        Expr::Negate(inner) => Expr::Negate(Box::new(substitute_parameters(inner, param_map))),
        Expr::Call(name, args) => Expr::Call(
            name.clone(),
            args.iter()
                .map(|arg| substitute_parameters(arg, param_map))
                .collect(),
        ),
        Expr::Member(receiver, member) => Expr::Member(
            Box::new(substitute_parameters(receiver, param_map)),
            member.clone(),
        ),
        Expr::CloneRecord(inner) => {
            Expr::CloneRecord(Box::new(substitute_parameters(inner, param_map)))
        }
        Expr::CollectionItem(coll, index) => Expr::CollectionItem(
            Box::new(substitute_parameters(coll, param_map)),
            Box::new(substitute_parameters(index, param_map)),
        ),
        Expr::TextLength(inner) => {
            Expr::TextLength(Box::new(substitute_parameters(inner, param_map)))
        }
        Expr::DateMethod(method, inner) => Expr::DateMethod(
            method.clone(),
            Box::new(substitute_parameters(inner, param_map)),
        ),
        Expr::DateShift(method, date_expr, shift_expr) => Expr::DateShift(
            method.clone(),
            Box::new(substitute_parameters(date_expr, param_map)),
            Box::new(substitute_parameters(shift_expr, param_map)),
        ),
        Expr::Integer(val) => Expr::Integer(*val),
        Expr::Decimal(val) => Expr::Decimal(val.clone()),
        Expr::Text(val) => Expr::Text(val.clone()),
        Expr::Boolean(val) => Expr::Boolean(*val),
        Expr::EmptyDate => Expr::EmptyDate,
        Expr::Attribute(att) => Expr::Attribute(att.clone()),
    }
}

/// Composes a DataSelector into a `For Each` statement:
/// - Validates that argument count matches parameter count.
/// - Builds parameter substitution map.
/// - Substitutes parameters in all DataSelector WHERE clauses.
/// - Merges selector WHEREs into `for_each.wheres`.
/// - Merges selector orders into `for_each.orders`.
/// - Returns a new `ForEachStmt` with combined clauses.
pub fn compose_data_selector(
    for_each: &crate::frontend::ForEachStmt,
    selector: &DataSelector,
    args: &[crate::frontend::Expr],
) -> Result<crate::frontend::ForEachStmt, String> {
    if args.len() != selector.parameters.len() {
        return Err(format!(
            "DataSelector '{}' expects {} arguments, but got {}",
            selector.name,
            selector.parameters.len(),
            args.len()
        ));
    }

    let mut param_map = BTreeMap::new();
    for (param, arg) in selector.parameters.iter().zip(args.iter()) {
        let key = param
            .name
            .strip_prefix('&')
            .unwrap_or(&param.name)
            .to_ascii_uppercase();
        param_map.insert(key, arg.clone());
    }

    let mut combined_wheres = for_each.wheres.clone();
    for where_expr in &selector.wheres {
        combined_wheres.push(substitute_parameters(where_expr, &param_map));
    }

    let mut combined_orders = for_each.orders.clone();
    for order in &selector.orders {
        if !combined_orders
            .iter()
            .any(|o| o.attribute.eq_ignore_ascii_case(&order.attribute))
        {
            combined_orders.push(order.clone());
        }
    }

    Ok(crate::frontend::ForEachStmt {
        explicit_attributes: for_each.explicit_attributes.clone(),
        using: for_each.using.clone(),
        wheres: combined_wheres,
        orders: combined_orders,
        defined_by: for_each.defined_by.clone(),
        body: for_each.body.clone(),
        when_none: for_each.when_none.clone(),
    })
}

/// Parses a DataSelector from its Rules source and Conditions source:
/// - `rules_source`: parsed for `Parm(...)` rules specifying parameters and modes.
/// - `conditions_source`: lines of `Order ...;` and `Where ...;`.
pub fn parse_data_selector(
    name: &str,
    rules_source: &str,
    conditions_source: &str,
) -> Result<DataSelector, String> {
    let trimmed_name = name.trim();
    if trimmed_name.is_empty() {
        return Err("DataSelector name cannot be empty".into());
    }
    if !crate::model::is_identifier(trimmed_name) {
        return Err(format!(
            "DataSelector name '{trimmed_name}' must be a valid identifier"
        ));
    }

    let mut parameters = Vec::new();
    let trimmed_rules = rules_source.trim();
    if !trimmed_rules.is_empty() {
        let rules_input = if !trimmed_rules.ends_with(';') {
            format!("{trimmed_rules};")
        } else {
            trimmed_rules.to_string()
        };
        let rules = crate::rules::parse_rules(&rules_input)?;
        for rule in rules {
            if let crate::rules::Rule::Parm(args) = rule {
                if !parameters.is_empty() {
                    return Err("duplicate Parm rule in DataSelector".into());
                }
                for arg in args {
                    let mode = match arg.mode.to_ascii_uppercase().as_str() {
                        "IN" => crate::model::ParamMode::In,
                        "OUT" => crate::model::ParamMode::Out,
                        "INOUT" => crate::model::ParamMode::InOut,
                        other => return Err(format!("unknown parameter mode '{other}'")),
                    };
                    let param_name = arg.name.strip_prefix('&').unwrap_or(&arg.name).to_string();
                    parameters.push(Parameter {
                        name: param_name,
                        mode,
                    });
                }
            }
        }
    }

    let mut orders = Vec::new();
    let mut wheres = Vec::new();
    let statements = split_condition_statements(conditions_source)?;

    for stmt in statements {
        let stmt_trimmed = stmt.trim();
        if stmt_trimmed.is_empty() {
            continue;
        }
        if starts_with_keyword(stmt_trimmed, "ORDER") {
            let rest = stmt_trimmed[5..].trim();
            parse_order_clause(rest, &mut orders)?;
        } else if starts_with_keyword(stmt_trimmed, "WHERE") {
            let rest = stmt_trimmed[5..].trim();
            if rest.is_empty() {
                return Err("Where condition cannot be empty".into());
            }
            let expr = crate::frontend::parse_expression_with_attributes(rest)?;
            wheres.push(expr);
        } else {
            return Err(format!(
                "expected 'Order' or 'Where' statement in DataSelector conditions, found '{stmt_trimmed}'"
            ));
        }
    }

    Ok(DataSelector {
        name: trimmed_name.to_string(),
        parameters,
        orders,
        wheres,
    })
}

fn starts_with_keyword(s: &str, kw: &str) -> bool {
    let kw_len = kw.len();
    if s.len() < kw_len {
        return false;
    }
    if !s[..kw_len].eq_ignore_ascii_case(kw) {
        return false;
    }
    if s.len() == kw_len {
        return true;
    }
    let next_char = s.as_bytes()[kw_len];
    next_char.is_ascii_whitespace() || next_char == b'(' || next_char == b':'
}

fn parse_order_clause(
    rest: &str,
    orders: &mut Vec<crate::frontend::ForEachOrder>,
) -> Result<(), String> {
    let unparenthesized = if rest.starts_with('(') && rest.ends_with(')') && rest.len() >= 2 {
        rest[1..rest.len() - 1].trim()
    } else {
        rest
    };
    if unparenthesized.is_empty() {
        return Err("Order clause requires at least one attribute or 'none'".into());
    }
    for item in unparenthesized.split(',') {
        let trimmed_item = item.trim();
        if trimmed_item.is_empty() || trimmed_item.eq_ignore_ascii_case("none") {
            continue;
        }
        let parts: Vec<&str> = trimmed_item.split_whitespace().collect();
        let (att_name, descending) =
            if parts.len() >= 2 && parts.last().unwrap().eq_ignore_ascii_case("desc") {
                let name = parts[..parts.len() - 1].join(" ");
                (name, true)
            } else if parts.len() >= 2 && parts.last().unwrap().eq_ignore_ascii_case("asc") {
                let name = parts[..parts.len() - 1].join(" ");
                (name, false)
            } else {
                (trimmed_item.to_string(), false)
            };
        let clean_att = att_name.trim().trim_matches(|c| c == '(' || c == ')');
        if !crate::model::is_identifier(clean_att) {
            return Err(format!(
                "invalid Order attribute '{clean_att}' in DataSelector"
            ));
        }
        orders.push(crate::frontend::ForEachOrder {
            attribute: clean_att.to_string(),
            descending,
        });
    }
    Ok(())
}

fn split_condition_statements(source: &str) -> Result<Vec<String>, String> {
    if source.len() > 1024 * 1024 {
        return Err("Conditions source exceeds 1 MiB".into());
    }
    let chars: Vec<char> = source.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut statements = Vec::new();
    let mut current = String::new();

    while i < len {
        let ch = chars[i];
        if ch == '/' && i + 1 < len && chars[i + 1] == '/' {
            i += 2;
            while i < len && chars[i] != '\n' && chars[i] != '\r' {
                i += 1;
            }
            current.push(' ');
            continue;
        }
        if ch == '/' && i + 1 < len && chars[i + 1] == '*' {
            i += 2;
            let mut closed = false;
            while i + 1 < len {
                if chars[i] == '*' && chars[i + 1] == '/' {
                    closed = true;
                    i += 2;
                    break;
                }
                i += 1;
            }
            if !closed {
                return Err("unclosed block comment in conditions".into());
            }
            current.push(' ');
            continue;
        }
        if ch == '\'' {
            current.push('\'');
            i += 1;
            let mut closed = false;
            while i < len {
                let c = chars[i];
                if c == '\'' {
                    if i + 1 < len && chars[i + 1] == '\'' {
                        current.push('\'');
                        current.push('\'');
                        i += 2;
                        continue;
                    }
                    current.push('\'');
                    closed = true;
                    i += 1;
                    break;
                }
                current.push(c);
                i += 1;
            }
            if !closed {
                return Err("unclosed string literal in conditions".into());
            }
            continue;
        }
        if ch == '"' {
            current.push('"');
            i += 1;
            let mut closed = false;
            while i < len {
                let c = chars[i];
                if c == '\\' && i + 1 < len {
                    current.push('\\');
                    current.push(chars[i + 1]);
                    i += 2;
                    continue;
                }
                if c == '"' {
                    current.push('"');
                    closed = true;
                    i += 1;
                    break;
                }
                current.push(c);
                i += 1;
            }
            if !closed {
                return Err("unclosed string literal in conditions".into());
            }
            continue;
        }
        if ch == ';' {
            let trimmed = current.trim();
            if !trimmed.is_empty() {
                statements.push(trimmed.to_string());
            }
            current.clear();
            i += 1;
            continue;
        }
        current.push(ch);
        i += 1;
    }

    let remaining = current.trim();
    if !remaining.is_empty() {
        for line in remaining.lines() {
            let trimmed = line.trim();
            if !trimmed.is_empty() {
                statements.push(trimmed.to_string());
            }
        }
    }

    Ok(statements)
}
