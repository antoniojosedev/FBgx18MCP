//! A small text template for generated classes whose shape repeats per item.
//!
//! Directives are written `@@...@@`:
//! - `@@name@@` a scalar of the scope, `@@.name@@` a field of the current list item;
//! - `@@EACH list@@ ... @@END@@` repeats its body for each item of `list`; text after
//!   `@@SEP@@` inside it is written between two items;
//! - `@@IF flag@@ ... @@ELSE@@ ... @@FI@@`, where `flag` is a scope or `.item` boolean, and
//!   `!flag` negates it.
//!
//! A line that holds directives and nothing else is dropped with its line break, so block
//! directives can sit on their own lines. Rendering fails, naming the key, on a missing or
//! mistyped value, so a template and its model cannot drift apart silently.

use std::collections::BTreeMap;

/// A value a template reads.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Value {
    Text(String),
    Flag(bool),
    List(Vec<Fields>),
}

/// The named values of a scope or of one list item.
pub type Fields = BTreeMap<String, Value>;

impl From<&str> for Value {
    fn from(value: &str) -> Self {
        Self::Text(value.to_string())
    }
}

impl From<String> for Value {
    fn from(value: String) -> Self {
        Self::Text(value)
    }
}

impl From<bool> for Value {
    fn from(value: bool) -> Self {
        Self::Flag(value)
    }
}

impl From<Vec<Fields>> for Value {
    fn from(value: Vec<Fields>) -> Self {
        Self::List(value)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Node {
    Text(String),
    Value(String),
    Each {
        list: String,
        body: Vec<Node>,
        separator: Vec<Node>,
    },
    If {
        flag: String,
        then: Vec<Node>,
        otherwise: Vec<Node>,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Token {
    Text(String),
    Directive(String),
}

/// A parsed template.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Template {
    nodes: Vec<Node>,
}

fn is_block(directive: &str) -> bool {
    matches!(directive, "END" | "SEP" | "ELSE" | "FI")
        || directive.starts_with("EACH ")
        || directive.starts_with("IF ")
}

fn is_name(text: &str) -> bool {
    let name = text.strip_prefix('.').unwrap_or(text);
    !name.is_empty() && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
}

fn is_directive(text: &str) -> bool {
    if let Some(list) = text.strip_prefix("EACH ") {
        return is_name(list) && !list.starts_with('.');
    }
    if let Some(flag) = text.strip_prefix("IF ") {
        return is_name(flag.strip_prefix('!').unwrap_or(flag));
    }
    matches!(text, "END" | "SEP" | "ELSE" | "FI") || is_name(text)
}

/// Splits one line into text and directives. A `@@` that does not open a directive is text.
fn tokenize_line(line: &str, tokens: &mut Vec<Token>) {
    let mut rest = line;
    let mut text = String::new();
    while let Some(start) = rest.find("@@") {
        let after = &rest[start + 2..];
        match after.find("@@") {
            Some(end) if is_directive(&after[..end]) => {
                text.push_str(&rest[..start]);
                if !text.is_empty() {
                    tokens.push(Token::Text(std::mem::take(&mut text)));
                }
                tokens.push(Token::Directive(after[..end].to_string()));
                rest = &after[end + 2..];
            }
            _ => {
                text.push_str(&rest[..start + 1]);
                rest = &rest[start + 1..];
            }
        }
    }
    text.push_str(rest);
    if !text.is_empty() {
        tokens.push(Token::Text(text));
    }
}

fn tokenize(source: &str) -> Vec<Token> {
    let mut tokens = Vec::new();
    let lines: Vec<&str> = source.split('\n').collect();
    for (index, line) in lines.iter().enumerate() {
        let mut line_tokens = Vec::new();
        tokenize_line(line, &mut line_tokens);
        let block_only = !line_tokens.is_empty()
            && line_tokens.iter().all(|token| match token {
                Token::Directive(d) => is_block(d),
                Token::Text(t) => t.trim().is_empty(),
            });
        if block_only {
            tokens.extend(
                line_tokens
                    .into_iter()
                    .filter(|token| matches!(token, Token::Directive(_))),
            );
        } else {
            tokens.extend(line_tokens);
            if index + 1 < lines.len() {
                tokens.push(Token::Text("\n".to_string()));
            }
        }
    }
    tokens
}

fn parse_block(
    tokens: &[Token],
    mut index: usize,
    stops: &[&str],
) -> Result<(Vec<Node>, usize), String> {
    let mut nodes = Vec::new();
    while index < tokens.len() {
        match &tokens[index] {
            Token::Text(text) => {
                nodes.push(Node::Text(text.clone()));
                index += 1;
            }
            Token::Directive(d) if stops.contains(&d.as_str()) => return Ok((nodes, index)),
            Token::Directive(d) => {
                if let Some(list) = d.strip_prefix("EACH ") {
                    let (body, at) = parse_block(tokens, index + 1, &["SEP", "END"])?;
                    let (separator, at) = if matches!(&tokens[at], Token::Directive(s) if s == "SEP")
                    {
                        parse_block(tokens, at + 1, &["END"])?
                    } else {
                        (Vec::new(), at)
                    };
                    nodes.push(Node::Each {
                        list: list.to_string(),
                        body,
                        separator,
                    });
                    index = at + 1;
                } else if let Some(flag) = d.strip_prefix("IF ") {
                    let (then, at) = parse_block(tokens, index + 1, &["ELSE", "FI"])?;
                    let (otherwise, at) = if matches!(&tokens[at], Token::Directive(s) if s == "ELSE")
                    {
                        parse_block(tokens, at + 1, &["FI"])?
                    } else {
                        (Vec::new(), at)
                    };
                    nodes.push(Node::If {
                        flag: flag.to_string(),
                        then,
                        otherwise,
                    });
                    index = at + 1;
                } else if is_block(d) {
                    return Err(format!("template has an unexpected @@{d}@@"));
                } else {
                    nodes.push(Node::Value(d.clone()));
                    index += 1;
                }
            }
        }
    }
    if stops.is_empty() {
        Ok((nodes, index))
    } else {
        Err(format!(
            "template block is not closed by {}",
            stops.join(" or ")
        ))
    }
}

impl Template {
    pub fn parse(source: &str) -> Result<Self, String> {
        let tokens = tokenize(source);
        let (nodes, _) = parse_block(&tokens, 0, &[])?;
        Ok(Self { nodes })
    }

    pub fn render(&self, scope: &Fields) -> Result<String, String> {
        let mut out = String::new();
        render(&self.nodes, scope, None, &mut out)?;
        Ok(out)
    }
}

fn lookup<'a>(
    name: &str,
    scope: &'a Fields,
    item: Option<&'a Fields>,
) -> Result<&'a Value, String> {
    match name.strip_prefix('.') {
        Some(field) => item
            .ok_or_else(|| format!("template reads item field '{field}' outside a list"))?
            .get(field)
            .ok_or_else(|| format!("template item has no field '{field}'")),
        None => scope
            .get(name)
            .ok_or_else(|| format!("template scope has no value '{name}'")),
    }
}

fn render(
    nodes: &[Node],
    scope: &Fields,
    item: Option<&Fields>,
    out: &mut String,
) -> Result<(), String> {
    for node in nodes {
        match node {
            Node::Text(text) => out.push_str(text),
            Node::Value(name) => match lookup(name, scope, item)? {
                Value::Text(text) => out.push_str(text),
                _ => return Err(format!("template value '{name}' is not text")),
            },
            Node::Each {
                list,
                body,
                separator,
            } => {
                let Value::List(items) = lookup(list, scope, item)? else {
                    return Err(format!("template value '{list}' is not a list"));
                };
                for (index, entry) in items.iter().enumerate() {
                    if index > 0 {
                        render(separator, scope, item, out)?;
                    }
                    render(body, scope, Some(entry), out)?;
                }
            }
            Node::If {
                flag,
                then,
                otherwise,
            } => {
                let (negated, name) = match flag.strip_prefix('!') {
                    Some(name) => (true, name),
                    None => (false, flag.as_str()),
                };
                let Value::Flag(value) = lookup(name, scope, item)? else {
                    return Err(format!("template value '{name}' is not a flag"));
                };
                let branch = if *value != negated { then } else { otherwise };
                render(branch, scope, item, out)?;
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fields(pairs: &[(&str, Value)]) -> Fields {
        pairs
            .iter()
            .map(|(k, v)| (k.to_string(), v.clone()))
            .collect()
    }

    #[test]
    fn lists_separators_and_flags_render_and_block_lines_vanish() {
        let template = Template::parse(
            "head @@name@@\n@@EACH items@@\n@@IF .on@@\n  @@.v@@ on\n@@ELSE@@\n  @@.v@@ off\n@@FI@@\n@@END@@\n(@@EACH items@@@@.v@@@@SEP@@, @@END@@) @@@x@@\n",
        )
        .unwrap();
        let scope = fields(&[
            ("name", "n".into()),
            ("x", "y".into()),
            (
                "items",
                Value::List(vec![
                    fields(&[("v", "a".into()), ("on", true.into())]),
                    fields(&[("v", "b".into()), ("on", false.into())]),
                ]),
            ),
        ]);
        assert_eq!(
            template.render(&scope).unwrap(),
            "head n\n  a on\n  b off\n(a, b) @y\n"
        );
    }

    #[test]
    fn a_missing_value_or_an_open_block_is_an_error() {
        let template = Template::parse("@@missing@@").unwrap();
        assert!(
            template
                .render(&Fields::new())
                .unwrap_err()
                .contains("missing")
        );
        assert!(Template::parse("@@EACH items@@ x").is_err());
        assert!(Template::parse("x @@FI@@").is_err());
    }
}
