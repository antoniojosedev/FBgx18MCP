use gx_engine::date::Date;

#[test]
fn dates_cover_native_leap_empty_and_boundary_observations() {
    let leap = Date::ymd(2024, 2, 29).unwrap();
    assert_eq!((leap.year(), leap.month(), leap.day()), (2024, 2, 29));
    let next = leap.add_days(1).unwrap();
    assert_eq!((next.month(), next.day()), (3, 1));
    assert_eq!(next.difference(leap), 1);
    assert_eq!(leap.add_days(-1).unwrap().day(), 28);
    for (y, m, d) in [(2023, 2, 29), (1900, 2, 29), (2024, 0, 1), (2024, 1, 0)] {
        let empty = Date::ymd(y, m, d).unwrap();
        assert!(empty.is_empty());
        assert_eq!((empty.year(), empty.month(), empty.day()), (0, 0, 0));
        let next = empty.add_days(1).unwrap();
        assert_eq!((next.year(), next.month(), next.day()), (1, 1, 2));
    }
    assert_eq!(Date::ymd(2000, 2, 29).unwrap().day(), 29);
    let upper = Date::ymd(9999, 12, 31).unwrap();
    assert_eq!(upper.year(), 9999);
    assert!(upper.add_days(1).is_err());
    assert!(Date::empty().add_days(-1).is_err());
    assert!(upper.add_days(i64::MAX).is_err());
    assert_eq!(Date::ymd(100, 1, 1).unwrap().year(), 100);
    // Native uses an environment-dependent Calendar window. Never silently guess it.
    for y in [-1, 0, 1, 39, 40, 99, 10000] {
        assert!(Date::ymd(y, 1, 1).is_err());
    }
}

#[test]
fn gregorian_round_trip_and_day_continuity() {
    let mut previous = Date::ymd(100, 1, 1).unwrap();
    for year in 100..=9999 {
        for month in 1..=12 {
            for day in 1..=31 {
                let current = Date::ymd(year, month, day).unwrap();
                if current.is_empty() {
                    continue;
                }
                assert_eq!(
                    (current.year(), current.month(), current.day()),
                    (year, month, day)
                );
                if (year, month, day) != (100, 1, 1) {
                    assert_eq!(current.difference(previous), 1);
                }
                previous = current;
            }
        }
    }
}

#[test]
fn measured_two_digit_window_is_explicit_and_bounded() {
    for (input, expected) in [
        (0, 2000),
        (1, 2001),
        (29, 2029),
        (30, 2030),
        (39, 2039),
        (40, 1940),
        (49, 1949),
        (50, 1950),
        (99, 1999),
        (100, 100),
    ] {
        assert_eq!(
            Date::ymd_with_window(input, 1, 1, Some(2039))
                .unwrap()
                .year(),
            expected
        );
    }
    assert_eq!(
        Date::ymd_with_window(39, 1, 1, Some(2029)).unwrap().year(),
        1939
    );
    assert!(Date::ymd_with_window(1, 1, 1, Some(98)).is_err());
    assert!(Date::ymd_with_window(1, 1, 1, Some(10000)).is_err());
    assert!(Date::ymd_with_window(-1, 1, 1, Some(2039)).is_err());
}

#[test]
fn calendar_shifts_clamp_like_measured_native_dates() {
    let start = Date::ymd(2024, 1, 31).unwrap();
    let next = start.add_months(1).unwrap();
    assert_eq!((next.year(), next.month(), next.day()), (2024, 2, 29));
    let previous = start.add_months(-1).unwrap();
    assert_eq!(
        (previous.year(), previous.month(), previous.day()),
        (2023, 12, 31)
    );
    let leap = Date::ymd(2024, 2, 29).unwrap();
    let next = leap.add_years(1).unwrap();
    assert_eq!((next.year(), next.month(), next.day()), (2025, 2, 28));
    assert_eq!(leap.add_years(-4).unwrap().day(), 29);
    let empty_month = Date::empty().add_months(1).unwrap();
    assert_eq!(
        (empty_month.year(), empty_month.month(), empty_month.day()),
        (1, 2, 1)
    );
    let empty_year = Date::empty().add_years(1).unwrap();
    assert_eq!(
        (empty_year.year(), empty_year.month(), empty_year.day()),
        (2, 1, 1)
    );
    assert!(Date::empty().add_months(-1).is_err());
    assert!(Date::ymd(9999, 12, 31).unwrap().add_months(1).is_err());
    for n in [i64::MIN, i64::MAX] {
        assert!(start.add_months(n).is_err());
        assert!(start.add_years(n).is_err());
    }
}

#[test]
fn calendar_numeric_conversion_does_not_round_or_wrap() {
    use gx_engine::types::Decimal;
    assert_eq!("1.000".parse::<Decimal>().unwrap().to_i64().unwrap(), 1);
    for value in ["0.1", "-0.1", "9223372036854775808", "-9223372036854775809"] {
        assert!(
            value.parse::<Decimal>().unwrap().to_i64().is_err(),
            "{value}"
        );
    }
}
