use gx_engine::gxtest::{Assertion, TestCase, TestSuite};

#[test]
fn test_numeric_equals_assertion() {
    let pass = Assertion::NumericEquals {
        expected: 42,
        actual: 42,
        message: "Answer".to_string(),
    };
    assert!(pass.evaluate().is_ok());

    let fail = Assertion::NumericEquals {
        expected: 42,
        actual: 10,
        message: "Wrong value".to_string(),
    };
    let err = fail.evaluate().unwrap_err();
    assert!(err.contains("Expected numeric 42, got 10"));
}

#[test]
fn test_string_equals_assertion_case_sensitive_and_insensitive() {
    let exact = Assertion::StringEquals {
        expected: "GeneXus".to_string(),
        actual: "GeneXus".to_string(),
        ignore_case: false,
        message: "".to_string(),
    };
    assert!(exact.evaluate().is_ok());

    let case_diff = Assertion::StringEquals {
        expected: "GeneXus".to_string(),
        actual: "genexus".to_string(),
        ignore_case: false,
        message: "".to_string(),
    };
    assert!(case_diff.evaluate().is_err());

    let ignore_case_pass = Assertion::StringEquals {
        expected: "GeneXus".to_string(),
        actual: "genexus".to_string(),
        ignore_case: true,
        message: "".to_string(),
    };
    assert!(ignore_case_pass.evaluate().is_ok());
}

#[test]
fn test_boolean_assertions() {
    let t = Assertion::IsTrue {
        condition: true,
        message: "Must be true".to_string(),
    };
    assert!(t.evaluate().is_ok());

    let f = Assertion::IsFalse {
        condition: false,
        message: "Must be false".to_string(),
    };
    assert!(f.evaluate().is_ok());

    let t_fail = Assertion::IsTrue {
        condition: false,
        message: "Failed".to_string(),
    };
    assert!(t_fail.evaluate().is_err());
}

#[test]
fn test_explicit_fail_assertion() {
    let fail = Assertion::Fail {
        message: "Database unreachable".to_string(),
    };
    let err = fail.evaluate().unwrap_err();
    assert!(err.contains("Explicit failure: Database unreachable"));
}

#[test]
fn test_execute_suite_all_passing() {
    let suite = TestSuite {
        name: "MathTests".to_string(),
        test_cases: vec![
            TestCase {
                name: "TestAddition".to_string(),
                target_object: "Calculator".to_string(),
                assertions: vec![
                    Assertion::NumericEquals {
                        expected: 5,
                        actual: 5,
                        message: "2 + 3".to_string(),
                    },
                    Assertion::IsTrue {
                        condition: true,
                        message: "".to_string(),
                    },
                ],
            },
            TestCase {
                name: "TestString".to_string(),
                target_object: "Formatter".to_string(),
                assertions: vec![Assertion::StringEquals {
                    expected: "ABC".to_string(),
                    actual: "ABC".to_string(),
                    ignore_case: false,
                    message: "".to_string(),
                }],
            },
        ],
    };

    let report = suite.execute();
    assert_eq!(report.total, 2);
    assert_eq!(report.passed, 2);
    assert_eq!(report.failed, 0);
    assert!(report.results.iter().all(|r| r.passed));
}

#[test]
fn test_execute_suite_with_failures() {
    let suite = TestSuite {
        name: "FailingSuite".to_string(),
        test_cases: vec![TestCase {
            name: "FailingCase".to_string(),
            target_object: "OrderProc".to_string(),
            assertions: vec![
                Assertion::NumericEquals {
                    expected: 100,
                    actual: 0,
                    message: "Stock level".to_string(),
                },
                Assertion::IsTrue {
                    condition: false,
                    message: "Check active".to_string(),
                },
            ],
        }],
    };

    let report = suite.execute();
    assert_eq!(report.total, 1);
    assert_eq!(report.passed, 0);
    assert_eq!(report.failed, 1);
    assert_eq!(report.results[0].failures.len(), 2);
}

#[test]
fn test_suite_json_roundtrip() {
    let json = r#"{
        "name": "JsonSuite",
        "test_cases": [
            {
                "name": "Case1",
                "target_object": "Proc1",
                "assertions": [
                    {
                        "kind": "IsTrue",
                        "condition": true,
                        "message": "Always true"
                    }
                ]
            }
        ]
    }"#;

    let suite = TestSuite::parse(json).expect("JSON should parse");
    assert_eq!(suite.name, "JsonSuite");
    assert_eq!(suite.test_cases.len(), 1);

    let report = suite.execute();
    assert_eq!(report.passed, 1);
}

#[test]
fn test_suite_fail_closed_on_empty_name() {
    let suite = TestSuite {
        name: "".to_string(),
        test_cases: vec![],
    };
    let err = suite.validate().unwrap_err();
    assert!(err.contains("TestSuite name cannot be empty"));
}

#[test]
fn test_suite_fail_closed_on_missing_target_object() {
    let suite = TestSuite {
        name: "Suite".to_string(),
        test_cases: vec![TestCase {
            name: "Test1".to_string(),
            target_object: "".to_string(),
            assertions: vec![],
        }],
    };
    let err = suite.validate().unwrap_err();
    assert!(err.contains("must define target_object"));
}

#[test]
fn test_suite_fail_closed_on_empty_source() {
    let err = TestSuite::parse("   ").unwrap_err();
    assert!(err.contains("source cannot be empty"));
}
