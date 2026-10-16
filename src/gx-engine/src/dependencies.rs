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
            Stmt::ReturnVoid | Stmt::Exit => {}
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
        }
    }
}

fn collect_expression_calls(expression: &Expr, callees: &mut HashSet<String>) {
    match expression {
        Expr::Call(name, arguments) => {
            callees.insert(name.clone());
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
        | Expr::Variable(_) => {}
    }
}
