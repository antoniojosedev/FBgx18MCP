using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class NavigationSqlService
    {
        private readonly NavigationService _navigation;

        public NavigationSqlService(NavigationService navigation)
        {
            _navigation = navigation;
        }

        public string Generate(string objectName, int? levelNumber = null)
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

                    var (where, parms, levelWarnings) = BuildWhere(l, num);
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

                    queries.Add(new JObject
                    {
                        ["level"] = num,
                        ["baseTable"] = baseTable,
                        ["indexUsed"] = (string)l["index"],
                        ["sql"] = sql.ToString(),
                        ["parametersExpected"] = parmsArr
                    });
                }

                return new JObject
                {
                    ["name"] = objectName,
                    ["queries"] = queries,
                    ["warnings"] = warnings
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private (string where, List<string> parms, List<string> warnings) BuildWhere(JToken level, int levelNum)
        {
            var parms = new List<string>();
            var warnings = new List<string>();

            var filtersArr = level["filters"] as JArray;
            if (filtersArr == null || filtersArr.Count == 0)
            {
                warnings.Add($"Level {levelNum}: OptimizedWhere not surfaced; SQL emitted without filters.");
                return ("", parms, warnings);
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
                }
                else if (!string.IsNullOrWhiteSpace(raw))
                {
                    clause = ReplaceVarsWithBinds(raw, parms);
                }

                if (!string.IsNullOrWhiteSpace(clause)) clauses.Add(clause);
            }

            return (string.Join(" AND ", clauses), parms, warnings);
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
