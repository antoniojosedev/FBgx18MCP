using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Named playbooks the LLM can pull in a single tool call instead of
    // exploring/inspecting first. Each recipe is a structured step list:
    // - goal: one-line summary of the outcome
    // - prereq: things to verify BEFORE the first mutating call
    // - steps: ordered [{ tool, args, why }]
    // - pitfalls: short list of common mistakes ("don't use create_object for WWP")
    //
    // Keep entries tight (≤ ~600 tokens each). Full prose lives in
    // ToolHelpCatalog; this is the routing layer.
    internal static class RecipeCatalog
    {
        public static JObject Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Error("Recipe name is required.", "Pass name='list' to enumerate recipes.");

            string key = name.Trim().ToLowerInvariant();
            if (key == "list" || key == "index")
                return new JObject
                {
                    ["recipes"] = new JArray(RecipeNames()),
                    ["hint"] = "Call genexus_recipe { name: '<recipeName>' } to fetch a single playbook."
                };

            if (Recipes.TryGetValue(key, out var build)) return build();

            return Error($"Unknown recipe '{name}'.",
                "Try one of: " + string.Join(", ", RecipeNames()));
        }

        private static JArray RecipeNames()
        {
            var arr = new JArray();
            foreach (var k in Recipes.Keys) arr.Add(k);
            return arr;
        }

        private static JObject Error(string message, string hint)
        {
            return new JObject
            {
                ["error"] = message,
                ["hint"] = hint,
                ["availableRecipes"] = RecipeNames()
            };
        }

        private static readonly Dictionary<string, Func<JObject>> Recipes
            = new Dictionary<string, Func<JObject>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wwp_on_transaction"] = () => new JObject
                {
                    ["goal"] = "Generate the full WorkWithPlus screen family (WW<Trn> + View + Export*) for a Transaction.",
                    ["prereq"] = new JArray("Object exists and is of type Transaction (verify with genexus_inspect.metadata)."),
                    ["steps"] = new JArray(
                        Step("genexus_inspect", new JObject { ["name"] = "<Trn>", ["include"] = new JArray("metadata") },
                             "Confirm parentType=Transaction. If WebPanel/SDPanel, switch to recipe 'wwp_on_webpanel'."),
                        Step("genexus_apply_pattern", new JObject { ["name"] = "<Trn>", ["pattern"] = "WorkWithPlus" },
                             "Engine generates WorkWithPlus<Trn> (host) + WW<Trn> + View<Trn> + Export* siblings. No template needed."),
                        Step("genexus_edit", new JObject { ["name"] = "WorkWithPlus<Trn>", ["part"] = "PatternInstance", ["mode"] = "patch", ["context"] = "<unique XML anchor>", ["operation"] = "Insert_After", ["content"] = "<new XML node>" },
                             "Shape the screen by editing the host's PatternInstance. Edits auto-project to the WebForm.")
                    ),
                    ["pitfalls"] = new JArray(
                        "Do NOT use genexus_create_object for WWP — apply_pattern is the only correct entry.",
                        "Do NOT edit WW<Trn>.WebForm directly; the next reapply will overwrite it. Edit WorkWithPlus<Trn>.PatternInstance instead."
                    )
                },

                ["wwp_on_webpanel"] = () => new JObject
                {
                    ["goal"] = "Direct-attach a WorkWithPlus host onto an existing WebPanel/SDPanel (no transaction family).",
                    ["prereq"] = new JArray(
                        "Object exists and is of type WebPanel or SDPanel (verify with genexus_inspect.metadata).",
                        "Know which `WorkWithPlus for Web Template` to use; common ones: MatIsoTemplate, TransactionResp2, PopoverEmpty, TransactionPopUp. If unsure, omit settings.template — the MCP auto-discovers and returns availableTemplates."
                    ),
                    ["steps"] = new JArray(
                        Step("genexus_inspect", new JObject { ["name"] = "<WebPanel>", ["include"] = new JArray("metadata") },
                             "ABSOLUTELY CHECK PARENT TYPE FIRST. If it returns 'Transaction', switch to recipe 'wwp_on_transaction'. Misrouting was the #1 reported bug."),
                        Step("genexus_apply_pattern", new JObject { ["name"] = "<WebPanel>", ["pattern"] = "WorkWithPlus", ["settings"] = new JObject { ["template"] = "<TemplateName>" } },
                             "Direct-attach via CreatePatternInstanceWithTemplate. Response carries parentType + bindingMode + patternHost so you can verify what got wired."),
                        Step("genexus_edit", new JObject { ["name"] = "WorkWithPlus<WebPanel>", ["part"] = "PatternInstance", ["mode"] = "patch" },
                             "Edits to the host auto-project onto the WebPanel's WebForm via UpdateParentObject.")
                    ),
                    ["pitfalls"] = new JArray(
                        "WebPanel + Transaction take DIFFERENT apply paths. Never assume — always inspect first.",
                        "settings.template must match a registered `WorkWithPlus for Web Template` object; pass empty to let MCP auto-discover.",
                        "Other types (Procedure, SDT, Domain, …) are rejected upfront with parentType in the error envelope."
                    )
                },

                ["create_popup"] = () => new JObject
                {
                    ["goal"] = "Create a popup WebPanel with editable form bindings (radio/combo/text + buttons) in ONE call.",
                    ["prereq"] = new JArray("Decide the spec: title, inputs, buttons, in/out parms."),
                    ["steps"] = new JArray(
                        Step("genexus_create_popup", new JObject {
                            ["name"] = "<PopupName>",
                            ["spec"] = new JObject {
                                ["title"] = "<Title>",
                                ["inputs"] = new JArray(new JObject { ["type"] = "radio", ["varName"] = "Opcao", ["label"] = "Opção", ["options"] = new JArray(new JObject { ["value"] = "S", ["label"] = "Sim" }, new JObject { ["value"] = "N", ["label"] = "Não" }) }),
                                ["buttons"] = new JArray(new JObject { ["caption"] = "OK", ["event"] = "Confirm" }),
                                ["inParms"] = new JArray("ItemId:Numeric(10)"),
                                ["outParms"] = new JArray("Opcao:Character(1)")
                            }
                        }, "Sets Form type='layout' so bindings render editable, plus Variables, Events and Rules in a single round-trip.")
                    ),
                    ["pitfalls"] = new JArray(
                        "Do NOT create the WebPanel then add inputs piecemeal — Form type defaults to 'free style' and radio/combo become read-only.",
                        "Use callerCode `<Popup>.Popup(...)` from the parent object. The `description` of the popup is the title-bar text."
                    )
                },

                ["edit_pattern_instance"] = () => new JObject
                {
                    ["goal"] = "Surgically edit a WWP host's PatternInstance XML without destroying surrounding state.",
                    ["prereq"] = new JArray(
                        "Host object name is `WorkWithPlus<Parent>` (verify it exists; if not, apply_pattern first).",
                        "Read the current PatternInstance to find a unique anchor."
                    ),
                    ["steps"] = new JArray(
                        Step("genexus_read", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance" },
                             "Find a unique line near the edit site (e.g. an existing <standardAction name='Trn_Delete'> or attribute id)."),
                        Step("genexus_edit", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance", ["mode"] = "patch", ["context"] = "<anchor line>", ["operation"] = "Insert_After", ["content"] = "<new XML>", ["dryRun"] = true },
                             "ALWAYS dryRun first to see the projected diff."),
                        Step("genexus_edit", new JObject { ["same as above without dryRun"] = true }, "Persist. Response includes childrenOrderedListReconciliation showing what the auto-reconciliation changed.")
                    ),
                    ["pitfalls"] = new JArray(
                        "Do NOT touch `childrenOrderedList` attributes by hand — the MCP rebuilds them from your XML child order on every save.",
                        "Avoid mode='full' unless you really intend a whole-tree rewrite; patch keeps surrounding state safe."
                    )
                },

                ["add_custom_button"] = () => new JObject
                {
                    ["goal"] = "Add a custom action button to a WWP grid/toolbar.",
                    ["steps"] = new JArray(
                        Step("genexus_read", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance" }, "Locate the `<standardAction name='Trn_Delete' />` (or similar) anchor."),
                        Step("genexus_edit", new JObject {
                            ["name"] = "WorkWithPlus<X>",
                            ["part"] = "PatternInstance",
                            ["mode"] = "patch",
                            ["operation"] = "Insert_After",
                            ["context"] = "<standardAction name=\"Trn_Delete\"",
                            ["content"] = "<userAction caption=\"Auditar\" name=\"Auditar\" buttonClass=\"btn ButtonGreen\" confirm=\"False\" />"
                        }, "Insert userAction next to existing standardActions. buttonClass values vary per theme — see ToolHelpCatalog for tokens like btn ButtonGreen/ButtonRed/ButtonCinza.")
                    ),
                    ["pitfalls"] = new JArray(
                        "Do NOT add the button to the parent Transaction — buttons live on the WWP host."
                    )
                }
            };

        private static JObject Step(string tool, JObject args, string why)
        {
            return new JObject { ["tool"] = tool, ["args"] = args, ["why"] = why };
        }
    }
}
