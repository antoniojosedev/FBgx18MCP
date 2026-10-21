//! Procedure call sites: the call forms the SDK output shows, and the temporaries they plan.

use super::*;

// ---- Procedure call sites ----------------------------------------------------------------------
// Each form below is the one the SDK output for KBTeste shows (see `csharp::calls` for the evidence).
// A call that does not match a form is refused by name, and the Procedure is not written.

impl<'a> CSharpEmitter<'a> {
    /// A Procedure call used as a value: `executeUdp`, which returns the Procedure's one output. Its
    /// arguments are the non-output parameters, in order, as the callee's `executeUdp` declares them.
    pub(super) fn emit_call_expression(&self, name: &str, args: &[Expr]) -> String {
        if !is_builtin_name(name) {
            if let Some(temp) = self.call_temps.borrow().get(&(name.as_ptr() as usize)) {
                // The statement's plan has already written this call's result to `temp`.
                return temp.clone();
            }
        }
        let Some(calls) = self.calls else {
            // No signature table: the call is written as it was (unit and WebPanel paths).
            let args_str = args
                .iter()
                .map(|a| self.emit_expr(a))
                .collect::<Vec<_>>()
                .join(", ");
            return format!("new {name}(context).executeUdp({args_str})");
        };
        if is_builtin_name(name) {
            self.fail(format!(
                "built-in {}() is accepted by the parser but this build writes no C# for it",
                name.to_ascii_uppercase()
            ));
            return String::new();
        }
        let Some(callee) = self.callee(calls, name) else {
            return String::new();
        };
        let params = &callee.params;
        let class = &callee.qualified_class();
        let outputs = params.iter().filter(|p| p.mode == ParamMode::Out).count();
        if outputs != 1 {
            self.fail(format!(
                "call to '{name}' in an expression: it must return exactly one value, and it has {outputs} output parameters"
            ));
            return String::new();
        }
        let input_params: Vec<&CallParam> =
            params.iter().filter(|p| p.mode != ParamMode::Out).collect();
        if args.len() != input_params.len() {
            self.fail(format!(
                "call to '{name}': {} argument(s) for {} input parameter(s)",
                args.len(),
                input_params.len()
            ));
            return String::new();
        }
        let mut parts = Vec::with_capacity(args.len());
        for (arg, param) in args.iter().zip(&input_params) {
            match param.mode {
                ParamMode::In => match self.input_argument(name, arg, &param.data_type, true) {
                    Some(text) => parts.push(text),
                    None => return String::new(),
                },
                _ => {
                    if !matches!(arg, Expr::Variable(_)) {
                        self.fail(format!(
                            "call to '{name}' in an expression: an in-out argument that is not a variable has no temporary the SDK output shows"
                        ));
                        return String::new();
                    }
                    parts.push(format!("ref {}", self.emit_expr(arg)));
                }
            }
        }
        if self.sdk_form() {
            return format!(
                "new {class}(context).executeUdp( {})",
                sdk_arguments(&parts)
            );
        }
        format!("new {class}(context).executeUdp({})", parts.join(", "))
    }

    /// An input (`in`) argument. A variable is written as it is; a literal is written as the SDK writes
    /// it for its parameter (`(decimal)(99)` for an integer, `1.235m` for a decimal). A nested Procedure
    /// call is written only inside an expression (the SDK's `executeUdp(new X(context).executeUdp(...))`).
    /// Any other form is refused.
    pub(super) fn input_argument(
        &self,
        name: &str,
        arg: &Expr,
        data_type: &DataType,
        nested_calls: bool,
    ) -> Option<String> {
        let decimal = to_csharp_type(data_type) == "decimal";
        match arg {
            Expr::Variable(_) => Some(self.emit_expr(arg)),
            Expr::Integer(value) if decimal => Some(format!("(decimal)({value})")),
            Expr::Decimal(_) if decimal => Some(self.emit_expr(arg)),
            Expr::Call(callee, _) if nested_calls && !is_builtin_name(callee) => {
                Some(self.emit_expr(arg))
            }
            _ => {
                self.fail(format!(
                    "call to '{name}': an input argument of this form has no SDK form in this build"
                ));
                None
            }
        }
    }

    /// A Procedure call as a statement: `execute(...)` with each argument in its parameter's mode. A
    /// variable takes its keyword (`out AV_B`, `ref AV_C`). A literal or arithmetic expression for an
    /// in-out parameter goes through the one decimal temporary (`GXt_decimal1 = (decimal)(7);`).
    pub(super) fn emit_statement_call(
        &self,
        name: &str,
        args: &[Expr],
        indent: &str,
        calls: &CallSignatures,
    ) -> String {
        let mut allocated = Vec::new();
        let out = self.plan_statement_call(name, args, indent, calls, &mut allocated);
        self.temps.borrow_mut().release(&allocated);
        out
    }

    pub(super) fn plan_statement_call(
        &self,
        name: &str,
        args: &[Expr],
        indent: &str,
        calls: &CallSignatures,
        allocated: &mut Vec<(&'static str, u32)>,
    ) -> String {
        let Some(callee) = self.callee(calls, name) else {
            return String::new();
        };
        let params = &callee.params;
        let class = &callee.qualified_class();
        if args.len() != params.len() {
            self.fail(format!(
                "call to '{name}': {} argument(s) for {} parameter(s)",
                args.len(),
                params.len()
            ));
            return String::new();
        }
        let mut before = String::new();
        let mut temp_in_use = false;
        let mut parts = Vec::with_capacity(params.len());
        for (arg, param) in args.iter().zip(params) {
            if !procedure_calls(arg).is_empty() {
                self.fail(format!(
                    "call to '{name}': a Procedure call inside an argument has no SDK form in this build"
                ));
                return String::new();
            }
            match param.mode {
                ParamMode::In => match self.input_argument(name, arg, &param.data_type, false) {
                    Some(text) => parts.push(text),
                    None => return String::new(),
                },
                ParamMode::Out | ParamMode::InOut => {
                    if matches!(arg, Expr::Variable(_)) {
                        let keyword = if param.mode == ParamMode::Out {
                            "out"
                        } else {
                            "ref"
                        };
                        parts.push(format!("{keyword} {}", self.emit_expr(arg)));
                        continue;
                    }
                    if param.mode == ParamMode::Out {
                        self.fail(format!(
                            "call to '{name}': an output argument must be a variable"
                        ));
                        return String::new();
                    }
                    let arithmetic = matches!(
                        arg,
                        Expr::Binary(
                            _,
                            BinOp::Add | BinOp::Subtract | BinOp::Multiply | BinOp::Divide,
                            _
                        )
                    );
                    let evidenced = matches!(arg, Expr::Integer(_)) || arithmetic;
                    if temp_type(&param.data_type) != Some("decimal") || !evidenced {
                        self.fail(format!(
                            "call to '{name}': an in-out argument of this form has no temporary the SDK output shows"
                        ));
                        return String::new();
                    }
                    if temp_in_use {
                        self.fail(format!(
                            "call to '{name}': two temporaries in one statement; this build emits one per statement, and the SDK's sibling and nested copy-in shapes are not emitted yet"
                        ));
                        return String::new();
                    }
                    temp_in_use = true;
                    let (temp, slot) = self.temps.borrow_mut().alloc("decimal");
                    allocated.push(slot);
                    before.push_str(&format!(
                        "{indent}{temp} = (decimal)({});\n",
                        self.emit_expr(arg)
                    ));
                    parts.push(format!("ref {temp}"));
                }
            }
        }
        if self.sdk_form() {
            return format!(
                "{before}{indent}new {class}(context ).execute( {}) ;\n",
                sdk_arguments(&parts)
            );
        }
        format!(
            "{before}{indent}new {class}(context).execute({});\n",
            parts.join(", ")
        )
    }

    /// An assignment whose value holds Procedure calls: the SDK's copy-in, calls and copy-out. Each call takes
    /// a temporary and copies the target into it, the calls its arguments need come before it, and the target
    /// takes the value back (`GXt_decimal1 = AV8Amount; ... AV8Amount = GXt_decimal1;`). A call nested in a
    /// larger value reads its temporary in its place (`AV8Amount = (decimal)(GXt_decimal1+GXt_decimal2)`).
    pub(super) fn emit_assignment_with_call(
        &self,
        cs_var: &str,
        data_type: &DataType,
        expr: &Expr,
        indent: &str,
        calls: &CallSignatures,
    ) -> String {
        let target = to_csharp_type(data_type);
        let plan = AssignPlan {
            cs_var,
            target: &target,
            indent,
            calls,
        };
        let mut allocated = Vec::new();
        let out = self.plan_assignment_with_call(&plan, expr, &mut allocated);
        self.temps.borrow_mut().release(&allocated);
        self.call_temps.borrow_mut().clear();
        out
    }

    pub(super) fn plan_assignment_with_call(
        &self,
        plan: &AssignPlan<'_>,
        expr: &Expr,
        allocated: &mut Vec<(&'static str, u32)>,
    ) -> String {
        let whole_value = matches!(expr, Expr::Call(name, _) if !is_builtin_name(name));
        let mut lines = String::new();
        let mut roots = Vec::new();
        for (name, args) in top_level_calls(expr) {
            match self.plan_call(plan, name, args, whole_value, &mut lines, allocated) {
                Some(temp) => roots.push(temp),
                None => return String::new(),
            }
        }
        if whole_value {
            // The whole value is the call's own temporary, with no cast: `AV9PairResult = GXt_decimal1;`.
            return format!("{lines}{}{} = {};\n", plan.indent, plan.cs_var, roots[0]);
        }
        if self.sdk_form() {
            if let Expr::Binary(left, op @ (BinOp::And | BinOp::Or), right) = expr {
                // `AV9Flag = (bool)(GXt_boolean3||GXt_boolean4);` (arusttypedcallprobe.cs).
                let joiner = if *op == BinOp::And { "&&" } else { "||" };
                return format!(
                    "{lines}{}{} = ({})({}{joiner}{});\n",
                    plan.indent,
                    plan.cs_var,
                    plan.target,
                    self.emit_expr(left),
                    self.emit_expr(right)
                );
            }
        }
        let value = self.emit_expr(expr);
        format!(
            "{lines}{}{} = ({})({value});\n",
            plan.indent, plan.cs_var, plan.target
        )
    }

    /// Plans one Procedure call of an assignment and returns the temporary its result is written to. The
    /// copy-in of that temporary comes first, then the calls its arguments need, then the call itself
    /// (arusttypedcallprobe.cs lines 70-74 and 91-97; apoa97435900.cs lines 71-75).
    pub(super) fn plan_call(
        &self,
        plan: &AssignPlan<'_>,
        name: &str,
        args: &[Expr],
        whole_value: bool,
        lines: &mut String,
        allocated: &mut Vec<(&'static str, u32)>,
    ) -> Option<String> {
        let callee = self.callee(plan.calls, name)?;
        let params = &callee.params;
        let class = &callee.qualified_class();
        let outputs: Vec<usize> = params
            .iter()
            .enumerate()
            .filter(|(_, p)| p.mode == ParamMode::Out)
            .map(|(i, _)| i)
            .collect();
        if outputs.len() != 1 {
            self.fail(format!(
                "call to '{name}' in an assignment: it must return exactly one value, and it has {} output parameters",
                outputs.len()
            ));
            return None;
        }
        let out_at = outputs[0];
        let out_type = to_csharp_type(&params[out_at].data_type);
        if whole_value && out_type != plan.target {
            self.fail(format!(
                "call to '{name}' in an assignment to '{}': its result is {out_type}, not {}",
                plan.cs_var, plan.target
            ));
            return None;
        }
        let Some(kind) = temp_type(&params[out_at].data_type) else {
            self.fail(format!(
                "call to '{name}' in an assignment to '{}': a {out_type} result has no temporary the SDK output shows",
                plan.cs_var
            ));
            return None;
        };
        let input_count = params.iter().filter(|p| p.mode != ParamMode::Out).count();
        if args.len() != input_count {
            self.fail(format!(
                "call to '{name}': {} argument(s) for {input_count} input parameter(s)",
                args.len()
            ));
            return None;
        }
        // The copy-in is the target itself when the temporary has the target's type. Otherwise a numeric
        // temporary starts at `0` (`GXt_decimal2 = 0;`, arusttypedcallprobe.cs line 79). No other initial
        // value of a temporary is evidenced, so a boolean temporary of another type is refused.
        let copy_in = if temp_cs_type(kind) == plan.target {
            plan.cs_var.to_string()
        } else if kind == "boolean" {
            self.fail(format!(
                "call to '{name}' in an assignment to '{}': a boolean temporary for a {} target has no evidenced initial value",
                plan.cs_var, plan.target
            ));
            return None;
        } else {
            "0".to_string()
        };
        let (temp, slot) = self.temps.borrow_mut().alloc(kind);
        allocated.push(slot);
        let _ = writeln!(lines, "{}{temp} = {copy_in};", plan.indent);
        self.call_temps
            .borrow_mut()
            .insert(name.as_ptr() as usize, temp.clone());
        let mut parts = Vec::with_capacity(params.len());
        let mut inputs = args.iter();
        for (index, param) in params.iter().enumerate() {
            if index == out_at {
                parts.push(format!("out {temp}"));
                continue;
            }
            let Some(arg) = inputs.next() else {
                self.fail(format!("call to '{name}': argument count changed"));
                return None;
            };
            match param.mode {
                ParamMode::In => {
                    for (nested, nested_args) in top_level_calls(arg) {
                        self.plan_call(plan, nested, nested_args, false, lines, allocated)?;
                    }
                    parts.push(self.input_argument(name, arg, &param.data_type, true)?);
                }
                _ => {
                    if !matches!(arg, Expr::Variable(_)) {
                        self.fail(format!(
                            "call to '{name}' in an assignment: an in-out argument that is not a variable needs a second temporary, which is not evidenced"
                        ));
                        return None;
                    }
                    parts.push(format!("ref {}", self.emit_expr(arg)));
                }
            }
        }
        if self.sdk_form() {
            let _ = writeln!(
                lines,
                "{}new {class}(context ).execute( {}) ;",
                plan.indent,
                sdk_arguments(&parts)
            );
        } else {
            let _ = writeln!(
                lines,
                "{}new {class}(context).execute({});",
                plan.indent,
                parts.join(", ")
            );
        }
        Some(temp)
    }
}

/// The Procedure calls of `expr` that are not inside another Procedure call's arguments, in source order.
/// A nested call is planned by the call that encloses it.
pub(super) fn top_level_calls(expr: &Expr) -> Vec<(&str, &[Expr])> {
    let mut found = Vec::new();
    collect_top_level(expr, &mut found);
    found
}

pub(super) fn collect_top_level<'e>(expr: &'e Expr, found: &mut Vec<(&'e str, &'e [Expr])>) {
    match expr {
        Expr::Paren(inner) => collect_top_level(inner, found),
        Expr::Call(name, args) => {
            if is_builtin_name(name) {
                for arg in args {
                    collect_top_level(arg, found);
                }
            } else {
                found.push((name.as_str(), args.as_slice()));
            }
        }
        Expr::Integer(_)
        | Expr::Decimal(_)
        | Expr::Text(_)
        | Expr::Boolean(_)
        | Expr::EmptyDate
        | Expr::Variable(_)
        | Expr::Attribute(_) => {}
        Expr::TextLength(inner)
        | Expr::Member(inner, _)
        | Expr::CloneRecord(inner)
        | Expr::DateMethod(_, inner)
        | Expr::Not(inner)
        | Expr::Negate(inner) => collect_top_level(inner, found),
        Expr::CollectionItem(a, b) | Expr::Binary(a, _, b) | Expr::DateShift(_, a, b) => {
            collect_top_level(a, found);
            collect_top_level(b, found);
        }
    }
}

/// The assignment whose Procedure calls are planned: its target variable, the C# type of the target, the
/// indentation of its lines and the Procedures of the build.
pub(super) struct AssignPlan<'p> {
    cs_var: &'p str,
    target: &'p str,
    indent: &'p str,
    calls: &'p CallSignatures,
}

/// Whether a statement that writes a member, attribute, collection or JSON value holds a Procedure call.
/// Those statements have no evidenced SDK form for a call, so such a call is refused.
pub(super) fn statement_has_procedure_call(stmt: &Stmt) -> bool {
    let has = |expr: &Expr| !procedure_calls(expr).is_empty();
    match stmt {
        Stmt::AssignField(target, _, value) => has(target) || has(value),
        Stmt::AssignAttribute(_, value) => has(value),
        Stmt::CollectionAdd(target, value, extra) => {
            has(target) || has(value) || extra.as_ref().is_some_and(has)
        }
        Stmt::CollectionRemove(target, value) | Stmt::FromJson(target, value) => {
            has(target) || has(value)
        }
        _ => false,
    }
}

/// The SDK's call arguments: each value after a space, its mode before it (`ref  AV9Calls`,
/// `  (decimal)(1)`; arustcallpairprobe.cs, afrj12224800.cs).
pub(super) fn sdk_arguments(parts: &[String]) -> String {
    parts
        .iter()
        .map(|part| match part.split_once(' ') {
            Some((keyword @ ("ref" | "out"), value)) => format!("{keyword}  {value}"),
            _ => format!(" {part}"),
        })
        .collect::<Vec<_>>()
        .join(", ")
}
