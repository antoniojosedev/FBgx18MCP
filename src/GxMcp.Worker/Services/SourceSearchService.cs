using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class SourceSearchCriteria
    {
        public string Callee { get; set; }
        public Dictionary<int, string> ArgMatches { get; set; }
        public string Pattern { get; set; }
        public bool CaseSensitive { get; set; }
        public string TypeFilter { get; set; }
        public List<string> Scope { get; set; } = new List<string> { "source" };
        public int MaxResults { get; set; } = 50;
        public bool IncludeComments { get; set; }
        // v2.3.8 (Task 2.1): hard wall-clock cap. Distinct from the legacy
        // internal 25s budget — when exceeded we return a structured Timeout
        // envelope with partial hits, never a silently empty result.
        public int TimeoutMs { get; set; } = 30000;
    }

    public class SourceSearchService
    {
        private readonly IndexCacheService _index;
        private readonly ObjectService _objectService;

        public SourceSearchService(IndexCacheService index, ObjectService objectService)
        {
            _index = index;
            _objectService = objectService;
        }

        public string SearchAsJson(SourceSearchCriteria c)
        {
            // v2.3.8 (Task 2.1): surface index readiness as a structured envelope
            // BEFORE touching the body of SearchCore — keeping the envelope check
            // in a separate method that doesn't reference KBObject types means
            // unit tests with a Cold index never trigger JIT-time resolution of
            // Artech.Architecture.Common, so they can run without the GeneXus
            // install on the probing path.
            var state = _index != null ? _index.GetState() : null;
            if (state != null && state.Status != "Ready")
            {
                var envelope = new JObject
                {
                    ["status"] = string.Equals(state.Status, "Reindexing", StringComparison.OrdinalIgnoreCase)
                        ? "Reindexing"
                        : "IndexCold",
                    ["retryAfterMs"] = state.EtaMs ?? 5000
                };
                if (state.Progress.HasValue) envelope["progress"] = state.Progress.Value;
                return envelope.ToString();
            }
            return SearchCore(c);
        }

        private string SearchCore(SourceSearchCriteria c)
        {
            try
            {
                if (string.IsNullOrEmpty(c.Callee) && string.IsNullOrEmpty(c.Pattern))
                    return "{\"error\":\"Provide 'callee' (semantic) or 'pattern' (regex).\"}";

                Regex rx = null;
                if (!string.IsNullOrEmpty(c.Pattern))
                {
                    var opts = RegexOptions.Compiled;
                    if (!c.CaseSensitive) opts |= RegexOptions.IgnoreCase;
                    rx = new Regex(c.Pattern, opts);
                }

                var hits = new JArray();
                var index = _index.GetIndex();

                // Pre-filter by literal tokens against the index so we skip FindObject for
                // entries that demonstrably reference none of them.
                var literals = ExtractLiteralTokens(c.Pattern, c.Callee);

                var entries = index.Objects.Values
                    .Where(e => e.Type == "Procedure" || e.Type == "DataProvider" || e.Type == "WebPanel" || e.Type == "Transaction")
                    .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
                    .Where(e => MatchesAnyLiteral(e, literals))
                    .ToList();

                // v2.3.8 (Task 2.1): hard wall-clock timeout — emits a Timeout
                // envelope with partial hits, replacing the legacy budgetExceeded
                // flag. The 25s internal cap is now driven by c.TimeoutMs (default
                // 30s) so callers can tune the budget per-call.
                int timeoutMs = c.TimeoutMs > 0 ? c.TimeoutMs : 0;
                var swBudget = System.Diagnostics.Stopwatch.StartNew();

                int produced = 0;
                int scanned = 0;
                foreach (var e in entries)
                {
                    if (produced >= c.MaxResults) break;
                    if (swBudget.ElapsedMilliseconds > timeoutMs)
                    {
                        var timeoutEnv = new JObject
                        {
                            ["status"] = "Timeout",
                            ["partialHits"] = hits,
                            ["totalScanned"] = scanned,
                            ["totalObjects"] = entries.Count,
                            ["timeoutMs"] = timeoutMs
                        };
                        return timeoutEnv.ToString();
                    }
                    scanned++;
                    KBObject obj;
                    try { obj = _objectService.FindObject(e.Name); } catch { continue; }
                    if (obj == null) continue;

                    foreach (var part in c.Scope ?? new List<string> { "source" })
                    {
                        if (produced >= c.MaxResults) break;
                        string src = TryGetPartSource(obj, part);
                        if (string.IsNullOrEmpty(src)) continue;
                        var lines = src.Split('\n');

                        if (!string.IsNullOrEmpty(c.Callee))
                        {
                            foreach (var call in SourceParser.ParseCalls(src, c.IncludeComments))
                            {
                                if (!CalleeMatches(call.Callee, c.Callee)) continue;
                                if (c.ArgMatches != null && !ArgsMatch(call.Args, c.ArgMatches)) continue;
                                if (rx != null)
                                {
                                    string ln = call.LineNumber - 1 < lines.Length ? lines[call.LineNumber - 1] : "";
                                    if (!rx.IsMatch(ln)) continue;
                                }
                                hits.Add(BuildHit(e, part, lines, call.LineNumber, call));
                                produced++;
                                if (produced >= c.MaxResults) break;
                            }
                        }
                        else if (rx != null)
                        {
                            for (int li = 0; li < lines.Length && produced < c.MaxResults; li++)
                            {
                                if (rx.IsMatch(lines[li]))
                                {
                                    hits.Add(BuildHit(e, part, lines, li + 1, null));
                                    produced++;
                                }
                            }
                        }
                    }
                }

                var response = new JObject
                {
                    ["status"] = "Ready",
                    ["count"] = produced,
                    ["truncated"] = produced >= c.MaxResults,
                    ["hits"] = hits
                };
                if (hits.Count > 0 && hits[0] is JObject topHit)
                {
                    response["_meta"] = new JObject
                    {
                        ["suggested_next"] = new JObject
                        {
                            ["tool"] = "genexus_read",
                            ["args"] = new JObject
                            {
                                ["name"] = topHit["objectName"]?.ToString(),
                                ["type"] = topHit["type"]?.ToString()
                            }
                        }
                    };
                }
                return response.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Alphanumeric runs >=3 chars; final regex.IsMatch still gates output so a
        // permissive pre-filter is safe.
        private static System.Collections.Generic.List<string> ExtractLiteralTokens(string pattern, string callee)
        {
            var toks = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(callee))
                foreach (Match m in Regex.Matches(callee, @"[A-Za-z0-9_]{3,}")) toks.Add(m.Value);
            if (!string.IsNullOrEmpty(pattern))
                foreach (Match m in Regex.Matches(pattern, @"[A-Za-z0-9_]{3,}")) toks.Add(m.Value);
            return toks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool MatchesAnyLiteral(Models.SearchIndex.IndexEntry e, System.Collections.Generic.List<string> literals)
        {
            if (literals == null || literals.Count == 0) return true;
            string snip = e.SourceSnippet ?? "";
            string nm = e.Name ?? "";
            foreach (var t in literals)
            {
                if (snip.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (nm.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (e.Keywords != null)
                {
                    for (int i = 0; i < e.Keywords.Count; i++)
                        if (e.Keywords[i] != null && e.Keywords[i].IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        private static bool CalleeMatches(string actual, string wanted)
        {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(wanted)) return false;
            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = actual.LastIndexOf('.');
            if (dot >= 0 && string.Equals(actual.Substring(dot + 1), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ArgsMatch(List<string> args, Dictionary<int, string> wanted)
        {
            foreach (var kv in wanted)
            {
                if (kv.Key < 0 || kv.Key >= args.Count) return false;
                if (!string.Equals(NormalizeLiteral(args[kv.Key]), NormalizeLiteral(kv.Value), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string NormalizeLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2);
            return s;
        }

        private static JObject BuildHit(SearchIndex.IndexEntry e, string part, string[] lines, int line, ParsedCall call)
        {
            const int ctx = 3;
            int idx = line - 1;
            string lineText = idx >= 0 && idx < lines.Length ? lines[idx] : "";

            var before = new JArray();
            for (int i = Math.Max(0, idx - ctx); i < idx; i++) before.Add(lines[i]);
            var after = new JArray();
            for (int i = idx + 1; i < Math.Min(lines.Length, idx + 1 + ctx); i++) after.Add(lines[i]);

            var hit = new JObject
            {
                ["objectName"] = e.Name,
                ["type"] = e.Type,
                ["part"] = part,
                ["lineNumber"] = line,
                ["lineText"] = lineText,
                ["contextBefore"] = before,
                ["contextAfter"] = after
            };
            if (call != null)
            {
                var argsArr = new JArray();
                foreach (var a in call.Args) argsArr.Add(a);
                hit["matchedCall"] = new JObject { ["callee"] = call.Callee, ["args"] = argsArr };
            }
            return hit;
        }

        private static string TryGetPartSource(KBObject obj, string partName)
        {
            try
            {
                if (string.Equals(partName, "source", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sp = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
                    return sp?.Source ?? "";
                }
                if (string.Equals(partName, "rules", StringComparison.OrdinalIgnoreCase))
                {
                    try { return ((dynamic)obj).Rules?.Source ?? ""; } catch { return ""; }
                }
                if (string.Equals(partName, "conditions", StringComparison.OrdinalIgnoreCase))
                {
                    try { return ((dynamic)obj).Conditions?.Source ?? ""; } catch { return ""; }
                }
                if (string.Equals(partName, "events", StringComparison.OrdinalIgnoreCase))
                {
                    try { return ((dynamic)obj).Events?.Source ?? ""; } catch { return ""; }
                }
            }
            catch { }
            return "";
        }
    }
}
