//! GeneXus Rules parser and AST for Procedures and Transactions.
//!
//! Parses and validates GeneXus Rules parts into structured ASTs:
//! - Parm([in:|out:|inout:]&var1, ...)
//! - Error('msg' | "msg") [if <cond>]
//! - Msg('msg' | "msg") [if <cond>]
//! - Default(&var | Att, <expr>)
//! - NoAccept(&var | Att) [if <cond>]
//! - Accept(&var | Att)
//! - Order(Att1, Att2, ...)
//! - Prompt(PromptObject, &var | Att, ...)
//! - Serial(Att, Step[, Range])
//! - Hidden(&var | Att)
//!
//! Supports single-line (//) and multi-line (/* */) comments,
//! string literals with internal semicolons and escaped quotes,
//! whitespace tolerance, and fail-closed diagnostics (unknown rule src0295,
//! duplicate Parm, unclosed comments/strings, bounds limits).

use crate::model::{is_identifier, parse_parameter};
use std::collections::BTreeSet;

/// A parameter argument inside a Parm(...) rule.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParmArg {
    pub mode: String,
    pub name: String,
    pub is_variable: bool,
}

/// A parsed GeneXus Rule.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Rule {
    Parm(Vec<ParmArg>),
    Error {
        message: String,
        condition: Option<String>,
    },
    Msg {
        message: String,
        condition: Option<String>,
    },
    Default {
        target: String,
        value: String,
    },
    NoAccept {
        target: String,
        condition: Option<String>,
    },
    Accept {
        target: String,
    },
    Order {
        attributes: Vec<String>,
    },
    Prompt {
        object: String,
        parameters: Vec<String>,
        condition: Option<String>,
    },
    Serial {
        attribute: String,
        step: String,
        range: Option<String>,
    },
    Hidden {
        target: String,
    },
}

/// Tokenizes a Rules source string into individual ';'-terminated statements,
/// stripping single-line (`//`) and multi-line (`/* */`) comments while preserving
/// string literal contents and escaping.
pub fn tokenize_statements(source: &str) -> Result<Vec<String>, String> {
    if source.len() > 1024 * 1024 {
        return Err("Rules source exceeds 1 MiB".into());
    }
    let mut statements = Vec::new();
    let mut current = String::new();
    let chars: Vec<char> = source.chars().collect();
    let len = chars.len();
    let mut i = 0;

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
                return Err("unclosed block comment in Rules".into());
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
                return Err("unclosed string literal in Rules".into());
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
                return Err("unclosed string literal in Rules".into());
            }
            continue;
        }
        if ch == ';' {
            let trimmed = current.trim();
            if trimmed.is_empty() {
                return Err("empty rule statement".into());
            }
            statements.push(trimmed.to_string());
            current.clear();
            i += 1;
            continue;
        }
        current.push(ch);
        i += 1;
    }

    let remaining = current.trim();
    if !remaining.is_empty() {
        return Err(format!(
            "Rule statement must terminate with a semicolon ';': {remaining}"
        ));
    }

    Ok(statements)
}

/// Splits comma-separated arguments at top level (outside quotes and parentheses).
pub fn split_comma_args(s: &str) -> Result<Vec<String>, String> {
    let mut args = Vec::new();
    let mut current = String::new();
    let chars: Vec<char> = s.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut paren_depth = 0;

    while i < len {
        let ch = chars[i];
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
                return Err("unclosed string literal in rule arguments".into());
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
                return Err("unclosed string literal in rule arguments".into());
            }
            continue;
        }
        if ch == '(' {
            paren_depth += 1;
            current.push(ch);
            i += 1;
            continue;
        }
        if ch == ')' {
            if paren_depth == 0 {
                return Err("unmatched closing parenthesis in rule arguments".into());
            }
            paren_depth -= 1;
            current.push(ch);
            i += 1;
            continue;
        }
        if ch == ',' && paren_depth == 0 {
            let trimmed = current.trim();
            if trimmed.is_empty() {
                return Err("empty argument in rule".into());
            }
            args.push(trimmed.to_string());
            current.clear();
            i += 1;
            continue;
        }
        current.push(ch);
        i += 1;
    }
    if paren_depth != 0 {
        return Err("unclosed parenthesis in rule arguments".into());
    }
    let trimmed = current.trim();
    if !trimmed.is_empty() {
        args.push(trimmed.to_string());
    } else if !args.is_empty() {
        return Err("trailing comma in rule arguments".into());
    }
    Ok(args)
}

/// Splits a rule statement into call part and optional condition (`if <cond>`).
pub fn split_condition(stmt: &str) -> Result<(&str, Option<&str>), String> {
    let chars: Vec<char> = stmt.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut paren_depth = 0;
    let mut if_index = None;

    while i < len {
        let ch = chars[i];
        if ch == '\'' {
            i += 1;
            while i < len {
                if chars[i] == '\'' {
                    if i + 1 < len && chars[i + 1] == '\'' {
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                i += 1;
            }
            continue;
        }
        if ch == '"' {
            i += 1;
            while i < len {
                if chars[i] == '\\' && i + 1 < len {
                    i += 2;
                    continue;
                }
                if chars[i] == '"' {
                    i += 1;
                    break;
                }
                i += 1;
            }
            continue;
        }
        if ch == '(' {
            paren_depth += 1;
            i += 1;
            continue;
        }
        if ch == ')' {
            if paren_depth == 0 {
                return Err("unmatched ')' in rule".into());
            }
            paren_depth -= 1;
            i += 1;
            continue;
        }
        if paren_depth == 0
            && (ch == 'i' || ch == 'I')
            && i + 1 < len
            && (chars[i + 1] == 'f' || chars[i + 1] == 'F')
        {
            let before_ok = i == 0 || chars[i - 1].is_whitespace() || chars[i - 1] == ')';
            let after_ok = i + 2 == len || chars[i + 2].is_whitespace() || chars[i + 2] == '(';
            if before_ok && after_ok {
                if_index = Some(i);
                break;
            }
        }
        i += 1;
    }

    if paren_depth != 0 {
        return Err("unclosed '(' in rule".into());
    }

    if let Some(idx) = if_index {
        let call_part = stmt[..idx].trim();
        let cond_part = stmt[idx + 2..].trim();
        if cond_part.is_empty() {
            return Err("missing condition expression after 'if'".into());
        }
        Ok((call_part, Some(cond_part)))
    } else {
        Ok((stmt.trim(), None))
    }
}

fn parse_call(call: &str) -> Result<(&str, &str), String> {
    let call = call.trim();
    let Some((kw, rest)) = call.split_once('(') else {
        return Err(format!(
            "invalid rule syntax: expected Keyword(args), found '{call}'"
        ));
    };
    let kw = kw.trim();
    if !is_identifier(kw) {
        return Err(format!("invalid rule keyword '{kw}'"));
    }
    let Some(args_inner) = rest.strip_suffix(')') else {
        return Err(format!("rule '{kw}' requires closing ')'"));
    };
    Ok((kw, args_inner))
}

fn parse_parm_args(args_str: &str) -> Result<Vec<ParmArg>, String> {
    let trimmed = args_str.trim();
    if trimmed.is_empty() {
        return Ok(Vec::new());
    }
    let entries = split_comma_args(trimmed)?;
    if entries.len() > 1024 {
        return Err("Parm rule exceeds 1024 parameters".into());
    }
    let mut names = BTreeSet::new();
    let mut args = Vec::new();

    for entry in entries {
        let (mode_part, name_part) = match entry.split_once(':') {
            Some((m, n)) => (m.trim(), n.trim()),
            None => ("inout", entry.trim()),
        };
        let mode = mode_part.to_ascii_uppercase();
        if !matches!(mode.as_str(), "IN" | "OUT" | "INOUT") {
            return Err(format!(
                "invalid Parm mode '{mode_part}': must be in, out, or inout"
            ));
        }
        let (is_variable, ident) = if let Some(stripped) = name_part.strip_prefix('&') {
            (true, stripped.trim())
        } else {
            (false, name_part)
        };
        if !is_identifier(ident) {
            return Err(format!(
                "Parm argument '{name_part}' must be a bounded ASCII identifier"
            ));
        }
        let canonical_name = ident.to_ascii_uppercase();
        if !names.insert(canonical_name.clone()) {
            return Err(format!("duplicate Parm argument {canonical_name}"));
        }
        args.push(ParmArg {
            mode,
            name: canonical_name,
            is_variable,
        });
    }

    Ok(args)
}

/// Parses full GeneXus Rules into a structured AST.
pub fn parse_rules(source: &str) -> Result<Vec<Rule>, String> {
    if source.len() > 1024 * 1024 {
        return Err("Rules source exceeds 1 MiB".into());
    }
    let stmts = tokenize_statements(source)?;
    let mut rules = Vec::new();
    let mut has_parm = false;

    for stmt in stmts {
        let (call_part, condition) = split_condition(&stmt)?;
        let (kw, args_inner) = parse_call(call_part)?;
        let kw_upper = kw.to_ascii_uppercase();

        let rule = match kw_upper.as_str() {
            "PARM" => {
                if condition.is_some() {
                    return Err("Parm rule does not support 'if' condition".into());
                }
                if has_parm {
                    return Err("duplicate Parm rule".into());
                }
                has_parm = true;
                let args = parse_parm_args(args_inner)?;
                Rule::Parm(args)
            }
            "ERROR" => {
                let msg = args_inner.trim();
                if msg.is_empty() {
                    return Err("Error rule requires a message argument".into());
                }
                Rule::Error {
                    message: msg.to_string(),
                    condition: condition.map(|s| s.to_string()),
                }
            }
            "MSG" => {
                let msg = args_inner.trim();
                if msg.is_empty() {
                    return Err("Msg rule requires a message argument".into());
                }
                Rule::Msg {
                    message: msg.to_string(),
                    condition: condition.map(|s| s.to_string()),
                }
            }
            "DEFAULT" => {
                if condition.is_some() {
                    return Err("Default rule does not support 'if' condition".into());
                }
                let args = split_comma_args(args_inner)?;
                if args.len() != 2 {
                    return Err("Default rule requires 2 arguments: Default(target, value)".into());
                }
                let target = args[0].trim().to_string();
                let clean_target = target.strip_prefix('&').unwrap_or(&target);
                if !is_identifier(clean_target) {
                    return Err(format!(
                        "Default target '{target}' must be a valid identifier"
                    ));
                }
                Rule::Default {
                    target,
                    value: args[1].trim().to_string(),
                }
            }
            "NOACCEPT" => {
                let args = split_comma_args(args_inner)?;
                if args.len() != 1 {
                    return Err("NoAccept rule requires 1 argument: NoAccept(target)".into());
                }
                let target = args[0].trim().to_string();
                let clean_target = target.strip_prefix('&').unwrap_or(&target);
                if !is_identifier(clean_target) {
                    return Err(format!(
                        "NoAccept target '{target}' must be a valid identifier"
                    ));
                }
                Rule::NoAccept {
                    target,
                    condition: condition.map(|s| s.to_string()),
                }
            }
            "ACCEPT" => {
                if condition.is_some() {
                    return Err("Accept rule does not support 'if' condition".into());
                }
                let args = split_comma_args(args_inner)?;
                if args.len() != 1 {
                    return Err("Accept rule requires 1 argument: Accept(target)".into());
                }
                let target = args[0].trim().to_string();
                let clean_target = target.strip_prefix('&').unwrap_or(&target);
                if !is_identifier(clean_target) {
                    return Err(format!(
                        "Accept target '{target}' must be a valid identifier"
                    ));
                }
                Rule::Accept { target }
            }
            "ORDER" => {
                if condition.is_some() {
                    return Err("Order rule does not support 'if' condition".into());
                }
                let args = split_comma_args(args_inner)?;
                if args.is_empty() {
                    return Err("Order rule requires at least one attribute or 'none'".into());
                }
                for att in &args {
                    let trimmed = att.trim();
                    let clean = trimmed.strip_prefix('&').unwrap_or(trimmed);
                    if !clean.eq_ignore_ascii_case("none") && !is_identifier(clean) {
                        return Err(format!(
                            "Order attribute '{trimmed}' must be a valid identifier or 'none'"
                        ));
                    }
                }
                Rule::Order {
                    attributes: args.into_iter().map(|a| a.trim().to_string()).collect(),
                }
            }
            "PROMPT" => {
                let args = split_comma_args(args_inner)?;
                if args.is_empty() {
                    return Err("Prompt rule requires a prompt object name".into());
                }
                let obj = args[0].trim().to_string();
                if !is_identifier(&obj) {
                    return Err(format!("Prompt object '{obj}' must be a valid identifier"));
                }
                Rule::Prompt {
                    object: obj,
                    parameters: args[1..].iter().map(|a| a.trim().to_string()).collect(),
                    condition: condition.map(|s| s.to_string()),
                }
            }
            "SERIAL" => {
                if condition.is_some() {
                    return Err("Serial rule does not support 'if' condition".into());
                }
                let args = split_comma_args(args_inner)?;
                if args.len() < 2 || args.len() > 3 {
                    return Err(
                        "Serial rule requires 2 or 3 arguments: Serial(att, step[, range])".into(),
                    );
                }
                let att = args[0].trim().to_string();
                if !is_identifier(&att) {
                    return Err(format!(
                        "Serial attribute '{att}' must be a valid identifier"
                    ));
                }
                Rule::Serial {
                    attribute: att,
                    step: args[1].trim().to_string(),
                    range: args.get(2).map(|r| r.trim().to_string()),
                }
            }
            "HIDDEN" => {
                if condition.is_some() {
                    return Err("Hidden rule does not support 'if' condition".into());
                }
                let args = split_comma_args(args_inner)?;
                if args.len() != 1 {
                    return Err("Hidden rule requires 1 argument: Hidden(target)".into());
                }
                let target = args[0].trim().to_string();
                let clean_target = target.strip_prefix('&').unwrap_or(&target);
                if !is_identifier(clean_target) {
                    return Err(format!(
                        "Hidden target '{target}' must be a valid identifier"
                    ));
                }
                Rule::Hidden { target }
            }
            "UNIQUE" => {
                return Err("'Unique' is not a valid GeneXus rule (the SDK reports src0295 'Regra desconhecida'). Enforce attribute uniqueness with a unique index: call genexus_structure action=create_index name=<Transaction> payload={\"attributes\":[\"<Attr>\"],\"unique\":true}, then genexus_lifecycle action=reorg to apply it to the database.".into());
            }
            _ => {
                return Err(format!("unknown GeneXus rule '{kw}' (src0295)"));
            }
        };

        rules.push(rule);
    }

    Ok(rules)
}

/// Extracts parameter strings for a Procedure from its Rules source.
/// Validates that Parm arguments are `&variables` (not attributes).
pub fn extract_procedure_parameters(source: &str) -> Result<Vec<String>, String> {
    let source = source.trim();
    if source.is_empty() {
        return Ok(Vec::new());
    }
    let rules = parse_rules(source)?;
    let mut parm_params = None;

    for rule in &rules {
        match rule {
            Rule::Parm(args) => {
                if parm_params.is_some() {
                    return Err("duplicate Parm rule".into());
                }
                let mut params = Vec::new();
                for arg in args {
                    if !arg.is_variable {
                        return Err(format!(
                            "Procedure Parm arguments must be &variables, found attribute '{}'",
                            arg.name
                        ));
                    }
                    let p = format!("{}:{}", arg.mode, arg.name);
                    parse_parameter(&p)?;
                    params.push(p);
                }
                parm_params = Some(params);
            }
            Rule::Error { .. } | Rule::Msg { .. } | Rule::Order { .. } | Rule::Prompt { .. } => {
                // Admitted procedure rules
            }
            Rule::Default { .. }
            | Rule::NoAccept { .. }
            | Rule::Accept { .. }
            | Rule::Serial { .. }
            | Rule::Hidden { .. } => {
                return Err(format!(
                    "rule '{:?}' is only supported in Transactions, not Procedures",
                    rule
                ));
            }
        }
    }

    Ok(parm_params.unwrap_or_default())
}

/// Standalone parameter derivation function for backward compatibility with the legacy Parm subset.
pub fn parameters(source: &str) -> Result<Vec<String>, String> {
    if source.len() > 1024 * 1024 {
        return Err("Rules source exceeds 1 MiB".into());
    }
    let source = source.trim();
    if source.is_empty() {
        return Ok(Vec::new());
    }
    let rule = source.strip_suffix(';').unwrap_or(source).trim();
    let Some((keyword, tail)) = rule.split_once('(') else {
        return Err("only a standalone Parm(...) rule is supported".into());
    };
    if !keyword.trim().eq_ignore_ascii_case("parm") {
        return Err("only a standalone Parm(...) rule is supported".into());
    }
    let body = tail
        .strip_suffix(')')
        .ok_or("Parm rule requires closing ')'")?;
    if body.trim().is_empty() {
        return Ok(Vec::new());
    }
    let mut names = BTreeSet::new();
    let mut parameters = Vec::new();
    for entry in body.split(',') {
        if parameters.len() == 1024 {
            return Err("Parm rule exceeds 1024 parameters".into());
        }
        let (mode, variable) = entry.split_once(':').unwrap_or(("inout", entry));
        let name = variable
            .trim()
            .strip_prefix('&')
            .ok_or("Parm arguments must be &variables")?;
        if !is_identifier(name) {
            return Err("Parm argument must be a bounded ASCII variable identifier".into());
        }
        let name = name.to_ascii_uppercase();
        if !names.insert(name.clone()) {
            return Err(format!("duplicate Parm argument {name}"));
        }
        let parameter = format!("{}:{name}", mode.trim().to_ascii_uppercase());
        parse_parameter(&parameter)?;
        parameters.push(parameter);
    }
    Ok(parameters)
}

#[cfg(test)]
mod tests {
    use super::parameters;

    #[test]
    fn native_authored_rules_preserve_modes_and_variable_order() {
        // Authored/read-back KBTeste evidence: typed-call and parameter-mode probes.
        assert_eq!(
            parameters("Parm(in:&Value, out:&Result);\r\n").unwrap(),
            ["IN:VALUE", "OUT:RESULT"]
        );
        assert_eq!(
            parameters("parm ( in : &A, out : &B, inout : &C );").unwrap(),
            ["IN:A", "OUT:B", "INOUT:C"]
        );
        assert_eq!(parameters("Parm(&A);").unwrap(), ["INOUT:A"]);
        assert!(parameters("\r\n  ").unwrap().is_empty());
    }

    #[test]
    fn unsupported_rules_and_ambiguous_arguments_fail_closed() {
        for source in [
            "Default(&A, 1);",
            "Parm(&A); Default(&A, 1);",
            "Parm(&A); Parm(&B);",
            "Parm(&A,);",
            "Parm(A);",
            "Parm(ref:&A);",
            "Parm(&A + 1);",
            "Parm(in:&a, out:&A);",
            "Parm(&A",
            "Parm(&A))",
            "Parm(&A);;",
            "Parm(in:out:&A);",
            "Parm(&é);",
            "Parm(&1A);",
            "Parm(/* mode */ &A);",
        ] {
            assert!(parameters(source).is_err(), "accepted {source}");
        }
        assert!(parameters(&" ".repeat(1024 * 1024 + 1)).is_err());
        assert!(parameters(&format!("Parm(&{});", "A".repeat(129))).is_err());
        let too_many = (0..1025)
            .map(|i| format!("&V{i}"))
            .collect::<Vec<_>>()
            .join(",");
        assert!(parameters(&format!("Parm({too_many});")).is_err());
    }
}
