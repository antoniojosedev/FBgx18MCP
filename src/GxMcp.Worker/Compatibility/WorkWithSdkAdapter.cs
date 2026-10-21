using System;
using System.Collections;
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
    internal static partial class WorkWithSdkAdapter
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
                // A machine without the WorkWith package is a supported configuration: the
                // adapter then reports XmlProjectionNativeUnavailable. The cause stays in the log.
                catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is ArgumentException
                    || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    Logger.Debug("[WorkWithSdkAdapter] Artech.Patterns.WorkWith could not be loaded: " + ex.GetType().Name + ": " + ex.Message);
                }
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
    }
}
