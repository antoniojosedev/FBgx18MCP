//! GXtest Unit Testing and Assertion Runner Engine (Row F4).
//!
//! Provides unit test suite modeling, assertions (NumericEquals, StringEquals,
//! True, False, Fail), test execution pipelines, and structured test reporting.

use serde::{Deserialize, Serialize};

use crate::dataview::strip_comments;

/// Types of assertions supported by the GXtest engine.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind")]
pub enum Assertion {
    NumericEquals {
        expected: i64,
        actual: i64,
        #[serde(default)]
        message: String,
    },
    StringEquals {
        expected: String,
        actual: String,
        #[serde(default)]
        ignore_case: bool,
        #[serde(default)]
        message: String,
    },
    IsTrue {
        condition: bool,
        #[serde(default)]
        message: String,
    },
    IsFalse {
        condition: bool,
        #[serde(default)]
        message: String,
    },
    Fail {
        message: String,
    },
}

impl Assertion {
    pub fn evaluate(&self) -> Result<(), String> {
        match self {
            Self::NumericEquals {
                expected,
                actual,
                message,
            } => {
                if expected == actual {
                    Ok(())
                } else {
                    let desc = if message.is_empty() {
                        format!("Expected numeric {expected}, got {actual}")
                    } else {
                        format!("{message}: Expected numeric {expected}, got {actual}")
                    };
                    Err(desc)
                }
            }
            Self::StringEquals {
                expected,
                actual,
                ignore_case,
                message,
            } => {
                let matches = if *ignore_case {
                    expected.eq_ignore_ascii_case(actual)
                } else {
                    expected == actual
                };
                if matches {
                    Ok(())
                } else {
                    let desc = if message.is_empty() {
                        format!("Expected string '{expected}', got '{actual}'")
                    } else {
                        format!("{message}: Expected string '{expected}', got '{actual}'")
                    };
                    Err(desc)
                }
            }
            Self::IsTrue { condition, message } => {
                if *condition {
                    Ok(())
                } else {
                    let desc = if message.is_empty() {
                        "Assertion failed: expected true, got false".to_string()
                    } else {
                        format!("{message}: expected true, got false")
                    };
                    Err(desc)
                }
            }
            Self::IsFalse { condition, message } => {
                if !*condition {
                    Ok(())
                } else {
                    let desc = if message.is_empty() {
                        "Assertion failed: expected false, got true".to_string()
                    } else {
                        format!("{message}: expected false, got true")
                    };
                    Err(desc)
                }
            }
            Self::Fail { message } => Err(format!("Explicit failure: {message}")),
        }
    }
}

/// A test case containing a series of assertions against a target Knowledge Base object.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TestCase {
    pub name: String,
    pub target_object: String,
    pub assertions: Vec<Assertion>,
}

impl TestCase {
    pub fn execute(&self) -> TestCaseResult {
        let mut failures = Vec::new();
        for (idx, a) in self.assertions.iter().enumerate() {
            if let Err(msg) = a.evaluate() {
                failures.push(format!("Assertion #{}: {msg}", idx + 1));
            }
        }

        let passed = failures.is_empty();
        TestCaseResult {
            name: self.name.clone(),
            target_object: self.target_object.clone(),
            passed,
            failures,
        }
    }
}

/// Result details of executing a single test case.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TestCaseResult {
    pub name: String,
    pub target_object: String,
    pub passed: bool,
    pub failures: Vec<String>,
}

/// A suite of GXtest test cases.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct TestSuite {
    pub name: String,
    pub test_cases: Vec<TestCase>,
}

/// Aggregated report of running a test suite.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SuiteExecutionReport {
    pub suite_name: String,
    pub total: usize,
    pub passed: usize,
    pub failed: usize,
    pub results: Vec<TestCaseResult>,
}

impl TestSuite {
    /// Parses a TestSuite from JSON text.
    pub fn parse(source: &str) -> Result<Self, String> {
        let clean = strip_comments(source);
        let trimmed = clean.trim();

        if trimmed.is_empty() {
            return Err("TestSuite source cannot be empty".to_string());
        }

        if trimmed.starts_with('{') {
            let suite: TestSuite = serde_json::from_str(trimmed)
                .map_err(|e| format!("Failed to parse TestSuite JSON: {e}"))?;
            suite.validate()?;
            return Ok(suite);
        }

        Err("Unsupported TestSuite format; provide valid JSON specification".to_string())
    }

    pub fn validate(&self) -> Result<(), String> {
        if self.name.is_empty() {
            return Err("TestSuite name cannot be empty".to_string());
        }
        for tc in &self.test_cases {
            if tc.name.is_empty() {
                return Err("TestCase name cannot be empty".to_string());
            }
            if tc.target_object.is_empty() {
                return Err(format!("TestCase '{}' must define target_object", tc.name));
            }
        }
        Ok(())
    }

    /// Executes all test cases in the suite and returns a summary execution report.
    pub fn execute(&self) -> SuiteExecutionReport {
        let mut results = Vec::new();
        let mut passed = 0;
        let mut failed = 0;

        for tc in &self.test_cases {
            let res = tc.execute();
            if res.passed {
                passed += 1;
            } else {
                failed += 1;
            }
            results.push(res);
        }

        SuiteExecutionReport {
            suite_name: self.name.clone(),
            total: self.test_cases.len(),
            passed,
            failed,
            results,
        }
    }
}
