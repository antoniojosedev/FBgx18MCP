//! Shared AST dependency traversal for incremental compilation and exchange projection.
use crate::frontend::{Expr, Stmt};
use std::collections::HashSet;

pub(crate) fn calls(body: &[Stmt]) -> HashSet<String> {
    let mut result = HashSet::new();
    collect_block_calls(body, &mut result);
    result
}

fn collect_block_calls(statements: &[Stmt], callees: &mut HashSet<String>) {
    for statement in statements {
        match statement {
            Stmt::ReturnVoid
            | Stmt::Exit
            | Stmt::DoSub(_)
            | Stmt::Commit
            | Stmt::Rollback
            | Stmt::Delete => {}
            Stmt::Sub(_, body) => {
                collect_block_calls(body, callees);
            }
            Stmt::CollectionAdd(receiver, value, opt_idx) => {
                collect_expression_calls(receiver, callees);
                collect_expression_calls(value, callees);
                if let Some(idx) = opt_idx {
                    collect_expression_calls(idx, callees);
                }
            }
            Stmt::CollectionRemove(receiver, idx) => {
                collect_expression_calls(receiver, callees);
                collect_expression_calls(idx, callees);
            }
            Stmt::CollectionClear(receiver) => {
                collect_expression_calls(receiver, callees);
            }
            Stmt::FromJson(receiver, json_expr) => {
                collect_expression_calls(receiver, callees);
                collect_expression_calls(json_expr, callees);
            }
            Stmt::FromXml(from_xml) => {
                collect_expression_calls(&from_xml.target, callees);
                collect_expression_calls(&from_xml.xml, callees);
                if let Some(ref root_expr) = from_xml.root {
                    collect_expression_calls(root_expr, callees);
                }
            }
            Stmt::ForIn(_, collection, body) => {
                collect_expression_calls(collection, callees);
                collect_block_calls(body, callees);
            }
            Stmt::AssignField(receiver, _, value) => {
                collect_expression_calls(receiver, callees);
                collect_expression_calls(value, callees);
            }
            Stmt::Assign(_, value)
            | Stmt::Print(value)
            | Stmt::MsgStatus(value)
            | Stmt::Return(value)
            | Stmt::Call(value) => {
                collect_expression_calls(value, callees);
            }
            Stmt::If(condition, then_body, else_body) => {
                collect_expression_calls(condition, callees);
                collect_block_calls(then_body, callees);
                collect_block_calls(else_body, callees);
            }
            Stmt::While(condition, body) => {
                collect_expression_calls(condition, callees);
                collect_block_calls(body, callees);
            }
            Stmt::For(_, initial, limit, _, body) => {
                collect_expression_calls(initial, callees);
                collect_expression_calls(limit, callees);
                collect_block_calls(body, callees);
            }
            Stmt::ForEach(for_each) => {
                if let Some(ref using) = for_each.using {
                    for arg in &using.arguments {
                        collect_expression_calls(arg, callees);
                    }
                }
                for where_expr in &for_each.wheres {
                    collect_expression_calls(where_expr, callees);
                }
                collect_block_calls(&for_each.body, callees);
                collect_block_calls(&for_each.when_none, callees);
            }
            Stmt::AssignAttribute(_, expr) => {
                collect_expression_calls(expr, callees);
            }
            Stmt::New(new_stmt) => {
                collect_block_calls(&new_stmt.body, callees);
                collect_block_calls(&new_stmt.when_duplicate, callees);
            }
        }
    }
}

fn is_aggregate_formula_call(name: &str, arguments: &[Expr]) -> bool {
    let upper = name.to_ascii_uppercase();
    if !matches!(
        upper.as_str(),
        "SUM" | "COUNT" | "MAX" | "MIN" | "FIND" | "AVERAGE" | "AVG"
    ) {
        return false;
    }
    if arguments.is_empty() {
        return true;
    }
    arguments.iter().any(has_attribute_ref)
}

fn has_attribute_ref(expr: &Expr) -> bool {
    match expr {
        Expr::Attribute(_) => true,
        Expr::Binary(l, _, r) => has_attribute_ref(l) || has_attribute_ref(r),
        Expr::Not(e) | Expr::Negate(e) | Expr::TextLength(e) => has_attribute_ref(e),
        Expr::Member(e, _) | Expr::CloneRecord(e) => has_attribute_ref(e),
        Expr::CollectionItem(r, i) => has_attribute_ref(r) || has_attribute_ref(i),
        Expr::DateMethod(_, e) => has_attribute_ref(e),
        Expr::DateShift(_, a, b) => has_attribute_ref(a) || has_attribute_ref(b),
        Expr::Call(_, args) => args.iter().any(has_attribute_ref),
        _ => false,
    }
}

fn collect_expression_calls(expression: &Expr, callees: &mut HashSet<String>) {
    match expression {
        Expr::Call(name, arguments) => {
            if !is_aggregate_formula_call(name, arguments) {
                callees.insert(name.clone());
            }
            for argument in arguments {
                collect_expression_calls(argument, callees);
            }
        }
        Expr::Binary(left, _, right)
        | Expr::DateShift(_, left, right)
        | Expr::CollectionItem(left, right) => {
            collect_expression_calls(left, callees);
            collect_expression_calls(right, callees);
        }
        Expr::Negate(value)
        | Expr::Not(value)
        | Expr::TextLength(value)
        | Expr::DateMethod(_, value)
        | Expr::Member(value, _)
        | Expr::CloneRecord(value) => collect_expression_calls(value, callees),
        Expr::Integer(_)
        | Expr::Decimal(_)
        | Expr::Text(_)
        | Expr::Boolean(_)
        | Expr::EmptyDate
        | Expr::Variable(_)
        | Expr::Attribute(_) => {}
    }
}
