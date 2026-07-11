using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Item #100 ("the Santo Graal") — full feature scaffold from a structured
    // spec. The agent parses its markdown / user story upstream and hands us a
    // typed JObject; we orchestrate a deterministic sequence of EXISTING tool
    // calls (create_object → apply_pattern → create_object per procedure → optional test stubs).
    //
    // Why a Service (not just a recipe): the recipe in RecipeCatalog is the
    // static playbook the agent fetches with genexus_recipe; this service is
    // what actually runs the plan. Keeping it mockable (IToolDispatcher) means
    // the unit tests don't need a live KB.
    public interface IToolDispatcher
    {
        // Invoke an underlying MCP tool by name (e.g. "genexus_create_object",
        // "genexus_apply_pattern"). Returns the raw JSON envelope the tool
        // would have returned. Implementations decide whether status="Error"
        // bubbles as an exception or as a return value; FeatureScaffoldService
        // treats any envelope whose `status` is not Ok/Success/Accepted as a
        // failure.
        JObject Invoke(string tool, JObject args);
    }

    public class FeatureScaffoldService
    {
        private readonly IToolDispatcher _dispatcher;

        public FeatureScaffoldService(IToolDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // Public entry: validate → plan → (execute|dryRun).
        public JObject Scaffold(JObject spec, bool dryRun)
        {
            var validation = ValidateSpec(spec);
            if (validation.Count > 0)
            {
                return JObject.Parse(McpResponse.Err(
                    code: "ValidationError",
                    message: "Feature spec failed validation.",
                    hint: "Fix the listed JSON paths and retry. Nothing was created.",
                    extra: new JObject { ["validation"] = JArray.FromObject(validation) }));
            }

            var plan = BuildPlan(spec);

            if (dryRun)
            {
                return JObject.Parse(McpResponse.Ok(
                    code: "DryRun",
                    result: new JObject
                    {
                        ["plan"] = plan,
                        ["hint"] = "No mutations performed. Re-run with dryRun=false to execute."
                    }));
            }

            return Execute(plan);
        }

        // Surfaced for testing — pure function, no side effects.
        public static List<JObject> ValidateSpec(JObject spec)
        {
            var errors = new List<JObject>();
            void Err(string path, string message)
            {
                errors.Add(new JObject { ["path"] = path, ["message"] = message });
            }

            if (spec == null)
            {
                Err("$", "spec is null");
                return errors;
            }

            // Top-level
            string name = spec["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                Err("$.name", "feature name is required");

            // entity
            var entity = spec["entity"] as JObject;
            if (entity == null)
            {
                Err("$.entity", "entity object is required");
            }
            else
            {
                string entType = entity["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(entType))
                    Err("$.entity.type", "entity.type is required (e.g. 'Transaction')");
                else if (!string.Equals(entType, "Transaction", StringComparison.OrdinalIgnoreCase))
                    Err("$.entity.type", "only entity.type='Transaction' is supported by this scaffold (got '" + entType + "')");

                string entName = entity["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(entName))
                    Err("$.entity.name", "entity.name is required");

                var attrs = entity["attributes"] as JArray;
                if (attrs == null || attrs.Count == 0)
                {
                    Err("$.entity.attributes", "entity.attributes must be a non-empty array");
                }
                else
                {
                    bool sawKey = false;
                    for (int i = 0; i < attrs.Count; i++)
                    {
                        var a = attrs[i] as JObject;
                        string p = "$.entity.attributes[" + i + "]";
                        if (a == null)
                        {
                            Err(p, "attribute entry must be an object");
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(a["name"]?.ToString()))
                            Err(p + ".name", "attribute name is required");
                        if (string.IsNullOrWhiteSpace(a["type"]?.ToString()))
                            Err(p + ".type", "attribute type is required (e.g. 'Numeric(8)', 'Character(60)')");
                        if (a["isKey"]?.Type == JTokenType.Boolean && a["isKey"].Value<bool>())
                            sawKey = true;
                    }
                    if (!sawKey)
                        Err("$.entity.attributes", "at least one attribute must have isKey=true");
                }
            }

            // procedures (optional)
            if (spec["procedures"] is JArray procs)
            {
                for (int i = 0; i < procs.Count; i++)
                {
                    var pr = procs[i] as JObject;
                    string p = "$.procedures[" + i + "]";
                    if (pr == null)
                    {
                        Err(p, "procedure entry must be an object");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(pr["name"]?.ToString()))
                        Err(p + ".name", "procedure name is required");
                    var parms = pr["parms"] as JArray;
                    if (parms == null)
                        Err(p + ".parms", "procedure parms array is required (use [] for no parms)");
                    else
                    {
                        for (int j = 0; j < parms.Count; j++)
                        {
                            string raw = parms[j]?.ToString();
                            if (string.IsNullOrWhiteSpace(raw))
                                Err(p + ".parms[" + j + "]", "parm entry is empty");
                            else if (!raw.Contains(":"))
                                Err(p + ".parms[" + j + "]", "parm must be 'in|out|inout:Name:Type', got '" + raw + "'");
                        }
                    }
                }
            }

            // ui block (optional — defaults to no pattern)
            if (spec["ui"] != null && !(spec["ui"] is JObject))
                Err("$.ui", "ui must be an object with optional list/edit/summary booleans");

            return errors;
        }

        // Build the executable plan as a JArray of { tool, args, why }.
        // Mirrors the shape RecipeCatalog uses so the agent can render the
        // dryRun output uniformly with recipe playbooks.
        public static JArray BuildPlan(JObject spec)
        {
            var plan = new JArray();
            var entity = (JObject)spec["entity"];
            string entName = entity["name"].ToString();

            // 1) Create the Transaction.
            var attrArr = new JArray();
            foreach (var a in (JArray)entity["attributes"])
            {
                var obj = new JObject
                {
                    ["name"] = a["name"],
                    ["type"] = a["type"]
                };
                if (a["isKey"]?.Type == JTokenType.Boolean && a["isKey"].Value<bool>())
                    obj["isKey"] = true;
                attrArr.Add(obj);
            }
            plan.Add(new JObject
            {
                ["tool"] = "genexus_create_object",
                ["args"] = new JObject
                {
                    ["type"] = "Transaction",
                    ["name"] = entName,
                    ["attributes"] = attrArr
                },
                ["why"] = "Create the root Transaction with its attribute structure."
            });

            // 2) Apply WorkWithPlus if any ui flag is set (list/edit/summary all
            //    come together — WWP gives the full screen family).
            var ui = spec["ui"] as JObject;
            bool wantUi = ui != null && (
                (ui["list"]?.Type == JTokenType.Boolean && ui["list"].Value<bool>()) ||
                (ui["edit"]?.Type == JTokenType.Boolean && ui["edit"].Value<bool>()) ||
                (ui["summary"]?.Type == JTokenType.Boolean && ui["summary"].Value<bool>()));
            if (wantUi)
            {
                plan.Add(new JObject
                {
                    ["tool"] = "genexus_apply_pattern",
                    ["args"] = new JObject
                    {
                        ["name"] = entName,
                        ["pattern"] = "WorkWithPlus"
                    },
                    ["why"] = "Generate the WorkWithPlus screen family (list/edit/summary in one shot)."
                });
            }

            // 3) Procedures — one create per spec.procedures[i]. Stub source
            //    block so the agent can later genexus_edit the body; parms come
            //    through as Rules content.
            if (spec["procedures"] is JArray procs)
            {
                bool wantTests = spec["tests"]?.Type == JTokenType.Boolean && spec["tests"].Value<bool>();
                foreach (JObject pr in procs)
                {
                    string pn = pr["name"].ToString();
                    var parms = (JArray)pr["parms"];
                    string parmRule = BuildParmRule(parms);
                    plan.Add(new JObject
                    {
                        ["tool"] = "genexus_create_object",
                        ["args"] = new JObject
                        {
                            ["type"] = "Procedure",
                            ["name"] = pn,
                            ["rules"] = parmRule,
                            ["source"] = "// TODO: implement " + pn + "\n"
                        },
                        ["why"] = "Procedure scaffold for '" + pn + "'."
                    });

                    if (wantTests)
                    {
                        plan.Add(new JObject
                        {
                            ["tool"] = "genexus_create_object",
                            ["args"] = new JObject
                            {
                                ["type"] = "Procedure",
                                ["name"] = pn + "Test",
                                ["rules"] = "parm(out:&result);",
                                ["source"] = "// GXtest stub for " + pn + "\n&result = 'TODO'\n"
                            },
                            ["why"] = "GXtest-shaped stub for '" + pn + "'."
                        });
                    }
                }
            }

            return plan;
        }

        private static string BuildParmRule(JArray parms)
        {
            if (parms == null || parms.Count == 0) return "parm();";
            var parts = new List<string>();
            foreach (var p in parms)
            {
                string raw = p?.ToString() ?? string.Empty;
                // "in:Course:Character(40)" → "in:&Course"; type lives on the
                // variable declaration which a later genexus_add_variable
                // pass would set. The Rules part only needs direction:&name.
                var bits = raw.Split(new[] { ':' }, 3);
                if (bits.Length >= 2)
                {
                    string dir = bits[0].Trim().ToLowerInvariant();
                    string nm = bits[1].Trim();
                    if (dir == "in" || dir == "out" || dir == "inout")
                        parts.Add(dir + ":&" + nm);
                    else
                        parts.Add("in:&" + nm);
                }
            }
            return "parm(" + string.Join(", ", parts) + ");";
        }

        private JObject Execute(JArray plan)
        {
            var completed = new JArray();
            for (int i = 0; i < plan.Count; i++)
            {
                var step = (JObject)plan[i];
                string tool = step["tool"].ToString();
                var args = (JObject)step["args"];

                JObject result;
                try
                {
                    result = _dispatcher.Invoke(tool, args) ?? new JObject();
                }
                catch (Exception ex)
                {
                    return JObject.Parse(McpResponse.Err(
                        code: "ScaffoldPartialFailure",
                        message: "Underlying tool '" + tool + "' threw at step " + i + ": " + ex.Message,
                        hint: "Inspect KB state and call genexus_undo if needed; then retry from step " + i + ".",
                        extra: new JObject
                        {
                            ["completedSteps"] = completed,
                            ["failedStep"] = new JObject { ["index"] = i, ["tool"] = tool, ["args"] = args }
                        }));
                }

                if (!IsOk(result))
                {
                    return JObject.Parse(McpResponse.Err(
                        code: "ScaffoldPartialFailure",
                        message: "Step " + i + " (" + tool + ") returned a non-Ok envelope.",
                        hint: "Fix and rerun manually, or call genexus_undo to revert prior steps.",
                        extra: new JObject
                        {
                            ["completedSteps"] = completed,
                            ["failedStep"] = new JObject { ["index"] = i, ["tool"] = tool, ["args"] = args, ["result"] = result }
                        }));
                }

                completed.Add(new JObject
                {
                    ["index"] = i,
                    ["tool"] = tool,
                    ["args"] = args,
                    ["result"] = result
                });
            }

            return JObject.Parse(McpResponse.Ok(
                code: "ScaffoldCompleted",
                result: new JObject
                {
                    ["completedSteps"] = completed,
                    ["stepCount"] = completed.Count
                }));
        }

        private static bool IsOk(JObject env)
        {
            if (env == null) return false;
            string s = env["status"]?.ToString();
            if (string.IsNullOrEmpty(s)) return env["error"] == null; // no status + no error = treat as ok
            // Dispatcher.Invoke can reach ANY registered tool, and several producers still
            // hand-roll legacy PascalCase envelopes instead of going through McpResponse —
            // confirmed still alive as of this writing: ForgeService ("Success"),
            // VersionControlService ("Success"), ObjectService.WorkerReload/CreateObject's
            // catch path ("Accepted"/"Error"), Program.cs soft-reload ("Accepted"). Do not
            // drop the legacy branch until those are migrated to McpResponse.Ok/Err.
            return string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase)       // canonical v2.8.0
                || string.Equals(s, "accepted", StringComparison.OrdinalIgnoreCase)  // canonical v2.8.0
                || string.Equals(s, "Ok", StringComparison.OrdinalIgnoreCase)        // legacy
                || string.Equals(s, "Success", StringComparison.OrdinalIgnoreCase)   // legacy
                || string.Equals(s, "Accepted", StringComparison.OrdinalIgnoreCase)  // legacy
                || string.Equals(s, "Created", StringComparison.OrdinalIgnoreCase);  // legacy
        }
    }
}
