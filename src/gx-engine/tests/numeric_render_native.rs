use gx_engine::types::{Decimal, NumericType};
use std::str::FromStr;

#[test]
fn numeric_render_matches_recorded_native_runtime() {
    // `&var.ToString()` matrix captured from the GeneXus 18 .NET runtime
    // (`numeric-render-native` scan, 120 cases / 244 observations): every
    // admitted precision 1..18 with its picture bound and first value over,
    // the scale corners where the separator borrows a picture place, exact
    // halves that round past the integer bound (the rounded content may leave
    // the picture, e.g. `9.95` in Numeric(3,1) renders `10.0`), negatives
    // where the sign place is not reserved in the narrowed regime, and the
    // width 15..18 regime where the `(decimal)`-narrowed bound admits one
    // extra digit. `implicit == Str(...).lstrip()` held for all 120 cases.
    // Fixture format: `value|precision|scale=[result]`; native emits the
    // padded picture, so the fixture keeps the leading spaces.
    let lines = include_str!("fixtures/numeric_render_native.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();
    for line in lines.lines() {
        let (left, right) = line.split_once("=[").expect(line);
        let expected = right.strip_suffix(']').expect(line);
        let parts: Vec<&str> = left.split('|').collect();
        let value = Decimal::from_str(parts[0]).expect(parts[0]);
        let precision: u8 = parts[1].parse().unwrap();
        let scale: u8 = parts[2].parse().unwrap();
        total += 1;
        let got = NumericType::new(precision, scale)
            .unwrap()
            .render(value)
            .unwrap();
        if got != expected {
            failures.push(format!("{line} engine [{got}]"));
        }
    }
    assert!(total > 100, "fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}
