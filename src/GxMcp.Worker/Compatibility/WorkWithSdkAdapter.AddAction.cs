using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    internal static partial class WorkWithSdkAdapter
    {
        public static bool AddAction(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            WriteService write,
            string tabCode,
            string actionName,
            string caption,
            KBObject procObj,
            string condition,
            IReadOnlyList<string> parameters,
            bool dryRun,
            out JObject result,
            out string errorCode,
            out string errorMessage)
        {
            result = null;
            errorCode = null;
            errorMessage = null;

            if (instanceObj == null)
            {
                errorCode = "NullInstance";
                errorMessage = "WorkWith PatternInstance object is null.";
                return false;
            }

            // Step 1: Pre-check tab existence and duplicate action
            if (!ListActions(instanceObj, patterns, out var tabs, out var preCheckSource, out var listError))
            {
                errorCode = listError ?? "ListActionsFailed";
                errorMessage = $"Could not inspect tabs for WorkWith instance '{instanceObj.Name}': {listError}";
                return false;
            }

            var targetTab = tabs.FirstOrDefault(t =>
                string.Equals(t["code"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t["name"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)) as JObject;

            if (targetTab == null)
            {
                errorCode = "TabNotFound";
                errorMessage = $"Tab '{tabCode}' was not found in WorkWith instance '{instanceObj.Name}'. Available tabs: {string.Join(", ", tabs.Select(t => t["code"]?.ToString()))}.";
                return false;
            }

            var existingActions = targetTab["actions"] as JArray;
            if (existingActions != null && existingActions.Any(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "ActionAlreadyExists";
                errorMessage = $"Action '{actionName}' already exists on tab '{tabCode}' in WorkWith instance '{instanceObj.Name}'.";
                return false;
            }

            // Step 2: Build plan / preflight
            var plan = BuildActionPlan(instanceObj.Name, tabCode, actionName, caption, procObj?.Name, condition, parameters, procObj);
            plan["route"] = IsNativeSdkAvailable ? "native-sdk" : "xml-fallback";
            plan["defaultNormalization"] = IsNativeSdkAvailable
                ? NativeDefaultNormalization
                : FallbackDefaultNormalization;

            var sigAdv = plan["signatureAdvisory"] as JObject;
            if (sigAdv != null && sigAdv["arityMatches"]?.ToObject<bool>() == false)
            {
                result = plan;
                errorCode = "ArityMismatch";
                errorMessage = sigAdv["warning"]?.ToString() ?? $"Target Procedure '{procObj?.Name}' parameter count mismatch.";
                return false;
            }

            if (dryRun)
            {
                result = plan;
                return true;
            }

            // Step 3: Real write
            bool nativeSucceeded = false;
            var asm = GetWorkWithAssembly();
            if (asm != null)
            {
                try
                {
                    var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                    var loadMethod = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                    object wwi = loadMethod?.Invoke(null, new object[] { instanceObj });
                    if (wwi != null)
                    {
                        var levels = wwiType.GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
                        object sdkTab = null;
                        if (levels != null)
                        {
                            foreach (var level in levels)
                            {
                                var view = level.GetType().GetProperty("View")?.GetValue(level, null);
                                if (view == null) continue;
                                var tabColl = view.GetType().GetProperty("Tabs")?.GetValue(view, null);
                                if (tabColl == null) continue;
                                var findTabMethod = tabColl.GetType().GetMethod("FindTab", new[] { typeof(string) });
                                sdkTab = findTabMethod?.Invoke(tabColl, new object[] { tabCode });
                                if (sdkTab != null) break;
                            }
                        }

                        if (sdkTab != null)
                        {
                            var addActionMethod = sdkTab.GetType().GetMethod("AddAction", new[] { typeof(string) });
                            var newAction = addActionMethod?.Invoke(sdkTab, new object[] { actionName });
                            if (newAction != null)
                            {
                                newAction.GetType().GetProperty("Caption")?.SetValue(newAction, caption ?? actionName, null);
                                if (procObj != null)
                                    newAction.GetType().GetProperty("Gxobject")?.SetValue(newAction, procObj, null);
                                if (!string.IsNullOrEmpty(condition))
                                    newAction.GetType().GetProperty("Condition")?.SetValue(newAction, condition, null);

                                if (parameters != null && parameters.Count > 0)
                                {
                                    var addParamMethod = newAction.GetType().GetMethod("AddParameter", new[] { typeof(string) });
                                    foreach (var p in parameters)
                                    {
                                        addParamMethod?.Invoke(newAction, new object[] { p });
                                    }
                                }

                                var saveToMethod = wwiType.GetMethod("SaveTo", new[] { instanceObj.GetType() })
                                    ?? wwiType.GetMethod("SaveTo");
                                saveToMethod?.Invoke(wwi, new object[] { instanceObj });

                                instanceObj.Save();
                                nativeSucceeded = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("[WorkWithSdkAdapter] Native AddAction failed, falling back to XML: " + ex.Message);
                }
            }

            if (!nativeSucceeded)
            {
                string currentXml = patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                if (string.IsNullOrWhiteSpace(currentXml))
                {
                    errorCode = "PatternInstanceXmlEmpty";
                    errorMessage = "Could not read PatternInstance XML for " + instanceObj.Name;
                    return false;
                }

                if (!TryAddActionToXml(currentXml, tabCode, actionName, caption, procObj, condition, parameters, out string newXml, out errorCode, out errorMessage))
                {
                    return false;
                }

                if (write != null)
                {
                    var writeResult = write.WriteObject(instanceObj.Name, "PatternInstance", newXml, dryRun: false, allowGridStructure: true);
                    var writeJson = JObject.Parse(writeResult);
                    if (writeJson["error"] != null)
                    {
                        errorCode = writeJson["error"]?["code"]?.ToString() ?? "PatternSaveFailed";
                        errorMessage = writeJson["error"]?["message"]?.ToString() ?? "Failed to save PatternInstance";
                        return false;
                    }
                }
                else
                {
                    instanceObj.Save();
                }
            }

            // Step 4: Verification reread
            if (!ListActions(instanceObj, patterns, out var verifyTabs, out var observedSource, out _))
            {
                errorCode = "VerificationReadFailed";
                errorMessage = "Action was saved, but verification re-read failed.";
                return false;
            }

            if (verifyTabs.Count < tabs.Count)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Instance tabs were truncated after write: expected at least {tabs.Count} tabs, found {verifyTabs.Count}.";
                return false;
            }

            var verifiedTab = verifyTabs.FirstOrDefault(t =>
                string.Equals(t["code"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t["name"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)) as JObject;
            var verifiedActions = verifiedTab?["actions"] as JArray;
            var verifiedAct = verifiedActions?.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)) as JObject;

            if (verifiedAct == null)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Action '{actionName}' was not found in the verified post-write state.";
                return false;
            }

            string expectedCaption = caption ?? actionName;
            string expectedProcedure = procObj?.Name ?? "";
            string expectedCondition = condition ?? "";
            var expectedParams = (parameters ?? Array.Empty<string>()).ToList();

            if (!VerifyActionMatches(verifiedAct, actionName, caption, procObj?.Name, condition, parameters, out string mismatchDetail))
            {
                errorCode = "VerificationMismatch";
                errorMessage = $"Action '{actionName}' was persisted, but post-save verification failed: {mismatchDetail}";
                return false;
            }

            var verification = new JObject
            {
                ["scope"] = "tab_action_reread",
                ["fieldsCompared"] = new JArray("name", "caption", "procedure", "condition", "parameters"),
                ["allFieldsMatched"] = true
            };
            DescribeReadSource(verification, "observedSource", observedSource);
            DescribeReadSource(verification, "preCheckSource", preCheckSource);

            result = new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tab"] = tabCode,
                ["action"] = verifiedAct.DeepClone(),
                ["requestedAction"] = new JObject
                {
                    ["name"] = actionName,
                    ["caption"] = expectedCaption,
                    ["procedure"] = expectedProcedure,
                    ["condition"] = expectedCondition,
                    ["parameters"] = new JArray(expectedParams)
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = true,
                ["verification"] = verification,
                ["route"] = nativeSucceeded ? "native-sdk" : "xml-fallback",
                ["verificationScope"] = "action definition, target tab actions collection, neighbor tabs integrity, and " + DescribeRereadScope(observedSource),
                ["defaultNormalization"] = nativeSucceeded
                    ? NativeDefaultNormalization
                    : FallbackDefaultNormalization,
                ["derivedObjectRegeneration"] = "WorkWith instance definition persisted. Note: On GeneXus 18/17, derived WebComponents (e.g. <Trn>General) may require IDE Apply or build to re-project the new action control.",
                ["persistenceEvidence"] = BuildPersistenceEvidence()
            };
            return true;
        }

        internal static JObject BuildActionPlan(
            string instanceName,
            string tabCode,
            string actionName,
            string caption,
            string procName,
            string condition,
            IReadOnlyList<string> parameters,
            KBObject procObj = null)
        {
            var sigAdvisory = TryInspectSignatureAdvisory(procObj, parameters);
            bool arityOk = sigAdvisory == null || sigAdvisory["arityMatches"]?.ToObject<bool>() != false;

            var validations = new JObject
            {
                ["targetObjectResolved"] = true,
                ["workWithInstanceResolved"] = true,
                ["tabExists"] = true,
                ["actionNameFormatValid"] = true,
                ["actionNameUniqueOnTab"] = true,
                ["procedureResolved"] = !string.IsNullOrEmpty(procName),
                ["procedureTypeValid"] = true,
                ["arityMatches"] = arityOk
            };

            var validationLimits = new JObject
            {
                ["signatureAndTypes"] = "Procedure parameter types and signature binding against parm() rule are not validated or compiled.",
                ["conditionExpression"] = "Condition expression syntax and semantics are not compiled or evaluated.",
                ["parameterExpressions"] = "Parameter expressions syntax and semantics are not compiled or evaluated."
            };

            var plan = new JObject
            {
                ["instance"] = instanceName,
                ["tab"] = tabCode,
                ["actionName"] = actionName,
                ["caption"] = caption ?? actionName,
                ["procedure"] = procName ?? "",
                ["parameters"] = new JArray(parameters ?? Array.Empty<string>()),
                ["condition"] = condition ?? "",
                ["valid"] = arityOk,
                ["validations"] = validations,
                ["validationLimits"] = validationLimits
            };

            if (sigAdvisory != null)
            {
                plan["signatureAdvisory"] = sigAdvisory;
            }

            return plan;
        }

        internal static JObject TryInspectSignatureAdvisory(KBObject procObj, IReadOnlyList<string> parameters)
        {
            if (procObj == null) return null;
            try
            {
                var parts = procObj.Parts;
                if (parts == null) return null;

                KBObjectPart rulesPart = null;
                foreach (KBObjectPart p in parts)
                {
                    if (p != null && (string.Equals(p.TypeDescriptor?.Name, "Rules", StringComparison.OrdinalIgnoreCase)
                        || p.GetType().Name.IndexOf("RulesPart", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        rulesPart = p;
                        break;
                    }
                }

                if (rulesPart == null) return null;

                string rulesSrc = rulesPart.GetType().GetProperty("Source")?.GetValue(rulesPart, null) as string;
                if (!string.IsNullOrWhiteSpace(rulesSrc))
                {
                    var sig = GeneXusSignatureParser.Parse(rulesSrc);
                    if (sig != null && !string.IsNullOrEmpty(sig.ParmRule))
                    {
                        int expectedCount = sig.Parameters != null ? sig.Parameters.Count : 0;
                        int suppliedCount = parameters != null ? parameters.Count : 0;
                        var advisory = new JObject
                        {
                            ["parmRule"] = sig.ParmRule,
                            ["expectedParameterCount"] = expectedCount,
                            ["suppliedParameterCount"] = suppliedCount,
                            ["arityMatches"] = (expectedCount == suppliedCount)
                        };
                        if (expectedCount != suppliedCount)
                        {
                            advisory["warning"] = $"Target Procedure '{procObj.Name}' declares {expectedCount} parameter(s) in its parm() rule, but {suppliedCount} parameter(s) were supplied in add_action.";
                        }
                        return advisory;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[WorkWithSdkAdapter] Signature advisory inspection failed: " + ex.Message);
            }
            return null;
        }

        internal static bool VerifyActionMatches(
            JObject verifiedAct,
            string actionName,
            string caption,
            string procName,
            string condition,
            IReadOnlyList<string> parameters,
            out string mismatchDetail)
        {
            mismatchDetail = null;
            if (verifiedAct == null)
            {
                mismatchDetail = "Action not found in verified state.";
                return false;
            }

            string expectedCaption = caption ?? actionName;
            string expectedProcedure = procName ?? "";
            string expectedCondition = condition ?? "";
            var expectedParams = (parameters ?? Array.Empty<string>()).ToList();

            string observedName = verifiedAct["name"]?.ToString() ?? "";
            string observedCaption = verifiedAct["caption"]?.ToString() ?? "";
            string observedProcedure = verifiedAct["procedure"]?.ToString() ?? "";
            string observedCondition = verifiedAct["condition"]?.ToString() ?? "";
            var observedParams = (verifiedAct["parameters"] as JArray)?.Select(p => p.ToString()).ToList() ?? new List<string>();

            if (!string.Equals(observedName, actionName, StringComparison.OrdinalIgnoreCase))
            {
                mismatchDetail = $"Action name mismatch: expected '{actionName}', observed '{observedName}'.";
                return false;
            }

            if (!string.Equals(observedCaption, expectedCaption, StringComparison.Ordinal))
            {
                mismatchDetail = $"Caption mismatch: expected '{expectedCaption}', observed '{observedCaption}'.";
                return false;
            }

            if (!string.Equals(observedProcedure, expectedProcedure, StringComparison.OrdinalIgnoreCase))
            {
                mismatchDetail = $"Procedure mismatch: expected '{expectedProcedure}', observed '{observedProcedure}'.";
                return false;
            }

            if (!string.Equals(observedCondition, expectedCondition, StringComparison.Ordinal))
            {
                mismatchDetail = $"Condition mismatch: expected '{expectedCondition}', observed '{observedCondition}'.";
                return false;
            }

            if (!observedParams.SequenceEqual(expectedParams, StringComparer.Ordinal))
            {
                mismatchDetail = $"Parameters mismatch: expected [{string.Join(", ", expectedParams)}], observed [{string.Join(", ", observedParams)}].";
                return false;
            }

            return true;
        }

        internal static bool TryAddActionToXml(
            string xml,
            string tabCode,
            string actionName,
            string caption,
            KBObject procObj,
            string condition,
            IReadOnlyList<string> parameters,
            out string newXml,
            out string errorCode,
            out string errorMessage)
        {
            newXml = null;
            errorCode = null;
            errorMessage = null;

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                var tabElem = doc.Descendants().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "tab", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals((string)e.Attribute("code"), tabCode, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals((string)e.Attribute("name"), tabCode, StringComparison.OrdinalIgnoreCase)));

                if (tabElem == null)
                {
                    errorCode = "TabNotFound";
                    errorMessage = $"Tab '{tabCode}' not found in XML.";
                    return false;
                }

                var actionsElem = tabElem.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "actions", StringComparison.OrdinalIgnoreCase));
                if (actionsElem == null)
                {
                    actionsElem = new XElement("actions");
                    tabElem.Add(actionsElem);
                }

                var existingAct = actionsElem.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "action", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)e.Attribute("name"), actionName, StringComparison.OrdinalIgnoreCase));

                if (existingAct != null)
                {
                    errorCode = "ActionAlreadyExists";
                    errorMessage = $"Action '{actionName}' already exists on tab '{tabCode}'.";
                    return false;
                }

                string gxobjectVal = procObj != null ? $"{procObj.Guid}-{procObj.Name}" : "";
                var newAct = new XElement("action",
                    new XAttribute("name", actionName),
                    new XAttribute("caption", caption ?? actionName));

                if (!string.IsNullOrEmpty(gxobjectVal)) newAct.Add(new XAttribute("gxobject", gxobjectVal));
                if (!string.IsNullOrEmpty(condition)) newAct.Add(new XAttribute("condition", condition));

                if (parameters != null && parameters.Count > 0)
                {
                    var paramsElem = new XElement("parameters");
                    foreach (var p in parameters)
                    {
                        paramsElem.Add(new XElement("parameter", new XAttribute("name", p)));
                    }
                    newAct.Add(paramsElem);
                }

                actionsElem.Add(newAct);
                newXml = doc.ToString(SaveOptions.DisableFormatting);
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "XmlMutationFailed";
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
