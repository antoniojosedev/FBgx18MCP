use gx_engine::types::Decimal;
use serde::Deserialize;
use std::{env, fs, path::PathBuf};

#[derive(Debug, Deserialize)]
struct OracleResult {
    id: String,
    ok: bool,
    value: Option<String>,
    error: Option<String>,
}

#[derive(Debug, Deserialize)]
struct Case {
    id: String,
    operation: String,
    left: String,
    right: String,
}

#[test]
#[ignore = "requires fresh .NET oracle; run scripts/validate_decimal.py"]
fn decimal_operations_match_independent_dotnet_oracle() {
    let result_path = env::var_os("GX_ENGINE_DECIMAL_ORACLE_RESULTS")
        .map(PathBuf::from)
        .expect("run scripts/validate_decimal.py to provide fresh .NET oracle results");
    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let case_path = env::var_os("GX_ENGINE_DECIMAL_ORACLE_CASES")
        .map(PathBuf::from)
        .unwrap_or_else(|| manifest.join("scripts/decimal-oracle/cases.json"));
    let cases: Vec<Case> = serde_json::from_slice(&fs::read(case_path).unwrap()).unwrap();
    let expected: Vec<OracleResult> = serde_json::from_slice(&fs::read(result_path).unwrap())
        .expect(".NET oracle must return valid JSON");
    assert_eq!(cases.len(), expected.len(), "oracle case count changed");

    for (case, oracle) in cases.iter().zip(expected) {
        assert_eq!(
            case.id, oracle.id,
            "oracle results must preserve case order"
        );
        let left: Decimal = case.left.parse().unwrap();
        let right: Decimal = case.right.parse().unwrap();
        let actual = match case.operation.as_str() {
            "add" => left.checked_add(right),
            "sub" => left.checked_sub(right),
            "mul" => left.checked_mul(right),
            "div" => left.checked_div(right),
            operation => panic!("unsupported oracle operation {operation:?}"),
        };

        if oracle.ok {
            let expected: Decimal = oracle
                .value
                .as_deref()
                .expect("successful oracle result needs a value")
                .parse()
                .unwrap();
            assert_eq!(
                actual.unwrap_or_else(|error| panic!("{}: {error}", case.id)),
                expected,
                "{}: {} {} {}",
                case.id,
                case.left,
                case.operation,
                case.right
            );
        } else {
            assert!(
                actual.is_err(),
                "{}: expected .NET {}",
                case.id,
                oracle.error.as_deref().unwrap_or("failure")
            );
        }
    }
}
