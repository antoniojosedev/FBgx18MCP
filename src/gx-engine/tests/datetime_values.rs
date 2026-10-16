use gx_engine::datetime::DateTime;

#[test]
fn native_datetime_rollover_empty_and_invalid_parts() {
    let value = DateTime::from_parts([2024, 2, 29, 23, 59, 58], None).unwrap();
    assert_eq!(
        (
            value.year(),
            value.month(),
            value.day(),
            value.hour(),
            value.minute(),
            value.second()
        ),
        (2024, 2, 29, 23, 59, 58)
    );
    let next = value.add_seconds(3).unwrap();
    assert_eq!(
        (next.month(), next.day(), next.hour(), next.second()),
        (3, 1, 0, 1)
    );
    assert_eq!(next.difference(value), 3);
    assert_eq!(value.difference(next), -3);
    let empty = DateTime::empty();
    assert!(empty.is_empty());
    assert_eq!(empty.year(), 0);
    let next = empty.add_seconds(1).unwrap();
    assert_eq!(
        (next.year(), next.month(), next.day(), next.second()),
        (1, 1, 1, 1)
    );
    let invalid_date = DateTime::from_parts([2023, 2, 29, 12, 1, 1], None).unwrap();
    assert_eq!(
        (
            invalid_date.year(),
            invalid_date.hour(),
            invalid_date.minute(),
            invalid_date.second()
        ),
        (1, 12, 1, 1)
    );
    for parts in [
        [2024, 1, 1, 24, 12, 34],
        [2024, 1, 1, 12, 60, 34],
        [2024, 1, 1, 12, 34, 60],
    ] {
        let value = DateTime::from_parts(parts, None).unwrap();
        assert_eq!(
            (value.year(), value.hour(), value.minute(), value.second()),
            (2024, 0, 0, 0)
        );
    }
    assert!(DateTime::empty().add_seconds(-1).is_err());
    assert!(
        DateTime::from_parts([9999, 12, 31, 23, 59, 59], None)
            .unwrap()
            .add_seconds(1)
            .is_err()
    );
    for n in [i64::MIN, i64::MAX] {
        assert!(value.add_seconds(n).is_err());
    }
    assert_eq!(
        DateTime::from_parts([39, 1, 1, 0, 0, 0], Some(2039))
            .unwrap()
            .year(),
        2039
    );
    assert_eq!(
        DateTime::from_parts([40, 1, 1, 0, 0, 0], Some(2039))
            .unwrap()
            .year(),
        1940
    );
    assert!(DateTime::from_parts([39, 1, 1, 0, 0, 0], None).is_err());
}

#[test]
fn backwards_rollover_uses_euclidean_day_arithmetic() {
    let value = DateTime::from_parts([2024, 3, 1, 0, 0, 0], None)
        .unwrap()
        .add_seconds(-1)
        .unwrap();
    assert_eq!(
        (
            value.year(),
            value.month(),
            value.day(),
            value.hour(),
            value.minute(),
            value.second()
        ),
        (2024, 2, 29, 23, 59, 59)
    );
    assert_eq!(value.add_seconds(86401).unwrap().day(), 2);
}
