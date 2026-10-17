//! Native Rust emission for explicitly declared Boolean/numeric/text procedures.
use crate::{
    frontend::{BinOp, Expr, Stmt},
    model::{DataType, ParamMode, ProcedureSource, Project, parse_parameter},
};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write;

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Boolean,
    Numeric,
    Text,
    Date,
    DateTime,
    Record,
    Collection,
}

fn kind(data_type: &DataType) -> Kind {
    match data_type {
        DataType::Boolean {} => Kind::Boolean,
        DataType::Record { .. } => Kind::Record,
        DataType::Collection { .. } => Kind::Collection,
        DataType::Date {} => Kind::Date,
        DataType::DateTime {} => Kind::DateTime,
        DataType::Numeric { .. } => Kind::Numeric,
        DataType::Character { .. } | DataType::Varchar { .. } => Kind::Text,
    }
}

struct Emitter<'a> {
    variables: &'a BTreeMap<String, DataType>,
    record_types: &'a [crate::model::RecordType],
    slots: BTreeMap<String, usize>,
    procedures: &'a [ProcedureSource],
    return_type: Option<&'a DataType>,
    /// Slots of out/inout parameters, copied back to the caller in parameter order.
    writebacks: Vec<usize>,
    date_two_digit_year_max: Option<u16>,
    sub_ids: &'a BTreeMap<String, usize>,
    is_sub: bool,
    proc_index: usize,
}

/// Validated `(mode, name)` pairs; the model has already checked every entry.
fn parameters(procedure: &ProcedureSource) -> Vec<(ParamMode, &str)> {
    procedure
        .params
        .iter()
        .map(|p| parse_parameter(p).expect("validated parameter"))
        .collect()
}

fn record_field_variant(data_type: &DataType) -> Result<&'static str, String> {
    match data_type {
        DataType::Numeric { .. } => Ok("Numeric"),
        DataType::Character { .. } | DataType::Varchar { .. } => Ok("Text"),
        DataType::Boolean {} => Ok("Boolean"),
        DataType::Date {} => Ok("Date"),
        DataType::DateTime {} => Ok("DateTime"),
        DataType::Record { .. } => Ok("Record"),
        DataType::Collection { .. } => Ok("Collection"),
    }
}

/// The value an `out` parameter holds on entry: GeneXus resets it to empty.
fn empty_value(
    data_type: &DataType,
    record_types: &[crate::model::RecordType],
    is_sub: bool,
) -> Result<String, String> {
    if let DataType::Record { schema } = data_type {
        let index = record_types
            .iter()
            .position(|t| &t.name == schema)
            .ok_or("unknown record schema")?;
        let steps_arg = if is_sub { "self.steps" } else { "steps" };
        return Ok(format!("r{index}({steps_arg})?"));
    }
    if let DataType::Collection { schema } = data_type {
        let index = record_types
            .iter()
            .position(|t| &t.name == schema)
            .ok_or("unknown record schema")?;
        let steps_arg = if is_sub { "self.steps" } else { "steps" };
        return Ok(format!(
            "Collection::with_prototype({schema:?}, r{index}({steps_arg})?)"
        ));
    }
    Ok(match kind(data_type) {
        Kind::Boolean => "false",
        Kind::Numeric => "\"0\".parse::<Decimal>()?",
        Kind::Text => "Text::new(\"\")?",
        Kind::Date => "Date::empty()",
        Kind::DateTime => "DateTime::empty()",
        Kind::Record | Kind::Collection => {
            return Err("container default requires a declared schema".into());
        }
    }
    .into())
}

fn assign_code(slot: usize, data_type: &DataType, value: &str, is_sub: bool) -> String {
    let target = if is_sub {
        format!("*self.v{slot}")
    } else {
        format!("v{slot}")
    };
    match data_type {
        DataType::Numeric { precision, scale } => {
            format!("{target} = NumericType::new({precision}, {scale})?.assign({value})?;")
        }
        _ => format!("{target} = {value};"),
    }
}

pub fn emit(project: &Project) -> Result<String, String> {
    let canonical = Project::from_json(&project.to_json()?)?;
    let parsed = canonical.parse()?;
    emit_parsed(&canonical, &parsed)
}

fn validate_subroutine_calls(
    stmts: &[Stmt],
    subroutines: &BTreeMap<String, Vec<Stmt>>,
) -> Result<(), String> {
    for stmt in stmts {
        match stmt {
            Stmt::DoSub(name) => {
                if !subroutines.contains_key(name) {
                    return Err(format!("call to undefined subroutine '{name}'"));
                }
            }
            Stmt::If(_, yes, no) => {
                validate_subroutine_calls(yes, subroutines)?;
                validate_subroutine_calls(no, subroutines)?;
            }
            Stmt::While(_, body) | Stmt::For(_, _, _, _, body) | Stmt::ForIn(_, _, body) => {
                validate_subroutine_calls(body, subroutines)?;
            }
            _ => {}
        }
    }
    Ok(())
}

pub(crate) fn emit_parsed(
    canonical: &Project,
    parsed: &[crate::frontend::Procedure],
) -> Result<String, String> {
    if canonical.procedures.iter().any(|p| {
        crate::frontend::is_typed_builtin(&p.name)
            && (p.name != "NEW" || !canonical.record_types.is_empty())
    }) {
        return Err("procedure name conflicts with a reserved builtin".into());
    }
    let entry = canonical
        .procedures
        .iter()
        .position(|p| p.name == canonical.entry)
        .unwrap();
    if !canonical.procedures[entry].params.is_empty()
        || canonical.procedures[entry].return_type.is_some()
    {
        return Err("typed entry must be parameterless without a return_type".into());
    }
    for procedure in &canonical.procedures {
        if matches!(
            procedure.return_type,
            Some(DataType::Record { .. } | DataType::Collection { .. })
        ) {
            return Err("record return types are not admitted".into());
        }
        for (_, parameter) in parameters(procedure) {
            if matches!(
                procedure.variables.get(parameter),
                Some(DataType::Collection { .. })
            ) {
                return Err("collection parameters are not admitted".into());
            }
            if matches!(
                procedure.variables.get(parameter),
                Some(DataType::Record { .. })
            ) && procedure.return_type.is_some()
            {
                return Err("record parameters currently require a void procedure".into());
            }
            if !procedure.variables.contains_key(parameter) {
                return Err(format!(
                    "typed parameter {parameter} requires a variable declaration"
                ));
            }
        }
    }
    let mut output = String::from("#![forbid(unsafe_code)]\n#[allow(dead_code)]\nmod types {\n");
    output.push_str(include_str!("types.rs"));
    output.push_str("\n}\nmod text {\n");
    output.push_str(include_str!("text.rs"));
    output.push_str("\n}\nuse types::{Decimal, NumericType};\nuse text::Text;\n");
    output.push_str("mod date {\n");
    output.push_str(include_str!("date.rs"));
    output.push_str("\n}\nuse date::Date;\n");
    output.push_str("mod datetime {\n");
    output.push_str(include_str!("datetime.rs"));
    output.push_str("\n}\nuse datetime::DateTime;\n");
    output.push_str("mod record_runtime {\n");
    output.push_str(include_str!("record_runtime.rs"));
    output.push_str("\n}\nuse record_runtime::{Field, Record, Collection};\n");
    for (index, schema) in canonical.record_types.iter().enumerate() {
        writeln!(output, "fn r{index}(steps: &mut u32) -> Result<Record, String> {{ let mut fields = std::collections::BTreeMap::new();").unwrap();
        for (name, data_type) in &schema.fields {
            if output.len() > 8 * 1024 * 1024 {
                return Err("typed generated source exceeds 8 MiB".into());
            }
            let variant = record_field_variant(data_type)?;
            let value = empty_value(data_type, &canonical.record_types, false)?;
            output.push_str(
                "*steps = steps.checked_sub(1).ok_or(\"execution step limit exceeded\")?;\n",
            );
            writeln!(
                output,
                "fields.insert({name:?}, Field::{variant}({value}));"
            )
            .unwrap();
        }
        writeln!(output, "Record::new({:?}, fields) }}", schema.name).unwrap();
    }
    for (index, procedure) in canonical.procedures.iter().enumerate() {
        let body = &parsed[index].body;
        let mut subroutines: BTreeMap<String, Vec<Stmt>> = BTreeMap::new();
        let mut main_body: Vec<Stmt> = Vec::new();
        for stmt in body {
            match stmt {
                Stmt::Sub(name, sub_body) => {
                    if subroutines.contains_key(name) {
                        return Err(format!("duplicate subroutine definition '{name}'"));
                    }
                    subroutines.insert(name.clone(), sub_body.clone());
                }
                other => main_body.push(other.clone()),
            }
        }
        validate_subroutine_calls(&main_body, &subroutines)?;
        for sub_body in subroutines.values() {
            validate_subroutine_calls(sub_body, &subroutines)?;
        }
        let sub_ids: BTreeMap<String, usize> = subroutines
            .keys()
            .enumerate()
            .map(|(i, name)| (name.clone(), i))
            .collect();
        if procedure.return_type.is_some() && !returns(&main_body) {
            return Err(format!(
                "typed helper {} must return on every path",
                procedure.name
            ));
        }
        let params = parameters(procedure);
        let slots: BTreeMap<String, usize> = procedure
            .variables
            .keys()
            .enumerate()
            .map(|(i, n)| (n.clone(), i))
            .collect();
        let writebacks: Vec<usize> = params
            .iter()
            .filter(|(mode, _)| *mode != ParamMode::In)
            .map(|(_, name)| slots[*name])
            .collect();
        if !subroutines.is_empty() {
            let ret_field = if let Some(rt) = procedure.return_type.as_ref() {
                format!("return_value: Option<{}>,\n", rust_type(rt))
            } else {
                String::new()
            };
            let mut var_fields = String::new();
            for (name, data_type) in &procedure.variables {
                let slot = slots[name];
                var_fields.push_str(&format!("v{slot}: &'a mut {},\n", rust_type(data_type)));
            }
            writeln!(
                output,
                "struct P{index}Sub<'a> {{\nsteps: &'a mut u32,\ndepth: u32,\nsub_depth: u32,\nreturn_in_sub: bool,\n{ret_field}{var_fields}}}\n"
            ).unwrap();
            writeln!(
                output,
                "impl<'a> P{index}Sub<'a> {{\nfn run(&mut self, sub_id: usize) -> Result<(), String> {{\nself.sub_depth += 1;\nif self.sub_depth > 128 {{\nreturn Err(\"subroutine call depth limit exceeded\".into());\n}}\n*self.steps = self.steps.checked_sub(1).ok_or(\"execution step limit exceeded\")?;\nmatch sub_id {{\n"
            ).unwrap();
            let sub_emitter = Emitter {
                variables: &procedure.variables,
                record_types: &canonical.record_types,
                slots: slots.clone(),
                procedures: &canonical.procedures,
                return_type: procedure.return_type.as_ref(),
                writebacks: writebacks.clone(),
                date_two_digit_year_max: canonical.date_two_digit_year_max,
                sub_ids: &sub_ids,
                is_sub: true,
                proc_index: index,
            };
            for (name, id) in &sub_ids {
                writeln!(output, "{id} => {{").unwrap();
                let mut sub_assigned = BTreeSet::new();
                sub_emitter.block(&subroutines[name], &mut sub_assigned, &mut output)?;
                writeln!(output, "}}").unwrap();
            }
            writeln!(
                output,
                "_ => unreachable!(),\n}}\nself.sub_depth -= 1;\nOk(())\n}}\n}}\n"
            )
            .unwrap();
        }
        let emitter = Emitter {
            variables: &procedure.variables,
            record_types: &canonical.record_types,
            slots,
            procedures: &canonical.procedures,
            return_type: procedure.return_type.as_ref(),
            writebacks,
            date_two_digit_year_max: canonical.date_two_digit_year_max,
            sub_ids: &sub_ids,
            is_sub: false,
            proc_index: index,
        };
        write!(output, "fn p{index}(steps: &mut u32, depth: u32").unwrap();
        for (i, (mode, name)) in params.iter().enumerate() {
            if *mode != ParamMode::Out {
                write!(output, ", a{i}: {}", rust_type(&procedure.variables[*name])).unwrap();
            }
        }
        let mut result_type = procedure
            .return_type
            .as_ref()
            .map(rust_type)
            .unwrap_or("()")
            .to_owned();
        if !emitter.writebacks.is_empty() {
            let copied: Vec<&str> = params
                .iter()
                .filter(|(mode, _)| *mode != ParamMode::In)
                .map(|(_, name)| rust_type(&procedure.variables[*name]))
                .collect();
            result_type = format!("({result_type}, {})", copied.join(", "));
        }
        writeln!(output, ") -> Result<{result_type}, String> {{").unwrap();
        output.push_str("if depth >= 128 { return Err(\"call depth limit exceeded\".into()); }\n");
        let mut assigned = BTreeSet::new();
        for (name, data_type) in &procedure.variables {
            let slot = emitter.slots[name];
            write!(output, "let mut v{slot}: {}", rust_type(data_type)).unwrap();
            if let Some(i) = params.iter().position(|(_, p)| p == name) {
                if params[i].0 == ParamMode::Out {
                    let value = empty_value(data_type, &canonical.record_types, false)?;
                    write!(output, " = {value}").unwrap();
                } else {
                    write!(output, " = {}", boundary(data_type, &format!("a{i}"))).unwrap();
                }
                assigned.insert(name.clone());
            } else if !subroutines.is_empty() {
                let value = empty_value(data_type, &canonical.record_types, false)?;
                write!(output, " = {value}").unwrap();
                assigned.insert(name.clone());
            }
            output.push_str(";\n");
        }
        emitter.block(&main_body, &mut assigned, &mut output)?;
        if procedure.return_type.is_none() {
            writeln!(output, "Ok({})", emitter.result("()")).unwrap();
        }
        output.push_str("}\n");
        if output.len() > 8 * 1024 * 1024 {
            return Err("typed generated source exceeds 8 MiB".into());
        }
    }
    writeln!(output, "fn main() {{ if let Err(error) = p{entry}(&mut 100000_u32, 0) {{ eprintln!(\"gx-engine runtime error: {{error}}\"); std::process::exit(70); }} }}").unwrap();
    Ok(output)
}

fn rust_type(data_type: &DataType) -> &'static str {
    match kind(data_type) {
        Kind::Boolean => "bool",
        Kind::Numeric => "Decimal",
        Kind::Text => "Text",
        Kind::Date => "Date",
        Kind::DateTime => "DateTime",
        Kind::Record => "Record",
        Kind::Collection => "Collection",
    }
}

fn boundary(data_type: &DataType, value: &str) -> String {
    match data_type {
        DataType::Boolean {}
        | DataType::Date {}
        | DataType::DateTime {}
        | DataType::Character { .. }
        | DataType::Varchar { .. }
        | DataType::Record { .. }
        | DataType::Collection { .. } => value.into(),
        DataType::Numeric { precision, scale } => {
            format!("NumericType::new({precision}, {scale})?.assign({value})?")
        }
    }
}

fn returns(body: &[Stmt]) -> bool {
    body.iter().any(|statement| match statement {
        Stmt::Return(_) | Stmt::ReturnVoid => true,
        Stmt::If(_, yes, no) => returns(yes) && returns(no),
        _ => false,
    })
}

impl Emitter<'_> {
    fn member_type(&self, receiver: &Expr, field: &str) -> Result<DataType, String> {
        match self.expression_type(receiver)? {
            DataType::Record { schema } => self
                .record_types
                .iter()
                .find(|t| t.name == schema)
                .ok_or("unknown record schema")?
                .fields
                .get(field)
                .cloned()
                .ok_or_else(|| format!("unknown field {schema}.{field}")),
            DataType::Collection { .. } if field == "COUNT" => Ok(DataType::Numeric {
                precision: 10,
                scale: 0,
            }),
            _ => Err("member receiver must be a record (or collection Count)".into()),
        }
    }

    fn expression_type(&self, expression: &Expr) -> Result<DataType, String> {
        match expression {
            Expr::Variable(name) => Ok(self.variable(name)?.1.clone()),
            Expr::Member(receiver, field) => self.member_type(receiver, field),
            Expr::CollectionItem(receiver, _) => match self.expression_type(receiver)? {
                DataType::Collection { schema } => Ok(DataType::Record { schema }),
                _ => Err("Item receiver must be a collection".into()),
            },
            Expr::CloneRecord(receiver) => {
                let data_type = self.expression_type(receiver)?;
                if matches!(
                    data_type,
                    DataType::Record { .. } | DataType::Collection { .. }
                ) {
                    Ok(data_type)
                } else {
                    Err("Clone receiver must be a record or collection".into())
                }
            }
            _ => Err("container expression must be a variable, member, Item or Clone".into()),
        }
    }

    fn container_assignment(
        &self,
        expected: &DataType,
        expression: &Expr,
        assigned: &mut BTreeSet<String>,
    ) -> Result<String, String> {
        if (matches!(expression, Expr::EmptyDate)
            || matches!(expression, Expr::Call(name, args) if name == "NEW" && args.is_empty()))
            && matches!(
                expected,
                DataType::Record { .. } | DataType::Collection { .. }
            )
        {
            return empty_value(expected, self.record_types, self.is_sub);
        }
        if &self.expression_type(expression)? != expected {
            return Err("record/collection schema mismatch".into());
        }
        self.expect(expression, assigned, kind(expected))
    }

    /// The `Ok` payload for a return: the value plus the copied-back parameters.
    fn result(&self, value: &str) -> String {
        if self.writebacks.is_empty() {
            return value.into();
        }
        let copied: Vec<String> = self
            .writebacks
            .iter()
            .map(|slot| format!("v{slot}.clone()"))
            .collect();
        format!("({value}, {})", copied.join(", "))
    }

    /// Nested calls run in the prelude before direct operands are captured, matching
    /// the native Pair(X, Bump(X)) probe. Copyback follows formal parameter order.
    fn lower_call(
        &self,
        name: &str,
        args: &[Expr],
        assigned: &mut BTreeSet<String>,
        prelude: &mut String,
        statement: bool,
    ) -> Result<(Option<Kind>, String), String> {
        let index = self
            .procedures
            .iter()
            .position(|p| p.name == name)
            .ok_or_else(|| {
                if matches!(
                    name.to_ascii_uppercase().as_str(),
                    "SUM" | "COUNT" | "MAX" | "MIN" | "FIND" | "AVERAGE" | "AVG"
                ) {
                    format!(
                        "database aggregate formula {name} requires the navigation/specifier engine and is not supported in standalone typed offline binaries"
                    )
                } else {
                    format!("unknown typed procedure {name}")
                }
            })?;
        let procedure = &self.procedures[index];
        let params = parameters(procedure);
        // GeneXus allows the last out parameter to be omitted at a function call;
        // that parameter becomes the expression's value instead of a caller target.
        let last_output = if !statement && procedure.return_type.is_none() {
            params.last().and_then(|(mode, name)| {
                (*mode == ParamMode::Out).then(|| &procedure.variables[*name])
            })
        } else {
            None
        };
        if matches!(
            last_output,
            Some(DataType::Record { .. } | DataType::Collection { .. })
        ) {
            return Err("record expression results are not admitted".into());
        }
        match (&procedure.return_type, statement) {
            (None, false) if last_output.is_none() => {
                return Err(format!(
                    "typed procedure {name} has no return_type; call it as a statement"
                ));
            }
            (Some(_), true) => {
                return Err(format!(
                    "typed procedure {name} returns a value; assign its result"
                ));
            }
            _ => {}
        }
        let expected_count = params.len() - usize::from(last_output.is_some());
        if args.len() != expected_count {
            return Err(format!("typed call {name} argument count mismatch"));
        }
        // Nested calls append to the prelude; operand bindings stay in this block.
        // Keeping these phases separate matches native call-side-effect visibility
        // and avoids overlapping mutable borrows of the shared budget.
        let mut code = String::from("{");
        // (tuple index, caller slot, caller type) for each copied-back argument.
        let mut targets = Vec::new();
        let mut copied = 0;
        for (i, (arg, (mode, parameter))) in args.iter().zip(&params).enumerate() {
            let expected_type = &procedure.variables[*parameter];
            let expected = kind(expected_type);
            if expected == Kind::Record {
                let Expr::Variable(target) = arg else {
                    return Err("record arguments must be same-schema variables".into());
                };
                if self.variable(target)?.1 != expected_type {
                    return Err("record argument schema mismatch".into());
                }
            }
            if *mode != ParamMode::In {
                copied += 1;
                match (mode, arg) {
                    (_, Expr::Variable(target)) => {
                        let (slot, data_type) = self.variable(target)?;
                        if kind(data_type) != expected {
                            return Err("typed value mismatch".into());
                        }
                        targets.push((copied, slot, data_type, target.clone()));
                    }
                    (ParamMode::Out, _) => {
                        return Err(format!(
                            "argument {} of {name} is out and must be a variable",
                            i + 1
                        ));
                    }
                    _ => {}
                }
                if *mode == ParamMode::Out {
                    continue;
                }
            }
            let value = self.lower_expect(arg, assigned, expected, prelude)?;
            write!(code, "let arg{i} = {value};").unwrap();
        }
        let (steps_arg, depth_arg) = if self.is_sub {
            ("self.steps", "self.depth + 1")
        } else {
            ("steps", "depth + 1")
        };
        write!(code, "p{index}({steps_arg}, {depth_arg}").unwrap();
        for (i, (mode, _)) in params.iter().enumerate() {
            if *mode != ParamMode::Out {
                write!(code, ", arg{i}").unwrap();
            }
        }
        code.push_str(")?}");
        // GeneXus hoists calls before evaluating the enclosing expression,
        // including calls in a logically skipped AND/OR operand.
        let call = format!("call{}", prelude.len());
        write!(prelude, "let {call} = {code};").unwrap();
        let mut value = call.clone();
        if !targets.is_empty() {
            for (k, slot, data_type, target) in &targets {
                prelude.push_str(&assign_code(
                    *slot,
                    data_type,
                    &format!("{call}.{k}"),
                    self.is_sub,
                ));
                assigned.insert(target.clone());
            }
        }
        if let Some(result_type) = last_output {
            let result_index = params
                .iter()
                .filter(|(mode, _)| *mode != ParamMode::In)
                .count();
            return Ok((Some(kind(result_type)), format!("{call}.{result_index}")));
        }
        if copied > 0 {
            value = format!("{call}.0");
        }
        Ok((procedure.return_type.as_ref().map(kind), value))
    }

    fn variable(&self, name: &str) -> Result<(usize, &DataType), String> {
        let data_type = self
            .variables
            .get(name)
            .ok_or_else(|| format!("undeclared typed variable {name}"))?;
        Ok((self.slots[name], data_type))
    }

    fn expression(
        &self,
        expr: &Expr,
        assigned: &mut BTreeSet<String>,
    ) -> Result<(Kind, String), String> {
        let mut prelude = String::new();
        let (kind, value) = self.lower(expr, assigned, &mut prelude)?;
        Ok((kind, format!("{{{prelude}{value}}}")))
    }

    fn lower_expect(
        &self,
        expr: &Expr,
        assigned: &mut BTreeSet<String>,
        expected: Kind,
        prelude: &mut String,
    ) -> Result<String, String> {
        let (actual, value) = self.lower(expr, assigned, prelude)?;
        if actual != expected {
            return Err("typed value mismatch".into());
        }
        Ok(value)
    }

    fn lower(
        &self,
        expr: &Expr,
        assigned: &mut BTreeSet<String>,
        prelude: &mut String,
    ) -> Result<(Kind, String), String> {
        let result = match expr {
            Expr::EmptyDate => (Kind::Date, "Date::empty()".into()),
            Expr::Integer(value) => (
                Kind::Numeric,
                format!("{:?}.parse::<Decimal>()?", value.to_string()),
            ),
            Expr::Decimal(text) => (Kind::Numeric, format!("{text:?}.parse::<Decimal>()?")),
            Expr::Boolean(value) => (Kind::Boolean, value.to_string()),
            Expr::Text(value) => (Kind::Text, format!("Text::new({value:?})?")),
            Expr::Attribute(_) => {
                return Err(
                    "Attribute expressions require specifier navigation and are not admitted in standalone typed binaries".into(),
                );
            }
            Expr::Member(receiver, field) => {
                let field_type = self.member_type(receiver, field)?;
                if matches!(self.expression_type(receiver)?, DataType::Collection { .. }) {
                    let receiver =
                        self.lower_expect(receiver, assigned, Kind::Collection, prelude)?;
                    (
                        Kind::Numeric,
                        format!("({receiver}).len().to_string().parse::<Decimal>()?"),
                    )
                } else {
                    let receiver = self.lower_expect(receiver, assigned, Kind::Record, prelude)?;
                    let variant = record_field_variant(&field_type)?;
                    (
                        kind(&field_type),
                        format!(
                            "match ({receiver}).get({field:?})? {{ Field::{variant}(value) => value, _ => return Err(\"record field type mismatch\".into()) }}"
                        ),
                    )
                }
            }
            Expr::CloneRecord(receiver) => {
                let data_type = self.expression_type(expr)?;
                let value = self.lower_expect(receiver, assigned, kind(&data_type), prelude)?;
                let cost = match &data_type {
                    DataType::Record { schema } => self.record_types.iter().find(|t| &t.name == schema)
                        .ok_or("unknown record schema")?.fields.len().to_string(),
                    DataType::Collection { .. } => "u32::try_from(clone_value.len().max(1)).map_err(|_| \"clone size overflow\")?".into(),
                    _ => return Err("Clone receiver must be a container".into()),
                };
                let steps_ref = if self.is_sub { "self.steps" } else { "steps" };
                (
                    kind(&data_type),
                    format!(
                        "{{let clone_value = {value}; *{steps_ref} = {steps_ref}.checked_sub({cost}).ok_or(\"execution step limit exceeded\")?; clone_value.shallow_clone()}}"
                    ),
                )
            }
            Expr::CollectionItem(receiver, index) => {
                if matches!(index.as_ref(), Expr::Integer(value) if *value <= 0) {
                    return Err("Item index must be positive".into());
                }
                let receiver = self.lower_expect(receiver, assigned, Kind::Collection, prelude)?;
                let index = self.lower_expect(index, assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Record,
                    format!("({receiver}).item(({index}).truncate_to_i64()?)?"),
                )
            }
            Expr::TextLength(value) => {
                let value = self.lower_expect(value, assigned, Kind::Text, prelude)?;
                (
                    Kind::Numeric,
                    format!("({value}).utf16_length().to_string().parse::<Decimal>()?"),
                )
            }
            Expr::Variable(name) => {
                let (slot, data_type) = self.variable(name)?;
                if !assigned.contains(name) && !self.is_sub {
                    return Err(format!("typed variable {name} read before assignment"));
                }
                let target = if self.is_sub {
                    format!("(*self.v{slot})")
                } else {
                    format!("v{slot}")
                };
                let value = if matches!(
                    kind(data_type),
                    Kind::Text | Kind::Record | Kind::Collection
                ) {
                    format!("{target}.clone()")
                } else {
                    target
                };
                (kind(data_type), value)
            }
            Expr::DateShift(method, receiver, offset) => {
                let (receiver_kind, receiver) = self.lower(receiver, assigned, prelude)?;
                if !matches!(receiver_kind, Kind::Date | Kind::DateTime) {
                    return Err("calendar shift receiver must be Date or DateTime".into());
                }
                if method == "DIFFERENCE" {
                    if receiver_kind == Kind::Date {
                        let offset = self.lower_expect(offset, assigned, Kind::Date, prelude)?;
                        (
                            Kind::Numeric,
                            format!("Decimal::from(({receiver}).difference({offset}))"),
                        )
                    } else {
                        let offset =
                            self.lower_expect(offset, assigned, Kind::DateTime, prelude)?;
                        (
                            Kind::Numeric,
                            format!("Decimal::from(({receiver}).difference({offset}))"),
                        )
                    }
                } else {
                    let offset = self.lower_expect(offset, assigned, Kind::Numeric, prelude)?;
                    let method = match (receiver_kind, method.as_str()) {
                        (Kind::Date, "ADDMONTHS") | (Kind::DateTime, "ADDMONTHS") => "add_months",
                        (Kind::Date, "ADDYEARS") | (Kind::DateTime, "ADDYEARS") => "add_years",
                        (Kind::Date, "ADDDAYS") | (Kind::DateTime, "ADDDAYS") => "add_days",
                        (Kind::DateTime, "ADDHOURS") => "add_hours",
                        (Kind::DateTime, "ADDMINUTES") => "add_minutes",
                        (Kind::DateTime, "ADDSECONDS") => "add_seconds",
                        _ => return Err("unsupported calendar shift receiver/method".into()),
                    };
                    (
                        receiver_kind,
                        format!("({receiver}).{method}(({offset}).to_i64()?)?"),
                    )
                }
            }
            Expr::DateMethod(method, receiver) => {
                let (receiver_kind, value) = self.lower(receiver, assigned, prelude)?;
                if method == "ISEMPTY" {
                    let code = match receiver_kind {
                        Kind::Date | Kind::DateTime | Kind::Collection => {
                            format!("({value}).is_empty()")
                        }
                        Kind::Numeric => {
                            format!("({value}).checked_cmp(\"0\".parse::<Decimal>()?)?.is_eq()")
                        }
                        Kind::Text => {
                            format!("({value}).trim_end_spaces().utf16_length() == 0")
                        }
                        Kind::Boolean => format!("!({value})"),
                        Kind::Record => {
                            return Err("IsEmpty() is not supported on records directly".into());
                        }
                    };
                    (Kind::Boolean, code)
                } else {
                    if !matches!(receiver_kind, Kind::Date | Kind::DateTime) {
                        return Err("calendar method receiver must be Date or DateTime".into());
                    }
                    let method = match method.as_str() {
                        "YEAR" => "year",
                        "MONTH" => "month",
                        "DAY" => "day",
                        "HOUR" => "hour",
                        "MINUTE" => "minute",
                        "SECOND" => "second",
                        _ => return Err("unsupported calendar component".into()),
                    };
                    (
                        Kind::Numeric,
                        format!("Decimal::from(({value}).{method}())"),
                    )
                }
            }
            Expr::Call(name, args) if name == "STR" => {
                if !(1..=3).contains(&args.len()) {
                    return Err("Str expects one to three arguments".into());
                }
                let literal = |index: usize, default: u8| -> Result<u8, String> {
                    match args.get(index) {
                        None => Ok(default),
                        Some(Expr::Integer(value)) => u8::try_from(*value).map_err(|_| {
                            "Str width/scale must be nonnegative integer literals".into()
                        }),
                        _ => Err("Str width/scale currently require integer literals".into()),
                    }
                };
                let width = literal(1, 10)?;
                let scale = literal(2, 0)?;
                if !(1..=18).contains(&width) || scale > width {
                    return Err("supported Str requires width 1..18 and scale <= width".into());
                }
                let value = self.lower_expect(&args[0], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Text,
                    format!("Text::new(&types::format_str({value}, {width}, {scale})?)?"),
                )
            }
            Expr::Call(name, args) if matches!(name.as_str(), "TRIM" | "LEN") => {
                let [argument] = args.as_slice() else {
                    return Err(format!("{name} currently requires exactly one argument"));
                };
                let value = self.lower_expect(argument, assigned, Kind::Text, prelude)?;
                if name == "LEN" {
                    (
                        Kind::Numeric,
                        format!("({value}).utf16_length().to_string().parse::<Decimal>()?"),
                    )
                } else {
                    (Kind::Text, format!("({value}).trim_spaces()"))
                }
            }
            Expr::Call(name, args) if name == "SUBSTR" => {
                if args.len() != 3 {
                    return Err("Substr expects three arguments".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let start = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                let length = self.lower_expect(&args[2], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Text,
                    format!("({text}).substring(({start}).to_i64()?, ({length}).to_i64()?)"),
                )
            }
            Expr::Call(name, args) if matches!(name.as_str(), "UPPER" | "LOWER") => {
                let [argument] = args.as_slice() else {
                    return Err(format!("{name} currently requires exactly one argument"));
                };
                let value = self.lower_expect(argument, assigned, Kind::Text, prelude)?;
                let method = if name == "UPPER" {
                    "to_upper"
                } else {
                    "to_lower"
                };
                (Kind::Text, format!("({value}).{method}()"))
            }
            Expr::Call(name, args) if name == "STRREPLACE" => {
                if args.len() != 3 {
                    return Err("StrReplace expects three arguments".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let find = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                let with = self.lower_expect(&args[2], assigned, Kind::Text, prelude)?;
                (
                    Kind::Text,
                    format!("({text}).str_replace(&({find}), &({with}))?"),
                )
            }
            Expr::Call(name, args) if matches!(name.as_str(), "LTRIM" | "RTRIM") => {
                let [argument] = args.as_slice() else {
                    return Err(format!("{name} requires exactly one argument"));
                };
                let value = self.lower_expect(argument, assigned, Kind::Text, prelude)?;
                let method = if name == "LTRIM" {
                    "trim_start_spaces"
                } else {
                    "trim_end_spaces"
                };
                (Kind::Text, format!("({value}).{method}()"))
            }
            Expr::Call(name, args) if matches!(name.as_str(), "PADL" | "PADR") => {
                if !(2..=3).contains(&args.len()) {
                    return Err(format!("{name} expects two or three arguments"));
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let size = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                let fill = if let Some(fill_arg) = args.get(2) {
                    self.lower_expect(fill_arg, assigned, Kind::Text, prelude)?
                } else {
                    "Text::new(\" \")?".into()
                };
                let method = if name == "PADL" {
                    "pad_left"
                } else {
                    "pad_right"
                };
                (
                    Kind::Text,
                    format!("({text}).{method}(({size}).to_i64()?, &({fill}))?"),
                )
            }
            Expr::Call(name, args) if name == "SPACE" => {
                let [argument] = args.as_slice() else {
                    return Err("Space requires exactly one argument".into());
                };
                let count = self.lower_expect(argument, assigned, Kind::Numeric, prelude)?;
                (Kind::Text, format!("Text::space(({count}).to_i64()?)?"))
            }
            Expr::Call(name, args) if name == "CONCAT" => {
                if !(2..=3).contains(&args.len()) {
                    return Err("Concat expects two or three arguments".into());
                }
                let s1 = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let s2 = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                let sep = if let Some(sep_arg) = args.get(2) {
                    let sep_val = self.lower_expect(sep_arg, assigned, Kind::Text, prelude)?;
                    format!("Some(&({sep_val}))")
                } else {
                    "None".into()
                };
                (
                    Kind::Text,
                    format!("({s1}).concat_with_sep(&({s2}), {sep})?"),
                )
            }
            Expr::Call(name, args)
                if matches!(name.as_str(), "STARTSWITH" | "ENDSWITH" | "CONTAINS") =>
            {
                let [s1_arg, s2_arg] = args.as_slice() else {
                    return Err(format!("{name} requires exactly two arguments"));
                };
                let s1 = self.lower_expect(s1_arg, assigned, Kind::Text, prelude)?;
                let s2 = self.lower_expect(s2_arg, assigned, Kind::Text, prelude)?;
                let method = match name.as_str() {
                    "STARTSWITH" => "starts_with",
                    "ENDSWITH" => "ends_with",
                    "CONTAINS" => "contains",
                    _ => unreachable!(),
                };
                (Kind::Boolean, format!("({s1}).{method}(&({s2}))"))
            }
            Expr::Call(name, args) if name == "STRSEARCH" => {
                if !(2..=3).contains(&args.len()) {
                    return Err("StrSearch expects two or three arguments".into());
                }
                let s1 = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let s2 = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                let start = if let Some(start_arg) = args.get(2) {
                    self.lower_expect(start_arg, assigned, Kind::Numeric, prelude)?
                } else {
                    "Decimal::from(1)".into()
                };
                (
                    Kind::Numeric,
                    format!(
                        "({s1}).str_search(&({s2}), ({start}).to_i64()?).to_string().parse::<Decimal>()?"
                    ),
                )
            }
            Expr::Call(name, args) if name == "INDEXOF" => {
                if args.len() != 2 {
                    return Err("IndexOf expects two arguments".into());
                }
                let receiver_type = self.expression_type(&args[0])?;
                match receiver_type {
                    DataType::Collection { schema: col_schema } => {
                        let item_type = self.expression_type(&args[1])?;
                        if item_type != (DataType::Record { schema: col_schema }) {
                            return Err("collection IndexOf item schema mismatch".into());
                        }
                        let receiver =
                            self.lower_expect(&args[0], assigned, Kind::Collection, prelude)?;
                        let item = self.lower_expect(&args[1], assigned, Kind::Record, prelude)?;
                        (
                            Kind::Numeric,
                            format!("Decimal::from(({receiver}).index_of(&({item})))"),
                        )
                    }
                    DataType::Character { .. } | DataType::Varchar { .. } => {
                        let s1 = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                        let s2 = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                        (
                            Kind::Numeric,
                            format!(
                                "({s1}).str_search(&({s2}), 1).to_string().parse::<Decimal>()?"
                            ),
                        )
                    }
                    _ => return Err("IndexOf receiver must be a text or collection".into()),
                }
            }
            Expr::Call(name, args) if name == "INT" => {
                if args.len() != 1 {
                    return Err("Int expects exactly one argument".into());
                }
                let val = self.lower_expect(&args[0], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Numeric,
                    format!("Decimal::from(({val}).floor_to_i64()?)"),
                )
            }
            Expr::Call(name, args) if name == "ROUND" => {
                if args.len() != 2 {
                    return Err("Round expects two arguments".into());
                }
                let val = self.lower_expect(&args[0], assigned, Kind::Numeric, prelude)?;
                let digits = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Numeric,
                    format!("({val}).round(({digits}).to_i64()?)?"),
                )
            }
            Expr::Call(name, args) if name == "TRUNC" => {
                if args.len() != 2 {
                    return Err("Trunc expects two arguments".into());
                }
                let val = self.lower_expect(&args[0], assigned, Kind::Numeric, prelude)?;
                let digits = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Numeric,
                    format!("({val}).trunc(({digits}).to_i64()?)?"),
                )
            }
            Expr::Call(name, args) if name == "VAL" => {
                if !(1..=2).contains(&args.len()) {
                    return Err("Val expects one or two arguments".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let dsep = if let Some(dsep_arg) = args.get(1) {
                    let dsep_val = self.lower_expect(dsep_arg, assigned, Kind::Text, prelude)?;
                    format!("Some(&({dsep_val}).to_string())")
                } else {
                    "None".into()
                };
                (
                    Kind::Numeric,
                    format!("Decimal::val(&({text}).to_string(), {dsep})"),
                )
            }
            Expr::Call(name, args) if name == "LEFT" => {
                if args.len() != 2 {
                    return Err("Left expects two arguments".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let size = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (Kind::Text, format!("({text}).left(({size}).to_i64()?)"))
            }
            Expr::Call(name, args) if name == "RIGHT" => {
                if args.len() != 2 {
                    return Err("Right expects two arguments".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let size = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (Kind::Text, format!("({text}).right(({size}).to_i64()?)?"))
            }
            Expr::Call(name, args) if name == "ASC" => {
                if args.len() != 1 {
                    return Err("Asc expects exactly one argument".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                (Kind::Numeric, format!("Decimal::from(({text}).asc())"))
            }
            Expr::Call(name, args) if name == "CHR" => {
                if args.len() != 1 {
                    return Err("Chr expects exactly one argument".into());
                }
                let ascii = self.lower_expect(&args[0], assigned, Kind::Numeric, prelude)?;
                (Kind::Text, format!("Text::chr(({ascii}).to_i64()?)?"))
            }
            Expr::Call(name, args) if name == "NOTNUMERIC" => {
                if args.len() != 1 {
                    return Err("NotNumeric expects exactly one argument".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                (Kind::Boolean, format!("({text}).not_numeric()"))
            }
            Expr::Call(name, args) if name == "LTRIMSTR" => {
                if !(2..=3).contains(&args.len()) {
                    return Err("LTrimStr expects two or three arguments".into());
                }
                let val = self.lower_expect(&args[0], assigned, Kind::Numeric, prelude)?;
                let len = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                let dec = if let Some(dec_arg) = args.get(2) {
                    self.lower_expect(dec_arg, assigned, Kind::Numeric, prelude)?
                } else {
                    "Decimal::from(0)".into()
                };
                (
                    Kind::Text,
                    format!(
                        "Text::new(&types::format_str({val}, ({len}).to_i64()? as u8, ({dec}).to_i64()? as u8)?.trim_start_matches(' '))?"
                    ),
                )
            }
            Expr::Call(name, args)
                if matches!(
                    name.as_str(),
                    "DAY" | "MONTH" | "YEAR" | "HOUR" | "MINUTE" | "SECOND" | "SEC" | "DOW"
                ) =>
            {
                if args.len() != 1 {
                    return Err(format!("{name} expects exactly one argument"));
                }
                let (receiver_kind, value) = self.lower(&args[0], assigned, prelude)?;
                if !matches!(receiver_kind, Kind::Date | Kind::DateTime) {
                    return Err(format!("{name} argument must be Date or DateTime"));
                }
                let method = match name.as_str() {
                    "DAY" => "day",
                    "MONTH" => "month",
                    "YEAR" => "year",
                    "HOUR" => "hour",
                    "MINUTE" => "minute",
                    "SECOND" | "SEC" => "second",
                    "DOW" => "day_of_week",
                    _ => unreachable!(),
                };
                (
                    Kind::Numeric,
                    format!("Decimal::from(({value}).{method}())"),
                )
            }
            Expr::Call(name, args)
                if matches!(name.as_str(), "ADDYR" | "ADDMTH" | "ADDDAYS" | "DADD") =>
            {
                if args.len() != 2 {
                    return Err(format!("{name} expects two arguments"));
                }
                let (receiver_kind, value) = self.lower(&args[0], assigned, prelude)?;
                if !matches!(receiver_kind, Kind::Date | Kind::DateTime) {
                    return Err(format!("{name} first argument must be Date or DateTime"));
                }
                let offset = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                let method = match name.as_str() {
                    "ADDYR" => "add_years",
                    "ADDMTH" => "add_months",
                    "ADDDAYS" | "DADD" => "add_days",
                    _ => unreachable!(),
                };
                (
                    receiver_kind,
                    format!("({value}).{method}(({offset}).to_i64()?)?"),
                )
            }
            Expr::Call(name, args) if name == "TADD" => {
                if args.len() != 2 {
                    return Err("TAdd expects two arguments".into());
                }
                let dt = self.lower_expect(&args[0], assigned, Kind::DateTime, prelude)?;
                let seconds = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::DateTime,
                    format!("({dt}).add_seconds(({seconds}).to_i64()?)?"),
                )
            }
            Expr::Call(name, args) if name == "DDIFF" => {
                if args.len() != 2 {
                    return Err("DDiff expects two arguments".into());
                }
                let (k1, v1) = self.lower(&args[0], assigned, prelude)?;
                let (k2, v2) = self.lower(&args[1], assigned, prelude)?;
                if !matches!(k1, Kind::Date | Kind::DateTime) || k1 != k2 {
                    return Err("DDiff requires two Date or two DateTime arguments".into());
                }
                if k1 == Kind::Date {
                    (
                        Kind::Numeric,
                        format!("Decimal::from(({v1}).difference({v2}))"),
                    )
                } else {
                    (
                        Kind::Numeric,
                        format!("Decimal::from(({v1}).difference({v2}) / 86400)"),
                    )
                }
            }
            Expr::Call(name, args) if name == "TDIFF" => {
                if args.len() != 2 {
                    return Err("TDiff expects two DateTime arguments".into());
                }
                let v1 = self.lower_expect(&args[0], assigned, Kind::DateTime, prelude)?;
                let v2 = self.lower_expect(&args[1], assigned, Kind::DateTime, prelude)?;
                (
                    Kind::Numeric,
                    format!("Decimal::from(({v1}).difference({v2}))"),
                )
            }
            Expr::Call(name, args) if name == "AGE" => {
                if !(1..=2).contains(&args.len()) {
                    return Err("Age expects one or two arguments".into());
                }
                let (k1, v1) = self.lower(&args[0], assigned, prelude)?;
                if !matches!(k1, Kind::Date | Kind::DateTime) {
                    return Err("Age argument must be Date or DateTime".into());
                }
                let v2 = if let Some(arg2) = args.get(1) {
                    let (k2, v2) = self.lower(arg2, assigned, prelude)?;
                    if k1 != k2 {
                        return Err("Age arguments must both be Date or both DateTime".into());
                    }
                    v2
                } else {
                    return Err("Age with 1 argument requires system clock which is not supported in deterministic engine".into());
                };
                (Kind::Numeric, format!("Decimal::from(({v1}).age({v2}))"))
            }
            Expr::Call(name, args) if name == "ENDOFMONTH" => {
                if args.len() != 1 {
                    return Err("EndOfMonth expects exactly one argument".into());
                }
                let (k, v) = self.lower(&args[0], assigned, prelude)?;
                if !matches!(k, Kind::Date | Kind::DateTime) {
                    return Err("EndOfMonth argument must be Date or DateTime".into());
                }
                (k, format!("({v}).end_of_month()"))
            }
            Expr::Call(name, args) if name == "RESETTIME" => {
                if args.len() != 1 {
                    return Err("ResetTime expects exactly one argument".into());
                }
                let (k, v) = self.lower(&args[0], assigned, prelude)?;
                if !matches!(k, Kind::Date | Kind::DateTime) {
                    return Err("ResetTime argument must be Date or DateTime".into());
                }
                (k, format!("({v}).reset_time()"))
            }
            Expr::Call(name, args) if name == "TOSTRING" => {
                if args.len() != 1 {
                    return Err("ToString expects zero arguments on receiver".into());
                }
                let (kind, value) = self.lower(&args[0], assigned, prelude)?;
                match kind {
                    Kind::Text => (Kind::Text, value),
                    Kind::Numeric => (Kind::Text, format!("Text::new(&({value}).to_string())?")),
                    Kind::Date => (Kind::Text, format!("Text::new(&({value}).to_text())?")),
                    Kind::DateTime => (Kind::Text, format!("Text::new(&({value}).to_text())?")),
                    Kind::Boolean => (
                        Kind::Text,
                        format!("Text::new(if {value} {{ \"true\" }} else {{ \"false\" }})?"),
                    ),
                    _ => return Err("ToString receiver must be a primitive value".into()),
                }
            }
            Expr::Call(name, args) if name == "YMDHMSTOT" => {
                if args.len() != 6 {
                    return Err("YMDHMSToT expects six Numeric arguments".into());
                }
                let mut values = Vec::new();
                for arg in args {
                    let value = self.lower_expect(arg, assigned, Kind::Numeric, prelude)?;
                    values.push(format!("({value}).to_i64()?"));
                }
                (
                    Kind::DateTime,
                    format!(
                        "DateTime::from_parts([{}], {:?})?",
                        values.join(","),
                        self.date_two_digit_year_max
                    ),
                )
            }
            Expr::Call(name, args) if name == "YMDTOD" => {
                if args.len() != 3 {
                    return Err("YMDToD expects three Numeric arguments".into());
                }
                let mut values = Vec::new();
                for arg in args {
                    values.push(self.lower_expect(arg, assigned, Kind::Numeric, prelude)?);
                }
                (
                    Kind::Date,
                    format!(
                        "Date::ymd_with_window(({}).to_i64()?, ({}).to_i64()?, ({}).to_i64()?, {:?})?",
                        values[0], values[1], values[2], self.date_two_digit_year_max
                    ),
                )
            }
            Expr::Call(name, args) if name == "IIF" => {
                if args.len() != 3 {
                    return Err(
                        "Iif expects three arguments (condition, trueExpr, falseExpr)".into(),
                    );
                }
                let cond = self.lower_expect(&args[0], assigned, Kind::Boolean, prelude)?;
                let mut true_prelude = String::new();
                let (true_kind, true_val) = self.lower(&args[1], assigned, &mut true_prelude)?;
                let mut false_prelude = String::new();
                let (false_kind, false_val) = self.lower(&args[2], assigned, &mut false_prelude)?;
                if true_kind != false_kind {
                    return Err("Iif branches must have the same type".into());
                }
                if matches!(true_kind, Kind::Record | Kind::Collection) {
                    return Err("Iif branches must be scalar values".into());
                }
                (
                    true_kind,
                    format!(
                        "(if {cond} {{ {true_prelude}{true_val} }} else {{ {false_prelude}{false_val} }})"
                    ),
                )
            }
            Expr::Call(name, args) if name == "ISEMPTY" => {
                if args.len() != 1 {
                    return Err("IsEmpty expects exactly one argument".into());
                }
                let (receiver_kind, value) = self.lower(&args[0], assigned, prelude)?;
                let code = match receiver_kind {
                    Kind::Date | Kind::DateTime | Kind::Collection => {
                        format!("({value}).is_empty()")
                    }
                    Kind::Numeric => {
                        format!("({value}).checked_cmp(\"0\".parse::<Decimal>()?)?.is_eq()")
                    }
                    Kind::Text => {
                        format!("({value}).trim_end_spaces().utf16_length() == 0")
                    }
                    Kind::Boolean => format!("!({value})"),
                    Kind::Record => {
                        return Err("IsEmpty is not supported on records directly".into());
                    }
                };
                (Kind::Boolean, code)
            }
            Expr::Call(name, args) if name == "NULLVALUE" => {
                if args.len() != 1 {
                    return Err("Nullvalue expects exactly one variable or field argument".into());
                }
                let target_type = match &args[0] {
                    Expr::Variable(var_name) => self.variable(var_name)?.1.clone(),
                    Expr::Member(receiver, field) => self.member_type(receiver, field)?,
                    _ => return Err("Nullvalue argument must be a variable or record field".into()),
                };
                match target_type {
                    DataType::Numeric { .. } => (Kind::Numeric, "\"0\".parse::<Decimal>()?".into()),
                    DataType::Character { .. } | DataType::Varchar { .. } => {
                        (Kind::Text, "Text::new(\"\")?".into())
                    }
                    DataType::Boolean {} => (Kind::Boolean, "false".into()),
                    DataType::Date {} => (Kind::Date, "Date::empty()".into()),
                    DataType::DateTime {} => (Kind::DateTime, "DateTime::empty()".into()),
                    DataType::Record { .. } | DataType::Collection { .. } => {
                        return Err("Nullvalue requires a scalar variable or field".into());
                    }
                }
            }
            Expr::Call(name, args) if name == "FORMAT" => {
                if !(1..=10).contains(&args.len()) {
                    return Err("Format expects between one and ten arguments".into());
                }
                let pattern = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let mut lowered_args = Vec::with_capacity(args.len() - 1);
                for arg in &args[1..] {
                    let (k, v) = self.lower(arg, assigned, prelude)?;
                    let text_expr = match k {
                        Kind::Text => v,
                        Kind::Numeric => format!("Text::new(&({v}).to_string())?"),
                        Kind::Boolean => format!("Text::bool_to_str({v})"),
                        Kind::Date | Kind::DateTime => format!("Text::new(&({v}).to_text())?"),
                        _ => return Err("Format arguments must be scalar values".into()),
                    };
                    lowered_args.push(text_expr);
                }
                (
                    Kind::Text,
                    format!(
                        "Text::format_template(&({pattern}), &[{}])?",
                        lowered_args.join(", ")
                    ),
                )
            }
            Expr::Call(name, args) if name == "NEWLINE" => {
                if !args.is_empty() {
                    return Err("NewLine expects zero arguments".into());
                }
                (Kind::Text, "Text::newline()".into())
            }
            Expr::Call(name, args) if name == "REPLICATE" => {
                if args.len() != 2 {
                    return Err("Replicate expects two arguments (text, count)".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let count = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (
                    Kind::Text,
                    format!("({text}).replicate(({count}).to_i64()?)?"),
                )
            }
            Expr::Call(name, args) if name == "CHARAT" => {
                if args.len() != 2 {
                    return Err("CharAt expects two arguments (text, index)".into());
                }
                let text = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let idx = self.lower_expect(&args[1], assigned, Kind::Numeric, prelude)?;
                (Kind::Text, format!("({text}).char_at(({idx}).to_i64()?)"))
            }
            Expr::Call(name, args) if name == "STRSEARCHREV" => {
                if !(2..=3).contains(&args.len()) {
                    return Err("StrSearchRev expects two or three arguments".into());
                }
                let s1 = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                let s2 = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                let start = if let Some(s) = args.get(2) {
                    let val = self.lower_expect(s, assigned, Kind::Numeric, prelude)?;
                    format!("({val}).to_i64()?")
                } else {
                    "-1".into()
                };
                (
                    Kind::Numeric,
                    format!("Decimal::from(({s1}).str_search_rev(&({s2}), {start}))"),
                )
            }
            Expr::Call(name, args) if name == "BOOLTOSTR" => {
                if args.len() != 1 {
                    return Err("BoolToStr expects one Boolean argument".into());
                }
                let val = self.lower_expect(&args[0], assigned, Kind::Boolean, prelude)?;
                (Kind::Text, format!("Text::bool_to_str({val})"))
            }
            Expr::Call(name, args) if matches!(name.as_str(), "TOBOOLEAN" | "STRTOBOOL") => {
                if args.len() != 1 {
                    return Err(format!("{name} expects one Text argument"));
                }
                let val = self.lower_expect(&args[0], assigned, Kind::Text, prelude)?;
                (Kind::Boolean, format!("({val}).to_boolean()"))
            }
            Expr::Call(name, args) if matches!(name.as_str(), "TOJSON" | "TOJSONSTRING") => {
                if args.len() != 1 {
                    return Err(format!("{name} expects one argument"));
                }
                let data_type = self.expression_type(&args[0])?;
                let k = kind(&data_type);
                if !matches!(k, Kind::Record | Kind::Collection) {
                    return Err(format!("{name} argument must be a record or collection"));
                }
                let target = self.lower_expect(&args[0], assigned, k, prelude)?;
                (Kind::Text, format!("Text::new(&({target}).to_json())?"))
            }
            Expr::Call(name, args) if matches!(name.as_str(), "FROMJSON" | "FROMJSONSTRING") => {
                if args.len() != 2 {
                    return Err(format!("{name} expects two arguments (target, json)"));
                }
                let data_type = self.expression_type(&args[0])?;
                let k = kind(&data_type);
                if !matches!(k, Kind::Record | Kind::Collection) {
                    return Err(format!("{name} target must be a record or collection"));
                }
                let target = self.lower_expect(&args[0], assigned, k, prelude)?;
                let json = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                (
                    Kind::Boolean,
                    format!("({target}).from_json(({json}).as_str())"),
                )
            }
            Expr::Call(name, args) if matches!(name.as_str(), "TOXML") => {
                if args.is_empty() || args.len() > 2 {
                    return Err("ToXml expects 1 or 2 arguments".into());
                }
                let data_type = self.expression_type(&args[0])?;
                let k = kind(&data_type);
                if !matches!(k, Kind::Record | Kind::Collection) {
                    return Err("ToXml argument must be a record or collection".into());
                }
                let target = self.lower_expect(&args[0], assigned, k, prelude)?;
                if args.len() == 2 {
                    let root = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                    (
                        Kind::Text,
                        format!("Text::new(&({target}).to_xml(Some(({root}).as_str())))?"),
                    )
                } else {
                    (Kind::Text, format!("Text::new(&({target}).to_xml(None))?"))
                }
            }
            Expr::Call(name, args) if matches!(name.as_str(), "FROMXML") => {
                if args.len() < 2 || args.len() > 3 {
                    return Err("FromXml expects 2 or 3 arguments (target, xml, [opt_root])".into());
                }
                let data_type = self.expression_type(&args[0])?;
                let k = kind(&data_type);
                if !matches!(k, Kind::Record | Kind::Collection) {
                    return Err("FromXml target must be a record or collection".into());
                }
                let target = self.lower_expect(&args[0], assigned, k, prelude)?;
                let xml = self.lower_expect(&args[1], assigned, Kind::Text, prelude)?;
                if args.len() == 3 {
                    let root = self.lower_expect(&args[2], assigned, Kind::Text, prelude)?;
                    (
                        Kind::Boolean,
                        format!("({target}).from_xml(({xml}).as_str(), Some(({root}).as_str()))"),
                    )
                } else {
                    (
                        Kind::Boolean,
                        format!("({target}).from_xml(({xml}).as_str(), None)"),
                    )
                }
            }
            Expr::Call(name, args) => {
                let (result, value) = self.lower_call(name, args, assigned, prelude, false)?;
                (result.expect("function calls return a value"), value)
            }
            Expr::Negate(value) => {
                let value = self.lower_expect(value, assigned, Kind::Numeric, prelude)?;
                (Kind::Numeric, format!("({value}).checked_neg()?"))
            }
            Expr::Not(value) => {
                let value = self.lower_expect(value, assigned, Kind::Boolean, prelude)?;
                (Kind::Boolean, format!("!({value})"))
            }
            Expr::Binary(left, operator, right) => {
                self.binary(left, *operator, right, assigned, prelude)?
            }
        };
        Ok(result)
    }

    fn expect(
        &self,
        expr: &Expr,
        assigned: &mut BTreeSet<String>,
        expected: Kind,
    ) -> Result<String, String> {
        if matches!(expr, Expr::EmptyDate) {
            return match expected {
                Kind::Date => Ok("Date::empty()".into()),
                Kind::DateTime => Ok("DateTime::empty()".into()),
                Kind::Numeric => Ok("\"0\".parse::<Decimal>()?".into()),
                Kind::Text => Ok("Text::new(\"\")?".into()),
                Kind::Boolean => Ok("false".into()),
                Kind::Record | Kind::Collection => Err("typed value mismatch".into()),
            };
        }
        let (actual, source) = self.expression(expr, assigned)?;
        if actual != expected {
            return Err("typed value mismatch".into());
        }
        Ok(source)
    }

    fn binary(
        &self,
        left: &Expr,
        operator: BinOp,
        right: &Expr,
        assigned: &mut BTreeSet<String>,
        prelude: &mut String,
    ) -> Result<(Kind, String), String> {
        let (left_kind, left) = self.lower(left, assigned, prelude)?;
        let (right_kind, right) = self.lower(right, assigned, prelude)?;
        if left_kind == Kind::Date
            && right_kind == Kind::Numeric
            && matches!(operator, BinOp::Add | BinOp::Subtract)
        {
            let days = format!("({right}).to_i64()?");
            let days = if operator == BinOp::Subtract {
                format!("({days}).checked_neg().ok_or(\"Date offset overflow\")?")
            } else {
                days
            };
            return Ok((Kind::Date, format!("({left}).add_days({days})?")));
        }
        if left_kind == Kind::Date && right_kind == Kind::Date && operator == BinOp::Subtract {
            return Ok((
                Kind::Numeric,
                format!("({left}).difference({right}).to_string().parse::<Decimal>()?"),
            ));
        }
        if left_kind != right_kind {
            return Err("binary operand type mismatch".into());
        }
        let result = match operator {
            BinOp::And | BinOp::Or => {
                if left_kind != Kind::Boolean {
                    return Err("logical operands must be Boolean".into());
                }
                let operator = if operator == BinOp::And { "&&" } else { "||" };
                (Kind::Boolean, format!("(({left}) {operator} ({right}))"))
            }
            BinOp::Add if left_kind == Kind::Text => {
                (Kind::Text, format!("({left}).checked_concat({right})?"))
            }
            BinOp::Add | BinOp::Subtract | BinOp::Multiply | BinOp::Divide => {
                if left_kind != Kind::Numeric {
                    return Err("arithmetic operands must be Numeric".into());
                }
                let method = match operator {
                    BinOp::Add => "checked_add",
                    BinOp::Subtract => "checked_sub",
                    BinOp::Multiply => "checked_mul",
                    _ => "checked_div",
                };
                (Kind::Numeric, format!("({left}).{method}({right})?"))
            }
            BinOp::Equal | BinOp::NotEqual => {
                if matches!(left_kind, Kind::Record | Kind::Collection) {
                    return Err("record/collection comparisons are not admitted".into());
                }
                let operator = if operator == BinOp::Equal { "==" } else { "!=" };
                (Kind::Boolean, format!("(({left}) {operator} ({right}))"))
            }
            BinOp::Less | BinOp::LessEqual | BinOp::Greater | BinOp::GreaterEqual => {
                if matches!(left_kind, Kind::Date | Kind::DateTime) {
                    let op = match operator {
                        BinOp::Less => "<",
                        BinOp::LessEqual => "<=",
                        BinOp::Greater => ">",
                        _ => ">=",
                    };
                    return Ok((Kind::Boolean, format!("(({left}) {op} ({right}))")));
                }
                if left_kind != Kind::Numeric {
                    return Err("ordering operands must be Numeric or Date".into());
                }
                let method = match operator {
                    BinOp::Less => "is_lt",
                    BinOp::LessEqual => "is_le",
                    BinOp::Greater => "is_gt",
                    _ => "is_ge",
                };
                (
                    Kind::Boolean,
                    format!("({left}).checked_cmp({right})?.{method}()"),
                )
            }
        };
        Ok(result)
    }

    fn block(
        &self,
        body: &[Stmt],
        assigned: &mut BTreeSet<String>,
        output: &mut String,
    ) -> Result<(), String> {
        let step_check = if self.is_sub {
            "*self.steps = self.steps.checked_sub(1).ok_or(\"execution step limit exceeded\")?;\n"
        } else {
            "*steps = steps.checked_sub(1).ok_or(\"execution step limit exceeded\")?;\n"
        };
        for statement in body {
            if output.len() > 8 * 1024 * 1024 {
                return Err("typed generated source exceeds 8 MiB".into());
            }
            output.push_str(step_check);
            match statement {
                Stmt::AssignField(receiver, field, expression) => {
                    let field_type = self.member_type(receiver, field)?;
                    let receiver = self.expect(receiver, assigned, Kind::Record)?;
                    let value = if matches!(
                        field_type,
                        DataType::Record { .. } | DataType::Collection { .. }
                    ) {
                        self.container_assignment(&field_type, expression, assigned)?
                    } else {
                        let value = self.expect(expression, assigned, kind(&field_type))?;
                        boundary(&field_type, &value)
                    };
                    let variant = record_field_variant(&field_type)?;
                    writeln!(output, "{{let field_value = {value}; ({receiver}).set({field:?}, Field::{variant}(field_value))?;}}").unwrap();
                }
                Stmt::CollectionAdd(receiver, expression, opt_idx) => {
                    let DataType::Collection { schema } = self.expression_type(receiver)? else {
                        return Err("Add receiver must be a collection".into());
                    };
                    if self.expression_type(expression)? != (DataType::Record { schema }) {
                        return Err("collection item schema mismatch".into());
                    }
                    let receiver = self.expect(receiver, assigned, Kind::Collection)?;
                    let value = self.expect(expression, assigned, Kind::Record)?;
                    if let Some(idx_expr) = opt_idx {
                        let idx = self.expect(idx_expr, assigned, Kind::Numeric)?;
                        writeln!(
                            output,
                            "{{let item = {value}; ({receiver}).insert(({idx}).to_i64()?, item)?;}}"
                        )
                        .unwrap();
                    } else {
                        writeln!(output, "{{let item = {value}; ({receiver}).add(item)?;}}")
                            .unwrap();
                    }
                }
                Stmt::CollectionRemove(receiver, idx_expr) => {
                    let DataType::Collection { .. } = self.expression_type(receiver)? else {
                        return Err("Remove receiver must be a collection".into());
                    };
                    let receiver = self.expect(receiver, assigned, Kind::Collection)?;
                    let idx = self.expect(idx_expr, assigned, Kind::Numeric)?;
                    writeln!(output, "({receiver}).remove(({idx}).to_i64()?)?;").unwrap();
                }
                Stmt::CollectionClear(receiver) => {
                    let DataType::Collection { .. } = self.expression_type(receiver)? else {
                        return Err("Clear receiver must be a collection".into());
                    };
                    let receiver = self.expect(receiver, assigned, Kind::Collection)?;
                    writeln!(output, "({receiver}).clear();").unwrap();
                }
                Stmt::Assign(name, expression) => {
                    let (slot, data_type) = self.variable(name)?;
                    let target = if self.is_sub {
                        format!("*self.v{slot}")
                    } else {
                        format!("v{slot}")
                    };
                    if matches!(
                        data_type,
                        DataType::Record { .. } | DataType::Collection { .. }
                    ) {
                        let value = self.container_assignment(data_type, expression, assigned)?;
                        writeln!(output, "{target} = {value};").unwrap();
                        assigned.insert(name.clone());
                        continue;
                    }
                    let value = self.expect(expression, assigned, kind(data_type))?;
                    match data_type {
                        DataType::Boolean {}
                        | DataType::Date {}
                        | DataType::DateTime {}
                        | DataType::Character { .. }
                        | DataType::Varchar { .. }
                        | DataType::Record { .. }
                        | DataType::Collection { .. } => writeln!(output, "{target} = {value};"),
                        DataType::Numeric { precision, scale } => writeln!(
                            output,
                            "{target} = NumericType::new({precision}, {scale})?.assign({value})?;"
                        ),
                    }
                    .unwrap();
                    assigned.insert(name.clone());
                }
                Stmt::Print(expression) | Stmt::MsgStatus(expression) => {
                    let (kind, value) = self.expression(expression, assigned)?;
                    if matches!(statement, Stmt::MsgStatus(_)) && kind != Kind::Text {
                        return Err("Msg(..., status) requires a text message".into());
                    }
                    if matches!(kind, Kind::Record | Kind::Collection) {
                        return Err("record display is not admitted; read a scalar field".into());
                    }
                    if matches!(kind, Kind::Date | Kind::DateTime) {
                        writeln!(output, "println!(\"{{}}\", {value}.to_text());").unwrap();
                        continue;
                    }
                    if let Expr::Variable(name) = expression
                        && let DataType::Numeric { precision, scale } = self.variable(name)?.1
                    {
                        writeln!(output, "println!(\"{{}}\", NumericType::new({precision}, {scale})?.render({value})?);").unwrap();
                        continue;
                    }
                    writeln!(output, "println!(\"{{}}\", {value});").unwrap();
                }
                Stmt::ReturnVoid => {
                    if self.return_type.is_some() {
                        return Err(
                            "bare Return is not valid in a value-returning procedure".into()
                        );
                    }
                    if self.is_sub {
                        writeln!(output, "self.return_in_sub = true; return Ok(());").unwrap();
                    } else {
                        writeln!(output, "return Ok({});", self.result("()")).unwrap();
                    }
                }
                Stmt::Return(expression) => {
                    if let Some(data_type) = self.return_type {
                        let value = self.expect(expression, assigned, kind(data_type))?;
                        let value = boundary(data_type, &value);
                        if self.is_sub {
                            writeln!(output, "self.return_value = Some({value}); self.return_in_sub = true; return Ok(());").unwrap();
                        } else {
                            writeln!(output, "return Ok({});", self.result(&value)).unwrap();
                        }
                    } else {
                        let value = self.expect(expression, assigned, Kind::Numeric)?;
                        if self.is_sub {
                            writeln!(
                                output,
                                "let _ = {value}; self.return_in_sub = true; return Ok(());"
                            )
                            .unwrap();
                        } else {
                            writeln!(output, "let _ = {value}; return Ok({});", self.result("()"))
                                .unwrap();
                        }
                    }
                }
                Stmt::Call(call) => {
                    let Expr::Call(name, args) = call else {
                        return Err("call statement must hold a call".into());
                    };
                    let mut prelude = String::new();
                    let (_, value) = self.lower_call(name, args, assigned, &mut prelude, true)?;
                    writeln!(output, "{{{prelude}let _ = {value};}}").unwrap();
                }
                Stmt::If(condition, yes, no) => {
                    let condition = self.expect(condition, assigned, Kind::Boolean)?;
                    writeln!(output, "if {condition} {{").unwrap();
                    let mut yes_assigned = assigned.clone();
                    self.block(yes, &mut yes_assigned, output)?;
                    output.push_str("} else {\n");
                    let mut no_assigned = assigned.clone();
                    self.block(no, &mut no_assigned, output)?;
                    output.push_str("}\n");
                    *assigned = match (returns(yes), returns(no)) {
                        (true, false) => no_assigned,
                        (false, true) => yes_assigned,
                        _ => yes_assigned.intersection(&no_assigned).cloned().collect(),
                    };
                }
                Stmt::For(name, initial, limit, step, body) => {
                    if *step == 0 {
                        return Err("For Step must be nonzero".into());
                    }
                    let (slot, data_type) = self.variable(name)?;
                    if kind(data_type) != Kind::Numeric {
                        return Err("For iterator must be Numeric".into());
                    }
                    let target = if self.is_sub {
                        format!("(*self.v{slot})")
                    } else {
                        format!("v{slot}")
                    };
                    let initial = self.expect(initial, assigned, Kind::Numeric)?;
                    writeln!(
                        output,
                        "{{{}",
                        assign_code(slot, data_type, &initial, self.is_sub)
                    )
                    .unwrap();
                    assigned.insert(name.clone());
                    let limit = self.expect(limit, assigned, Kind::Numeric)?;
                    let comparison = if *step > 0 { "is_le" } else { "is_ge" };
                    writeln!(output, "while {{let for_limit = {limit}; {target}.checked_cmp(for_limit)?.{comparison}()}} {{\n{step_check}").unwrap();
                    self.block(body, &mut assigned.clone(), output)?;
                    let increment = format!(
                        "{target}.checked_add(({step}_i64).to_string().parse::<Decimal>()?)?"
                    );
                    writeln!(
                        output,
                        "{}\n}}\n}}",
                        assign_code(slot, data_type, &increment, self.is_sub)
                    )
                    .unwrap();
                }
                Stmt::While(condition, body) => {
                    let condition = self.expect(condition, assigned, Kind::Boolean)?;
                    writeln!(output, "while {condition} {{\n{step_check}").unwrap();
                    self.block(body, &mut assigned.clone(), output)?;
                    output.push_str("}\n");
                }
                Stmt::ForIn(variable, collection, body) => {
                    let (slot, var_type) = self.variable(variable)?;
                    let DataType::Collection { schema: col_schema } =
                        self.expression_type(collection)?
                    else {
                        return Err("For in collection must be a collection".into());
                    };
                    let DataType::Record { schema: var_schema } = var_type else {
                        return Err(
                            "For in iterator must be a record matching collection schema".into(),
                        );
                    };
                    if var_schema != &col_schema {
                        return Err("For in item schema mismatch with collection".into());
                    }
                    let col_val = self.expect(collection, assigned, Kind::Collection)?;
                    let target = if self.is_sub {
                        format!("*self.v{slot}")
                    } else {
                        format!("v{slot}")
                    };
                    writeln!(output, "for __idx in 1..=({col_val}).len() {{\n{step_check}{target} = ({col_val}).item(__idx as i64)?;\n").unwrap();
                    let mut loop_assigned = assigned.clone();
                    loop_assigned.insert(variable.clone());
                    self.block(body, &mut loop_assigned, output)?;
                    writeln!(output, "}}").unwrap();
                }
                Stmt::DoSub(name) => {
                    let sub_id = self
                        .sub_ids
                        .get(name)
                        .ok_or_else(|| format!("unknown subroutine {name}"))?;
                    if self.is_sub {
                        writeln!(
                            output,
                            "self.run({sub_id})?; if self.return_in_sub {{ return Ok(()); }}"
                        )
                        .unwrap();
                    } else {
                        let mut sub_fields = vec![
                            "steps".to_string(),
                            "depth".to_string(),
                            "sub_depth: 0".to_string(),
                            "return_in_sub: false".to_string(),
                        ];
                        if self.return_type.is_some() {
                            sub_fields.push("return_value: None".to_string());
                        }
                        for slot in self.slots.values() {
                            sub_fields.push(format!("v{slot}: &mut v{slot}"));
                        }
                        let inst =
                            format!("P{}Sub {{ {} }}", self.proc_index, sub_fields.join(", "));
                        if self.return_type.is_some() {
                            writeln!(
                                output,
                                "{{\nlet (__ret_in_sub, __ret_val) = {{\nlet mut __sub_runner = {inst};\n__sub_runner.run({sub_id})?;\n(__sub_runner.return_in_sub, __sub_runner.return_value)\n}};\nif __ret_in_sub {{\nlet __val = __ret_val.ok_or(\"subroutine returned without value\")?;\nreturn Ok({});\n}}\n}}",
                                self.result("__val")
                            )
                            .unwrap();
                        } else {
                            writeln!(
                                output,
                                "{{\nlet __ret_in_sub = {{\nlet mut __sub_runner = {inst};\n__sub_runner.run({sub_id})?;\n__sub_runner.return_in_sub\n}};\nif __ret_in_sub {{\nreturn Ok({});\n}}\n}}",
                                self.result("()")
                            )
                            .unwrap();
                        }
                    }
                }
                Stmt::Sub(..) => {
                    return Err("subroutines cannot be nested or placed inside blocks".into());
                }
                Stmt::Exit => {
                    writeln!(output, "break;").unwrap();
                }
                Stmt::Commit => {
                    writeln!(output, "// commit;").unwrap();
                }
                Stmt::Rollback => {
                    writeln!(output, "// rollback;").unwrap();
                }
                Stmt::FromJson(receiver, json_expr) => {
                    let data_type = self.expression_type(receiver)?;
                    let kind_type = kind(&data_type);
                    if !matches!(kind_type, Kind::Record | Kind::Collection) {
                        return Err("FromJson receiver must be a record or collection".into());
                    }
                    let receiver_code = self.expect(receiver, assigned, kind_type)?;
                    let json_code = self.expect(json_expr, assigned, Kind::Text)?;
                    if let Expr::Variable(name) = receiver.as_ref() {
                        assigned.insert(name.clone());
                    }
                    writeln!(
                        output,
                        "let _ = ({receiver_code}).from_json(({json_code}).as_str());"
                    )
                    .unwrap();
                }
                Stmt::FromXml(from_xml) => {
                    let data_type = self.expression_type(&from_xml.target)?;
                    let kind_type = kind(&data_type);
                    if !matches!(kind_type, Kind::Record | Kind::Collection) {
                        return Err("FromXml receiver must be a record or collection".into());
                    }
                    let receiver_code = self.expect(&from_xml.target, assigned, kind_type)?;
                    let xml_code = self.expect(&from_xml.xml, assigned, Kind::Text)?;
                    let opt_root_code = if let Some(ref root_expr) = from_xml.root {
                        let root_code = self.expect(root_expr, assigned, Kind::Text)?;
                        format!("Some(({root_code}).as_str())")
                    } else {
                        "None".to_string()
                    };
                    if let Expr::Variable(name) = from_xml.target.as_ref() {
                        assigned.insert(name.clone());
                    }
                    writeln!(
                        output,
                        "let _ = ({receiver_code}).from_xml(({xml_code}).as_str(), {opt_root_code});"
                    )
                    .unwrap();
                }
                Stmt::ForEach(_) => {
                    return Err(
                        "For Each database statements require specifier navigation and are not admitted in standalone typed binaries".into(),
                    );
                }
                Stmt::AssignAttribute(_, _) => {
                    return Err(
                        "Attribute assignment requires specifier navigation and is not admitted in standalone typed binaries".into(),
                    );
                }
            }
        }
        Ok(())
    }
}
