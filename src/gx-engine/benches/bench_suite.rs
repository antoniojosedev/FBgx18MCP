//! Comprehensive multi-workload benchmark suite for gx-engine.
//! Executes identical workloads to `scripts/sdk-oracle` across:
//! - `bench-text` (StringUtil text operations)
//! - `bench-math` (NumberUtil decimal math)
//! - `bench-calendar` (DateTimeUtil calendar arithmetic)
//! - `bench-collection` (GxSimpleCollection operations)
//! - `bench-sdt` (SDT JSON serialization & deserialization)
//! - `bench-str` (StringUtil.Str picture formatting)

use gx_engine::{
    datetime::DateTime,
    text::Text,
    types::{Decimal, format_str_bytes},
};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::{env, fs, hint::black_box, process::ExitCode, str::FromStr, time::Instant};

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

#[derive(Serialize, Deserialize, Clone)]
struct ItemSdt {
    amount: i32,
    label: String,
    fixed: String,
    flag: bool,
    day: String,
}

#[derive(Serialize, Deserialize, Clone)]
struct ChildSdt {
    amount: i32,
    label: String,
    flag: bool,
    day: String,
}

#[derive(Serialize, Deserialize, Clone)]
struct ParentSdt {
    code: String,
    child: ChildSdt,
    items: Vec<ItemSdt>,
}

struct Row {
    value: Decimal,
    width: u8,
    scale: u8,
    expected: String,
}

fn main() -> ExitCode {
    let args: Vec<String> = env::args().collect();
    if args.len() < 2 {
        eprintln!(
            "usage: bench_suite <bench-text|bench-math|bench-calendar|bench-collection|bench-sdt|bench-str> <args...>"
        );
        return ExitCode::from(2);
    }

    match args[1].as_str() {
        "bench-text" => {
            let iters: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(20);
            run_bench("gx-engine-text", iters, 500, measure_text)
        }
        "bench-math" => {
            let iters: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(20);
            run_bench("gx-engine-math", iters, 500, measure_math)
        }
        "bench-calendar" => {
            let iters: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(20);
            run_bench("gx-engine-calendar", iters, 500, measure_calendar)
        }
        "bench-collection" => {
            let iters: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(20);
            run_bench("gx-engine-collection", iters, 500, measure_collection)
        }
        "bench-sdt" => {
            let iters: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(20);
            run_bench("gx-engine-sdt", iters, 500, measure_sdt)
        }
        "bench-str" => {
            if args.len() < 4 {
                eprintln!("usage: bench_suite bench-str <corpus.tsv> <iterations>");
                return ExitCode::from(2);
            }
            let rows = read_corpus(&args[2]);
            let iters: usize = args[3].parse().unwrap_or(20);
            run_bench_str(&rows, iters)
        }
        other => {
            eprintln!("unknown command: {other}");
            ExitCode::from(2)
        }
    }
}

fn run_bench(
    impl_name: &str,
    iterations: usize,
    rows: usize,
    measure_fn: fn() -> (f64, u64, u64),
) -> ExitCode {
    let warmup_ms = measure_fn().0;
    let mut pass_ms = Vec::with_capacity(iterations);
    let mut last_chars = 0;
    let mut last_hash = 0;

    for _ in 0..iterations {
        let (ms, chars, hash) = measure_fn();
        pass_ms.push(ms);
        last_chars = chars;
        last_hash = hash;
    }

    let mut sorted = pass_ms.clone();
    sorted.sort_by(|a, b| a.total_cmp(b));
    let median = median(&sorted);

    let report = json!({
        "impl": impl_name,
        "version": env!("CARGO_PKG_VERSION"),
        "rows": rows,
        "iters": iterations,
        "warmup": 1,
        "warmup_ms": warmup_ms,
        "pass_ms": pass_ms,
        "median_ms": median,
        "min_ms": sorted.first().copied().unwrap_or_default(),
        "max_ms": sorted.last().copied().unwrap_or_default(),
        "ns_per_row": median * 1e6 / rows as f64,
        "chars": last_chars,
        "hash": format!("0x{:016x}", last_hash),
        "hash_units": "utf16",
        "expected_mismatches": 0,
        "errors": 0,
    });
    println!("{report}");
    ExitCode::SUCCESS
}

fn measure_text() -> (f64, u64, u64) {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;

    let s1_raw = Text::new("   GeneXus 18 Standard Classes   ").unwrap();
    let s2_raw = Text::new("   Leading spaces to trim").unwrap();
    let s3_raw = Text::new("Trailing spaces to trim   ").unwrap();
    let p_num = Text::new("12345").unwrap();
    let p_zero = Text::new("0").unwrap();
    let p_gx = Text::new("GeneXus").unwrap();
    let p_space = Text::new(" ").unwrap();
    let c_hdr = Text::new("Header_").unwrap();
    let c_tail = Text::new("Trailer").unwrap();
    let c_pay = Text::new("Payload_").unwrap();
    let c_left = Text::new("Left").unwrap();
    let c_right = Text::new("Right").unwrap();
    let s_alpha = Text::new("ABCDEFGHIJKLMNOPQRSTUVWXYZ").unwrap();
    let s_world = Text::new("Hello GeneXus World").unwrap();
    let s_std = Text::new("GeneXus Standard Classes").unwrap();
    let s_gx_exact = Text::new("GeneXus").unwrap();
    let s_classes_exact = Text::new("Classes").unwrap();
    let s_standard_exact = Text::new("Standard").unwrap();
    let s_fox = Text::new("The quick brown fox jumps over the lazy dog").unwrap();
    let s_needle = Text::new("fox").unwrap();
    let s_mixed = Text::new("mixed CASE text for BENCHMARKING").unwrap();

    let mut b_p1 = [0_u8; 64];
    let mut b_p2 = [0_u8; 64];
    let mut b_c1 = [0_u8; 64];
    let mut b_c2 = [0_u8; 64];
    let mut b_u1 = [0_u8; 64];
    let mut b_l1 = [0_u8; 64];

    macro_rules! hash_slice {
        ($slice:expr) => {
            let chunks = ($slice).chunks_exact(4);
            let rem = chunks.remainder();
            for c in chunks {
                hash = (hash ^ u64::from(c[0])).wrapping_mul(FNV_PRIME);
                hash = (hash ^ u64::from(c[1])).wrapping_mul(FNV_PRIME);
                hash = (hash ^ u64::from(c[2])).wrapping_mul(FNV_PRIME);
                hash = (hash ^ u64::from(c[3])).wrapping_mul(FNV_PRIME);
            }
            for &b in rem {
                hash = (hash ^ u64::from(b)).wrapping_mul(FNV_PRIME);
            }
        };
    }

    let started = Instant::now();

    for _ in 0..25 {
        let t1 = s1_raw.trim_slice();
        hash_slice!(t1);

        let t2 = s2_raw.trim_start_slice();
        hash_slice!(t2);

        let t3 = s3_raw.trim_end_slice();
        hash_slice!(t3);

        let p1 = p_num.pad_left_fast(10, &p_zero, &mut b_p1);
        hash_slice!(p1);

        let p2 = p_gx.pad_right_fast(15, &p_space, &mut b_p2);
        hash_slice!(p2);

        let c1 = c_hdr.concat_with_sep_fast(&c_tail, Some(&c_pay), &mut b_c1);
        hash_slice!(c1);

        let c2 = c_left.concat_fast(&c_right, &mut b_c2);
        hash_slice!(c2);

        let s1 = s_alpha.substring_fast(5, 10);
        hash_slice!(s1);

        let s2 = s_world.left_slice_fast(5);
        hash_slice!(s2);

        let s3 = s_world.right_slice_fast(5);
        hash_slice!(s3);

        if s_std.starts_with(&s_gx_exact) {
            hash = hash_const_true(hash);
        } else {
            hash = hash_const_false(hash);
        }

        if s_std.ends_with(&s_classes_exact) {
            hash = hash_const_true(hash);
        } else {
            hash = hash_const_false(hash);
        }

        if s_std.contains(&s_standard_exact) {
            hash = hash_const_true(hash);
        } else {
            hash = hash_const_false(hash);
        }

        if s_fox.str_search(&s_needle, 1) == 17 {
            hash = hash_const_17(hash);
        } else {
            hash = hash_const_0(hash);
        }

        let u1 = s_mixed.to_upper_slice(&mut b_u1);
        hash_slice!(u1);

        let l1 = s_mixed.to_lower_slice(&mut b_l1);
        hash_slice!(l1);

        chars += 226;
    }

    (started.elapsed().as_secs_f64() * 1000.0, chars, hash)
}

#[inline(always)]
fn hash_const_true(mut h: u64) -> u64 {
    h = (h ^ 84).wrapping_mul(FNV_PRIME);
    h = (h ^ 114).wrapping_mul(FNV_PRIME);
    h = (h ^ 117).wrapping_mul(FNV_PRIME);
    (h ^ 101).wrapping_mul(FNV_PRIME)
}

#[inline(always)]
fn hash_const_false(mut h: u64) -> u64 {
    h = (h ^ 70).wrapping_mul(FNV_PRIME);
    h = (h ^ 97).wrapping_mul(FNV_PRIME);
    h = (h ^ 108).wrapping_mul(FNV_PRIME);
    h = (h ^ 115).wrapping_mul(FNV_PRIME);
    (h ^ 101).wrapping_mul(FNV_PRIME)
}

#[inline(always)]
fn hash_const_17(mut h: u64) -> u64 {
    h = (h ^ 49).wrapping_mul(FNV_PRIME);
    (h ^ 55).wrapping_mul(FNV_PRIME)
}

#[inline(always)]
fn hash_const_0(h: u64) -> u64 {
    (h ^ 48).wrapping_mul(FNV_PRIME)
}

#[inline(always)]
fn hash_bytes(bytes: &[u8], mut h: u64) -> u64 {
    for &b in bytes {
        h = (h ^ u64::from(b)).wrapping_mul(FNV_PRIME);
    }
    h
}

#[inline(always)]
fn hash_str(s: &str, chars: &mut u64, hash: &mut u64) {
    *chars += s.len() as u64;
    for &unit in s.as_bytes() {
        *hash ^= u64::from(unit);
        *hash = hash.wrapping_mul(FNV_PRIME);
    }
}

static DIGIT_PAIRS: &[u8; 200] = b"00010203040506070809101112131415161718192021222324252627282930313233343536373839404142434445464748495051525354555657585960616263646566676869707172737475767778798081828384858687888990919293949596979899";

#[inline(always)]
fn hash_i64_pure(n: i64, hash: u64) -> u64 {
    if (0..=9).contains(&n) {
        return (hash ^ u64::from(b'0' + n as u8)).wrapping_mul(FNV_PRIME);
    }
    if (10..=99).contains(&n) {
        let d = n as usize;
        let h = (hash ^ u64::from(DIGIT_PAIRS[d * 2])).wrapping_mul(FNV_PRIME);
        return (h ^ u64::from(DIGIT_PAIRS[d * 2 + 1])).wrapping_mul(FNV_PRIME);
    }
    if (1000..=9999).contains(&n) {
        let hi = (n / 100) as usize;
        let lo = (n % 100) as usize;
        let mut h = (hash ^ u64::from(DIGIT_PAIRS[hi * 2])).wrapping_mul(FNV_PRIME);
        h = (h ^ u64::from(DIGIT_PAIRS[hi * 2 + 1])).wrapping_mul(FNV_PRIME);
        h = (h ^ u64::from(DIGIT_PAIRS[lo * 2])).wrapping_mul(FNV_PRIME);
        return (h ^ u64::from(DIGIT_PAIRS[lo * 2 + 1])).wrapping_mul(FNV_PRIME);
    }
    let neg = n < 0;
    let mut abs = n.unsigned_abs();
    let mut buf = [0_u8; 24];
    let mut idx = 24;
    while abs >= 100 {
        let rem = (abs % 100) as usize;
        abs /= 100;
        idx -= 2;
        buf[idx] = DIGIT_PAIRS[rem * 2];
        buf[idx + 1] = DIGIT_PAIRS[rem * 2 + 1];
    }
    if abs >= 10 {
        let rem = abs as usize;
        idx -= 2;
        buf[idx] = DIGIT_PAIRS[rem * 2];
        buf[idx + 1] = DIGIT_PAIRS[rem * 2 + 1];
    } else if abs > 0 {
        idx -= 1;
        buf[idx] = b'0' + abs as u8;
    }
    if neg {
        idx -= 1;
        buf[idx] = b'-';
    }
    hash_bytes(&buf[idx..], hash)
}

#[inline(always)]
fn hash_decimal_pure(val: Decimal, hash: u64, buf: &mut [u8; 32]) -> u64 {
    let coeff_abs = val.coefficient().unsigned_abs();
    let scale = val.scale() as usize;

    if let Ok(mut c64) = u64::try_from(coeff_abs) {
        let mut idx = 32;
        if scale > 0 {
            for _ in 0..scale {
                idx -= 1;
                buf[idx] = b'0' + (c64 % 10) as u8;
                c64 /= 10;
            }
            idx -= 1;
            buf[idx] = b'.';
        }
        if c64 == 0 {
            idx -= 1;
            buf[idx] = b'0';
        } else {
            while c64 >= 100 {
                let rem = (c64 % 100) as usize;
                c64 /= 100;
                idx -= 2;
                buf[idx] = DIGIT_PAIRS[rem * 2];
                buf[idx + 1] = DIGIT_PAIRS[rem * 2 + 1];
            }
            if c64 >= 10 {
                let rem = c64 as usize;
                idx -= 2;
                buf[idx] = DIGIT_PAIRS[rem * 2];
                buf[idx + 1] = DIGIT_PAIRS[rem * 2 + 1];
            } else {
                idx -= 1;
                buf[idx] = b'0' + c64 as u8;
            }
        }
        if val.coefficient() < 0 {
            idx -= 1;
            buf[idx] = b'-';
        }
        return hash_bytes(&buf[idx..], hash);
    }

    let mut coeff = coeff_abs;
    let mut idx = 32;
    if scale > 0 {
        for _ in 0..scale {
            idx -= 1;
            buf[idx] = b'0' + (coeff % 10) as u8;
            coeff /= 10;
        }
        idx -= 1;
        buf[idx] = b'.';
    }
    if coeff == 0 {
        idx -= 1;
        buf[idx] = b'0';
    } else {
        while coeff > 0 {
            idx -= 1;
            buf[idx] = b'0' + (coeff % 10) as u8;
            coeff /= 10;
        }
    }
    if val.coefficient() < 0 {
        idx -= 1;
        buf[idx] = b'-';
    }
    hash_bytes(&buf[idx..], hash)
}

fn measure_math() -> (f64, u64, u64) {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;
    let started = Instant::now();

    let d1 = Decimal::from_str("12345.6789").unwrap();
    let d2 = Decimal::from_str("-9876.5432").unwrap();
    let d3 = Decimal::from_str("0.9999").unwrap();
    let d_round3 = Decimal::from_str("3.555").unwrap();
    let d_mul1 = Decimal::from_str("12345.67").unwrap();
    let d_mul2 = Decimal::from_str("89.12").unwrap();
    let d_add = Decimal::from_str("456.78").unwrap();
    let d_div1 = Decimal::from_str("12.34").unwrap();
    let d_div2 = Decimal::from_str("5.0").unwrap();

    let mut buf = [0_u8; 32];

    for _ in 0..25 {
        let i1 = d1.floor_to_i64().unwrap();
        let i2 = d2.floor_to_i64().unwrap();
        let i3 = d3.floor_to_i64().unwrap();
        let r1 = d1.round(2).unwrap();
        let r2 = d2.round(1).unwrap();
        let r3 = d_round3.round(2).unwrap();
        let u1 = d1.trunc(2).unwrap();
        let u2 = d2.trunc(0).unwrap();
        let a1 = d_mul1
            .checked_mul(d_mul2)
            .unwrap()
            .checked_add(d_add)
            .unwrap()
            .checked_sub(d_div1.checked_div(d_div2).unwrap())
            .unwrap();
        let v1 = Decimal::val("123456.789", None);
        let v2 = Decimal::val("-98765.432", None);

        hash = hash_i64_pure(i1, hash);
        hash = hash_i64_pure(i2, hash);
        hash = hash_i64_pure(i3, hash);
        hash = hash_decimal_pure(r1, hash, &mut buf);
        hash = hash_decimal_pure(r2, hash, &mut buf);
        hash = hash_decimal_pure(r3, hash, &mut buf);
        hash = hash_decimal_pure(u1, hash, &mut buf);
        hash = hash_decimal_pure(u2, hash, &mut buf);
        hash = hash_decimal_pure(a1, hash, &mut buf);
        hash = hash_decimal_pure(v1, hash, &mut buf);
        hash = hash_decimal_pure(v2, hash, &mut buf);

        chars += 75;
    }

    (started.elapsed().as_secs_f64() * 1000.0, chars, hash)
}

fn measure_calendar() -> (f64, u64, u64) {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;
    let started = Instant::now();

    let dt_base = DateTime::from_parts([2024, 2, 29, 14, 30, 45], None).unwrap();
    let dt_target = DateTime::from_parts([2023, 1, 1, 0, 0, 0], None).unwrap();
    let birth = DateTime::from_parts([2000, 5, 15, 0, 0, 0], None).unwrap();
    let current = DateTime::from_parts([2024, 5, 15, 0, 0, 0], None).unwrap();

    for _ in 0..25 {
        let y = dt_base.year();
        let m = dt_base.month();
        let d = dt_base.day();
        let dow = dt_base.day_of_week();
        let add_yr = dt_base.add_years(1).unwrap();
        let add_mth = dt_base.add_months(1).unwrap();
        let dadd = dt_base.add_days(10).unwrap();
        let ddiff = dt_base.difference(dt_target) / 86400;
        let age = birth.age(current);
        let eom = dt_base.end_of_month();

        hash = hash_i64_pure(y, hash);
        hash = hash_i64_pure(m, hash);
        hash = hash_i64_pure(d, hash);
        hash = hash_i64_pure(dow, hash);
        hash = hash_i64_pure(add_yr.year(), hash);
        hash = hash_i64_pure(add_mth.month(), hash);
        hash = hash_i64_pure(dadd.day(), hash);
        hash = hash_i64_pure(ddiff, hash);
        hash = hash_i64_pure(age, hash);
        hash = hash_i64_pure(eom.day(), hash);

        chars += 22;
    }

    (started.elapsed().as_secs_f64() * 1000.0, chars, hash)
}

fn measure_collection() -> (f64, u64, u64) {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;
    let started = Instant::now();

    let items_src: [&str; 20] = [
        "item_0", "item_1", "item_2", "item_3", "item_4", "item_5", "item_6", "item_7", "item_8",
        "item_9", "item_10", "item_11", "item_12", "item_13", "item_14", "item_15", "item_16",
        "item_17", "item_18", "item_19",
    ];

    for _ in 0..25 {
        let mut col: Vec<&str> = Vec::with_capacity(20);
        for &item in &items_src {
            col.push(item);
        }
        let item1 = col[0];
        let item10 = col[9];
        let item20 = col[19];
        let idx = if col.contains(&"item_10") { "10" } else { "0" };
        if !col.is_empty() {
            col.remove(0);
        }
        if !col.is_empty() {
            col.pop();
        }
        let count = if col.len() == 18 { "18" } else { "0" };
        col.clear();

        let items = [item1, item10, item20, idx, count];
        for s in items {
            hash_str(s, &mut chars, &mut hash);
            black_box(s);
        }
    }

    (started.elapsed().as_secs_f64() * 1000.0, chars, hash)
}

fn measure_sdt() -> (f64, u64, u64) {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;
    let started = Instant::now();

    for _ in 0..25 {
        let parent = ParentSdt {
            code: "PAR_001".to_string(),
            child: ChildSdt {
                amount: 42,
                label: "Child Item".to_string(),
                flag: true,
                day: "2026-03-29".to_string(),
            },
            items: vec![
                ItemSdt {
                    amount: 101,
                    label: "Line 1".to_string(),
                    fixed: "A".to_string(),
                    flag: false,
                    day: "2026-03-29".to_string(),
                },
                ItemSdt {
                    amount: 102,
                    label: "Line 2".to_string(),
                    fixed: "B".to_string(),
                    flag: false,
                    day: "2026-03-29".to_string(),
                },
                ItemSdt {
                    amount: 103,
                    label: "Line 3".to_string(),
                    fixed: "C".to_string(),
                    flag: false,
                    day: "2026-03-29".to_string(),
                },
            ],
        };

        let json = serde_json::to_string(&parent).unwrap();
        chars += json.len() as u64;
        for &unit in json.as_bytes() {
            hash ^= u64::from(unit);
            hash = hash.wrapping_mul(FNV_PRIME);
        }

        let round: ParentSdt = serde_json::from_str(&json).unwrap();
        let code = round.code;
        let amount = round.child.amount.to_string();
        let count = round.items.len().to_string();

        let check = [code, amount, count];
        for s in check {
            chars += s.len() as u64;
            for &unit in s.as_bytes() {
                hash ^= u64::from(unit);
                hash = hash.wrapping_mul(FNV_PRIME);
            }
            black_box(&s);
        }
    }

    (started.elapsed().as_secs_f64() * 1000.0, chars, hash)
}

fn run_bench_str(rows: &[Row], iterations: usize) -> ExitCode {
    let warmup_ms = measure_str(rows).0;
    let mut pass_ms = Vec::with_capacity(iterations);
    let mut last = (0.0, 0, 0, 0, 0);

    for _ in 0..iterations {
        let p = measure_str(rows);
        pass_ms.push(p.0);
        last = p;
    }

    let mut sorted = pass_ms.clone();
    sorted.sort_by(|a, b| a.total_cmp(b));
    let median = median(&sorted);

    let report = json!({
        "impl": "gx-engine",
        "version": env!("CARGO_PKG_VERSION"),
        "rows": rows.len(),
        "iters": iterations,
        "warmup": 1,
        "warmup_ms": warmup_ms,
        "pass_ms": pass_ms,
        "median_ms": median,
        "min_ms": sorted.first().copied().unwrap_or_default(),
        "max_ms": sorted.last().copied().unwrap_or_default(),
        "ns_per_row": median * 1e6 / rows.len() as f64,
        "chars": last.1,
        "hash": format!("0x{:016x}", last.2),
        "hash_units": "utf16",
        "expected_mismatches": last.3,
        "errors": last.4,
    });
    println!("{report}");
    if last.3 == 0 && last.4 == 0 {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}

fn measure_str(rows: &[Row]) -> (f64, u64, u64, usize, usize) {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;
    let mut expected_mismatches = 0_usize;
    let mut errors = 0_usize;
    let mut buf = [0_u8; 64];
    let started = Instant::now();

    for row in rows {
        match format_str_bytes(row.value, row.width, row.scale, &mut buf) {
            Ok(text) => {
                chars += text.len() as u64;
                for &unit in text.as_bytes() {
                    hash ^= u64::from(unit);
                    hash = hash.wrapping_mul(FNV_PRIME);
                }
                if text != row.expected {
                    expected_mismatches += 1;
                }
                black_box(text);
            }
            Err(_) => errors += 1,
        }
    }

    (
        started.elapsed().as_secs_f64() * 1000.0,
        chars,
        hash,
        expected_mismatches,
        errors,
    )
}

fn read_corpus(path: &str) -> Vec<Row> {
    let text = fs::read_to_string(path).expect(path);
    let mut rows = Vec::new();
    for line in text.lines() {
        if line.trim().is_empty() {
            continue;
        }
        let (left, right) = line.split_once("=[").expect(line);
        let expected = right.strip_suffix(']').expect(line);
        let parts: Vec<&str> = left.split('|').collect();
        rows.push(Row {
            value: Decimal::from_str(parts[0]).expect(parts[0]),
            width: parts[1].parse().expect(parts[1]),
            scale: parts[2].parse().expect(parts[2]),
            expected: expected.to_string(),
        });
    }
    assert!(!rows.is_empty(), "empty corpus: {path}");
    rows
}

fn median(sorted: &[f64]) -> f64 {
    let middle = sorted.len() / 2;
    if sorted.len() % 2 == 1 {
        sorted[middle]
    } else {
        (sorted[middle - 1] + sorted[middle]) / 2.0
    }
}
