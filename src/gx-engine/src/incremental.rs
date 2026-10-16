use crate::{
    backend,
    frontend::{self, Procedure},
    model::{DataType, Project},
};
use std::collections::{HashMap, HashSet, VecDeque};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Compilation {
    pub source: String,
    pub reparsed: Vec<String>,
    pub reused: Vec<String>,
    pub affected: Vec<String>,
}

#[derive(Default)]
pub struct IncrementalCompiler {
    state: Option<SessionState>,
}

struct SessionState {
    format_version: u32,
    date_two_digit_year_max: Option<u16>,
    record_types: Vec<crate::model::RecordType>,
    entry: String,
    procedures: HashMap<String, CachedProcedure>,
    calls: CallGraph,
}

struct CachedProcedure {
    params: Vec<String>,
    source: String,
    parsed: Procedure,
    variables: std::collections::BTreeMap<String, DataType>,
    return_type: Option<DataType>,
}

type CallGraph = HashMap<String, HashSet<String>>;

impl IncrementalCompiler {
    pub fn new() -> Self {
        Self::default()
    }

    /// Updates one transactional front-end session and emits a fully validated candidate.
    ///
    /// Parsed bodies are reused only when canonical parameters and source text match exactly.
    /// Backend validation/emission still runs for every revision; this is not an executable cache.
    pub fn update(&mut self, project: &Project) -> Result<Compilation, String> {
        let canonical = Project::from_json(&project.to_json()?)?;
        let old = self.state.as_ref();
        let mut procedures = HashMap::with_capacity(canonical.procedures.len());
        let mut parsed_in_project_order = Vec::with_capacity(canonical.procedures.len());
        let mut reparsed = Vec::new();
        let mut reused = Vec::new();
        let mut changed = HashSet::new();

        for source in &canonical.procedures {
            let name = source.name.clone();
            let cached = old.and_then(|state| state.procedures.get(&name));
            let next = match cached {
                Some(previous)
                    if previous.params == source.params && previous.source == source.source =>
                {
                    reused.push(name.clone());
                    if previous.variables != source.variables
                        || previous.return_type != source.return_type
                    {
                        changed.insert(name.clone());
                    }
                    let mut reused = previous.clone_for_reuse();
                    reused.variables = source.variables.clone();
                    reused.return_type = source.return_type.clone();
                    reused
                }
                _ => {
                    let parsed = frontend::parse_body(&source.source)
                        .map_err(|error| format!("procedure {}: {error}", source.name))?;
                    reparsed.push(name.clone());
                    changed.insert(name.clone());
                    CachedProcedure {
                        params: source.params.clone(),
                        source: source.source.clone(),
                        variables: source.variables.clone(),
                        return_type: source.return_type.clone(),
                        parsed: Procedure {
                            name: source.name.clone(),
                            params: source.params.clone(),
                            body: parsed,
                        },
                    }
                }
            };
            parsed_in_project_order.push(next.parsed.clone());
            procedures.insert(name, next);
        }

        let current_names: HashSet<String> = procedures.keys().cloned().collect();
        if let Some(previous) = old {
            if previous.format_version != canonical.format_version
                || previous.date_two_digit_year_max != canonical.date_two_digit_year_max
                || previous.record_types != canonical.record_types
            {
                changed.extend(current_names.iter().cloned());
            }
            for name in previous.procedures.keys() {
                if !current_names.contains(name) {
                    changed.insert(name.clone());
                }
            }
            if previous.entry != canonical.entry {
                changed.insert(previous.entry.clone());
                changed.insert(canonical.entry.clone());
            }
        }

        let calls = collect_call_graph(&parsed_in_project_order);
        let affected = affected_procedures(changed, old.map(|previous| &previous.calls), &calls);

        // Always revalidate the full candidate, even when every parsed body was reused.
        let source = if canonical.format_version == 2 {
            crate::typed_backend::emit_parsed(&canonical, &parsed_in_project_order)?
        } else {
            backend::emit(&parsed_in_project_order, &canonical.entry)?
        };

        reparsed.sort();
        reused.sort();
        let candidate = SessionState {
            format_version: canonical.format_version,
            date_two_digit_year_max: canonical.date_two_digit_year_max,
            record_types: canonical.record_types,
            entry: canonical.entry,
            procedures,
            calls,
        };
        self.state = Some(candidate);

        Ok(Compilation {
            source,
            reparsed,
            reused,
            affected,
        })
    }
}

impl CachedProcedure {
    fn clone_for_reuse(&self) -> Self {
        Self {
            params: self.params.clone(),
            source: self.source.clone(),
            parsed: self.parsed.clone(),
            variables: self.variables.clone(),
            return_type: self.return_type.clone(),
        }
    }
}

fn collect_call_graph(procedures: &[Procedure]) -> CallGraph {
    procedures
        .iter()
        .map(|procedure| {
            let callees = crate::dependencies::calls(&procedure.body);
            (procedure.name.clone(), callees)
        })
        .collect()
}

fn affected_procedures(
    changed: HashSet<String>,
    old_calls: Option<&CallGraph>,
    new_calls: &CallGraph,
) -> Vec<String> {
    let mut reverse: HashMap<String, HashSet<String>> = HashMap::new();
    for graph in old_calls.into_iter().chain(std::iter::once(new_calls)) {
        for (caller, callees) in graph {
            for callee in callees {
                reverse
                    .entry(callee.clone())
                    .or_default()
                    .insert(caller.clone());
            }
        }
    }

    let mut affected = HashSet::new();
    let mut pending: VecDeque<String> = changed.into_iter().collect();
    while let Some(name) = pending.pop_front() {
        if !affected.insert(name.clone()) {
            continue;
        }
        if let Some(callers) = reverse.get(&name) {
            pending.extend(callers.iter().cloned());
        }
    }

    let mut affected: Vec<String> = affected.into_iter().collect();
    affected.sort();
    affected
}

#[cfg(test)]
mod tests {
    use super::{Compilation, IncrementalCompiler};
    use crate::model::{ProcedureSource, Project};

    fn procedure(name: &str, params: &[&str], source: &str) -> ProcedureSource {
        ProcedureSource {
            name: name.to_owned(),
            params: params
                .iter()
                .map(|parameter| (*parameter).to_owned())
                .collect(),
            variables: Default::default(),
            return_type: None,
            source: source.to_owned(),
        }
    }

    fn project(entry: &str, procedures: Vec<ProcedureSource>) -> Project {
        Project {
            format_version: 1,
            date_two_digit_year_max: None,
            record_types: Vec::new(),
            entry: entry.to_owned(),
            procedures,
        }
    }

    fn main_and_double(double_source: &str, double_params: &[&str], call: &str) -> Project {
        project(
            "main",
            vec![
                procedure(
                    "main",
                    &[],
                    &format!("&value = {call}\nPrint(&value)\nReturn 0"),
                ),
                procedure("double", double_params, double_source),
            ],
        )
    }

    fn assert_clean_emission(project: &Project, compilation: &Compilation) {
        assert_eq!(
            compilation.source,
            crate::compile(project).expect("clean compilation should succeed")
        );
    }

    #[test]
    fn reuses_unchanged_bodies_and_matches_clean_emission() {
        let initial = main_and_double("Return &number * 2", &["number"], "double(21)");
        let mut compiler = IncrementalCompiler::new();

        let first = compiler.update(&initial).expect("first revision");
        assert_eq!(first.reparsed, ["DOUBLE", "MAIN"]);
        assert!(first.reused.is_empty());
        assert_eq!(first.affected, ["DOUBLE", "MAIN"]);
        assert_clean_emission(&initial, &first);

        let unchanged = compiler.update(&initial).expect("unchanged revision");
        assert!(unchanged.reparsed.is_empty());
        assert_eq!(unchanged.reused, ["DOUBLE", "MAIN"]);
        assert!(unchanged.affected.is_empty());
        assert_clean_emission(&initial, &unchanged);
    }

    #[test]
    fn body_signature_add_delete_and_case_only_revisions_are_accounted_for() {
        let initial = main_and_double("Return &number * 2", &["number"], "double(21)");
        let mut compiler = IncrementalCompiler::new();
        compiler.update(&initial).expect("initial revision");

        let body_change = main_and_double("Return &number * 3", &["number"], "double(21)");
        let compilation = compiler.update(&body_change).expect("body change");
        assert_eq!(compilation.reparsed, ["DOUBLE"]);
        assert_eq!(compilation.reused, ["MAIN"]);
        assert_eq!(compilation.affected, ["DOUBLE", "MAIN"]);
        assert_clean_emission(&body_change, &compilation);

        let signature_change =
            main_and_double("Return &number * 2", &["number", "factor"], "double(21, 0)");
        let compilation = compiler
            .update(&signature_change)
            .expect("signature change");
        assert_eq!(compilation.reparsed, ["DOUBLE", "MAIN"]);
        assert!(compilation.reused.is_empty());
        assert_eq!(compilation.affected, ["DOUBLE", "MAIN"]);
        assert_clean_emission(&signature_change, &compilation);

        let mut with_addition = signature_change.clone();
        with_addition
            .procedures
            .push(procedure("unused", &[], "Return 9"));
        let compilation = compiler.update(&with_addition).expect("added procedure");
        assert_eq!(compilation.reparsed, ["UNUSED"]);
        assert_eq!(compilation.reused, ["DOUBLE", "MAIN"]);
        assert_eq!(compilation.affected, ["UNUSED"]);
        assert_clean_emission(&with_addition, &compilation);

        let compilation = compiler
            .update(&signature_change)
            .expect("deleted procedure");
        assert!(compilation.reparsed.is_empty());
        assert_eq!(compilation.reused, ["DOUBLE", "MAIN"]);
        assert_eq!(compilation.affected, ["UNUSED"]);
        assert_clean_emission(&signature_change, &compilation);

        let case_only = project(
            "MAIN",
            vec![
                procedure(
                    "mAiN",
                    &[],
                    "&value = double(21, 0)\nPrint(&value)\nReturn 0",
                ),
                procedure("DoUbLe", &["NUMBER", "FACTOR"], "Return &number * 2"),
            ],
        );
        let compilation = compiler.update(&case_only).expect("case-only revision");
        assert!(compilation.reparsed.is_empty());
        assert_eq!(compilation.reused, ["DOUBLE", "MAIN"]);
        assert!(compilation.affected.is_empty());
        assert_clean_emission(&case_only, &compilation);

        let mut reordered = case_only.clone();
        reordered.procedures.reverse();
        let compilation = compiler
            .update(&reordered)
            .expect("procedure reorder revision");
        assert!(compilation.reparsed.is_empty());
        assert_eq!(compilation.reused, ["DOUBLE", "MAIN"]);
        assert!(compilation.affected.is_empty());
        assert_clean_emission(&reordered, &compilation);
    }

    #[test]
    fn entry_change_affects_both_entries_and_their_callers() {
        let first = project(
            "main",
            vec![
                procedure("main", &[], "Return first()"),
                procedure("first", &[], "Return helper()"),
                procedure("helper", &[], "Return 7"),
            ],
        );
        let second = project(
            "helper",
            vec![
                procedure("main", &[], "Return first()"),
                procedure("first", &[], "Return 1"),
                procedure("helper", &[], "Return 7"),
            ],
        );
        let mut compiler = IncrementalCompiler::new();
        compiler.update(&first).expect("initial revision");
        let compilation = compiler.update(&second).expect("entry change");
        assert_eq!(compilation.reparsed, ["FIRST"]);
        assert_eq!(compilation.reused, ["HELPER", "MAIN"]);
        assert_eq!(compilation.affected, ["FIRST", "HELPER", "MAIN"]);
        assert_clean_emission(&second, &compilation);
    }

    #[test]
    fn failed_revision_rolls_back_then_accepts_recovery() {
        let initial = main_and_double("Return &number * 2", &["number"], "double(21)");
        let mut compiler = IncrementalCompiler::new();
        compiler.update(&initial).expect("initial revision");

        let invalid = main_and_double("Return Missing()", &["number"], "double(21)");
        assert!(compiler.update(&invalid).is_err());

        let after_failure = compiler
            .update(&initial)
            .expect("rollback preserved old state");
        assert!(after_failure.reparsed.is_empty());
        assert_eq!(after_failure.reused, ["DOUBLE", "MAIN"]);
        assert!(after_failure.affected.is_empty());
        assert_clean_emission(&initial, &after_failure);

        let recovered = main_and_double("Return &number * 3", &["number"], "double(21)");
        let compilation = compiler.update(&recovered).expect("recovery revision");
        assert_eq!(compilation.reparsed, ["DOUBLE"]);
        assert_eq!(compilation.reused, ["MAIN"]);
        assert_eq!(compilation.affected, ["DOUBLE", "MAIN"]);
        assert_clean_emission(&recovered, &compilation);
    }

    #[test]
    fn call_graph_cycles_terminate_and_propagate_affected_callers() {
        let initial = project(
            "main",
            vec![
                procedure("main", &[], "Return first()"),
                procedure("first", &[], "Return second()"),
                procedure("second", &[], "Return first()"),
            ],
        );
        let changed = project(
            "main",
            vec![
                procedure("main", &[], "Return first()"),
                procedure("first", &[], "Return second()"),
                procedure("second", &[], "Return first() + 1"),
            ],
        );
        let mut compiler = IncrementalCompiler::new();
        compiler.update(&initial).expect("initial cyclic revision");
        let compilation = compiler.update(&changed).expect("updated cyclic revision");
        assert_eq!(compilation.reparsed, ["SECOND"]);
        assert_eq!(compilation.reused, ["FIRST", "MAIN"]);
        assert_eq!(compilation.affected, ["FIRST", "MAIN", "SECOND"]);
        assert_clean_emission(&changed, &compilation);
    }

    #[test]
    #[ignore = "informational release microbenchmark; run with --release --ignored --nocapture"]
    fn release_microbenchmark_session_vs_clean_compile() {
        use std::time::{Duration, Instant};

        const PROCEDURE_COUNT: usize = 200;
        const ARITHMETIC_NODES_PER_HELPER: usize = 40;
        const REPETITIONS: usize = 8;

        fn arithmetic_source(last_term: usize) -> String {
            let mut source = String::from("Return &value");
            for term in 1..ARITHMETIC_NODES_PER_HELPER {
                source.push_str(&format!(" + {term}"));
            }
            source.push_str(&format!(" + {last_term}"));
            source
        }

        let mut procedures = Vec::with_capacity(PROCEDURE_COUNT);
        procedures.push(procedure(
            "main",
            &[],
            "&result = P000(1)\nPrint(&result)\nReturn 0",
        ));
        for index in 0..(PROCEDURE_COUNT - 1) {
            let name = format!("P{index:03}");
            procedures.push(procedure(
                &name,
                &["value"],
                &arithmetic_source(ARITHMETIC_NODES_PER_HELPER),
            ));
        }
        let initial = project("main", procedures);
        let mut edited = initial.clone();
        edited
            .procedures
            .iter_mut()
            .find(|procedure| procedure.name == "P100")
            .expect("synthetic edit target")
            .source = arithmetic_source(ARITHMETIC_NODES_PER_HELPER + 1);

        let clean_initial = crate::compile(&initial).expect("clean initial compilation");
        let mut unchanged_session = IncrementalCompiler::new();
        let session_initial = unchanged_session
            .update(&initial)
            .expect("initial session compilation");
        assert_eq!(session_initial.source, clean_initial);
        let unchanged = unchanged_session
            .update(&initial)
            .expect("unchanged session compilation");
        assert_eq!(unchanged.source, clean_initial);

        let clean_edited = crate::compile(&edited).expect("clean edited compilation");
        let mut edit_session = IncrementalCompiler::new();
        let session_initial = edit_session
            .update(&initial)
            .expect("initial edit-session compilation");
        assert_eq!(session_initial.source, clean_initial);
        let session_edited = edit_session
            .update(&edited)
            .expect("one-body edit compilation");
        assert_eq!(session_edited.source, clean_edited);
        assert_eq!(session_edited.reparsed, ["P100"]);

        let clean_unchanged_started = Instant::now();
        for _ in 0..REPETITIONS {
            std::hint::black_box(crate::compile(&initial).expect("clean compilation"));
        }
        let clean_unchanged = clean_unchanged_started.elapsed();

        let session_unchanged_started = Instant::now();
        for _ in 0..REPETITIONS {
            std::hint::black_box(
                unchanged_session
                    .update(&initial)
                    .expect("unchanged session update"),
            );
        }
        let session_unchanged = session_unchanged_started.elapsed();

        let mut clean_edit_time = Duration::ZERO;
        for _ in 0..REPETITIONS {
            let started = Instant::now();
            let output = crate::compile(&edited).expect("clean edited compilation");
            clean_edit_time += started.elapsed();
            std::hint::black_box(output);
        }

        let mut session_edit_time = Duration::ZERO;
        for _ in 0..REPETITIONS {
            edit_session
                .update(&initial)
                .expect("reset edit-session baseline");
            let started = Instant::now();
            let output = edit_session
                .update(&edited)
                .expect("one-body session update");
            session_edit_time += started.elapsed();
            std::hint::black_box(output);
        }

        println!(
            "release incremental microbenchmark: {PROCEDURE_COUNT} procedures, {ARITHMETIC_NODES_PER_HELPER} arithmetic nodes/helper, {REPETITIONS} repetitions\n\
             unchanged: session {session_unchanged:?}, clean compile {clean_unchanged:?}\n\
             one-body edit: session {session_edit_time:?}, clean compile {clean_edit_time:?}"
        );
    }
}
