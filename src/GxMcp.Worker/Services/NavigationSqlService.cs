using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class NavigationSqlService
    {
        private readonly NavigationService _navigation;
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public NavigationSqlService(NavigationService navigation)
            : this(navigation, null, null)
        {
        }

        public NavigationSqlService(NavigationService navigation, KbService kbService, ObjectService objectService)
        {
            _navigation = navigation;
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Generate(string objectName, int? levelNumber = null)
            => Generate(objectName, levelNumber, includeExecutionPlan: false, includeIndexAdvisor: false);

        public string Generate(string objectName, int? levelNumber, bool includeExecutionPlan, bool includeIndexAdvisor)
        {
            try
            {
                string navJson = _navigation.GetNavigation(objectName);
                var nav = JObject.Parse(navJson);
                if (nav["error"] != null) return navJson; // pass through

                var queries = new JArray();
                var warnings = new JArray();

                var levels = nav["levels"] as JArray ?? new JArray();
                foreach (var l in levels)
                {
                    int num = l["number"]?.ToObject<int?>() ?? 0;
                    if (levelNumber.HasValue && num != levelNumber.Value) continue;

                    string baseTable = (string)l["baseTable"];
                    if (string.IsNullOrEmpty(baseTable))
                    {
                        warnings.Add($"Level {num}: no base table");
                        continue;
                    }

                    var (where, parms, levelWarnings, structuredFilters) = BuildWhere(l, num);
                    foreach (var w in levelWarnings) warnings.Add(w);

                    var sql = new StringBuilder();
                    sql.Append("SELECT * FROM ").Append(baseTable);
                    if (!string.IsNullOrEmpty(where)) sql.Append(" WHERE ").Append(where);

                    var orderArr = l["order"] as JArray;
                    if (orderArr != null && orderArr.Count > 0)
                    {
                        var orderCols = orderArr.Select(o => (string)o).Where(s => !string.IsNullOrWhiteSpace(s));
                        if (orderCols.Any())
                            sql.Append(" ORDER BY ").Append(string.Join(", ", orderCols));
                    }

                    var parmsArr = new JArray();
                    foreach (var p in parms) parmsArr.Add(p);

                    var q = new JObject
                    {
                        ["level"] = num,
                        ["baseTable"] = baseTable,
                        ["indexUsed"] = (string)l["index"],
                        ["sql"] = sql.ToString(),
                        ["parametersExpected"] = parmsArr,
                    };
                    if (structuredFilters != null && structuredFilters.Count > 0)
                        q["filters"] = structuredFilters;
                    queries.Add(q);
                }

                var result = new JObject
                {
                    ["name"] = objectName,
                    ["queries"] = queries,
                    ["warnings"] = warnings,
                };

                // Item 34: optional EXPLAIN annotation per query. Always
                // planUnavailable=true here — the worker has no DB connection.
                if (includeExecutionPlan)
                {
                    int dbmsType = TryGetDbmsType();
                    ExecutionPlanFetcher.AttachExecutionPlans(queries, dbmsType);
                    result["dbmsFamily"] = ExecutionPlanFetcher.ResolveDbmsFamily(dbmsType);
                }

                // Item 44: heuristic index advisor.
                if (includeIndexAdvisor)
                {
                    var existing = CollectExistingIndexes(queries);
                    result["indexAdvisor"] = IndexAdvisor.BuildAdvisor(queries, existing);
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private int TryGetDbmsType()
        {
            try
            {
                if (_kbService == null) return 0;
                dynamic kb = _kbService.GetKB();
                if (kb == null) return 0;
                dynamic ds = ((dynamic)kb.DesignModel.Environment.TargetModel).DataStore;
                if (ds != null && ds.Dbms != 0) return (int)ds.Dbms;
            }
            catch { }
            return 0;
        }

        private IDictionary<string, JArray> CollectExistingIndexes(JArray queries)
        {
            var map = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            if (_objectService == null) return map;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in queries)
            {
                string baseTable = (string)q["baseTable"];
                if (string.IsNullOrEmpty(baseTable) || !seen.Add(baseTable)) continue;
                try
                {
                    var tbl = _objectService.FindObject(baseTable) as Artech.Genexus.Common.Objects.Table;
                    if (tbl == null) { map[baseTable] = new JArray(); continue; }
                    var arr = new JArray();
                    dynamic dIndexesPart = ((dynamic)tbl).TableIndexes;
                    if (dIndexesPart != null && dIndexesPart.Indexes != null)
                    {
                        foreach (dynamic idxObj in dIndexesPart.Indexes)
                        {
                            dynamic idx = idxObj.Index; if (idx == null) continue;
                            var cols = new JArray();
                            if (idx.IndexStructure != null && idx.IndexStructure.Members != null)
                            {
                                foreach (dynamic m in idx.IndexStructure.Members)
                                {
                                    string n = m.Attribute != null ? (string)m.Attribute.Name : (string)m.Name;
                                    if (!string.IsNullOrEmpty(n)) cols.Add(n);
                                }
                            }
                            arr.Add(new JObject { ["name"] = (string)idx.Name, ["columns"] = cols });
                        }
                    }
                    map[baseTable] = arr;
                }
                catch
                {
                    map[baseTable] = new JArray();
                }
            }
            return map;
        }

        private (string where, List<string> parms, List<string> warnings, JArray structuredFilters) BuildWhere(JToken level, int levelNum)
        {
            var parms = new List<string>();
            var warnings = new List<string>();
            var structured = new JArray();

            var filtersArr = level["filters"] as JArray;
            if (filtersArr == null || filtersArr.Count == 0)
            {
                warnings.Add($"Level {levelNum}: OptimizedWhere not surfaced; SQL emitted without filters.");
                return ("", parms, warnings, structured);
            }

            var clauses = new List<string>();
            foreach (var f in filtersArr)
            {
                // Prefer structured fields if surfaced; else fall back to raw expression
                string attribute = (string)f["attribute"];
                string op = (string)f["op"];
                string value = (string)f["value"];
                string raw = (string)f["expression"];

                string clause = null;
                if (!string.IsNullOrWhiteSpace(attribute) && !string.IsNullOrWhiteSpace(op))
                {
                    string rhs = string.IsNullOrWhiteSpace(value) ? "?" : ReplaceVarsWithBinds(value, parms);
                    clause = $"{attribute} {op} {rhs}";
                    structured.Add(new JObject { ["attribute"] = attribute, ["op"] = op });
                }
                else if (!string.IsNullOrWhiteSpace(raw))
                {
                    clause = ReplaceVarsWithBinds(raw, parms);
                }

                if (!string.IsNullOrWhiteSpace(clause)) clauses.Add(clause);
            }

            return (string.Join(" AND ", clauses), parms, warnings, structured);
        }

        private static string ReplaceVarsWithBinds(string input, List<string> parms)
        {
            // Replace &VarName with :VarName and collect names. Idempotent on already-bind syntax.
            return System.Text.RegularExpressions.Regex.Replace(input ?? "", @"&(\w+)", m =>
            {
                string name = m.Groups[1].Value;
                if (!parms.Contains(name)) parms.Add(name);
                return ":" + name;
            });
        }
    }
}
