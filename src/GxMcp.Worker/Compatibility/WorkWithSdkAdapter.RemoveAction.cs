using System;
using System.Collections;
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
        public static bool RemoveAction(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            WriteService write,
            string tabCode,
            string actionName,
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

            if (string.IsNullOrWhiteSpace(tabCode))
            {
                errorCode = "MissingTab";
                errorMessage = "tab is required for remove_action.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(actionName))
            {
                errorCode = "MissingActionName";
                errorMessage = "actionName is required for remove_action.";
                return false;
            }

            // Step 1: Pre-check tab existence and action existence
            if (!ListActions(instanceObj, patterns, out var tabs, out _, out var listError))
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
            var targetAction = existingActions?.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)) as JObject;

            if (targetAction == null)
            {
                errorCode = "ActionNotFound";
                string available = existingActions != null && existingActions.Count > 0
                    ? string.Join(", ", existingActions.Select(a => a["name"]?.ToString()))
                    : "(none)";
                errorMessage = $"Action '{actionName}' was not found on tab '{tabCode}' in WorkWith instance '{instanceObj.Name}'. Available actions: {available}.";
                return false;
            }

            var validations = new JObject
            {
                ["targetObjectResolved"] = true,
                ["workWithInstanceResolved"] = true,
                ["tabExists"] = true,
                ["actionFound"] = true
            };

            var plan = new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tab"] = tabCode,
                ["actionName"] = actionName,
                ["action"] = targetAction,
                ["valid"] = true,
                ["validations"] = validations,
                ["route"] = IsNativeSdkAvailable ? "native-sdk" : "xml-fallback",
                ["defaultNormalization"] = IsNativeSdkAvailable ? NativeDefaultNormalization : FallbackDefaultNormalization
            };

            // Step 2: Handle dryRun
            if (dryRun)
            {
                result = plan;
                return true;
            }

            // Step 3: Real remove
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
                            var removeActionMethod = sdkTab.GetType().GetMethod("RemoveAction", new[] { typeof(string) });
                            if (removeActionMethod != null)
                            {
                                removeActionMethod.Invoke(sdkTab, new object[] { actionName });
                            }
                            else
                            {
                                var actionsProp = sdkTab.GetType().GetProperty("Actions");
                                var actionsList = actionsProp?.GetValue(sdkTab, null) as IEnumerable;
                                if (actionsList != null)
                                {
                                    object actionToRemove = null;
                                    foreach (var act in actionsList)
                                    {
                                        string name = act.GetType().GetProperty("Name")?.GetValue(act, null) as string;
                                        if (string.Equals(name, actionName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            actionToRemove = act;
                                            break;
                                        }
                                    }
                                    if (actionToRemove != null)
                                    {
                                        var removeMethod = actionsList.GetType().GetMethod("Remove", new[] { actionToRemove.GetType() })
                                            ?? actionsList.GetType().GetMethod("Remove");
                                        removeMethod?.Invoke(actionsList, new object[] { actionToRemove });
                                    }
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
                catch (Exception ex)
                {
                    Logger.Warn("[WorkWithSdkAdapter] Native RemoveAction failed, falling back to XML: " + ex.Message);
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

                if (!TryRemoveActionFromXml(currentXml, tabCode, actionName, out string newXml, out errorCode, out errorMessage))
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
            if (!ListActions(instanceObj, patterns, out var verifyTabs, out _, out _))
            {
                errorCode = "VerificationReadFailed";
                errorMessage = "Action was removed, but verification re-read failed.";
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

            if (verifiedAct != null)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Action '{actionName}' was still present in the verified post-write state.";
                return false;
            }

            if (existingActions != null)
            {
                foreach (var neighbor in existingActions)
                {
                    string neighborName = neighbor["name"]?.ToString();
                    if (string.Equals(neighborName, actionName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (verifiedActions == null || !verifiedActions.Any(a => string.Equals(a["name"]?.ToString(), neighborName, StringComparison.OrdinalIgnoreCase)))
                    {
                        errorCode = "VerificationFailed";
                        errorMessage = $"Neighbor action '{neighborName}' was unexpectedly lost after removing '{actionName}'.";
                        return false;
                    }
                }
            }

            result = new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tab"] = tabCode,
                ["removedAction"] = actionName,
                ["remainingActions"] = verifiedActions ?? new JArray(),
                ["verified"] = true,
                ["route"] = nativeSucceeded ? "native-sdk" : "xml-fallback",
                ["verificationScope"] = "removed action absence, neighbor actions integrity, and post-write reread of the received in-memory instance",
                ["defaultNormalization"] = nativeSucceeded ? NativeDefaultNormalization : FallbackDefaultNormalization,
                ["persistenceEvidence"] = BuildPersistenceEvidence()
            };

            return true;
        }

        internal static bool TryRemoveActionFromXml(
            string xml,
            string tabCode,
            string actionName,
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
                var existingAct = actionsElem?.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "action", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)e.Attribute("name"), actionName, StringComparison.OrdinalIgnoreCase));

                if (existingAct == null)
                {
                    errorCode = "ActionNotFound";
                    errorMessage = $"Action '{actionName}' not found on tab '{tabCode}'.";
                    return false;
                }

                existingAct.Remove();
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
