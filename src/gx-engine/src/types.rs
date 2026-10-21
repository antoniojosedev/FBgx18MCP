//! Typed semantic values. Decimal storage and Numeric display width are separate.
//! Checked storage deliberately diagnoses overflow instead of native integer wrapping.

pub use crate::decimal::{
    Decimal, POW10_I128, POW10_U64, POW10_U128, format_str, format_str_bytes,
};
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NumericType {
    precision: u8,
    scale: u8,
}

impl NumericType {
    pub fn new(precision: u8, scale: u8) -> Result<Self, String> {
        if !(1..=18).contains(&precision) || scale > precision {
            return Err("supported Numeric requires precision 1..18 and scale <= precision".into());
        }
        Ok(Self { precision, scale })
    }

    pub fn assign(self, value: Decimal) -> Result<Decimal, String> {
        if self.scale != 0 {
            return Ok(value);
        }
        let whole = value.truncate();
        // GeneXus 18 .NET boundary probes observed Int16/Int32/Int64 storage.
        // Preserve those ranges, but diagnose overflow rather than reproduce wrapping.
        let (minimum, maximum) = match self.precision {
            1..=4 => (i128::from(i16::MIN), i128::from(i16::MAX)),
            5..=9 => (i128::from(i32::MIN), i128::from(i32::MAX)),
            _ => (i128::from(i64::MIN), i128::from(i64::MAX)),
        };
        if !(minimum..=maximum).contains(&whole) {
            return Err(
                "numeric integer storage overflow (native wrapping is not reproduced)".into(),
            );
        }
        Decimal::new(whole, 0)
    }

    /// Render the stored value exactly as native `&var.ToString()` does.
    ///
    /// Native evidence (`numeric-render-native` scans over precision 1..18):
    /// the implicit conversion is `StringUtil.Str(value, precision, scale)`
    /// itself - padding included - so the picture pipeline (bound narrowing
    /// through `(decimal)`, separator place, star count, rounding past the
    /// integer bound) is shared with [`format_str`] instead of re-derived.
    pub fn render(self, value: Decimal) -> Result<String, String> {
        format_str(value, self.precision, self.scale)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Value {
    Boolean(bool),
    Numeric(Decimal),
}

impl Value {
    pub fn as_boolean(self) -> Result<bool, String> {
        match self {
            Self::Boolean(value) => Ok(value),
            Self::Numeric(_) => Err("expected Boolean, got Numeric".into()),
        }
    }

    pub fn as_numeric(self) -> Result<Decimal, String> {
        match self {
            Self::Numeric(value) => Ok(value),
            Self::Boolean(_) => Err("expected Numeric, got Boolean".into()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn numeric_type_and_value_operations() {
        let n_type = NumericType::new(10, 2).expect("valid numeric type");
        let val = Decimal::new(12345, 2).expect("valid decimal");
        let assigned = n_type.assign(val).expect("valid assignment");
        assert_eq!(assigned, val);

        let value_enum = Value::Numeric(assigned);
        assert_eq!(value_enum.as_numeric().unwrap(), val);

        let bool_val = Value::Boolean(true);
        assert!(bool_val.as_numeric().is_err());
    }
}
