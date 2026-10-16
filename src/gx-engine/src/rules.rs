//! Admission of the standalone `Parm` rule subset, not a general Rules compiler.
//! The catalog retains the original text; this module derives only parameter modes.
use crate::model::{is_identifier, parse_parameter};
use std::collections::BTreeSet;

pub(crate) fn parameters(source: &str) -> Result<Vec<String>, String> {
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
