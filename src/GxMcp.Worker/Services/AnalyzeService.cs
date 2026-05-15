using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class AnalyzeService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        private readonly UIService _uiService;
        private readonly NavigationService _navigationService;

        // v2.3.8 (Task 1.4): unified graph navigation. ImpactAnalysis used to
        // run an inline BFS over CalledBy here; it now delegates to
        // CallerGraphService.GetCallersTransitive so callers/callees logic
        // lives in one place.
        private readonly CallerGraphService _graph;

        public AnalyzeService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService, NavigationService navigationService = null, UIService uiService = null, CallerGraphService graph = null)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _navigationService = navigationService;
            _uiService = uiService;
            _graph = graph ?? new CallerGraphService(indexCacheService);
        }

        // Test-friendly ctor matching the plan signature.
        public AnalyzeService(IndexCacheService index, ObjectService objSvc, CallerGraphService graph)
        {
            _indexCacheService = index;
            _objectService = objSvc;
            _graph = graph ?? new CallerGraphService(index);
        }

        public string Analyze(string target, string typeFilter = null)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (target == null) return "{\"status\":\"KB analysis not implemented for all objects yet\"}";

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                
                // PERFORMANCE (W-A4): de-duplicate references before issuing SDK.Get calls.
                // GetReferences() returns one edge per call-site, so a procedure that calls the
                // same target 10× would previously cost 10 Get() round-trips. The N stays the
                // same when each reference is unique, but cuts hard in real KBs where repeated
                // edges are common. This is the safe portion of the audited N+1 win — touching
                // the SDK semantics (batched fetch / reverse-resolve via index) needs its own
                // regression suite, so deliberately left out here.
                var calls = new JArray();
                var seenRefKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in obj.GetReferences())
                {
                    string refKey = null;
                    try { refKey = reference.To?.ToString(); } catch { }
                    if (!string.IsNullOrEmpty(refKey) && !seenRefKeys.Add(refKey)) continue;

                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) {
                        var cObj = new JObject();
                        cObj["name"] = targetObj.Name;
                        cObj["type"] = targetObj.TypeDescriptor.Name;
                        cObj["description"] = targetObj.Description;
                        calls.Add(cObj);
                    }
                }
                result["calls"] = calls;

                // Add Impact Radius if Index is available
                try {
                    var impact = GetImpactAnalysis(obj.Name);
                    result["impactAnalysis"] = JObject.Parse(impact);
                } catch {}

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Backwards-compatible entry point used by Analyze() and other callers
        // that don't care about the index-readiness envelope.
        public string GetImpactAnalysis(string targetName)
        {
            return ImpactAnalysis(targetName, waitForIndex: true);
        }

        // v2.3.8 (Task 1.4): index-aware impact analysis. Delegates the BFS to
        // CallerGraphService and adds a waitForIndex contract so callers can
        // opt out of blocking on a cold/reindexing index.
        public string ImpactAnalysis(string targetName, bool waitForIndex = true, int waitTimeoutMs = 30000)
            => ImpactAnalysis(targetName, waitForIndex, waitTimeoutMs, System.Threading.CancellationToken.None);

        // v2.3.8 (post-Task 7.2): same path, threaded with a CancellationToken so
        // a Control:Cancel from the gateway side-channel can stop the BFS mid-walk.
        public string ImpactAnalysis(string targetName, bool waitForIndex, int waitTimeoutMs, System.Threading.CancellationToken ct)
        {
            try
            {
                // 1) Index-readiness envelope.
                var state = _indexCacheService?.GetState();
                if (state != null && state.Status != "Ready")
                {
                    if (!waitForIndex)
                    {
                        var env = new JObject { ["status"] = state.Status };
                        if (state.EtaMs.HasValue) env["etaMs"] = state.EtaMs.Value;
                        if (state.Progress.HasValue) env["progress"] = state.Progress.Value;
                        return env.ToString();
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < waitTimeoutMs)
                    {
                        var s = _indexCacheService.GetState();
                        if (s != null && s.Status == "Ready") break;
                        System.Threading.Thread.Sleep(200);
                    }
                    var finalState = _indexCacheService.GetState();
                    if (finalState == null || finalState.Status != "Ready")
                    {
                        return new JObject
                        {
                            ["status"] = "Timeout",
                            ["waitedMs"] = sw.ElapsedMilliseconds
                        }.ToString();
                    }
                }

                var index = _indexCacheService?.GetIndex();
                if (index == null || index.Objects == null) return Models.McpResponse.Error("Index not found", targetName, null, "Run the KB indexing flow before requesting impact analysis.");

                // 2) Name resolution — preserve the existing priority ordering
                //    (Procedure > Transaction > Table) so consumers see the
                //    same canonical name they used to.
                SearchIndex.IndexEntry targetNode = null;
                string foundKey = null;

                if (targetName != null && targetName.Contains(":") && index.Objects.TryGetValue(targetName, out targetNode))
                {
                    foundKey = targetName;
                }
                else
                {
                    var possibleKeys = index.Objects.Keys.Where(k => k.EndsWith(":" + targetName, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (possibleKeys.Count == 0)
                    {
                        var obj = _objectService?.FindObject(targetName);
                        if (obj != null)
                        {
                            return "{\"target\": \"" + obj.Name + "\", \"status\": \"Indexing in progress for this object. Please retry in a few seconds.\", \"totalAffected\": 0}";
                        }
                        return Models.McpResponse.Error("Object not found in index", targetName, null, "The object was not found in the search index or the active Knowledge Base.");
                    }

                    foundKey = possibleKeys.FirstOrDefault(k => k.StartsWith("Procedure:", StringComparison.OrdinalIgnoreCase))
                             ?? possibleKeys.FirstOrDefault(k => k.StartsWith("Transaction:", StringComparison.OrdinalIgnoreCase))
                             ?? possibleKeys.FirstOrDefault(k => k.StartsWith("Table:", StringComparison.OrdinalIgnoreCase))
                             ?? possibleKeys.First();

                    index.Objects.TryGetValue(foundKey, out targetNode);
                }

                if (targetNode == null)
                {
                    return Models.McpResponse.Error("Object not found in index", targetName, null, "The object was not found in the search index or the active Knowledge Base.");
                }

                targetName = targetNode.Name; // canonical name

                // 3) Delegate the BFS to the unified graph service.
                var graph = _graph ?? new CallerGraphService(_indexCacheService);
                var callersResult = graph.GetCallersTransitive(targetName, 200, ct);
                if (ct.IsCancellationRequested) return new JObject { ["status"] = "Cancelled", ["target"] = targetName }.ToString();
                var calleesResult = graph.GetCalleesTransitive(targetName, 200, ct);
                if (ct.IsCancellationRequested) return new JObject { ["status"] = "Cancelled", ["target"] = targetName }.ToString();

                // De-dupe + index-aware enrichment (score / entry points / etc.).
                var affected = new HashSet<string>(callersResult.Nodes, StringComparer.OrdinalIgnoreCase);

                int score = 0;
                var entryPoints = new List<string>();
                foreach (var name in affected)
                {
                    if (index.Objects.TryGetValue(name, out var node) ||
                        TryFindByBareName(index, name, out node))
                    {
                        if (node != null)
                        {
                            score += GetTypeWeight(node.Type);
                            if (IsEntryPoint(node)) entryPoints.Add(name);
                        }
                    }
                }

                var json = new JObject();
                json["status"] = "Ready";
                json["target"] = targetName;
                json["totalAffected"] = affected.Count;
                json["blastRadiusScore"] = score;
                json["riskLevel"] = score > 100 ? "High" : (score > 20 ? "Medium" : "Low");

                var callersArr = new JArray();
                foreach (var c in callersResult.Nodes) callersArr.Add(c);
                json["callers"] = callersArr;
                json["callersTruncated"] = callersResult.Truncated;

                var calleesArr = new JArray();
                foreach (var c in calleesResult.Nodes) calleesArr.Add(c);
                json["callees"] = calleesArr;
                json["calleesTruncated"] = calleesResult.Truncated;
                json["maxDepth"] = Math.Max(callersResult.Depth, calleesResult.Depth);

                var topEntryPoints = new JArray();
                foreach (var ep in entryPoints.Take(50)) topEntryPoints.Add(ep);
                json["affectedEntryPoints"] = topEntryPoints;

                var topAffected = new JArray();
                foreach (var aff in affected.Take(50)) topAffected.Add(aff);
                json["topImpacted"] = topAffected;

                return json.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"Impact Analysis failed: " + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Helper: graph service returns bare names (e.g. "A"), but the index
        // keys things as "Type:Name". Resolve a bare name back to its entry by
        // scanning the keys once we don't have a direct hit.
        private static bool TryFindByBareName(SearchIndex index, string bareName, out SearchIndex.IndexEntry entry)
        {
            entry = null;
            if (index?.Objects == null || string.IsNullOrEmpty(bareName)) return false;
            foreach (var kv in index.Objects)
            {
                var v = kv.Value;
                if (v != null && string.Equals(v.Name, bareName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = v;
                    return true;
                }
            }
            return false;
        }

        private int GetTypeWeight(string type)
        {
            if (string.IsNullOrEmpty(type)) return 1;
            var t = type.ToLower();
            if (t.Contains("transaction")) return 10;
            if (t.Contains("webpanel")) return 5;
            if (t.Contains("procedure")) return 3;
            if (t.Contains("dataselector")) return 5;
            if (t.Contains("attribute")) return 8;
            return 1;
        }

        private bool IsEntryPoint(GxMcp.Worker.Models.SearchIndex.IndexEntry node)
        {
            if (node.Type == "Transaction" || node.Type == "WebPanel") return true;
            if (!string.IsNullOrEmpty(node.Description) && node.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }


        public string GetAttributeMetadata(string name)
        {
            try
            {
                var kb = _kbService.GetKB();
                foreach (var obj in kb.DesignModel.Objects.GetByName(null, null, name))
                {
                    if (string.Equals(obj.TypeDescriptor.Name, "Attribute", StringComparison.OrdinalIgnoreCase))
                    {
                        dynamic attr = obj;
                        var json = new JObject();
                        json["name"] = attr.Name;
                        json["description"] = attr.Description;
                        json["type"] = attr.Type.ToString();
                        json["length"] = (int)attr.Length;
                        json["decimals"] = (int)attr.Decimals;
                        
                        try {
                            if (attr.Table != null) {
                                json["table"] = attr.Table.Name;
                            }
                        } catch {}

                        return json.ToString();
                    }
                }
                return "{\"error\":\"Attribute not found\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetVariables(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (vPart == null) return Models.McpResponse.Error("Variables part not found", name, "Variables", "The object does not expose a Variables part.", obj.Name, obj.TypeDescriptor?.Name, new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)));

                var variables = new JArray();
                int idx = 0;
                foreach (Variable var in vPart.Variables)
                {
                    idx++;
                    var item = new JObject();
                    item["name"] = var.Name;
                    item["type"] = var.Type.ToString();
                    // Layout XML uses AttID="var:N" — surface the internal id so agents don't grep the generated .cs.
                    int? internalId = VariableInjector.GetVariableInternalId(var, idx);
                    if (internalId.HasValue) item["internalId"] = internalId.Value;
                    var managedBy = GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(var.Name);
                    if (managedBy != null) item["managedBy"] = managedBy;
                    variables.Add(item);
                }

                var result = new JObject();
                result["variables"] = variables;
                result["source"] = VariableInjector.GetVariablesAsText((dynamic)vPart);
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetHierarchy(string name, string typeFilter = null)
        {
            try
            {
                var kb = _kbService.GetKB();
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;

                // PERFORMANCE (W-A4): dedup outgoing references — same justification as Analyze.
                var calls = new JArray();
                var seenOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in obj.GetReferences())
                {
                    string refKey = null;
                    try { refKey = reference.To?.ToString(); } catch { }
                    if (!string.IsNullOrEmpty(refKey) && !seenOut.Add(refKey)) continue;

                    var targetObj = kb.DesignModel.Objects.Get(reference.To);
                    if (targetObj != null) calls.Add(new JObject {
                        ["name"] = targetObj.Name,
                        ["type"] = targetObj.TypeDescriptor.Name,
                        ["description"] = targetObj.Description
                    });
                }
                result["calls"] = calls;

                // PERFORMANCE (W-A4): dedup incoming references.
                var calledBy = new JArray();
                var seenIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in obj.GetReferencesTo())
                {
                    string refKey = null;
                    try { refKey = reference.From?.ToString(); } catch { }
                    if (!string.IsNullOrEmpty(refKey) && !seenIn.Add(refKey)) continue;

                    var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                    if (sourceObj != null) calledBy.Add(new JObject {
                        ["name"] = sourceObj.Name,
                        ["type"] = sourceObj.TypeDescriptor.Name,
                        ["description"] = sourceObj.Description
                    });
                }
                result["calledBy"] = calledBy;

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetConversionContext(string name, JArray include = null, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["description"] = obj.Description;

                // Attribute-specific: surface physical tables that host this attribute
                if (obj is global::Artech.Genexus.Common.Objects.Attribute)
                {
                    try
                    {
                        var index = _indexCacheService.GetIndex();
                        if (index.Objects.TryGetValue($"Attribute:{obj.Name}", out var entry) && entry != null)
                        {
                            var tablesArr = new JArray();
                            if (entry.Tables != null)
                            {
                                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var t in entry.Tables)
                                {
                                    if (!string.IsNullOrWhiteSpace(t) && seen.Add(t))
                                        tablesArr.Add(t);
                                }
                            }
                            result["tables"] = tablesArr;
                        }
                        else
                        {
                            result["tables"] = new JArray();
                        }
                    }
                    catch { /* swallow — keep response valid */ }
                }

                // DataProvider-specific: returnsSDT + readsFromTables
                if (obj is Artech.Genexus.Common.Objects.DataProvider dpObj)
                {
                    try
                    {
                        string returnsSdt = TryGetDataProviderReturnsSdt(dpObj);
                        if (!string.IsNullOrEmpty(returnsSdt))
                            result["returnsSDT"] = returnsSdt;

                        var tables = ResolveDataProviderTablesRead(dpObj);
                        if (tables.Count > 0)
                        {
                            var arr = new JArray();
                            foreach (var t in tables) arr.Add(t);
                            result["readsFromTables"] = arr;
                        }
                    }
                    catch { /* swallow — keep response valid */ }
                }

                bool includeAll = (include == null || include.Count == 0);
                HashSet<string> requested = includeAll ? new HashSet<string>() : new HashSet<string>(include.Select(i => i.ToString().ToLower()));

                // PERFORMANCE: Parallel execution of metadata extraction
                var tasks = new List<Task>();

                // 1. Signature (Parameters)
                if (includeAll || requested.Contains("signature"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                            lock (result) {
                                if (!string.IsNullOrEmpty(parmRule)) result["parmRule"] = parmRule;
                                var parameters = new JArray();
                                foreach (var p in parms) parameters.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                                result["parameters"] = parameters;
                            }
                        } catch {}
                    }));
                }

                // 2. Variables
                if (includeAll || requested.Contains("variables"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                            if (vPart != null) {
                                var variables = new JArray();
                                int idxLocal = 0;
                                foreach (Variable v in vPart.Variables) {
                                    idxLocal++;
                                    var entry = new JObject { ["name"] = v.Name, ["type"] = v.Type.ToString(), ["length"] = (int)v.Length, ["decimals"] = (int)v.Decimals };
                                    // FR#1 + FR#13: also surface internalId here.
                                    int? id = VariableInjector.GetVariableInternalId(v, idxLocal);
                                    if (id.HasValue) entry["internalId"] = id.Value;
                                    variables.Add(entry);
                                }
                                lock (result) result["variables"] = variables;
                            }
                        } catch {}
                    }));
                }

                // 3. Structure (Rules/Events)
                if (includeAll || requested.Contains("structure"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var rules = GetPartSourceByName(obj, "Rules");
                            lock (result) result["rules"] = rules;
                            
                            if (obj is Procedure || obj is WebPanel) {
                                var conditions = GetPartSourceByName(obj, "Conditions");
                                lock (result) result["conditions"] = conditions;
                            }
                            if (obj is Transaction || obj is WebPanel) {
                                var events = GetPartSourceByName(obj, "Events");
                                lock (result) result["events"] = events;
                            }
                        } catch {}
                    }));
                }

                // 4. Domains & Enums
                if (includeAll || requested.Contains("metadata") || requested.Contains("variables"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var domains = new JArray();
                            var processedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            dynamic vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                            if (vPart != null) {
                                foreach (var v in vPart.Variables) {
                                    dynamic dv = v;
                                    var domain = dv.Domain ?? (dv.Attribute != null ? dv.Attribute.Domain : null);
                                    if (domain != null && !processedDomains.Contains(domain.Name)) {
                                        processedDomains.Add(domain.Name);
                                        var dObj = new JObject { ["name"] = domain.Name };
                                        var values = new JArray();
                                        foreach (var ev in ((dynamic)domain).EnumValues) values.Add(new JObject { ["name"] = ev.Name, ["value"] = ev.Value });
                                        dObj["values"] = values;
                                        domains.Add(dObj);
                                    }
                                }
                            }
                            if (domains.Count > 0) lock (result) result["domains"] = domains;
                        } catch {}
                    }));
                }

                // 5. Callers (incoming references) — surfaces top-N callers so the agent can
                // skip a follow-up analyze(mode=impact) / query usedby:* call. Opt-in via
                // include=["callers"] or default (when no include filter is provided).
                if (includeAll || requested.Contains("callers"))
                {
                    tasks.Add(Task.Run(() => {
                        try {
                            var kb = _kbService.GetKB();
                            var callers = new JArray();
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            const int maxCallers = 20;
                            foreach (var reference in obj.GetReferencesTo())
                            {
                                string refKey = null;
                                try { refKey = reference.From?.ToString(); } catch { }
                                if (string.IsNullOrEmpty(refKey) || !seen.Add(refKey)) continue;

                                var sourceObj = kb.DesignModel.Objects.Get(reference.From);
                                if (sourceObj == null) continue;
                                callers.Add(new JObject {
                                    ["name"] = sourceObj.Name,
                                    ["type"] = sourceObj.TypeDescriptor.Name
                                });
                                if (callers.Count >= maxCallers) break;
                            }
                            lock (result) {
                                result["callers"] = callers;
                                result["callersTruncated"] = callers.Count >= maxCallers;
                            }
                        } catch {}
                    }));
                }

                // Wait for all metadata tasks to complete (with timeout for safety)
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));

                // 5. UI Structure (Sync because it's usually fast or has its own internal logic)
                if (_uiService != null && (includeAll || requested.Contains("structure"))) {
                    try { result["uiStructure"] = _uiService.GetSimplifiedUIStructure(obj); } catch {}
                }

                // 5.0.1 SDT items — surfaces the structural items of an SDT so the agent can
                // confirm field names/types/lengths from inspect without an additional
                // genexus_read part=Structure call. Friction-report 2026-05-13 #7.
                if ((includeAll || requested.Contains("structure")) &&
                    obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var sdtItems = BuildSdtItemsJson(obj);
                        if (sdtItems != null) result["sdtStructure"] = sdtItems;
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug("[Analyze] BuildSdtItemsJson failed for " + obj.Name + ": " + ex.Message);
                    }
                }

                // 5.1 Controls + valid events repertoire (opt-in: include=["controls"] or include=["events_repertoire"])
                if (_uiService != null && (requested.Contains("controls") || requested.Contains("events_repertoire"))) {
                    try { result["controls"] = _uiService.GetControlsRepertoire(obj); } catch {}
                }

                // 5.2 Navigation (For Each / Group base tables, indexes, filters) — opt-in only (heavy)
                if (_navigationService != null && requested.Contains("navigation"))
                {
                    try
                    {
                        string navJson = _navigationService.GetNavigation(obj.Name);
                        var navObj = Newtonsoft.Json.Linq.JObject.Parse(navJson);
                        if (navObj["error"] != null)
                            result["navigation"] = new JObject { ["error"] = navObj["error"] };
                        else
                            result["navigation"] = navObj;
                    }
                    catch (Exception ex)
                    {
                        result["navigation"] = new JObject { ["error"] = ex.Message };
                    }
                }

                // 6. Metadata (Sync)
                if (includeAll || requested.Contains("metadata")) result["wwpMetadata"] = GetWWPMetadata(obj);

                // 6.1 Available parts (Sync) — discovery aid
                if (includeAll || requested.Contains("parts"))
                {
                    try
                    {
                        var partsArr = new JArray();
                        var available = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                        foreach (var p in available) partsArr.Add(p);
                        result["availableParts"] = partsArr;
                    }
                    catch { }
                }

                // 7. Final Summary
                result["summary"] = GenerateSummary(obj, result);

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GenerateSummary(KBObject obj, JObject fullResult)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{obj.TypeDescriptor.Name} {obj.Name}: {obj.Description}. ");
                
                var parms = fullResult["parameters"] as JArray;
                if (parms != null && parms.Count > 0)
                    sb.Append($"Accepts {parms.Count} parameters. ");
                
                var vars = fullResult["variables"] as JArray;
                if (vars != null && vars.Count > 0)
                    sb.Append($"Uses {vars.Count} local variables. ");

                if (fullResult["uiStructure"] != null && fullResult["uiStructure"].Type != JTokenType.Null)
                    sb.Append("Has a user interface. ");

                if (fullResult["wwpMetadata"] != null && fullResult["wwpMetadata"].Type != JTokenType.Null)
                    sb.Append("Uses WorkWithPlus patterns. ");

                return sb.ToString().Trim();
            }
            catch { return $"{obj.TypeDescriptor.Name} {obj.Name}"; }
        }

        private JObject GetWWPMetadata(KBObject obj)
        {
            var wwp = new JObject();
            try {
                dynamic dObj = obj;
                if (dObj.Properties != null) {
                    foreach (dynamic prop in dObj.Properties) {
                        string name = prop.Name;
                        if (name == "Pattern") wwp["pattern"] = prop.Value?.ToString();
                        else if (name == "MasterPage") wwp["masterPage"] = prop.Value?.ToString();
                    }
                }

                bool isWWP = false;
                try
                {
                    if (obj.Parts.Cast<KBObjectPart>().Any(p =>
                        (p.TypeDescriptor?.Name ?? string.Empty).IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        isWWP = true;
                    }
                }
                catch { }
                if (!isWWP && !string.IsNullOrEmpty(obj.Description) &&
                    (obj.Description.IndexOf("WorkWithPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     obj.Description.IndexOf("WWP", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    isWWP = true;
                }
                wwp["isWorkWithPlusAware"] = isWWP;
            } catch {}
            return wwp;
        }

        private string GetPartSourceByName(KBObject obj, string name)
        {
            try {
                var part = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.TypeDescriptor.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (part is ISource source) return source.Source;
            } catch {}
            return null;
        }

        public string GetSignature(string name, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(name, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(name, _indexCacheService.GetIndex());

                var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["parmRule"] = parmRule;
                
                var parmArray = new JArray();
                foreach (var p in parms) {
                    parmArray.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                }
                result["parameters"] = parmArray;
                
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string TryGetDataProviderReturnsSdt(Artech.Genexus.Common.Objects.DataProvider dp)
        {
            // 1) Look for output() rule
            try
            {
                string rules = ((dynamic)dp).Rules?.Source as string ?? "";
                if (!string.IsNullOrEmpty(rules))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(rules,
                        @"\boutput\s*\(\s*&?(\w+)\s*\)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string varName = m.Groups[1].Value;
                        string sdtFromVar = TryResolveSdtFromVariable(dp, varName);
                        if (!string.IsNullOrEmpty(sdtFromVar)) return sdtFromVar;
                        return varName;
                    }
                }
            }
            catch { }

            // 2) Try direct properties exposed by the SDK
            foreach (string prop in new[] { "OutputType", "ReturnType", "ReturnTypeName" })
            {
                try
                {
                    var pi = dp.GetType().GetProperty(prop);
                    if (pi == null) continue;
                    var v = pi.GetValue(dp);
                    if (v != null)
                    {
                        string s = v.ToString();
                        if (!string.IsNullOrEmpty(s) && !s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return s;
                    }
                }
                catch { }
            }

            return null;
        }

        private string TryResolveSdtFromVariable(KBObject obj, string variableName)
        {
            if (string.IsNullOrEmpty(variableName)) return null;
            try
            {
                dynamic vPart = obj.Parts.Cast<KBObjectPart>()
                    .FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart", StringComparison.OrdinalIgnoreCase));
                if (vPart == null) return null;
                foreach (var v in vPart.Variables)
                {
                    try
                    {
                        string name = (string)((dynamic)v).Name;
                        if (!string.Equals(name, variableName, StringComparison.OrdinalIgnoreCase)) continue;

                        string sdtName = null;
                        try { sdtName = ((dynamic)v).PromptInformation?.SDTName as string; } catch { }
                        if (!string.IsNullOrEmpty(sdtName)) return sdtName;

                        string baseType = ((dynamic)v).Type?.ToString();
                        if (!string.IsNullOrEmpty(baseType) && baseType.IndexOf("SDT", StringComparison.OrdinalIgnoreCase) >= 0)
                            return baseType;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private List<string> ResolveDataProviderTablesRead(Artech.Genexus.Common.Objects.DataProvider dp)
        {
            var tables = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Use the resolved navigation report if available
            if (_navigationService != null)
            {
                try
                {
                    string navJson = _navigationService.GetNavigation(dp.Name);
                    var nav = JObject.Parse(navJson);
                    if (nav["error"] == null && nav["levels"] is JArray levels)
                    {
                        foreach (var l in levels)
                        {
                            string t = (string)l["baseTable"];
                            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t)) tables.Add(t);
                        }
                    }
                }
                catch { }
            }

            // 2) Fallback: enriched index entry's Tables list (filter to actual Tables only)
            if (tables.Count == 0)
            {
                try
                {
                    var index = _indexCacheService.GetIndex();
                    if (index.Objects.TryGetValue($"DataProvider:{dp.Name}", out var entry) && entry?.Tables != null)
                    {
                        foreach (var name in entry.Tables)
                        {
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (index.Objects.TryGetValue($"Table:{name}", out _) && seen.Add(name))
                                tables.Add(name);
                        }
                    }
                }
                catch { }
            }

            return tables;
        }

        public string ExplainCode(string target, string codeSnippet)
        {
            try
            {
                // This is a placeholder for AI-powered explanation.
                // In a real scenario, this would call LLM.
                return "{\"explanation\":\"Code analysis simulation\",\"originalCode\":\"" + CommandDispatcher.EscapeJsonString(codeSnippet ?? "") + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private static readonly Guid SDT_STRUCTURE_PART_GUID = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        // Walk an SDT's structural items via reflection over the SDK's SDTLevel surface and
        // produce a JSON-friendly tree the agent can inspect without a follow-up read call.
        // Returns null when the SDT structure part can't be located so callers fall back to
        // their existing surfaces; callers also surface the DSL text for human eyes.
        private static JObject BuildSdtItemsJson(KBObject sdt)
        {
            if (sdt == null) return null;

            KBObjectPart structure = null;
            foreach (KBObjectPart p in sdt.Parts)
            {
                try
                {
                    string descName = p.TypeDescriptor?.Name ?? "";
                    string className = p.GetType().Name;
                    if (p.Type == SDT_STRUCTURE_PART_GUID ||
                        descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        structure = p;
                        break;
                    }
                }
                catch { }
            }
            if (structure == null) return null;

            dynamic ds = structure;
            dynamic root = null;
            try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }
            if (root == null) return null;

            var items = new JArray();
            try
            {
                foreach (dynamic child in root.Items)
                {
                    var node = BuildSdtItemNode(child);
                    if (node != null) items.Add(node);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[Analyze] SDT item walk failed: " + ex.Message);
            }

            int leafCount = 0, levelCount = 0;
            CountSdtItems(items, ref leafCount, ref levelCount);

            return new JObject
            {
                ["itemCount"] = leafCount,
                ["levelCount"] = levelCount,
                ["items"] = items
            };
        }

        private static void CountSdtItems(JArray items, ref int leafCount, ref int levelCount)
        {
            if (items == null) return;
            foreach (var it in items)
            {
                var children = it["children"] as JArray;
                if (children != null && children.Count > 0)
                {
                    levelCount++;
                    CountSdtItems(children, ref leafCount, ref levelCount);
                }
                else
                {
                    leafCount++;
                }
            }
        }

        private static JObject BuildSdtItemNode(dynamic item)
        {
            if (item == null) return null;
            var node = new JObject();
            try { node["name"] = (string)item.Name; } catch { node["name"] = ""; }

            // Detect compound (level) vs leaf. SDK exposes IsLeafItem on most builds; some
            // older surfaces hide it, so fall back to a child probe.
            bool isLeaf;
            try { isLeaf = (bool)item.IsLeafItem; }
            catch
            {
                bool hasChild = false;
                try { foreach (var _ in item.Items) { hasChild = true; break; } } catch { }
                isLeaf = !hasChild;
            }

            try { node["isCollection"] = (bool)item.IsCollection; } catch { node["isCollection"] = false; }

            if (isLeaf)
            {
                string typeStr = null;
                try { typeStr = item.Type != null ? item.Type.ToString() : null; } catch { }
                if (!string.IsNullOrEmpty(typeStr)) node["type"] = typeStr;

                // Length / Decimals are exposed on most SDTItem surfaces; harvest defensively.
                try { object len = item.Length; if (len != null) node["length"] = Convert.ToInt32(len); } catch { }
                try { object dec = item.Decimals; if (dec != null) node["decimals"] = Convert.ToInt32(dec); } catch { }
                try { object sig = item.Signed; if (sig != null) node["signed"] = Convert.ToBoolean(sig); } catch { }
                node["isLevel"] = false;
            }
            else
            {
                node["isLevel"] = true;
                var children = new JArray();
                try
                {
                    foreach (dynamic c in item.Items)
                    {
                        var sub = BuildSdtItemNode(c);
                        if (sub != null) children.Add(sub);
                    }
                }
                catch { }
                node["children"] = children;
            }

            return node;
        }
    }
}
