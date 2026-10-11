using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Compatibility adapter for standard GeneXus WorkWith (WorkWith for Web).
    /// Uses Artech.Patterns.WorkWith.WorkWithInstance when available on the STA thread,
    /// and provides structured XML fallback for test and headless environments.
    /// </summary>
    internal static class WorkWithSdkAdapter
    {
        private static Assembly _cachedAssembly;
        private static bool _assemblyResolved;
        private static readonly object _assemblyLock = new object();

        internal static Assembly GetWorkWithAssembly()
        {
            if (_assemblyResolved) return _cachedAssembly;
            lock (_assemblyLock)
            {
                if (_assemblyResolved) return _cachedAssembly;
                try
                {
                    _cachedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Patterns.WorkWith", StringComparison.OrdinalIgnoreCase));
                    if (_cachedAssembly != null)
                    {
                        _assemblyResolved = true;
                        return _cachedAssembly;
                    }

                    string gxPath = GeneXusInstallPath.Resolve();
                    if (!string.IsNullOrEmpty(gxPath))
                    {
                        string candidate = Path.Combine(gxPath, "Packages", "Patterns", "WorkWith", "Artech.Patterns.WorkWith.dll");
                        if (File.Exists(candidate))
                        {
                            _cachedAssembly = Assembly.LoadFrom(candidate);
                            _assemblyResolved = true;
                            return _cachedAssembly;
                        }
                    }

                    _cachedAssembly = Assembly.Load("Artech.Patterns.WorkWith");
                }
                catch { }
                _assemblyResolved = true;
                return _cachedAssembly;
            }
        }

        internal static bool IsNativeSdkAvailable => GetWorkWithAssembly() != null;

        public static bool ListActions(KBObject instanceObj, PatternAnalysisService patterns, out JArray tabsArray, out string error)
        {
            tabsArray = null;
            error = null;

            if (instanceObj == null)
            {
                error = "NullInstance";
                return false;
            }

            var asm = GetWorkWithAssembly();
            if (asm != null)
            {
                try
                {
                    var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                    if (wwiType != null)
                    {
                        var loadMethod = wwiType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                        object wwi = loadMethod != null ? loadMethod.Invoke(null, new object[] { instanceObj }) : null;
                        if (wwi != null)
                        {
                            var levelsProp = wwiType.GetProperty("Levels");
                            var levels = levelsProp?.GetValue(wwi, null) as IEnumerable;
                            var tabsList = new JArray();
                            if (levels != null)
                            {
                                foreach (var level in levels)
                                {
                                    var view = level.GetType().GetProperty("View")?.GetValue(level, null);
                                    if (view == null) continue;
                                    var tabs = view.GetType().GetProperty("Tabs")?.GetValue(view, null) as IEnumerable;
                                    if (tabs == null) continue;
                                    foreach (var tab in tabs)
                                    {
                                        string code = tab.GetType().GetProperty("Code")?.GetValue(tab, null)?.ToString() ?? "";
                                        string name = tab.GetType().GetProperty("Name")?.GetValue(tab, null)?.ToString() ?? code;
                                        string type = tab.GetType().GetProperty("Type")?.GetValue(tab, null)?.ToString() ?? "Tabular";
                                        var tabObj = new JObject
                                        {
                                            ["code"] = code,
                                            ["name"] = name,
                                            ["type"] = type
                                        };
                                        var actions = tab.GetType().GetProperty("Actions")?.GetValue(tab, null) as IEnumerable;
                                        var actionsArray = new JArray();
                                        if (actions != null)
                                        {
                                            foreach (var act in actions)
                                            {
                                                string actName = act.GetType().GetProperty("Name")?.GetValue(act, null)?.ToString() ?? "";
                                                string caption = act.GetType().GetProperty("Caption")?.GetValue(act, null)?.ToString();
                                                string procName = act.GetType().GetProperty("GxobjectName")?.GetValue(act, null)?.ToString();
                                                if (string.IsNullOrEmpty(procName))
                                                {
                                                    var gxObj = act.GetType().GetProperty("Gxobject")?.GetValue(act, null) as KBObject;
                                                    procName = gxObj?.Name;
                                                }
                                                string condition = act.GetType().GetProperty("Condition")?.GetValue(act, null)?.ToString();
                                                var parameters = act.GetType().GetProperty("Parameters")?.GetValue(act, null) as IEnumerable;
                                                var paramList = new JArray();
                                                if (parameters != null)
                                                {
                                                    foreach (var p in parameters)
                                                    {
                                                        string pName = p.GetType().GetProperty("Name")?.GetValue(p, null)?.ToString();
                                                        if (!string.IsNullOrEmpty(pName)) paramList.Add(pName);
                                                    }
                                                }
                                                actionsArray.Add(new JObject
                                                {
                                                    ["name"] = actName,
                                                    ["caption"] = caption ?? actName,
                                                    ["procedure"] = procName ?? "",
                                                    ["condition"] = condition ?? "",
                                                    ["parameters"] = paramList
                                                });
                                            }
                                        }
                                        tabObj["actions"] = actionsArray;
                                        tabsList.Add(tabObj);
                                    }
                                }
                            }
                            tabsArray = tabsList;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("[WorkWithSdkAdapter] Native ListActions failed, falling back to XML: " + ex.Message);
                }
            }

            string xml = patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "PatternInstanceXmlEmpty";
                return false;
            }
            return TryListActionsFromXml(xml, out tabsArray, out error);
        }

        internal static bool TryListActionsFromXml(string xml, out JArray tabsArray, out string error)
        {
            tabsArray = null;
            error = null;
            try
            {
                var doc = XDocument.Parse(xml);
                var tabsList = new JArray();
                var tabsElements = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "tab", StringComparison.OrdinalIgnoreCase));
                foreach (var tab in tabsElements)
                {
                    string code = (string)tab.Attribute("code") ?? (string)tab.Attribute("name") ?? "";
                    string name = (string)tab.Attribute("name") ?? code;
                    string type = (string)tab.Attribute("type") ?? "Tabular";
                    var tabObj = new JObject
                    {
                        ["code"] = code,
                        ["name"] = name,
                        ["type"] = type
                    };
                    var actionsArray = new JArray();
                    var actionsContainer = tab.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "actions", StringComparison.OrdinalIgnoreCase));
                    if (actionsContainer != null)
                    {
                        foreach (var act in actionsContainer.Elements().Where(e => string.Equals(e.Name.LocalName, "action", StringComparison.OrdinalIgnoreCase)))
                        {
                            string actName = (string)act.Attribute("name") ?? "";
                            string caption = (string)act.Attribute("caption");
                            string gxobject = (string)act.Attribute("gxobject") ?? "";
                            if (gxobject.Contains("-"))
                            {
                                var parts = gxobject.Split(new[] { '-' }, 6);
                                if (parts.Length == 6) gxobject = parts[5];
                            }
                            string condition = (string)act.Attribute("condition");
                            var paramList = new JArray();
                            var paramsElem = act.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "parameters", StringComparison.OrdinalIgnoreCase));
                            if (paramsElem != null)
                            {
                                foreach (var p in paramsElem.Elements().Where(e => string.Equals(e.Name.LocalName, "parameter", StringComparison.OrdinalIgnoreCase)))
                                {
                                    string pName = (string)p.Attribute("name");
                                    if (!string.IsNullOrEmpty(pName)) paramList.Add(pName);
                                }
                            }
                            actionsArray.Add(new JObject
                            {
                                ["name"] = actName,
                                ["caption"] = caption ?? actName,
                                ["procedure"] = gxobject,
                                ["condition"] = condition ?? "",
                                ["parameters"] = paramList
                            });
                        }
                    }
                    tabObj["actions"] = actionsArray;
                    tabsList.Add(tabObj);
                }
                tabsArray = tabsList;
                return true;
            }
            catch (Exception ex)
            {
                error = "InvalidXml: " + ex.Message;
                return false;
            }
        }

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
            if (!ListActions(instanceObj, patterns, out var tabs, out var listError))
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

            // Step 2: Handle dryRun
            if (dryRun)
            {
                result = new JObject
                {
                    ["instance"] = instanceObj.Name,
                    ["tab"] = tabCode,
                    ["actionName"] = actionName,
                    ["caption"] = caption ?? actionName,
                    ["procedure"] = procObj?.Name ?? "",
                    ["parameters"] = new JArray(parameters ?? Array.Empty<string>()),
                    ["condition"] = condition ?? ""
                };
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
            if (!ListActions(instanceObj, patterns, out var verifyTabs, out _))
            {
                errorCode = "VerificationReadFailed";
                errorMessage = "Action was saved, but verification re-read failed.";
                return false;
            }

            var verifiedTab = verifyTabs.FirstOrDefault(t =>
                string.Equals(t["code"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t["name"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)) as JObject;
            var verifiedActions = verifiedTab?["actions"] as JArray;
            var verifiedAct = verifiedActions?.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase));

            if (verifiedAct == null)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Action '{actionName}' was not found in the verified post-write state.";
                return false;
            }

            result = new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tab"] = tabCode,
                ["action"] = new JObject
                {
                    ["name"] = actionName,
                    ["caption"] = caption ?? actionName,
                    ["procedure"] = procObj?.Name ?? "",
                    ["condition"] = condition ?? "",
                    ["parameters"] = new JArray(parameters ?? Array.Empty<string>())
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = true,
                ["derivedObjectRegeneration"] = "WorkWith instance definition persisted. Note: On GeneXus 18/17, derived WebComponents (e.g. <Trn>General) may require IDE Apply or build to re-project the new action control."
            };
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
                newXml = doc.ToString();
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
