use gx_engine::{
    build,
    frontend::{Stmt, parse_body},
    model::{DataType, ProcedureSource, Project},
};
use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);
struct Scratch(std::path::PathBuf);
impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-tx-control-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn execute_project(input: &Project) -> std::process::Output {
    let scratch = Scratch::new();
    let artifact = build::build(input, &scratch.0).unwrap();
    Command::new(artifact.executable).output().unwrap()
}

fn project_with_source(source: &str) -> Project {
    Project {
        format_version: 2,
        entry: "Main".into(),
        date_two_digit_year_max: None,
        record_types: vec![],
        procedures: vec![ProcedureSource {
            name: "Main".into(),
            params: vec![],
            return_type: None,
            variables: [
                (
                    "Status".into(),
                    DataType::Numeric {
                        precision: 4,
                        scale: 0,
                    },
                ),
                (
                    "Out".into(),
                    DataType::Numeric {
                        precision: 8,
                        scale: 0,
                    },
                ),
            ]
            .into_iter()
            .collect(),
            source: source.into(),
        }],
    }
}

#[test]
fn parse_commit_and_rollback_variants() {
    let source = r#"
        Commit
        Rollback
        commit()
        rollback()
        Commit;
        Rollback;
    "#;
    let stmts = parse_body(source).unwrap();
    assert_eq!(
        stmts,
        vec![
            Stmt::Commit,
            Stmt::Rollback,
            Stmt::Commit,
            Stmt::Rollback,
            Stmt::Commit,
            Stmt::Rollback,
        ]
    );
}

#[test]
fn commit_and_rollback_inside_conditionals_and_loops() {
    let source = r#"
        &Status = 1
        &Out = 0
        If &Status = 1
            &Out = 100
            Commit
        Else
            &Out = -1
            Rollback
        EndIf

        For &i = 1 To 3
            Commit
        EndFor

        Do 'Finish'

        Sub 'Finish'
            Rollback
        EndSub
    "#;
    let stmts = parse_body(source).unwrap();
    assert_eq!(stmts.len(), 6);
    match &stmts[2] {
        Stmt::If(_, yes, no) => {
            assert!(yes.contains(&Stmt::Commit));
            assert!(no.contains(&Stmt::Rollback));
        }
        other => panic!("expected Stmt::If, got {other:?}"),
    }
    match &stmts[3] {
        Stmt::For(_, _, _, _, body) => {
            assert_eq!(body, &vec![Stmt::Commit]);
        }
        other => panic!("expected Stmt::For, got {other:?}"),
    }
    match &stmts[4] {
        Stmt::DoSub(name) => {
            assert_eq!(name, "FINISH");
        }
        other => panic!("expected Stmt::DoSub, got {other:?}"),
    }
    match &stmts[5] {
        Stmt::Sub(name, body) => {
            assert_eq!(name, "FINISH");
            assert_eq!(body, &vec![Stmt::Rollback]);
        }
        other => panic!("expected Stmt::Sub, got {other:?}"),
    }
}

#[test]
fn compile_and_run_procedure_with_commit_and_rollback() {
    let source = r#"
        &Status = 1
        &Out = 42
        If &Status = 1
            Commit
        Else
            Rollback
        EndIf
        Print(&Out)
    "#;
    let project = project_with_source(source);
    let emitted = gx_engine::compile(&project).unwrap();
    assert!(emitted.contains("// commit;"));
    assert!(emitted.contains("// rollback;"));

    let output = execute_project(&project);
    assert!(output.status.success());
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(stdout.trim(), "42");
}

#[test]
fn compile_procedure_with_subroutine_commit_rollback() {
    let source = r#"
        &Out = 10
        Do 'Transact'
        Print(&Out)

        Sub 'Transact'
            &Out = &Out + 5
            Commit
            Rollback
        EndSub
    "#;
    let project = project_with_source(source);
    let output = execute_project(&project);
    assert!(output.status.success());
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(stdout.trim(), "15");
}

#[test]
fn legacy_format_version_1_rejects_commit_and_rollback() {
    let project = Project {
        format_version: 1,
        entry: "Main".into(),
        date_two_digit_year_max: None,
        record_types: vec![],
        procedures: vec![ProcedureSource {
            name: "Main".into(),
            params: vec![],
            return_type: None,
            variables: Default::default(),
            source: "Commit".into(),
        }],
    };
    let err = gx_engine::compile(&project).unwrap_err();
    assert!(err.contains("format_version 2"), "{err}");

    let project2 = Project {
        format_version: 1,
        entry: "Main".into(),
        date_two_digit_year_max: None,
        record_types: vec![],
        procedures: vec![ProcedureSource {
            name: "Main".into(),
            params: vec![],
            return_type: None,
            variables: Default::default(),
            source: "Rollback".into(),
        }],
    };
    let err2 = gx_engine::compile(&project2).unwrap_err();
    assert!(err2.contains("format_version 2"), "{err2}");
}

#[test]
fn commit_and_rollback_inside_while_loop() {
    let source = r#"
        &Count = 0
        Do While &Count < 3
            &Count = &Count + 1
            Commit
        EndDo
        Print(&Count)
    "#;
    let mut project = project_with_source(source);
    project.procedures[0].variables.insert(
        "Count".into(),
        DataType::Numeric {
            precision: 4,
            scale: 0,
        },
    );
    let output = execute_project(&project);
    assert!(output.status.success());
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(stdout.trim(), "3");
}

#[test]
fn commit_and_rollback_in_exportfile_xml_and_catalog() {
    let xml = r#"<ExportFile><Objects>
<Object guid="main-proc-id" name="MainProc" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
  <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff"><Source><![CDATA[
      &Total = 100
      If &Total > 50
          Commit
      Else
          Rollback
      EndIf
      Print(&Total)
  ]]></Source></Part>
  <Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
    <Variable Name="Total">
      <Properties>
        <Property><Name>TYPE</Name><Value>Numeric</Value></Property>
        <Property><Name>Length</Name><Value>8</Value></Property>
      </Properties>
    </Variable>
  </Part>
</Object>
</Objects></ExportFile>"#;

    let catalog = gx_engine::exchange::catalog_from_xml(xml).unwrap();
    let entry_id = gx_engine::catalog::ObjectId::new("main-proc-id").unwrap();
    let project = gx_engine::kb::project_from_catalog(&catalog, &entry_id, None).unwrap();
    let output = execute_project(&project);
    assert!(output.status.success());
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(stdout.trim(), "100");
}
