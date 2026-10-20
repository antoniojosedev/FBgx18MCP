//! Typed lowering of collection DataProviders to the existing Procedure emitter.
//!
//! Only explicit output metadata, local variable assignments and item groups (optionally
//! iterating a declared collection with `input &item in &items`) are admitted. Database
//! navigation, conditions, nested output groups and Rules remain errors, never discarded.

use super::{
    ProcedureClass,
    calls::{CallSignatures, is_builtin_name},
};
use crate::{
    catalog::{KbObject, ObjectKind},
    dataprovider::strip_comments,
    frontend::{Expr, Stmt, parse_body, parse_expression},
    model::{DataType, ParamMode, ProcedureSource},
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, VecDeque};

pub const OUTPUT_PART: &str = "DP_OUTPUT";
const RESULT: &str = "GxeDpResult";
const ROW: &str = "GxeDpRow";
const CALL_RESULT: &str = "GxeDpCallResult";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DataProviderOutput {
    pub name: String,
    pub data_type: DataType,
}

/// Parse only the declared OutputSDT reference, without guessing from the source's group name.
/// Resolution is supplied by the native reader, using its model's SDT type and entity ids.
pub(crate) fn output_reference(properties: Option<&str>) -> Result<i32, String> {
    let doc = roxmltree::Document::parse(properties.ok_or("DataProvider properties are missing")?)
        .map_err(|e| format!("DataProvider properties: {e}"))?;
    let mut output = None;
    for node in doc.descendants().filter(|n| n.has_tag_name("Property")) {
        let text = |name| {
            node.children()
                .find(|n| n.has_tag_name(name))
                .and_then(|n| n.text())
        };
        if text("Name") != Some("OutputSDT") {
            continue;
        }
        if output.is_some() {
            return Err("duplicate DataProvider OutputSDT property".into());
        }
        let value = text("Value").ok_or("empty DataProvider OutputSDT")?;
        let (guid, id) = value
            .rsplit_once('-')
            .ok_or("malformed DataProvider OutputSDT")?;
        if !guid.eq_ignore_ascii_case("447527b5-9210-4523-898b-5dccb17be60a") {
            return Err("DataProvider OutputSDT is not an SDT reference".into());
        }
        let id: i32 = id
            .parse()
            .map_err(|_| "invalid DataProvider OutputSDT entity id")?;
        if id <= 0 {
            return Err("invalid DataProvider OutputSDT entity id".into());
        }
        output = Some(id);
    }
    output.ok_or_else(|| "DataProvider OutputSDT is missing".into())
}

fn identifier(name: &str) -> bool {
    let mut chars = name.chars();
    chars
        .next()
        .is_some_and(|c| c.is_ascii_alphabetic() || c == '_')
        && chars.all(|c| c.is_ascii_alphanumeric() || c == '_')
}

/// Keep braces in strings intact; only unquoted braces and line endings delimit this subset.
fn lines(source: &str) -> Result<VecDeque<String>, String> {
    if source.len() > 1024 * 1024 {
        return Err("DataProvider source exceeds 1 MiB".into());
    }
    let cleaned = strip_comments(source)?;
    let mut chars = cleaned.chars().peekable();
    let mut quote = None;
    let mut line = String::new();
    let mut tokens = VecDeque::new();
    while let Some(ch) = chars.next() {
        if let Some(q) = quote {
            line.push(ch);
            if ch == q {
                if chars.peek() == Some(&q) {
                    line.push(chars.next().unwrap());
                } else {
                    quote = None;
                }
            }
        } else if matches!(ch, '\'' | '"') {
            quote = Some(ch);
            line.push(ch);
        } else if matches!(ch, '{' | '}' | '\r' | '\n') {
            if !line.trim().is_empty() {
                tokens.push_back(line.trim().to_string());
            }
            line.clear();
            if matches!(ch, '{' | '}') {
                tokens.push_back(ch.to_string());
            }
        } else {
            line.push(ch);
        }
    }
    if quote.is_some() {
        return Err("unclosed DataProvider string".into());
    }
    if !line.trim().is_empty() {
        tokens.push_back(line.trim().to_string());
    }
    if tokens.len() > 10000 {
        return Err("DataProvider exceeds 10000 source segments".into());
    }
    Ok(tokens)
}

fn expect(tokens: &mut VecDeque<String>, expected: &str) -> Result<(), String> {
    match tokens.pop_front() {
        Some(actual) if actual.eq_ignore_ascii_case(expected) => Ok(()),
        actual => Err(format!(
            "DataProvider expected '{expected}', found {actual:?}"
        )),
    }
}

fn variable<'a>(
    variables: &'a BTreeMap<String, DataType>,
    name: &str,
) -> Result<&'a DataType, String> {
    let name = name
        .strip_prefix('&')
        .ok_or("DataProvider input requires a variable")?;
    variables
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, ty)| ty)
        .ok_or_else(|| format!("DataProvider variable '&{name}' is not declared"))
}

fn input_loop(
    header: &str,
    variables: &BTreeMap<String, DataType>,
    body: Vec<Stmt>,
) -> Result<Stmt, String> {
    let fields: Vec<_> = header.split_whitespace().collect();
    let [input, item, in_keyword, collection] = fields.as_slice() else {
        return Err(format!("unsupported DataProvider input: {header}"));
    };
    if !input.eq_ignore_ascii_case("input") || !in_keyword.eq_ignore_ascii_case("in") {
        return Err(format!("unsupported DataProvider input: {header}"));
    }
    let DataType::Record {
        schema: item_schema,
    } = variable(variables, item)?
    else {
        return Err("DataProvider input item must be a declared record".into());
    };
    let DataType::BaseCollection { schema, .. } = variable(variables, collection)? else {
        return Err("DataProvider input requires a declared SDT collection".into());
    };
    if !item_schema.eq_ignore_ascii_case(schema) {
        return Err("DataProvider input item/collection types differ".into());
    }
    Ok(Stmt::ForIn(
        item.trim_start_matches('&').to_string(),
        parse_expression(collection).map_err(|e| e.to_string())?,
        body,
    ))
}

fn item_body(tokens: &mut VecDeque<String>, names: &Names) -> Result<Vec<Stmt>, String> {
    expect(tokens, "{")?;
    let mut body = vec![Stmt::Assign(
        names.row.clone(),
        Expr::Call("NEW".into(), vec![]),
    )];
    if names.sdk {
        // The SDK adds the new item first, then sets its fields (sidebaritemsdp.cs).
        body.push(Stmt::CollectionAdd(
            Box::new(Expr::Variable(names.result.clone())),
            Expr::Variable(names.row.clone()),
            None,
        ));
    }
    let mut fields = BTreeSet::new();
    while tokens.front().is_some_and(|line| line != "}") {
        let line = tokens.pop_front().unwrap();
        let (field, value) = line
            .split_once('=')
            .ok_or_else(|| format!("unsupported DataProvider item statement: {line}"))?;
        let field = field.trim();
        if !identifier(field) || !fields.insert(field.to_ascii_uppercase()) {
            return Err(format!("invalid or duplicate DataProvider field '{field}'"));
        }
        let value = parse_expression(value.trim()).map_err(|e| e.to_string())?;
        body.push(Stmt::AssignField(
            Box::new(Expr::Variable(names.row.clone())),
            field.into(),
            value,
        ));
    }
    expect(tokens, "}")?;
    if !names.sdk {
        body.push(Stmt::CollectionAdd(
            Box::new(Expr::Variable(names.result.clone())),
            Expr::Variable(names.row.clone()),
            None,
        ));
    }
    Ok(body)
}

/// The names a DataProvider's output is written with. Read with stored variable ids, they are the
/// SDK's: `Gxm2rootcol` for the root collection, `Gxm1<output>` for an item, `GXt_objcol_<class><n>`
/// for a call temporary, `GXV<n>` for a For-In counter (general/ui/sidebaritemsdp.cs).
struct Names {
    sdk: bool,
    result: String,
    row: String,
}

pub fn procedure_class(object: &KbObject) -> Result<ProcedureClass, String> {
    if object.kind != ObjectKind::DataProvider {
        return Err("expected DataProvider".into());
    }
    if let Some(reason) = object.refusal.as_ref().or(object.not_generated.as_ref()) {
        return Err(reason.clone());
    }
    for part in &object.source_parts {
        if !matches!(
            part.name.to_ascii_uppercase().as_str(),
            "SOURCE"
                | "VARIABLES"
                | "VARIABLE_IDS"
                | "RULES"
                | "DP_OUTPUT"
                | "DOCUMENTATION"
                | "HELP"
        ) && !part.source.trim().is_empty()
        {
            return Err(format!(
                "DataProvider part '{}' is not supported",
                part.name
            ));
        }
    }
    let part = |name: &str| {
        object
            .source_parts
            .iter()
            .find(|p| p.name.eq_ignore_ascii_case(name))
            .map(|p| p.source.as_str())
    };
    if !strip_comments(part("RULES").unwrap_or_default())?
        .trim()
        .is_empty()
    {
        return Err("DataProvider Rules are not supported".into());
    }
    let output: DataProviderOutput = serde_json::from_str(
        part(OUTPUT_PART).ok_or("DataProvider requires explicit output metadata")?,
    )
    .map_err(|e| format!("DataProvider output: {e}"))?;
    let DataType::BaseCollection { schema, item, .. } = &output.data_type else {
        return Err("DataProvider output must be an SDT root collection".into());
    };
    if !identifier(&output.name) || !identifier(item) {
        return Err("invalid DataProvider output names".into());
    }
    let mut variables: BTreeMap<String, DataType> =
        serde_json::from_str(part("VARIABLES").unwrap_or("{}"))
            .map_err(|e| format!("DataProvider Variables: {e}"))?;
    if variables.keys().any(|key| {
        [RESULT, ROW, CALL_RESULT]
            .iter()
            .any(|reserved| key.eq_ignore_ascii_case(reserved))
    }) {
        return Err("DataProvider variables conflict with generated output variables".into());
    }
    let mut var_ids: BTreeMap<String, u32> = match part(crate::udm::reader::VARIABLE_IDS_PART) {
        Some(ids) => {
            serde_json::from_str(ids).map_err(|e| format!("DataProvider variable ids: {e}"))?
        }
        None => BTreeMap::new(),
    };
    let names = if var_ids.is_empty() {
        Names {
            sdk: false,
            result: RESULT.into(),
            row: ROW.into(),
        }
    } else {
        Names {
            sdk: true,
            result: "Gxm2rootcol".into(),
            row: format!("Gxm1{}", output.name.to_ascii_lowercase()),
        }
    };
    if names.sdk
        && variables.keys().any(|key| {
            [&names.result, &names.row]
                .iter()
                .any(|reserved| key.eq_ignore_ascii_case(reserved))
        })
    {
        return Err("DataProvider variables conflict with generated output variables".into());
    }
    let mut tokens = lines(part("SOURCE").ok_or("DataProvider SOURCE is missing")?)?;
    expect(&mut tokens, &output.name)?;
    expect(&mut tokens, "{")?;
    let mut body = Vec::new();
    while tokens.front().is_some_and(|line| line != "}") {
        if tokens.front().unwrap().starts_with('&') {
            let line = tokens.pop_front().unwrap();
            let statements = parse_body(&line).map_err(|e| e.to_string())?;
            let [Stmt::Assign(name, _)] = statements.as_slice() else {
                return Err(format!("unsupported DataProvider statement: {line}"));
            };
            variable(&variables, &format!("&{name}"))?;
            body.extend(statements);
        } else {
            let group = tokens.pop_front().unwrap();
            let header = if group.eq_ignore_ascii_case(item) {
                tokens.front().filter(|v| *v != "{").cloned()
            } else if group
                .to_ascii_lowercase()
                .starts_with(&format!("{} input ", item.to_ascii_lowercase()))
            {
                Some(group[item.len()..].trim().to_string())
            } else {
                return Err(format!("unsupported DataProvider output group '{group}'"));
            };
            if group.eq_ignore_ascii_case(item) && header.is_some() {
                tokens.pop_front();
            }
            let item_body = item_body(&mut tokens, &names)?;
            if let Some(header) = header {
                body.push(input_loop(&header, &variables, item_body)?);
            } else {
                body.extend(item_body);
            }
        }
    }
    expect(&mut tokens, "}")?;
    if !tokens.is_empty() {
        return Err("trailing DataProvider source is not supported".into());
    }
    variables.insert(
        names.row.clone(),
        DataType::Record {
            schema: schema.clone(),
        },
    );
    variables.insert(names.result.clone(), output.data_type);
    let for_in = body.iter().any(|stmt| matches!(stmt, Stmt::ForIn(..)));
    if names.sdk && for_in {
        // The SDK's For-In counter takes the id after its two output variables (`AV9GXV1` after
        // ids 1-6 in sidebaritemsdp.cs; ids 7 and 8 are not written). One loop is evidenced.
        if body
            .iter()
            .filter(|stmt| matches!(stmt, Stmt::ForIn(..)))
            .count()
            > 1
        {
            return Err(
                "a DataProvider with more than one input loop has no SDK form in this build".into(),
            );
        }
        let next = var_ids.values().max().copied().unwrap_or(0) + 3;
        var_ids.insert("GXV1".into(), next);
        variables.insert(
            "GXV1".into(),
            DataType::Numeric {
                precision: 9,
                scale: 0,
            },
        );
    }
    let mut class = ProcedureClass::from_source(&ProcedureSource {
        name: object.name.clone(),
        source: String::new(),
        params: vec![format!("out:&{}", names.result)],
        variables,
        return_type: None,
    })?;
    class.body = body;
    if names.sdk {
        class = class.with_var_ids(var_ids);
    }
    Ok(class)
}

/// Preserve the SDK's typed copy-in/out temporary for a collection-result assignment.
/// Resolve only the evidenced zero-input, one-output shape after all callees are known;
/// the general Procedure emitter's collection-result refusal is unchanged.
pub fn resolve_collection_calls(
    mut class: ProcedureClass,
    calls: &CallSignatures,
) -> Result<ProcedureClass, String> {
    let mut body = Vec::new();
    for statement in std::mem::take(&mut class.body) {
        if let Stmt::Assign(name, Expr::Call(callee, args)) = &statement {
            if !is_builtin_name(callee) {
                let ty = variable(&class.variables, &format!("&{name}"))?.clone();
                let target = calls
                    .get(callee)
                    .ok_or_else(|| format!("DataProvider callee '{callee}' is not generated"))?;
                if !args.is_empty()
                    || !matches!(ty, DataType::BaseCollection { .. })
                    || target.params.len() != 1
                    || target.params[0].mode != ParamMode::Out
                    || target.params[0].data_type != ty
                {
                    return Err(format!(
                        "DataProvider result call '{callee}' requires one matching collection output and no inputs"
                    ));
                }
                let temporary = if class.var_ids.is_empty() {
                    format!("{CALL_RESULT}{}", body.len())
                } else {
                    // `GXt_objcol_SdtProgramNames_ProgramName1` (sidebaritemsdp.cs).
                    let DataType::BaseCollection { schema, .. } = &ty else {
                        unreachable!("checked above")
                    };
                    let calls_before = body
                        .iter()
                        .filter(|stmt| matches!(stmt, Stmt::Call(_)))
                        .count();
                    format!(
                        "GXt_objcol_{}{}",
                        crate::csharp::types::to_csharp_type(&DataType::Record {
                            schema: schema.clone()
                        }),
                        calls_before + 1
                    )
                };
                if class
                    .variables
                    .keys()
                    .any(|key| key.eq_ignore_ascii_case(&temporary))
                {
                    return Err(
                        "DataProvider call temporary conflicts with a declared variable".into(),
                    );
                }
                class.variables.insert(temporary.clone(), ty);
                body.push(Stmt::Assign(
                    temporary.clone(),
                    Expr::Variable(name.clone()),
                ));
                body.push(Stmt::Call(Expr::Call(
                    callee.clone(),
                    vec![Expr::Variable(temporary.clone())],
                )));
                body.push(Stmt::Assign(name.clone(), Expr::Variable(temporary)));
                continue;
            }
        }
        body.push(statement);
    }
    class.body = body;
    Ok(class)
}

#[cfg(test)]
mod tests {
    use super::output_reference;
    #[test]
    fn output_reference_requires_one_valid_sdt_reference() {
        let valid = "<Properties><Property><Name>OutputSDT</Name><Value>447527b5-9210-4523-898b-5dccb17be60a-58</Value></Property></Properties>";
        assert_eq!(output_reference(Some(valid)).unwrap(), 58);
        for bad in [
            "",
            "<Properties/>",
            &valid.replace("-58", "-0"),
            &valid.replace("447527b5", "00000000"),
            &valid.replace(
                "</Properties>",
                "<Property><Name>OutputSDT</Name><Value>x</Value></Property></Properties>",
            ),
        ] {
            assert!(output_reference(Some(bad)).is_err(), "must refuse {bad}");
        }
        assert!(output_reference(None).is_err());
    }
}
