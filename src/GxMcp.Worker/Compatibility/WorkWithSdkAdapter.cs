using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    /// Which reader produced a ListActions/ListFilters result. Read responses and write
    /// receipts copy this instead of claiming an in-memory instance reread for every
    /// path: the XML projection is a read of the same in-memory part, not evidence of
    /// the typed instance state (issue #589).
    internal enum WorkWithReadSource
    {
        /// The typed WorkWithInstance loaded from the in-memory PatternInstance part.
        NativeInstance,
        /// XML projection: the Artech.Patterns.WorkWith assembly could not be resolved.
        XmlProjectionNativeUnavailable,
        /// XML projection: WorkWithInstance or its Load member was not found, or Load returned null.
        XmlProjectionNativeInstanceAbsent,
        /// XML projection: the typed instance exposed no Levels collection.
        XmlProjectionNativeLevelsAbsent,
        /// XML projection: the native read threw (logged at Debug).
        XmlProjectionNativeReadFailed
    }

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

        /// Test seam: when set, replaces the assembly lookup so the native-unavailable
        /// refusals can be exercised without a GeneXus installation.
        internal static Func<Assembly> AssemblyResolverOverride;

        /// Disclosed in both the dryRun plan and the write receipt.
        ///
        /// The earlier wording claimed "pattern bindings are preserved", which is
        /// the opposite of what happens to the attribute that gets materialized:
        /// `page="<default>"` becomes a concrete `10`, and GeneXus then treats it
        /// as an explicit instance-level override. A later change to the pattern
        /// template default no longer propagates to this instance. The instance
        /// keeps working and its identity, tabs and actions are intact - what it
        /// loses is the link to the template default, and saying otherwise led an
        /// agent to reason about future default changes from a false premise
        /// (issue #477).
        internal const string NativeDefaultNormalization =
            "Native WorkWith SDK serialization normalizes pattern defaults (such as page=\"<default>\" to concrete values) upon save. "
            + "The instance stays functional and its pattern identity, tabs and actions are intact, but a materialized attribute "
            + "becomes an explicit instance override and will no longer follow a later change to the pattern template default.";

        internal const string FallbackDefaultNormalization =
            "XML fallback preserves existing pattern XML defaults verbatim without normalization.";

        internal const string InMemoryInstanceRereadSource = "in_memory_instance_reread";
        internal const string XmlProjectionSource = "xml_projection";

        internal static string ReadSourceLabel(WorkWithReadSource source)
        {
            return source == WorkWithReadSource.NativeInstance ? InMemoryInstanceRereadSource : XmlProjectionSource;
        }

        /// Why the XML projection was read instead of the typed instance; null on the native route.
        internal static string ReadSourceReason(WorkWithReadSource source)
        {
            switch (source)
            {
                case WorkWithReadSource.XmlProjectionNativeUnavailable: return "native_sdk_unavailable";
                case WorkWithReadSource.XmlProjectionNativeInstanceAbsent: return "native_instance_absent";
                case WorkWithReadSource.XmlProjectionNativeLevelsAbsent: return "native_levels_absent";
                case WorkWithReadSource.XmlProjectionNativeReadFailed: return "native_read_failed";
                default: return null;
            }
        }

        /// Writes `field` with the source label and, on the XML projection, `<field>Reason`.
        internal static void DescribeReadSource(JObject target, string field, WorkWithReadSource source)
        {
            target[field] = ReadSourceLabel(source);
            string reason = ReadSourceReason(source);
            if (reason != null) target[field + "Reason"] = reason;
        }

        private static string DescribeRereadScope(WorkWithReadSource source)
        {
            return source == WorkWithReadSource.NativeInstance
                ? "post-write in-memory instance re-read"
                : "post-write PatternInstance XML projection re-read";
        }

        /// Loads the typed WorkWithInstance for a read. When it returns false, `fallback`
        /// says why the caller must read the XML projection instead; a throwing Load is
        /// reported, not rethrown.
        private static bool TryLoadNativeInstance(KBObject instanceObj, string operation, out Type wwiType, out object wwi, out WorkWithReadSource fallback)
        {
            wwiType = null;
            wwi = null;
            var asm = GetWorkWithAssembly();
            if (asm == null)
            {
                fallback = WorkWithReadSource.XmlProjectionNativeUnavailable;
                return false;
            }

            try
            {
                wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                var loadMethod = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                wwi = loadMethod?.Invoke(null, new object[] { instanceObj });
                if (wwi == null)
                {
                    fallback = WorkWithReadSource.XmlProjectionNativeInstanceAbsent;
                    return false;
                }

                fallback = WorkWithReadSource.NativeInstance;
                return true;
            }
            catch (Exception ex)
            {
                LogNativeReadFallback(operation, ex);
                fallback = WorkWithReadSource.XmlProjectionNativeReadFailed;
                return false;
            }
        }

        private static void LogNativeReadFallback(string operation, Exception ex)
        {
            Logger.Debug("[WorkWithSdkAdapter] Native " + operation + " failed, falling back to XML: " + ex.Message);
        }

        internal static Assembly GetWorkWithAssembly()
        {
            var resolverOverride = AssemblyResolverOverride;
            if (resolverOverride != null) return resolverOverride();
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

        public static bool ListActions(KBObject instanceObj, PatternAnalysisService patterns, out JArray tabsArray, out WorkWithReadSource source, out string error)
        {
            tabsArray = null;
            source = WorkWithReadSource.NativeInstance;
            error = null;

            if (instanceObj == null)
            {
                error = "NullInstance";
                return false;
            }

            return ListActionsCore(
                instanceObj,
                () => patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _),
                out tabsArray,
                out source,
                out error);
        }

        /// Native-first read with the XML projection as fallback. `readPatternXml` is the
        /// seam that lets every path run without a Knowledge Base; `source` names the
        /// reader that produced `tabsArray`.
        internal static bool ListActionsCore(KBObject instanceObj, Func<string> readPatternXml, out JArray tabsArray, out WorkWithReadSource source, out string error)
        {
            tabsArray = null;
            error = null;

            if (TryLoadNativeInstance(instanceObj, "ListActions", out var wwiType, out var wwi, out source))
            {
                try
                {
                    tabsArray = ReadNativeTabs(wwiType, wwi);
                    return true;
                }
                catch (Exception ex)
                {
                    LogNativeReadFallback("ListActions", ex);
                    source = WorkWithReadSource.XmlProjectionNativeReadFailed;
                }
            }

            string xml = readPatternXml?.Invoke();
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "PatternInstanceXmlEmpty";
                return false;
            }
            return TryListActionsFromXml(xml, out tabsArray, out error);
        }

        private static JArray ReadNativeTabs(Type wwiType, object wwi)
        {
            var levels = wwiType.GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
            var tabsList = new JArray();
            if (levels == null) return tabsList;

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
            return tabsList;
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

        internal sealed class FilterLevelCandidate
        {
            public string Id;
            public bool HasSelection;
            public object Filter;
        }

        private static string LevelIdOrDefault(string id, int index)
        {
            if (!string.IsNullOrWhiteSpace(id)) return id;
            return index == 0 ? "Root" : "Level" + index;
        }

        /// Fail-closed Level selection shared by the native and XML readers and by the
        /// native writer: one Level resolves implicitly, two or more require `level`.
        internal static bool TryResolveFilterLevel(
            IList<FilterLevelCandidate> candidates,
            string levelId,
            out FilterLevelCandidate resolved,
            out string error,
            out string errorMessage)
        {
            resolved = null;
            error = null;
            errorMessage = null;
            string available = string.Join(", ", candidates.Select(c => c.Id));

            if (!string.IsNullOrWhiteSpace(levelId))
            {
                resolved = candidates.FirstOrDefault(c => string.Equals(c.Id, levelId, StringComparison.OrdinalIgnoreCase));
                if (resolved == null)
                {
                    error = "FilterLevelNotFound";
                    errorMessage = candidates.Count == 0
                        ? $"Level '{levelId}' was not found: the WorkWith instance has no Levels."
                        : $"Level '{levelId}' was not found in the WorkWith instance. Available levels: {available}.";
                    return false;
                }
            }
            else if (candidates.Count == 0)
            {
                error = "FilterLevelNotFound";
                errorMessage = "The WorkWith instance has no Levels.";
                return false;
            }
            else if (candidates.Count > 1)
            {
                error = "FilterLevelAmbiguous";
                errorMessage = $"The WorkWith instance has {candidates.Count} Levels; pass `level` to choose one. Candidates: {available}.";
                return false;
            }
            else
            {
                resolved = candidates[0];
            }

            if (!resolved.HasSelection)
            {
                error = "WorkWithSelectionMissing";
                errorMessage = $"Level '{resolved.Id}' has no selection node.";
                resolved = null;
                return false;
            }

            if (resolved.Filter == null)
            {
                error = "WorkWithFilterNotPresent";
                errorMessage = $"Standard WorkWith filter node is not present on the selection of Level '{resolved.Id}'. Create the filter in the GeneXus IDE.";
                resolved = null;
                return false;
            }

            return true;
        }

        public static bool ListFilters(KBObject instanceObj, PatternAnalysisService patterns, string levelId, out JObject filters, out WorkWithReadSource source, out string error, out string errorMessage)
        {
            filters = null;
            source = WorkWithReadSource.NativeInstance;
            error = null;
            errorMessage = null;

            if (instanceObj == null)
            {
                error = "NullInstance";
                errorMessage = "WorkWith PatternInstance object is null.";
                return false;
            }

            return ListFiltersCore(
                instanceObj,
                () => patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _),
                levelId,
                out filters,
                out source,
                out error,
                out errorMessage);
        }

        /// Native-first read with the XML projection as fallback. `readPatternXml` is the
        /// seam that lets every path run without a Knowledge Base; `source` names the
        /// reader that produced `filters`. A Level resolution error on the native route
        /// is returned as-is: the projection never masks it.
        internal static bool ListFiltersCore(KBObject instanceObj, Func<string> readPatternXml, string levelId, out JObject filters, out WorkWithReadSource source, out string error, out string errorMessage)
        {
            filters = null;
            error = null;
            errorMessage = null;

            if (TryLoadNativeInstance(instanceObj, "ListFilters", out var wwiType, out var wwi, out source))
            {
                try
                {
                    var levels = wwiType.GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
                    if (levels != null)
                    {
                        return ReadNativeFilters(wwi, levelId, out filters, out error, out errorMessage);
                    }

                    source = WorkWithReadSource.XmlProjectionNativeLevelsAbsent;
                }
                catch (Exception ex)
                {
                    LogNativeReadFallback("ListFilters", ex);
                    source = WorkWithReadSource.XmlProjectionNativeReadFailed;
                }
            }

            string xml = readPatternXml?.Invoke();
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "PatternInstanceXmlEmpty";
                errorMessage = "PatternInstance XML was empty; filters could not be read.";
                return false;
            }

            return TryListFiltersFromXml(xml, levelId, out filters, out error, out errorMessage);
        }

        /// Projects the filter of one Level from an already loaded typed WorkWithInstance
        /// (the `list_filters` shape). Shared by the native read path and the removal
        /// orchestration so tests can read a fake typed model through the same code.
        internal static bool ReadNativeFilters(object wwi, string levelId, out JObject filters, out string error, out string errorMessage)
        {
            filters = null;
            error = null;
            errorMessage = null;

            var levels = wwi?.GetType().GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
            if (levels == null)
            {
                error = "WorkWithLevelsUnavailable";
                errorMessage = "The typed WorkWith instance exposes no Levels collection.";
                return false;
            }

            if (!TryResolveFilterLevel(CollectNativeFilterLevels(levels), levelId, out var resolved, out error, out errorMessage))
                return false;

            filters = ReadNativeFilter(resolved);
            return true;
        }

        private static List<FilterLevelCandidate> CollectNativeFilterLevels(IEnumerable levels)
        {
            var candidates = new List<FilterLevelCandidate>();
            int index = 0;
            foreach (var lvl in levels)
            {
                string id = lvl.GetType().GetProperty("Id")?.GetValue(lvl, null)?.ToString()
                    ?? lvl.GetType().GetProperty("Name")?.GetValue(lvl, null)?.ToString();
                var selection = lvl.GetType().GetProperty("Selection")?.GetValue(lvl, null);
                candidates.Add(new FilterLevelCandidate
                {
                    Id = LevelIdOrDefault(id, index),
                    HasSelection = selection != null,
                    Filter = selection?.GetType().GetProperty("Filter")?.GetValue(selection, null)
                });
                index++;
            }
            return candidates;
        }

        /// Typed reader over the loaded WorkWith model. The filter attribute's `default`
        /// (schema attribute `default`, typed member `FilterAttributeElement.Default`) is
        /// always reported as a string, "" when unset, so both readers share one shape
        /// and verification never compares null with "" (issue #588).
        internal static JObject ReadNativeFilter(FilterLevelCandidate level)
        {
            object filter = level.Filter;
            var attrsArray = new JArray();
            var filterAttrs = filter.GetType().GetProperty("Attributes")?.GetValue(filter, null) as IEnumerable;
            if (filterAttrs != null)
            {
                foreach (var fa in filterAttrs)
                {
                    string faName = fa.GetType().GetProperty("Name")?.GetValue(fa, null)?.ToString() ?? "";
                    string faDesc = fa.GetType().GetProperty("Description")?.GetValue(fa, null)?.ToString();
                    // DomainName is the SDK's own plain-name projection of the typed Domain
                    // reference; the ToString fallback may carry the `{guid}-Name` form.
                    string faDom = fa.GetType().GetProperty("DomainName")?.GetValue(fa, null)?.ToString()
                        ?? StripObjectReferenceGuid(fa.GetType().GetProperty("Domain")?.GetValue(fa, null)?.ToString());
                    string faVar = fa.GetType().GetProperty("VariableName")?.GetValue(fa, null)?.ToString();
                    string faDefault = fa.GetType().GetProperty("Default")?.GetValue(fa, null)?.ToString();

                    var attrObj = new JObject
                    {
                        ["name"] = faName,
                        ["description"] = faDesc ?? faName,
                        ["domain"] = faDom ?? "",
                        ["default"] = faDefault ?? "",
                        ["variableName"] = faVar ?? ("&" + faName)
                    };
                    attrsArray.Add(attrObj);
                }
            }

            var condsArray = new JArray();
            var conds = filter.GetType().GetProperty("Conditions")?.GetValue(filter, null) as IEnumerable;
            if (conds != null)
            {
                foreach (var c in conds)
                {
                    string val = c.GetType().GetProperty("Value")?.GetValue(c, null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                        condsArray.Add(val);
                }
            }

            return new JObject
            {
                ["level"] = level.Id,
                ["attributes"] = attrsArray,
                ["conditions"] = condsArray
            };
        }

        /// Read-only XML projection of the standard WorkWith filter, used when the native
        /// surface is unavailable. The real nodes are `filterAttribute` under `attributes`
        /// and `condition` under `conditions` (see PatternChildOrderReconciler).
        internal static bool TryListFiltersFromXml(string xml, string levelId, out JObject filters, out string error, out string errorMessage)
        {
            filters = null;
            error = null;
            errorMessage = null;
            try
            {
                var doc = XDocument.Parse(xml);
                if (!TryResolveFilterLevel(CollectXmlFilterLevels(doc), levelId, out var resolved, out error, out errorMessage))
                    return false;

                var filter = (XElement)resolved.Filter;
                var attrsArray = new JArray();
                var attrsContainer = ChildNamed(filter, "attributes");
                if (attrsContainer != null)
                {
                    foreach (var a in attrsContainer.Elements().Where(e => HasLocalName(e, "filterAttribute")))
                    {
                        string aName = (string)a.Attribute("name") ?? "";
                        var attrObj = new JObject
                        {
                            ["name"] = aName,
                            ["description"] = (string)a.Attribute("description") ?? aName,
                            ["domain"] = StripObjectReferenceGuid((string)a.Attribute("domain")) ?? "",
                            ["default"] = (string)a.Attribute("default") ?? "",
                            ["variableName"] = (string)a.Attribute("variableName") ?? ("&" + aName)
                        };
                        attrsArray.Add(attrObj);
                    }
                }

                var condsArray = new JArray();
                var condsContainer = ChildNamed(filter, "conditions");
                if (condsContainer != null)
                {
                    foreach (var c in condsContainer.Elements().Where(e => HasLocalName(e, "condition")))
                    {
                        string val = (string)c.Attribute("value");
                        if (!string.IsNullOrWhiteSpace(val)) condsArray.Add(val);
                    }
                }

                filters = new JObject
                {
                    ["level"] = resolved.Id,
                    ["attributes"] = attrsArray,
                    ["conditions"] = condsArray
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "XmlParseError: " + ex.Message;
                errorMessage = "PatternInstance XML could not be parsed: " + ex.Message;
                return false;
            }
        }

        private static List<FilterLevelCandidate> CollectXmlFilterLevels(XDocument doc)
        {
            var candidates = new List<FilterLevelCandidate>();
            var levels = doc.Descendants().Where(e => HasLocalName(e, "level")).ToList();
            if (levels.Count == 0)
            {
                // Some XML variants store Selection directly under root
                var directSelection = doc.Descendants().FirstOrDefault(e => HasLocalName(e, "selection"));
                if (directSelection != null)
                    candidates.Add(new FilterLevelCandidate { Id = "Root", HasSelection = true, Filter = ChildNamed(directSelection, "filter") });
                return candidates;
            }

            int index = 0;
            foreach (var lvl in levels)
            {
                var selection = ChildNamed(lvl, "selection");
                candidates.Add(new FilterLevelCandidate
                {
                    Id = LevelIdOrDefault((string)lvl.Attribute("id") ?? (string)lvl.Attribute("name"), index),
                    HasSelection = selection != null,
                    Filter = selection != null ? ChildNamed(selection, "filter") : null
                });
                index++;
            }
            return candidates;
        }

        private static bool HasLocalName(XElement element, string localName)
        {
            return string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);
        }

        private static XElement ChildNamed(XElement parent, string localName)
        {
            return parent.Elements().FirstOrDefault(e => HasLocalName(e, localName));
        }

        /// Pattern XML stores object references as `{guid}-{Name}`; readers report the name.
        private static string StripObjectReferenceGuid(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return reference;
            var parts = reference.Split(new[] { '-' }, 6);
            return parts.Length == 6 && Guid.TryParse(string.Join("-", parts, 0, 5), out _) ? parts[5] : reference;
        }

        private static readonly Regex FilterAttributeNamePattern = new Regex(@"^&?[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        /// Deterministic request validation: identifier shape of the filter attribute name
        /// (an optional leading `&` marks a variable), no empty condition, and no condition
        /// repeated within the request or against the conditions already on the filter.
        internal static bool ValidateFilterRequest(
            string attributeName,
            IList<string> conditions,
            JArray existingConditions,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(attributeName) || !FilterAttributeNamePattern.IsMatch(attributeName))
            {
                errorCode = "InvalidFilterAttributeName";
                errorMessage = $"Filter attribute name '{attributeName}' is not a valid identifier (letters, digits and underscore, optionally prefixed with '&').";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (existingConditions != null)
            {
                foreach (var existing in existingConditions)
                    seen.Add(existing.ToString());
            }

            if (conditions == null) return true;
            for (int i = 0; i < conditions.Count; i++)
            {
                string condition = conditions[i];
                if (string.IsNullOrWhiteSpace(condition))
                {
                    errorCode = "FilterConditionEmpty";
                    errorMessage = $"conditions[{i}] is empty; every filter condition must be a non-empty expression.";
                    return false;
                }

                if (!seen.Add(condition))
                {
                    errorCode = "FilterConditionDuplicate";
                    errorMessage = $"conditions[{i}] '{condition}' repeats another condition in the request or one already present on the filter.";
                    return false;
                }
            }

            return true;
        }

        /// A KB object as the filter authoring path sees it: enough identity to build the
        /// typed reference, the advisory and the receipt without holding the SDK type in
        /// hermetic tests.
        internal sealed class FilterKbObjectRef
        {
            public string Name;
            public Guid Guid;
            public string Type;
            public object Instance;

            public static FilterKbObjectRef FromKbObject(KBObject obj)
            {
                if (obj == null) return null;
                return new FilterKbObjectRef
                {
                    Name = obj.Name,
                    Guid = obj.Guid,
                    Type = obj.TypeDescriptor?.Name,
                    Instance = obj
                };
            }
        }

        /// Lookup seam: (name or GUID, object type name) -> the KB object, or null.
        internal delegate FilterKbObjectRef FilterObjectLookup(string nameOrGuid, string typeName);

        internal sealed class FilterDomainResolution
        {
            public string Requested;
            public string Name;
            public Guid Guid;
            /// "name" or "guid": which form of the request resolved.
            public string ResolvedBy;
            public object Instance;
        }

        internal const string FilterDomainTypedPersistence =
            "Applied through the typed FilterAttributeElement.Domain reference: the SDK stores a KBObjectReference (`domainReference`) "
            + "that serializes as `{guid}-Name` in the PatternInstance XML; list_filters reports the plain Domain name.";

        internal const string FilterDomainStringPersistence =
            "Applied through the FilterAttributeElement.DomainName string setter: the SDK stores the plain Domain name.";

        /// Resolves `domain` against the KB Domains by name or GUID; the `{guid}-Name`
        /// form the pattern XML stores is accepted too. A Domain that is given but does
        /// not resolve is an input error: the typed `Domain` reference cannot be built
        /// from a name the KB does not know (issue #587).
        internal static bool TryResolveFilterDomain(
            string domain,
            FilterObjectLookup lookup,
            out FilterDomainResolution resolution,
            out string errorCode,
            out string errorMessage)
        {
            resolution = null;
            errorCode = null;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(domain)) return true;

            string requested = domain.Trim();
            var attempts = new List<KeyValuePair<string, string>>();
            if (TrySplitObjectReference(requested, out string guidPart, out string namePart))
            {
                attempts.Add(new KeyValuePair<string, string>(guidPart, "guid"));
                attempts.Add(new KeyValuePair<string, string>(namePart, "name"));
            }
            else
            {
                attempts.Add(new KeyValuePair<string, string>(requested, Guid.TryParse(requested, out _) ? "guid" : "name"));
            }

            foreach (var attempt in attempts)
            {
                var found = lookup?.Invoke(attempt.Key, "Domain");
                if (found == null) continue;
                if (!string.IsNullOrEmpty(found.Type) && !string.Equals(found.Type, "Domain", StringComparison.OrdinalIgnoreCase)) continue;

                resolution = new FilterDomainResolution
                {
                    Requested = requested,
                    Name = string.IsNullOrEmpty(found.Name) ? attempt.Key : found.Name,
                    Guid = found.Guid,
                    ResolvedBy = attempt.Value,
                    Instance = found.Instance
                };
                return true;
            }

            string tried = string.Join(" and ", attempts.Select(a => (a.Value == "guid" ? "GUID '" : "name '") + a.Key + "'"));
            errorCode = "FilterDomainNotFound";
            errorMessage = $"Domain '{requested}' was not found in the Knowledge Base (looked up by {tried}). Pass the name or GUID of an existing Domain, or omit `domain`.";
            return false;
        }

        /// `{guid}-{Name}` is how pattern XML stores object references.
        private static bool TrySplitObjectReference(string reference, out string guidPart, out string namePart)
        {
            guidPart = null;
            namePart = null;
            if (string.IsNullOrEmpty(reference)) return false;
            var parts = reference.Split(new[] { '-' }, 6);
            if (parts.Length != 6) return false;
            string candidate = string.Join("-", parts, 0, 5);
            if (!Guid.TryParse(candidate, out _) || string.IsNullOrWhiteSpace(parts[5])) return false;
            guidPart = candidate;
            namePart = parts[5];
            return true;
        }

        /// The `&` prefix stays accepted on the filter attribute name but is not a
        /// discriminator: whether the filter targets a KB Attribute or a variable is
        /// decided by looking the plain name up as an Attribute (issue #587).
        internal static FilterKbObjectRef ResolveFilterAttributeTarget(string attributeName, FilterObjectLookup lookup)
        {
            if (lookup == null || string.IsNullOrWhiteSpace(attributeName)) return null;
            string plain = attributeName.Trim().TrimStart('&');
            if (plain.Length == 0) return null;
            var match = lookup(plain, "Attribute");
            if (match != null && !string.IsNullOrEmpty(match.Type) && !string.Equals(match.Type, "Attribute", StringComparison.OrdinalIgnoreCase))
                return null;
            return match;
        }

        /// Same structured shape as add_action's `signatureAdvisory`: the inspected facts,
        /// one boolean verdict, and `warning` only when the verdict is negative. The schema
        /// applies `domain` to variables only; a filter attribute that names a KB Attribute
        /// takes its type from that Attribute, so the Domain is still written but reported
        /// as advisory rather than refused (issue #587).
        internal static JObject BuildFilterDomainAdvisory(string attributeName, FilterKbObjectRef attributeTarget, FilterDomainResolution domain)
        {
            if (domain == null) return null;
            bool appliesToVariable = attributeTarget == null;
            var advisory = new JObject
            {
                ["domain"] = domain.Name,
                ["domainGuid"] = domain.Guid.ToString(),
                ["filterAttribute"] = attributeName ?? "",
                ["filterAttributeResolvedAs"] = appliesToVariable ? "Variable" : "Attribute",
                ["appliesToVariable"] = appliesToVariable
            };
            if (!appliesToVariable)
            {
                advisory["warning"] = $"Domain '{domain.Name}' was resolved, but filter attribute '{attributeName}' names the KB Attribute '{attributeTarget.Name}'. "
                    + "The WorkWith schema applies `domain` only to variables (a filter on an Attribute takes its type from the Attribute), "
                    + "so the Domain is stored on the filter attribute but the pattern may not use it.";
            }
            return advisory;
        }

        private static JObject DescribeDomainResolution(FilterDomainResolution resolution, string appliedThrough)
        {
            var described = new JObject
            {
                ["requested"] = resolution.Requested,
                ["name"] = resolution.Name,
                ["guid"] = resolution.Guid.ToString(),
                ["resolvedBy"] = resolution.ResolvedBy
            };
            if (appliedThrough == null)
            {
                described["applyPolicy"] = "typed FilterAttributeElement.Domain reference when the SDK exposes it (GeneXus 18 does); the DomainName string setter only on SDK builds that expose one";
            }
            else
            {
                described["appliedThrough"] = appliedThrough;
                described["persistedFormat"] = appliedThrough == "Domain" ? FilterDomainTypedPersistence : FilterDomainStringPersistence;
            }
            return described;
        }

        /// Filter authoring writes only through the native WorkWith SDK. Authoring the
        /// filter through raw PatternInstance XML is exactly what the structural preflight
        /// refuses, so there is deliberately no XML write fallback here (issue #559).
        /// `defaultValue` is the schema's `default` field: stored as given, never compiled
        /// (issue #588).
        public static bool AddFilterAttribute(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            string levelId,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            FilterObjectLookup lookup,
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

            if (!ListFilters(instanceObj, patterns, levelId, out var existingFilters, out var preCheckSource, out errorCode, out errorMessage))
                return false;

            var existingAttrs = existingFilters["attributes"] as JArray ?? new JArray();
            var existingConditions = existingFilters["conditions"] as JArray;
            string resolvedLevel = existingFilters["level"]?.ToString();

            if (!ValidateFilterRequest(attributeName, conditions, existingConditions, out errorCode, out errorMessage))
                return false;

            if (existingAttrs.Any(a => string.Equals(a["name"]?.ToString(), attributeName, StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "FilterAttributeAlreadyExists";
                errorMessage = $"Filter attribute '{attributeName}' already exists in WorkWith instance '{instanceObj.Name}'.";
                return false;
            }

            if (!TryResolveFilterDomain(domain, lookup, out var domainResolution, out errorCode, out errorMessage))
                return false;

            JObject domainAdvisory = null;
            if (domainResolution != null)
                domainAdvisory = BuildFilterDomainAdvisory(attributeName, ResolveFilterAttributeTarget(attributeName, lookup), domainResolution);

            var plan = BuildFilterPlan(instanceObj.Name, resolvedLevel, attributeName, description, domain, defaultValue, conditions, domainResolution, domainAdvisory);

            if (GetWorkWithAssembly() == null)
            {
                result = plan;
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter authoring is unavailable: the Artech.Patterns.WorkWith assembly could not be resolved. Filter writes have no XML fallback.";
                return false;
            }

            if (dryRun)
            {
                result = plan;
                return true;
            }

            if (!TryAddFilterAttributeNative(instanceObj, resolvedLevel, attributeName, description, domainResolution, defaultValue, conditions, out string domainAppliedThrough, out errorCode, out errorMessage))
            {
                result = plan;
                return false;
            }

            if (!ListFilters(instanceObj, patterns, resolvedLevel, out var verifyFilters, out var observedSource, out _, out _))
            {
                errorCode = "VerificationReadFailed";
                errorMessage = "Filter attribute was saved, but verification re-read failed.";
                return false;
            }

            var verifiedAttrs = verifyFilters["attributes"] as JArray ?? new JArray();
            var verifiedConditions = verifyFilters["conditions"] as JArray ?? new JArray();
            int expectedAttributeCount = existingAttrs.Count + 1;
            if (verifiedAttrs.Count < expectedAttributeCount)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Filter attributes were truncated after write: expected at least {expectedAttributeCount}, found {verifiedAttrs.Count}.";
                return false;
            }

            var verifiedAttr = verifiedAttrs.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), attributeName, StringComparison.OrdinalIgnoreCase)) as JObject;
            if (verifiedAttr == null)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Filter attribute '{attributeName}' was not found in the verified post-write state.";
                return false;
            }

            // The re-read reports the plain Domain name (DomainName), so a request made
            // by GUID or `{guid}-Name` is verified against the resolved name.
            if (!VerifyFilterAttributeMatches(verifiedAttr, verifiedConditions, attributeName, description, domainResolution?.Name, defaultValue, conditions, out string mismatchDetail))
            {
                errorCode = "VerificationMismatch";
                errorMessage = $"Filter attribute '{attributeName}' was persisted, but post-save verification failed: {mismatchDetail}";
                return false;
            }

            result = BuildFilterReceipt(instanceObj.Name, resolvedLevel, verifiedAttr, verifiedConditions, attributeName, description, domain, defaultValue, conditions, preCheckSource, observedSource, domainResolution, domainAdvisory, domainAppliedThrough);
            return true;
        }

        internal static JObject BuildFilterPlan(
            string instanceName,
            string level,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            FilterDomainResolution domainResolution = null,
            JObject domainAdvisory = null)
        {
            var filterAttribute = new JObject { ["name"] = attributeName };
            if (!string.IsNullOrWhiteSpace(description)) filterAttribute["description"] = description;
            string plannedDomain = domainResolution?.Name ?? domain;
            if (!string.IsNullOrWhiteSpace(plannedDomain)) filterAttribute["domain"] = plannedDomain;
            if (!string.IsNullOrWhiteSpace(defaultValue)) filterAttribute["default"] = defaultValue;

            var validations = new JObject
            {
                ["targetObjectResolved"] = true,
                ["workWithInstanceResolved"] = true,
                ["levelResolved"] = true,
                ["filterPresent"] = true,
                ["attributeNameFormatValid"] = true,
                ["attributeNameUnique"] = true,
                ["conditionsNonEmptyAndUnique"] = true
            };
            if (domainResolution != null)
            {
                validations["domainResolved"] = true;
                validations["domainAppliesToVariable"] = domainAdvisory == null || domainAdvisory["appliesToVariable"]?.ToObject<bool>() != false;
            }

            var plan = new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["filterAttribute"] = filterAttribute,
                ["conditions"] = new JArray(conditions ?? Array.Empty<string>()),
                ["route"] = "native-sdk",
                ["validations"] = validations,
                ["validationLimits"] = new JObject
                {
                    ["attributeName"] = "The filter attribute name is stored as given; it is looked up as a KB Attribute only to classify the Domain advisory, not validated.",
                    ["domain"] = "The Domain is resolved against KB Domains by name or GUID and applied as a typed reference; its fitness for the filter variable's type is not evaluated and the pattern's own resolver is not run.",
                    ["default"] = "The default value is stored as given; it is not compiled or validated against the type of the attribute or variable.",
                    ["conditionExpressions"] = "Filter condition expressions are not compiled or evaluated."
                },
                ["defaultNormalization"] = NativeDefaultNormalization
            };

            if (domainResolution != null) plan["domainResolution"] = DescribeDomainResolution(domainResolution, appliedThrough: null);
            if (domainAdvisory != null) plan["domainAdvisory"] = domainAdvisory;
            return plan;
        }

        internal sealed class NativeFilterSurface
        {
            public MethodInfo Load;
            public PropertyInfo Levels;
            public MethodInfo SaveTo;
            public MethodInfo AddFilterAttribute;
            public MethodInfo AddCondition;
            public PropertyInfo Description;
            /// Typed Domain reference setter (the supported write path on GeneXus 18).
            public PropertyInfo Domain;
            /// String setter fallback; GeneXus 18 exposes DomainName read-only, so this is null there.
            public PropertyInfo DomainName;
            public PropertyInfo Default;
        }

        /// GeneXus 18 exposes `Domain` as a writable Domain reference and `DomainName` as
        /// a read-only projection of it (`Domain?.Name`), so the typed reference is the
        /// supported write path; a string setter is kept only as the fallback for SDK
        /// builds that expose one.
        internal static void SelectDomainSetters(Type attributeType, out PropertyInfo typedDomain, out PropertyInfo stringDomain)
        {
            typedDomain = null;
            stringDomain = null;
            if (attributeType == null) return;

            var domain = attributeType.GetProperty("Domain");
            if (domain != null && domain.CanWrite && domain.PropertyType != typeof(string)) typedDomain = domain;
            stringDomain = new[] { attributeType.GetProperty("DomainName"), domain }
                .FirstOrDefault(p => p != null && p.CanWrite && p.PropertyType == typeof(string));
        }

        /// Applies the resolved Domain to a new filter attribute element: the typed
        /// reference when the SDK accepts the resolved object, the string setter otherwise.
        internal static bool TryApplyFilterDomain(
            PropertyInfo typedDomain,
            PropertyInfo stringDomain,
            object attributeElement,
            FilterDomainResolution domain,
            out string appliedThrough,
            out string failure)
        {
            appliedThrough = null;
            failure = null;
            if (domain == null) return true;

            if (typedDomain != null && domain.Instance != null && typedDomain.PropertyType.IsInstanceOfType(domain.Instance))
            {
                typedDomain.SetValue(attributeElement, domain.Instance, null);
                appliedThrough = "Domain";
                return true;
            }

            if (stringDomain != null)
            {
                stringDomain.SetValue(attributeElement, domain.Name, null);
                appliedThrough = "DomainName";
                return true;
            }

            failure = typedDomain != null
                ? $"the resolved Domain '{domain.Name}' ({domain.Instance?.GetType().FullName ?? "null"}) is not accepted by FilterAttributeElement.Domain ({typedDomain.PropertyType.FullName}) and no DomainName string setter exists"
                : "FilterAttributeElement exposes neither a Domain reference setter nor a DomainName string setter";
            return false;
        }

        internal static bool TryResolveNativeFilterSurface(
            Type instanceType,
            bool needsDescription,
            bool needsDomain,
            bool needsDefault,
            bool needsConditions,
            out NativeFilterSurface surface,
            out string missingMember)
        {
            surface = null;
            missingMember = null;
            var asm = GetWorkWithAssembly();
            if (asm == null)
            {
                missingMember = "the Artech.Patterns.WorkWith assembly could not be resolved";
                return false;
            }

            try
            {
                var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                var filterType = asm.GetType("Artech.Patterns.WorkWith.FilterElement");
                var resolved = new NativeFilterSurface
                {
                    Load = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static),
                    Levels = wwiType?.GetProperty("Levels"),
                    SaveTo = (instanceType != null ? wwiType?.GetMethod("SaveTo", new[] { instanceType }) : null)
                        ?? wwiType?.GetMethod("SaveTo"),
                    AddFilterAttribute = filterType?.GetMethod("AddFilterAttribute", new[] { typeof(string) }),
                    AddCondition = filterType?.GetMethod("AddCondition", new[] { typeof(string) })
                };
                var attributeType = resolved.AddFilterAttribute?.ReturnType;
                resolved.Description = attributeType?.GetProperty("Description");
                SelectDomainSetters(attributeType, out resolved.Domain, out resolved.DomainName);
                var defaultProperty = attributeType?.GetProperty("Default");
                resolved.Default = defaultProperty != null && defaultProperty.CanWrite && defaultProperty.PropertyType == typeof(string)
                    ? defaultProperty
                    : null;

                var missing = new List<string>();
                if (wwiType == null) missing.Add("WorkWithInstance");
                if (resolved.Load == null) missing.Add("WorkWithInstance.Load");
                if (resolved.Levels == null) missing.Add("WorkWithInstance.Levels");
                if (resolved.SaveTo == null) missing.Add("WorkWithInstance.SaveTo");
                if (filterType == null) missing.Add("FilterElement");
                if (resolved.AddFilterAttribute == null) missing.Add("FilterElement.AddFilterAttribute(string)");
                if (needsConditions && resolved.AddCondition == null) missing.Add("FilterElement.AddCondition(string)");
                if (needsDescription && (resolved.Description == null || !resolved.Description.CanWrite)) missing.Add("FilterAttributeElement.Description setter");
                if (needsDomain && resolved.Domain == null && resolved.DomainName == null) missing.Add("FilterAttributeElement.Domain reference setter (or DomainName string setter)");
                if (needsDefault && resolved.Default == null) missing.Add("FilterAttributeElement.Default string setter");
                if (missing.Count > 0)
                {
                    missingMember = "unresolved member(s): " + string.Join(", ", missing);
                    return false;
                }

                surface = resolved;
                return true;
            }
            catch (Exception ex)
            {
                missingMember = "surface reflection failed: " + ex.Message;
                return false;
            }
        }

        /// Native write: every SDK member is resolved before the typed model is touched,
        /// and nothing is saved unless every requested piece was applied.
        internal static bool TryAddFilterAttributeNative(
            KBObject instanceObj,
            string levelId,
            string attributeName,
            string description,
            FilterDomainResolution domain,
            string defaultValue,
            IList<string> conditions,
            out string domainAppliedThrough,
            out string errorCode,
            out string errorMessage)
        {
            domainAppliedThrough = null;
            errorCode = null;
            errorMessage = null;
            bool hasDescription = !string.IsNullOrWhiteSpace(description);
            bool hasDomain = domain != null;
            bool hasDefault = !string.IsNullOrWhiteSpace(defaultValue);
            bool hasConditions = conditions != null && conditions.Count > 0;

            if (!TryResolveNativeFilterSurface(instanceObj?.GetType(), hasDescription, hasDomain, hasDefault, hasConditions, out var surface, out string missingMember))
            {
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter authoring is unavailable: " + missingMember + ". Filter writes have no XML fallback.";
                return false;
            }

            bool writtenToPart = false;
            try
            {
                object wwi = surface.Load.Invoke(null, new object[] { instanceObj });
                if (wwi == null)
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = "WorkWithInstance.Load returned null for the PatternInstance.";
                    return false;
                }

                if (!TryAuthorFilterAttributeOnTypedModel(surface, wwi, levelId, attributeName, description, domain, defaultValue, conditions, out _, out domainAppliedThrough, out errorCode, out errorMessage))
                    return false;

                surface.SaveTo.Invoke(wwi, new object[] { instanceObj });
                writtenToPart = true;
                instanceObj.Save();
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : ex;
                errorCode = "WorkWithFilterAuthoringFailed";
                errorMessage = "Native WorkWith filter authoring failed: " + cause.Message
                    + (writtenToPart
                        ? " The typed model was written to the in-memory PatternInstance part but Save failed; reopen the object before retrying."
                        : " Nothing was saved.");
                return false;
            }
        }

        /// Mutates the loaded typed model only: resolves the Level, adds the filter
        /// attribute, applies the supplied fields and conditions. The caller owns
        /// `SaveTo` and the KBObject save, so this step can be exercised without a KB.
        /// SDK exceptions propagate to the caller's handler.
        internal static bool TryAuthorFilterAttributeOnTypedModel(
            NativeFilterSurface surface,
            object workWith,
            string levelId,
            string attributeName,
            string description,
            FilterDomainResolution domain,
            string defaultValue,
            IList<string> conditions,
            out object newAttribute,
            out string domainAppliedThrough,
            out string errorCode,
            out string errorMessage)
        {
            newAttribute = null;
            domainAppliedThrough = null;
            errorCode = null;
            errorMessage = null;

            var levels = surface.Levels.GetValue(workWith, null) as IEnumerable;
            var candidates = levels != null ? CollectNativeFilterLevels(levels) : new List<FilterLevelCandidate>();
            if (!TryResolveFilterLevel(candidates, levelId, out var resolved, out errorCode, out errorMessage))
                return false;

            object created = surface.AddFilterAttribute.Invoke(resolved.Filter, new object[] { attributeName });
            if (created == null)
            {
                errorCode = "WorkWithFilterAuthoringFailed";
                errorMessage = "FilterElement.AddFilterAttribute returned null; nothing was saved.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(description)) surface.Description.SetValue(created, description, null);
            if (domain != null && !TryApplyFilterDomain(surface.Domain, surface.DomainName, created, domain, out domainAppliedThrough, out string domainFailure))
            {
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter authoring is unavailable: " + domainFailure + ". Nothing was saved.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(defaultValue)) surface.Default.SetValue(created, defaultValue, null);
            if (conditions != null)
            {
                foreach (var condition in conditions)
                    surface.AddCondition.Invoke(resolved.Filter, new object[] { condition });
            }

            newAttribute = created;
            return true;
        }

        /// Only the fields the request supplied are compared; `default` joins the list
        /// exactly like description and domain, so an omitted request value is never
        /// compared against the reader's "" normalization.
        internal static string[] ComparedFilterFields(string description, string domain, string defaultValue)
        {
            var fields = new List<string> { "name" };
            if (!string.IsNullOrWhiteSpace(description)) fields.Add("description");
            if (!string.IsNullOrWhiteSpace(domain)) fields.Add("domain");
            if (!string.IsNullOrWhiteSpace(defaultValue)) fields.Add("default");
            fields.Add("conditions");
            return fields.ToArray();
        }

        internal static bool VerifyFilterAttributeMatches(
            JObject observedAttribute,
            JArray observedConditions,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            out string mismatchDetail)
        {
            mismatchDetail = null;
            if (observedAttribute == null)
            {
                mismatchDetail = "Filter attribute not found in verified state.";
                return false;
            }

            string observedName = observedAttribute["name"]?.ToString() ?? "";
            if (!string.Equals(observedName, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                mismatchDetail = $"Filter attribute name mismatch: expected '{attributeName}', observed '{observedName}'.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                string observedDescription = observedAttribute["description"]?.ToString() ?? "";
                if (!string.Equals(observedDescription, description, StringComparison.Ordinal))
                {
                    mismatchDetail = $"Description mismatch: expected '{description}', observed '{observedDescription}'.";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(domain))
            {
                string observedDomain = observedAttribute["domain"]?.ToString() ?? "";
                if (!string.Equals(observedDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchDetail = $"Domain mismatch: expected '{domain}', observed '{observedDomain}'.";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                string observedDefault = observedAttribute["default"]?.ToString() ?? "";
                if (!string.Equals(observedDefault, defaultValue, StringComparison.Ordinal))
                {
                    mismatchDetail = $"Default mismatch: expected '{defaultValue}', observed '{observedDefault}'.";
                    return false;
                }
            }

            var observed = observedConditions?.Select(c => c.ToString()).ToList() ?? new List<string>();
            foreach (var condition in conditions ?? Array.Empty<string>())
            {
                if (!observed.Contains(condition, StringComparer.Ordinal))
                {
                    mismatchDetail = $"Condition '{condition}' was not found in the verified filter conditions [{string.Join(", ", observed)}].";
                    return false;
                }
            }

            return true;
        }

        internal static JObject BuildFilterReceipt(
            string instanceName,
            string level,
            JObject observedAttribute,
            JArray observedConditions,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            WorkWithReadSource preCheckSource,
            WorkWithReadSource observedSource,
            FilterDomainResolution domainResolution = null,
            JObject domainAdvisory = null,
            string domainAppliedThrough = null)
        {
            var verification = new JObject
            {
                ["scope"] = "filter_attribute_reread",
                ["fieldsCompared"] = new JArray(ComparedFilterFields(description, domain, defaultValue)),
                ["allFieldsMatched"] = true
            };
            DescribeReadSource(verification, "observedSource", observedSource);
            DescribeReadSource(verification, "preCheckSource", preCheckSource);

            var receipt = new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["filterAttribute"] = observedAttribute.DeepClone(),
                ["conditions"] = observedConditions.DeepClone(),
                ["requested"] = new JObject
                {
                    ["name"] = attributeName,
                    ["description"] = description ?? "",
                    ["domain"] = domain ?? "",
                    ["default"] = defaultValue ?? "",
                    ["conditions"] = new JArray(conditions ?? Array.Empty<string>())
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = true,
                ["verification"] = verification,
                ["route"] = "native-sdk",
                ["verificationScope"] = "filter attribute definition, the Level's filter conditions, filter attribute count integrity, and " + DescribeRereadScope(observedSource),
                ["defaultNormalization"] = NativeDefaultNormalization,
                ["derivedObjectRegeneration"] = "not_guaranteed",
                ["persistenceEvidence"] = BuildPersistenceEvidence()
            };

            if (domainResolution != null) receipt["domainResolution"] = DescribeDomainResolution(domainResolution, domainAppliedThrough ?? "Domain");
            if (domainAdvisory != null) receipt["domainAdvisory"] = domainAdvisory;
            return receipt;
        }

        /// Issue #590: what the post-save verification is and is not. Every write
        /// receipt rereads the typed WorkWithInstance through `Load(instanceObj)` on
        /// the same object that was just saved (the XML projection, when used, reads
        /// the same in-memory PatternInstance part), so `verified` proves that the
        /// typed model round-trips through SaveTo/Save in memory. Nothing in the
        /// receipt re-opens the persisted definition from the model store: the reread
        /// is not independent of the saved instance and is not evidence that the
        /// change survives a Worker restart or is visible in the GeneXus IDE. The
        /// durability proof is the reload smoke named in `durabilitySmoke`.
        internal const string PersistenceEvidenceRereadOf = "received_in_memory_instance";

        internal const string PersistenceEvidenceLimit =
            "saved and persisted report that the SDK Save completed; verified reports that the post-save reread of the received "
            + "in-memory instance matched the request. That reread is not an independent read of the persisted definition from the "
            + "model store and is not proof that the change survives a Worker restart or is visible in the GeneXus IDE.";

        internal const string PersistenceDurabilitySmoke =
            "scripts/workwith_persistence_smoke.ps1: write, restart the Worker with genexus_worker_reload, then reread with "
            + "list_filters/list_actions on the new Worker of a disposable WorkWith KB.";

        /// The declared limit of every WorkWith write receipt (`add_action`,
        /// `remove_action`, `add_filter_attribute`). It never changes `verified`;
        /// it says what `verified` was measured against.
        internal static JObject BuildPersistenceEvidence()
        {
            return new JObject
            {
                ["rereadOf"] = PersistenceEvidenceRereadOf,
                ["independentPersistedReread"] = false,
                ["provesDurability"] = false,
                ["limit"] = PersistenceEvidenceLimit,
                ["durabilitySmoke"] = PersistenceDurabilitySmoke
            };
        }

        // ── remove_filter_attribute (issue #586) ─────────────────────────────────

        /// Reads the filter of one Level as `list_filters` projects it. The remove
        /// orchestration takes this as a delegate so the native collection write can
        /// be exercised against a fake typed model without a Knowledge Base.
        internal delegate bool FilterReader(string levelId, out JObject filters, out string error, out string errorMessage);

        private static readonly Regex FilterAttributeIdentifierPattern = new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        internal const string RemoveFilterNoXmlFallback = "Filter writes have no XML fallback.";

        private static readonly string[] RemoveFilterReferencesChecked =
        {
            "conditions of the resolved Level's filter (lexical &Variable reference analysis)",
            "default of every neighbor filter attribute on the same filter"
        };

        private static readonly string[] RemoveFilterReferencesNotVerified =
        {
            "WebForm, Events and Rules of the derived objects (Selection WebPanel, <Trn>General)",
            "other Levels of this instance and other pattern instances",
            "Knowledge Base objects outside the WorkWith instance"
        };

        /// Filter removal writes only through the native WorkWith SDK, exactly like
        /// AddFilterAttribute: the typed FilterElement exposes no removal member, so the
        /// write operates the native Attributes/Conditions collections (BaseCollection
        /// Remove) and refuses with WorkWithSdkUnavailable when that surface is absent.
        /// There is deliberately no XML write fallback (issue #586).
        public static bool RemoveFilterAttribute(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            string levelId,
            string attributeName,
            IList<string> removeConditions,
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

            // The post-save reread goes through ListFilters, which may fall back to the XML
            // projection; the receipt reports the reader that actually answered (issue #589).
            var observedSource = WorkWithReadSource.NativeInstance;
            FilterReader reader = (string level, out JObject filters, out string error, out string message) =>
                ListFilters(instanceObj, patterns, level, out filters, out observedSource, out error, out message);

            bool succeeded = RemoveFilterAttributeCore(
                instanceObj,
                instanceObj.Name,
                GetWorkWithAssembly(),
                reader,
                () => instanceObj.Save(),
                levelId,
                attributeName,
                removeConditions,
                dryRun,
                out result,
                out errorCode,
                out errorMessage);

            if (!dryRun && result?["verification"] is JObject verification && verification["observedSource"]?.ToString() != "none")
            {
                verification.Remove("observedSourceReason");
                DescribeReadSource(verification, "observedSource", observedSource);
                result["verificationScope"] = result["verificationScope"]?.ToString().Replace("post-write in-memory instance re-read", DescribeRereadScope(observedSource));
            }

            return succeeded;
        }

        internal static bool RemoveFilterAttributeCore(
            KBObject instanceObj,
            string instanceName,
            Assembly workWithAssembly,
            FilterReader readFilters,
            Action persist,
            string levelId,
            string attributeName,
            IList<string> removeConditions,
            bool dryRun,
            out JObject result,
            out string errorCode,
            out string errorMessage)
        {
            result = null;
            errorCode = null;
            errorMessage = null;

            if (!readFilters(levelId, out var existingFilters, out errorCode, out errorMessage))
                return false;

            string resolvedLevel = existingFilters?["level"]?.ToString();
            if (!PlanFilterAttributeRemoval(instanceName, existingFilters, attributeName, removeConditions, out var plan, out errorCode, out errorMessage))
            {
                result = plan;
                return false;
            }

            if (workWithAssembly == null)
            {
                result = plan;
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter removal is unavailable: the Artech.Patterns.WorkWith assembly could not be resolved. " + RemoveFilterNoXmlFallback;
                return false;
            }

            if (dryRun)
            {
                result = plan;
                return true;
            }

            string normalizedName = FilterConditionReferenceMatcher.NormalizeAttributeName(attributeName);
            var conditionsToRemove = removeConditions ?? Array.Empty<string>();
            if (!TryRemoveFilterAttributeNative(instanceObj, workWithAssembly, persist, resolvedLevel, normalizedName, conditionsToRemove, out var removal, out errorCode, out errorMessage))
                return false;

            if (!readFilters(resolvedLevel, out var observedFilters, out var readError, out var readMessage))
            {
                // The write happened; the receipt must not claim a verified removal
                // without a reread, and it must not invent a new state either.
                result = BuildRemoveFilterReceipt(instanceName, resolvedLevel, removal, null, attributeName, normalizedName, conditionsToRemove,
                    verified: false, allFieldsMatched: null,
                    unverifiedReason: "verification re-read failed after save: " + (readError ?? "unknown") + (string.IsNullOrWhiteSpace(readMessage) ? "" : " - " + readMessage));
                return true;
            }

            var expectedAttributes = plan["preservedAttributes"] as JArray ?? new JArray();
            var expectedConditions = (plan["preservedConditions"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>();
            if (!VerifyFilterAttributeRemoval(expectedAttributes, expectedConditions, normalizedName, conditionsToRemove, observedFilters, out string mismatchDetail))
            {
                result = BuildRemoveFilterReceipt(instanceName, resolvedLevel, removal, observedFilters, attributeName, normalizedName, conditionsToRemove,
                    verified: false, allFieldsMatched: false, unverifiedReason: mismatchDetail);
                errorCode = "VerificationMismatch";
                errorMessage = $"Filter attribute '{normalizedName}' was saved, but post-save verification failed: {mismatchDetail}";
                return false;
            }

            result = BuildRemoveFilterReceipt(instanceName, resolvedLevel, removal, observedFilters, attributeName, normalizedName, conditionsToRemove,
                verified: true, allFieldsMatched: true, unverifiedReason: null);
            return true;
        }

        private static string ConditionVerdictLabel(FilterConditionReferenceMatcher.ReferenceVerdict verdict)
        {
            switch (verdict)
            {
                case FilterConditionReferenceMatcher.ReferenceVerdict.Referenced: return "referenced";
                case FilterConditionReferenceMatcher.ReferenceVerdict.Unanalyzable: return "unanalyzable";
                default: return "not_referenced";
            }
        }

        /// Pure removal planner over the `list_filters` projection: validates the request,
        /// locates the target, classifies every condition with the reference matcher and
        /// refuses when an unlisted condition (or a neighbor's default) still references the
        /// variable. The plan it returns is the dryRun payload and the verification baseline.
        internal static bool PlanFilterAttributeRemoval(
            string instanceName,
            JObject existingFilters,
            string attributeName,
            IList<string> removeConditions,
            out JObject plan,
            out string errorCode,
            out string errorMessage)
        {
            plan = null;
            errorCode = null;
            errorMessage = null;

            string normalizedName = FilterConditionReferenceMatcher.NormalizeAttributeName(attributeName);
            if (!FilterAttributeIdentifierPattern.IsMatch(normalizedName))
            {
                errorCode = "InvalidFilterAttributeName";
                errorMessage = $"Filter attribute name '{attributeName}' is not a valid identifier (letters, digits and underscore, optionally prefixed with '&').";
                return false;
            }

            var requested = removeConditions ?? Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < requested.Count; i++)
            {
                string condition = requested[i];
                if (string.IsNullOrWhiteSpace(condition))
                {
                    errorCode = "FilterConditionEmpty";
                    errorMessage = $"removeConditions[{i}] is empty; every condition to remove must be its exact text.";
                    return false;
                }

                if (!seen.Add(condition))
                {
                    errorCode = "FilterConditionDuplicate";
                    errorMessage = $"removeConditions[{i}] '{condition}' repeats another entry of removeConditions; list each condition once (every exact occurrence on the filter is removed).";
                    return false;
                }
            }

            string level = existingFilters?["level"]?.ToString();
            var existingAttrs = existingFilters?["attributes"] as JArray ?? new JArray();
            var existingConditions = (existingFilters?["conditions"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>();

            var target = existingAttrs.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), normalizedName, StringComparison.OrdinalIgnoreCase)) as JObject;
            if (target == null)
            {
                string available = existingAttrs.Count > 0 ? string.Join(", ", existingAttrs.Select(a => a["name"]?.ToString())) : "(none)";
                errorCode = "FilterAttributeNotFound";
                errorMessage = $"Filter attribute '{normalizedName}' was not found on the filter of Level '{level}' in WorkWith instance '{instanceName}'. Available filter attributes: {available}.";
                return false;
            }

            var removeEntries = new JArray();
            for (int i = 0; i < requested.Count; i++)
            {
                int occurrences = existingConditions.Count(c => string.Equals(c, requested[i], StringComparison.Ordinal));
                if (occurrences == 0)
                {
                    errorCode = "FilterConditionNotFound";
                    errorMessage = $"removeConditions[{i}] '{requested[i]}' is not present on the filter of Level '{level}' (exact, case-sensitive text). Present conditions: [{string.Join(", ", existingConditions)}].";
                    return false;
                }

                var analysis = FilterConditionReferenceMatcher.Analyze(requested[i], normalizedName);
                removeEntries.Add(new JObject
                {
                    ["value"] = requested[i],
                    ["occurrences"] = occurrences,
                    ["referencesAttribute"] = analysis.BlocksRemoval
                });
            }

            var referencing = new JArray();
            var incidental = new JArray();
            var analyzedTexts = new HashSet<string>(StringComparer.Ordinal);
            var unlisted = new List<string>();
            foreach (var condition in existingConditions)
            {
                if (!analyzedTexts.Add(condition)) continue;
                var analysis = FilterConditionReferenceMatcher.Analyze(condition, normalizedName);
                if (analysis.BlocksRemoval)
                {
                    bool listed = seen.Contains(condition);
                    referencing.Add(new JObject
                    {
                        ["value"] = condition,
                        ["verdict"] = ConditionVerdictLabel(analysis.Verdict),
                        ["reason"] = analysis.Reason ?? "",
                        ["listedForRemoval"] = listed
                    });
                    if (!listed) unlisted.Add(condition);
                }
                else if (analysis.IncidentalMentions.Count > 0)
                {
                    incidental.Add(new JObject
                    {
                        ["value"] = condition,
                        ["mentions"] = new JArray(analysis.IncidentalMentions)
                    });
                }
            }

            var neighborDefaultReferences = new JArray();
            var preservedAttributes = new JArray();
            foreach (var attr in existingAttrs.OfType<JObject>())
            {
                if (ReferenceEquals(attr, target)) continue;
                preservedAttributes.Add(attr.DeepClone());
                string neighborDefault = attr["default"]?.ToString();
                if (string.IsNullOrWhiteSpace(neighborDefault)) continue;
                var analysis = FilterConditionReferenceMatcher.Analyze(neighborDefault, normalizedName);
                if (analysis.BlocksRemoval)
                {
                    neighborDefaultReferences.Add(new JObject
                    {
                        ["attribute"] = attr["name"]?.ToString() ?? "",
                        ["default"] = neighborDefault,
                        ["verdict"] = ConditionVerdictLabel(analysis.Verdict),
                        ["reason"] = analysis.Reason ?? ""
                    });
                }
            }

            var preservedConditions = new JArray(existingConditions.Where(c => !seen.Contains(c)));

            plan = new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["filterAttribute"] = target.DeepClone(),
                ["removeConditions"] = removeEntries,
                ["referencingConditions"] = referencing,
                ["incidentalMentions"] = incidental,
                ["neighborDefaultReferences"] = neighborDefaultReferences,
                ["preservedAttributes"] = preservedAttributes,
                ["preservedConditions"] = preservedConditions,
                ["route"] = "native-sdk",
                ["validations"] = new JObject
                {
                    ["targetObjectResolved"] = true,
                    ["workWithInstanceResolved"] = true,
                    ["levelResolved"] = true,
                    ["filterPresent"] = true,
                    ["attributeNameFormatValid"] = true,
                    ["attributeFound"] = true,
                    ["removeConditionsNonEmptyAndUnique"] = true,
                    ["removeConditionsPresentOnFilter"] = true,
                    ["noUnlistedConditionReferencesAttribute"] = unlisted.Count == 0,
                    ["noNeighborDefaultReferencesAttribute"] = neighborDefaultReferences.Count == 0
                },
                ["validationLimits"] = new JObject
                {
                    ["conditionReferences"] = "References are found lexically: a whole &Variable token outside string literals and comments. Bare attribute names, member accesses and literal/comment text are reported as incidental mentions, not references. A condition that cannot be analyzed is treated as a reference (fail-closed).",
                    ["referencesOutsideConditions"] = "Only the listed scopes are checked; WebForm, Events and Rules of the derived objects are not read and may still reference the variable.",
                    ["conditionExpressions"] = "Filter condition expressions are not compiled or evaluated."
                },
                ["references"] = new JObject
                {
                    ["checked"] = new JArray(RemoveFilterReferencesChecked),
                    ["notVerified"] = new JArray(RemoveFilterReferencesNotVerified)
                },
                ["policy"] = new JObject
                {
                    ["duplicateRemoveConditions"] = "refused with FilterConditionDuplicate",
                    ["repeatedOccurrencesOnFilter"] = "every exact (Ordinal) occurrence of a listed condition is removed",
                    ["unlistedConditions"] = "never removed; an unlisted condition that references the variable refuses the removal",
                    ["filterNode"] = "the filter node and its attributes/conditions containers are never removed"
                },
                ["defaultNormalization"] = NativeDefaultNormalization,
                ["derivedObjectRegeneration"] = "not_guaranteed"
            };

            if (unlisted.Count > 0)
            {
                errorCode = "FilterAttributeReferencedByConditions";
                errorMessage = $"Filter attribute '{normalizedName}' (&{normalizedName}) is still referenced by {unlisted.Count} condition(s) not listed in removeConditions: [{string.Join(" | ", unlisted)}]. List them in removeConditions to remove them together with the attribute, or keep the attribute.";
                return false;
            }

            if (neighborDefaultReferences.Count > 0)
            {
                errorCode = "FilterAttributeReferencedByAttributeDefault";
                errorMessage = $"Filter attribute '{normalizedName}' (&{normalizedName}) is referenced by the default of neighbor filter attribute(s): {string.Join(", ", neighborDefaultReferences.Select(n => n["attribute"]?.ToString()))}. Change those defaults in the GeneXus IDE before removing the attribute.";
                return false;
            }

            return true;
        }

        private sealed class NativeFilterRemovalSurface
        {
            public MethodInfo Load;
            public PropertyInfo Levels;
            public MethodInfo SaveTo;
            public PropertyInfo Attributes;
            public PropertyInfo Conditions;
        }

        private static bool TryResolveNativeFilterRemovalSurface(Assembly asm, Type instanceType, out NativeFilterRemovalSurface surface, out string missingMember)
        {
            surface = null;
            missingMember = null;
            if (asm == null)
            {
                missingMember = "the Artech.Patterns.WorkWith assembly could not be resolved";
                return false;
            }

            try
            {
                var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                var filterType = asm.GetType("Artech.Patterns.WorkWith.FilterElement");
                var resolved = new NativeFilterRemovalSurface
                {
                    Load = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static),
                    Levels = wwiType?.GetProperty("Levels"),
                    SaveTo = (instanceType != null ? wwiType?.GetMethod("SaveTo", new[] { instanceType }) : null)
                        ?? wwiType?.GetMethod("SaveTo"),
                    Attributes = filterType?.GetProperty("Attributes"),
                    Conditions = filterType?.GetProperty("Conditions")
                };

                var missing = new List<string>();
                if (wwiType == null) missing.Add("WorkWithInstance");
                if (resolved.Load == null) missing.Add("WorkWithInstance.Load");
                if (resolved.Levels == null) missing.Add("WorkWithInstance.Levels");
                if (resolved.SaveTo == null) missing.Add("WorkWithInstance.SaveTo");
                if (filterType == null) missing.Add("FilterElement");
                if (resolved.Attributes == null) missing.Add("FilterElement.Attributes");
                if (resolved.Conditions == null) missing.Add("FilterElement.Conditions");
                if (missing.Count > 0)
                {
                    missingMember = "unresolved member(s): " + string.Join(", ", missing);
                    return false;
                }

                surface = resolved;
                return true;
            }
            catch (Exception ex)
            {
                missingMember = "surface reflection failed: " + ex.Message;
                return false;
            }
        }

        private static string ReadElementString(object element, string propertyName)
        {
            return element?.GetType().GetProperty(propertyName)?.GetValue(element, null)?.ToString();
        }

        /// The native collections (`BaseCollection<T>`) expose `Remove(T)`; a fake or a
        /// future SDK may expose it through a different declared parameter type, so the
        /// lookup falls back to any single-parameter `Remove` assignable from the element.
        private static MethodInfo ResolveCollectionRemove(object collection, object element)
        {
            if (collection == null || element == null) return null;
            var collectionType = collection.GetType();
            var elementType = element.GetType();
            var exact = collectionType.GetMethod("Remove", new[] { elementType });
            if (exact != null) return exact;
            return collectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Remove"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsAssignableFrom(elementType));
        }

        private static bool InvokeCollectionRemove(MethodInfo remove, object collection, object element)
        {
            object outcome = remove.Invoke(collection, new[] { element });
            return !(outcome is bool removed) || removed;
        }

        /// Native removal: every SDK member and every element to remove is resolved before
        /// the typed model is touched; the listed conditions (all exact occurrences) go
        /// first, then the attribute, then SaveTo and the instance Save. The filter node
        /// and its containers are never removed.
        internal static bool TryRemoveFilterAttributeNative(
            KBObject instanceObj,
            Assembly asm,
            Action persist,
            string levelId,
            string attributeName,
            IList<string> removeConditions,
            out JObject removal,
            out string errorCode,
            out string errorMessage)
        {
            removal = null;
            errorCode = null;
            errorMessage = null;
            string normalizedName = FilterConditionReferenceMatcher.NormalizeAttributeName(attributeName);
            var listed = new HashSet<string>(removeConditions ?? Array.Empty<string>(), StringComparer.Ordinal);

            if (!TryResolveNativeFilterRemovalSurface(asm, instanceObj?.GetType(), out var surface, out string missingMember))
            {
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter removal is unavailable: " + missingMember + ". " + RemoveFilterNoXmlFallback;
                return false;
            }

            bool mutated = false;
            bool writtenToPart = false;
            try
            {
                object wwi = surface.Load.Invoke(null, new object[] { instanceObj });
                if (wwi == null)
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = "WorkWithInstance.Load returned null for the PatternInstance. Nothing was saved.";
                    return false;
                }

                var levels = surface.Levels.GetValue(wwi, null) as IEnumerable;
                var candidates = levels != null ? CollectNativeFilterLevels(levels) : new List<FilterLevelCandidate>();
                if (!TryResolveFilterLevel(candidates, levelId, out var resolved, out errorCode, out errorMessage))
                    return false;

                var attributes = surface.Attributes.GetValue(resolved.Filter, null) as IEnumerable;
                var conditions = surface.Conditions.GetValue(resolved.Filter, null) as IEnumerable;
                if (attributes == null || conditions == null)
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = "FilterElement.Attributes or FilterElement.Conditions returned null. Nothing was saved.";
                    return false;
                }

                object targetElement = null;
                foreach (var fa in attributes)
                {
                    if (string.Equals(ReadElementString(fa, "Name"), normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetElement = fa;
                        break;
                    }
                }

                if (targetElement == null)
                {
                    errorCode = "FilterAttributeNotFound";
                    errorMessage = $"Filter attribute '{normalizedName}' is no longer present on the native filter of Level '{resolved.Id}'. Nothing was saved.";
                    return false;
                }

                var conditionElements = new List<KeyValuePair<string, object>>();
                var unlistedReferences = new List<string>();
                foreach (var ce in conditions)
                {
                    string value = ReadElementString(ce, "Value");
                    if (value == null) continue;
                    if (listed.Contains(value))
                    {
                        conditionElements.Add(new KeyValuePair<string, object>(value, ce));
                        continue;
                    }

                    if (FilterConditionReferenceMatcher.Analyze(value, normalizedName).BlocksRemoval)
                        unlistedReferences.Add(value);
                }

                var missingListed = listed.Where(l => conditionElements.All(ce => !string.Equals(ce.Key, l, StringComparison.Ordinal))).ToList();
                if (missingListed.Count > 0)
                {
                    errorCode = "FilterConditionNotFound";
                    errorMessage = $"Listed condition(s) are no longer present on the native filter of Level '{resolved.Id}': [{string.Join(" | ", missingListed)}]. The filter changed since it was read; nothing was saved.";
                    return false;
                }

                if (unlistedReferences.Count > 0)
                {
                    errorCode = "FilterAttributeReferencedByConditions";
                    errorMessage = $"Filter attribute '{normalizedName}' is referenced by condition(s) not listed in removeConditions on the native filter: [{string.Join(" | ", unlistedReferences)}]. Nothing was saved.";
                    return false;
                }

                var removeAttribute = ResolveCollectionRemove(attributes, targetElement);
                if (removeAttribute == null)
                {
                    errorCode = "WorkWithSdkUnavailable";
                    errorMessage = "Native WorkWith filter removal is unavailable: unresolved member(s): FilterAttributesElement.Remove(FilterAttributeElement). " + RemoveFilterNoXmlFallback;
                    return false;
                }

                MethodInfo removeCondition = null;
                if (conditionElements.Count > 0)
                {
                    removeCondition = ResolveCollectionRemove(conditions, conditionElements[0].Value);
                    if (removeCondition == null)
                    {
                        errorCode = "WorkWithSdkUnavailable";
                        errorMessage = "Native WorkWith filter removal is unavailable: unresolved member(s): ConditionsElement.Remove(ConditionElement). " + RemoveFilterNoXmlFallback;
                        return false;
                    }
                }

                var removedAttribute = new JObject
                {
                    ["name"] = ReadElementString(targetElement, "Name") ?? normalizedName,
                    ["description"] = ReadElementString(targetElement, "Description") ?? normalizedName,
                    ["domain"] = ReadElementString(targetElement, "DomainName") ?? ReadElementString(targetElement, "Domain") ?? "",
                    ["variableName"] = ReadElementString(targetElement, "VariableName") ?? ("&" + normalizedName)
                };

                var removedConditions = new JArray();
                foreach (var group in conditionElements.GroupBy(ce => ce.Key, StringComparer.Ordinal))
                {
                    int removedCount = 0;
                    foreach (var pair in group)
                    {
                        mutated = true;
                        if (!InvokeCollectionRemove(removeCondition, conditions, pair.Value))
                        {
                            errorCode = "WorkWithFilterAuthoringFailed";
                            errorMessage = $"The native Conditions collection refused to remove '{pair.Key}'. The typed in-memory model may be partially modified; reopen the object before retrying. Nothing was saved.";
                            return false;
                        }
                        removedCount++;
                    }
                    removedConditions.Add(new JObject { ["value"] = group.Key, ["occurrencesRemoved"] = removedCount });
                }

                mutated = true;
                if (!InvokeCollectionRemove(removeAttribute, attributes, targetElement))
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = $"The native Attributes collection refused to remove '{normalizedName}'. The typed in-memory model may be partially modified; reopen the object before retrying. Nothing was saved.";
                    return false;
                }

                surface.SaveTo.Invoke(wwi, new object[] { instanceObj });
                writtenToPart = true;
                persist?.Invoke();

                removal = new JObject
                {
                    ["removedFilterAttribute"] = removedAttribute,
                    ["removedConditions"] = removedConditions
                };
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : ex;
                errorCode = "WorkWithFilterAuthoringFailed";
                errorMessage = "Native WorkWith filter removal failed: " + cause.Message
                    + (writtenToPart
                        ? " The typed model was written to the in-memory PatternInstance part but Save failed; reopen the object before retrying."
                        : mutated
                            ? " The typed in-memory model may be partially modified; reopen the object before retrying. Nothing was saved."
                            : " Nothing was saved.");
                return false;
            }
        }

        /// Field-by-field verification of the reread filter: the removed attribute and
        /// every listed condition must be absent, every neighbor attribute must keep its
        /// name, description and domain in order, and every preserved condition text must
        /// come back in order. Counts alone are never enough.
        internal static bool VerifyFilterAttributeRemoval(
            JArray expectedAttributes,
            IList<string> expectedConditions,
            string removedAttributeName,
            IList<string> removedConditions,
            JObject observedFilters,
            out string mismatchDetail)
        {
            mismatchDetail = null;
            if (observedFilters == null)
            {
                mismatchDetail = "No filter state was observed after save.";
                return false;
            }

            string removedName = FilterConditionReferenceMatcher.NormalizeAttributeName(removedAttributeName);
            var observedAttrs = (observedFilters["attributes"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var observedConditions = (observedFilters["conditions"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>();

            if (observedAttrs.Any(a => string.Equals(a["name"]?.ToString(), removedName, StringComparison.OrdinalIgnoreCase)))
            {
                mismatchDetail = $"Filter attribute '{removedName}' is still present after save.";
                return false;
            }

            foreach (var removed in removedConditions ?? Array.Empty<string>())
            {
                if (observedConditions.Contains(removed, StringComparer.Ordinal))
                {
                    mismatchDetail = $"Condition '{removed}' is still present after save.";
                    return false;
                }
            }

            var expected = expectedAttributes?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (observedAttrs.Count != expected.Count)
            {
                mismatchDetail = $"Neighbor filter attribute count mismatch: expected {expected.Count} [{string.Join(", ", expected.Select(a => a["name"]?.ToString()))}], observed {observedAttrs.Count} [{string.Join(", ", observedAttrs.Select(a => a["name"]?.ToString()))}].";
                return false;
            }

            for (int i = 0; i < expected.Count; i++)
            {
                string expectedName = expected[i]["name"]?.ToString() ?? "";
                string observedName = observedAttrs[i]["name"]?.ToString() ?? "";
                if (!string.Equals(expectedName, observedName, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchDetail = $"Neighbor filter attribute at position {i}: expected '{expectedName}', observed '{observedName}'.";
                    return false;
                }

                string expectedDescription = expected[i]["description"]?.ToString() ?? "";
                string observedDescription = observedAttrs[i]["description"]?.ToString() ?? "";
                if (!string.Equals(expectedDescription, observedDescription, StringComparison.Ordinal))
                {
                    mismatchDetail = $"Neighbor filter attribute '{expectedName}' description mismatch: expected '{expectedDescription}', observed '{observedDescription}'.";
                    return false;
                }

                string expectedDomain = expected[i]["domain"]?.ToString() ?? "";
                string observedDomain = observedAttrs[i]["domain"]?.ToString() ?? "";
                if (!string.Equals(expectedDomain, observedDomain, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchDetail = $"Neighbor filter attribute '{expectedName}' domain mismatch: expected '{expectedDomain}', observed '{observedDomain}'.";
                    return false;
                }
            }

            var expectedConditionList = expectedConditions ?? Array.Empty<string>();
            if (!observedConditions.SequenceEqual(expectedConditionList, StringComparer.Ordinal))
            {
                mismatchDetail = $"Preserved conditions mismatch: expected [{string.Join(" | ", expectedConditionList)}], observed [{string.Join(" | ", observedConditions)}].";
                return false;
            }

            return true;
        }

        /// The receipt reuses the `saved`/`persisted`/`verified` trio of the other
        /// WorkWith receipts: a failed reread or a field mismatch is `verified: false` with
        /// its reason, never a new state and never a verified removal without a reread.
        internal static JObject BuildRemoveFilterReceipt(
            string instanceName,
            string level,
            JObject removal,
            JObject observedFilters,
            string requestedAttribute,
            string normalizedAttribute,
            IList<string> removeConditions,
            bool verified,
            bool? allFieldsMatched,
            string unverifiedReason)
        {
            var verification = new JObject
            {
                ["scope"] = "filter_reread",
                ["fieldsCompared"] = new JArray("attributes[].name", "attributes[].description", "attributes[].domain", "conditions[]"),
                ["allFieldsMatched"] = allFieldsMatched.HasValue ? new JValue(allFieldsMatched.Value) : JValue.CreateNull(),
                ["observedSource"] = observedFilters != null ? "in_memory_instance_reread" : "none",
                ["referencesChecked"] = new JArray(RemoveFilterReferencesChecked),
                ["referencesNotVerified"] = new JArray(RemoveFilterReferencesNotVerified)
            };
            if (!verified) verification["reason"] = unverifiedReason ?? "verification did not run";

            return new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["removedFilterAttribute"] = removal?["removedFilterAttribute"]?.DeepClone(),
                ["removedConditions"] = removal?["removedConditions"]?.DeepClone() ?? new JArray(),
                ["remainingAttributes"] = observedFilters?["attributes"]?.DeepClone(),
                ["remainingConditions"] = observedFilters?["conditions"]?.DeepClone(),
                ["requested"] = new JObject
                {
                    ["attribute"] = requestedAttribute ?? "",
                    ["normalizedAttribute"] = normalizedAttribute ?? "",
                    ["removeConditions"] = new JArray(removeConditions ?? Array.Empty<string>())
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = verified,
                ["verification"] = verification,
                ["route"] = "native-sdk",
                ["verificationScope"] = "removed filter attribute absence, listed condition absence, every neighbor filter attribute (name, description, domain) in order, every preserved condition text in order, and post-write in-memory instance re-read",
                ["defaultNormalization"] = NativeDefaultNormalization,
                ["derivedObjectRegeneration"] = "not_guaranteed",
                ["persistenceEvidence"] = BuildPersistenceEvidence()
            };
        }
    }
}
