//! The shapes of integer-literal expressions the SDK prints specially.

use super::*;

/// Whether an expression is built from integer literals only, with `+`, `-`, `*` or `/`.
pub(super) fn integer_literals_only(expr: &Expr) -> bool {
    match expr {
        Expr::Paren(inner) => integer_literals_only(inner),
        Expr::Integer(_) => true,
        Expr::Binary(
            left,
            BinOp::Add | BinOp::Subtract | BinOp::Multiply | BinOp::Divide,
            right,
        ) => integer_literals_only(left) && integer_literals_only(right),
        _ => false,
    }
}

/// The `(precision, scale)` the SDK gives a bare numeric message of an integer expression, where the
/// message is written with `StringUtil.LTrimStr`. Three SDK samples fix it, and only these shapes are
/// written: integer literals combined by `+`, `-` or `*` take the digit count of the widest literal and
/// scale 0 (`21*2` gives 2,0 in arustdisplayprobe.cs line 77; `1234567890+5` gives 10,0), and a division
/// of integer literals takes 15,5 (`7/3` in arustdisplayprobe2.cs). Any other numeric message is refused.
/// The rule is inferred from these samples, not from GeneXus documentation.
pub(super) fn literal_message_format(expr: &Expr) -> Option<(u32, u32)> {
    fn digits(expr: &Expr) -> Option<u32> {
        match expr {
            Expr::Paren(inner) => digits(inner),
            Expr::Integer(value) => Some(value.unsigned_abs().to_string().len() as u32),
            Expr::Binary(left, BinOp::Add | BinOp::Subtract | BinOp::Multiply, right) => {
                Some(digits(left)?.max(digits(right)?))
            }
            _ => None,
        }
    }
    match expr {
        Expr::Paren(inner) => literal_message_format(inner),
        Expr::Binary(_, BinOp::Divide, _) if integer_literals_only(expr) => Some((15, 5)),
        Expr::Binary(_, BinOp::Add | BinOp::Subtract | BinOp::Multiply, _) => {
            digits(expr).map(|precision| (precision, 0))
        }
        _ => None,
    }
}
