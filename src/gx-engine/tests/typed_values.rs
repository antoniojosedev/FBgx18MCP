use gx_engine::types::{Decimal, NumericType, Value};

fn decimal(text: &str) -> Decimal {
    text.parse().unwrap()
}

#[test]
fn decimal_storage_is_distinct_from_rendering() {
    let numeric = NumericType::new(5, 2).unwrap();
    let value = numeric.assign(decimal("1.235")).unwrap();
    assert_eq!(value, decimal("1.235"));
    // Native `&var.ToString()` emits the padded picture (numeric-render-native
    // scan: raw " 1.24" for Numeric(5,2)).
    assert_eq!(numeric.render(value).unwrap(), " 1.24");
    assert_eq!(numeric.render(decimal("-1.235")).unwrap(), "-1.24");
    assert_eq!(numeric.render(decimal("1.234")).unwrap(), " 1.23");
    assert_eq!(numeric.render(decimal("0")).unwrap(), " 0.00");
    assert_eq!(numeric.render(decimal("-0.004")).unwrap(), " 0.00");
}

#[test]
fn integer_assignment_truncates_but_never_wraps() {
    let whole = NumericType::new(3, 0).unwrap();
    assert_eq!(whole.assign(decimal("1.5")).unwrap(), decimal("1"));
    assert_eq!(whole.assign(decimal("-1.5")).unwrap(), decimal("-1"));
    assert_eq!(whole.assign(decimal("1000")).unwrap(), decimal("1000"));
    assert_eq!(whole.render(decimal("1000")).unwrap(), "***");
    // A documented safety divergence from native Int16 storage wrapping.
    assert!(whole.assign(decimal("32770")).is_err());
}

#[test]
fn decimal_to_i64_preserves_checked_integral_conversion() {
    assert_eq!(decimal("9223372036854775807").to_i64().unwrap(), i64::MAX);
    assert_eq!(decimal("-9223372036854775808").to_i64().unwrap(), i64::MIN);
    assert_eq!(decimal("42.000").to_i64().unwrap(), 42);
    assert!(decimal("42.5").to_i64().is_err());
    assert!(decimal("9223372036854775808").to_i64().is_err());
    assert!(decimal("-9223372036854775809").to_i64().is_err());
}

#[test]
fn decimal_truncate_to_i64_truncates_toward_zero_for_indices() {
    assert_eq!(decimal("42.000").truncate_to_i64().unwrap(), 42);
    assert_eq!(decimal("1.9").truncate_to_i64().unwrap(), 1);
    assert_eq!(decimal("2.7").truncate_to_i64().unwrap(), 2);
    assert_eq!(decimal("0.9").truncate_to_i64().unwrap(), 0);
    assert_eq!(decimal("-0.5").truncate_to_i64().unwrap(), 0);
    assert_eq!(decimal("-1.9").truncate_to_i64().unwrap(), -1);
    assert_eq!(
        decimal("9223372036854775807").truncate_to_i64().unwrap(),
        i64::MAX
    );
    assert_eq!(
        decimal("-9223372036854775808").truncate_to_i64().unwrap(),
        i64::MIN
    );
    assert!(decimal("9223372036854775808").truncate_to_i64().is_err());
}

#[test]
fn decimal_math_is_exact_and_bounded() {
    assert_eq!(
        decimal("0.1").checked_add(decimal("0.2")).unwrap(),
        decimal("0.3")
    );
    assert_eq!(
        decimal("1.235").checked_add(decimal("0.001")).unwrap(),
        decimal("1.236")
    );
    assert_eq!(
        decimal("-2.5").checked_mul(decimal("4")).unwrap(),
        decimal("-10")
    );
    assert_eq!(
        decimal("1").checked_div(decimal("8")).unwrap(),
        decimal("0.125")
    );
    assert_eq!(
        decimal("-1").checked_div(decimal("8")).unwrap(),
        decimal("-0.125")
    );
    assert!(decimal("1").checked_div(decimal("0")).is_err());
    assert!(
        decimal("79228162514264337593543950335")
            .checked_add(decimal("1"))
            .is_err()
    );
    assert!(
        decimal("79228162514264337593543950335")
            .checked_mul(decimal("2"))
            .is_err()
    );
    assert_eq!(decimal("1.000"), decimal("1"));
}

#[test]
fn decimal_arithmetic_matches_dotnet_precision_boundaries() {
    const MAX: &str = "79228162514264337593543950335";
    const MAX_MINUS_TWO: &str = "79228162514264337593543950333";

    // A 96-bit operand aligned to scale 28 needs a >128-bit intermediate,
    // but the correctly rounded result is still representable.
    assert_eq!(
        decimal(MAX)
            .checked_add(decimal("-0.0000000000000000000000000001"))
            .unwrap(),
        decimal(MAX)
    );
    assert_eq!(
        decimal(MAX).checked_add(decimal("0.1")).unwrap(),
        decimal(MAX)
    );
    assert!(decimal(MAX).checked_add(decimal("0.5")).is_err());

    // Products that exceed 96 coefficient bits are reduced in scale and
    // rounded to nearest-even rather than rejected or silently truncated.
    assert_eq!(
        decimal(MAX).checked_mul(decimal("0.2")).unwrap(),
        decimal("15845632502852867518708790067")
    );
    assert_eq!(
        decimal(MAX).checked_mul(decimal("0.5")).unwrap(),
        decimal("39614081257132168796771975168")
    );
    assert_eq!(
        decimal(MAX_MINUS_TWO).checked_mul(decimal("0.5")).unwrap(),
        decimal("39614081257132168796771975166")
    );

    assert_eq!(
        decimal("1").checked_div(decimal("3")).unwrap(),
        decimal("0.3333333333333333333333333333")
    );
    assert_eq!(
        decimal("1").checked_div(decimal("6")).unwrap(),
        decimal("0.1666666666666666666666666667")
    );
    assert_eq!(
        decimal("0.0000000000000000000000000001")
            .checked_div(decimal("3"))
            .unwrap(),
        decimal("0")
    );
    assert!(decimal(MAX).checked_div(decimal("0.1")).is_err());
}

#[test]
fn decimal_signed_zero_is_canonical_and_does_not_leak_a_sign() {
    let negative_zero = decimal("-0");
    assert_eq!(negative_zero, decimal("0"));
    assert_eq!(negative_zero.to_string(), "0");
    assert_eq!(
        negative_zero.checked_mul(decimal("-2")).unwrap(),
        decimal("0")
    );
    assert_eq!(
        negative_zero.checked_div(decimal("3")).unwrap(),
        decimal("0")
    );
}

#[test]
fn numeric_rendering_matches_observed_width_behavior() {
    let amount = NumericType::new(5, 2).unwrap();
    let whole = NumericType::new(3, 0).unwrap();
    assert_eq!(
        NumericType::new(18, 10)
            .unwrap()
            .render(decimal("79228162514264337593543950335"))
            .unwrap(),
        "******************"
    );
    assert_eq!(amount.render(decimal("1000.12")).unwrap(), "*****");
    assert_eq!(
        amount
            .render(decimal("79228162514264337593543950335"))
            .unwrap(),
        "*****"
    );
    assert_eq!(amount.render(decimal("999.995")).unwrap(), "*****");
    assert_eq!(whole.render(decimal("-1000")).unwrap(), "***");
    assert_eq!(amount.render(decimal("0.125")).unwrap(), " 0.13");
    assert_eq!(amount.render(decimal("-0.125")).unwrap(), "-0.13");
    assert_eq!(whole.assign(decimal("1.55")).unwrap(), decimal("1"));
}

#[test]
fn types_reject_mixed_values_and_invalid_declarations() {
    assert!(Value::Boolean(true).as_boolean().unwrap());
    assert!(Value::Numeric(decimal("1")).as_boolean().is_err());
    assert!(Value::Boolean(false).as_numeric().is_err());
    assert!(NumericType::new(0, 0).is_err());
    assert!(NumericType::new(19, 0).is_err());
    assert!(NumericType::new(3, 4).is_err());
    for invalid in [
        "",
        "1e3",
        " 1",
        "1 ",
        ".",
        "1.2.3",
        "NaN",
        "79228162514264337593543950336",
        "0.00000000000000000000000000001",
    ] {
        assert!(invalid.parse::<Decimal>().is_err(), "accepted {invalid:?}");
    }
}

#[test]
fn integer_storage_width_boundaries_are_checked() {
    for (precision, min, max) in [
        (1, -32768_i128, 32767_i128),
        (2, -32768, 32767),
        (3, -32768, 32767),
        (4, -32768, 32767),
        (5, -2147483648, 2147483647),
        (6, -2147483648, 2147483647),
        (9, -2147483648, 2147483647),
        (10, -9223372036854775808, 9223372036854775807),
        (12, -9223372036854775808, 9223372036854775807),
        (18, -9223372036854775808, 9223372036854775807),
    ] {
        let data_type = NumericType::new(precision, 0).unwrap();
        for value in [min, max] {
            assert_eq!(
                data_type.assign(decimal(&value.to_string())).unwrap(),
                decimal(&value.to_string())
            );
        }
        for value in [min - 1, max + 1] {
            assert!(
                data_type.assign(decimal(&value.to_string())).is_err(),
                "precision={precision}, value={value}"
            );
        }
    }
}
