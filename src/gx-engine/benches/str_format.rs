//! `bench-str` counterpart to `scripts/sdk-oracle`: measures the engine's
//! `format_str` over the corpus format shared with the native probe and prints
//! one JSON report line. Run through `scripts/benchmark.py` or directly:
//!
//! `cargo bench --offline --locked --bench str_format -- <corpus.tsv> <iterations>`
//!
//! Cargo runs the target with the crate root as the working directory; pass an
//! absolute corpus path (as `scripts/benchmark.py` does).

use gx_engine::types::{Decimal, format_str};
use serde_json::json;
use std::{env, fs, hint::black_box, process::ExitCode, str::FromStr, time::Instant};

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

struct Row {
    value: Decimal,
    width: u8,
    scale: u8,
    expected: String,
}

struct Pass {
    ms: f64,
    chars: u64,
    hash: u64,
    expected_mismatches: usize,
    errors: usize,
}

fn main() -> ExitCode {
    let args: Vec<String> = env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: str_format <corpus.tsv> <iterations>");
        return ExitCode::from(2);
    }

    let rows = read_corpus(&args[1]);
    let iterations: usize = match args[2].parse() {
        Ok(iterations) if iterations > 0 => iterations,
        _ => {
            eprintln!("iterations must be positive");
            return ExitCode::from(2);
        }
    };

    // One full pass warms caches so measured passes reflect steady state.
    let warmup_ms = measure(&rows).ms;
    let mut pass_ms = Vec::with_capacity(iterations);
    let mut last = None;
    for _ in 0..iterations {
        let pass = measure(&rows);
        pass_ms.push(pass.ms);
        last = Some(pass);
    }

    let last = last.expect("positive iteration count");
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
        "chars": last.chars,
        "hash": format!("0x{:016x}", last.hash),
        "hash_units": "utf16",
        "expected_mismatches": last.expected_mismatches,
        "errors": last.errors,
    });
    println!("{report}");
    if last.expected_mismatches == 0 && last.errors == 0 {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}

fn measure(rows: &[Row]) -> Pass {
    let mut chars = 0_u64;
    let mut hash = FNV_OFFSET;
    let mut expected_mismatches = 0_usize;
    let mut errors = 0_usize;
    let started = Instant::now();
    for row in rows {
        match format_str(row.value, row.width, row.scale) {
            Ok(text) => {
                chars += text.len() as u64;
                for &unit in text.as_bytes() {
                    hash ^= u64::from(unit);
                    hash = hash.wrapping_mul(FNV_PRIME);
                }
                if text != row.expected {
                    expected_mismatches += 1;
                }
                black_box(&text);
            }
            Err(_) => errors += 1,
        }
    }

    Pass {
        ms: started.elapsed().as_secs_f64() * 1000.0,
        chars,
        hash,
        expected_mismatches,
        errors,
    }
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
