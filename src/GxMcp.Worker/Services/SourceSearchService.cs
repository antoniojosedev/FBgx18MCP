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
        /// <summary>
        /// Item 22: wider field search. Values: source (default), caption,
        /// description, parmNames. When any non-source value is present the
        /// search scans that metadata field instead of / in addition to source.
        /// </summary>
        public List<string> Fields { get; set; } = null; // null = default [source]
        public int MaxResults { get; set; } = 50;
        public bool IncludeComments { get; set; }
        // v2.3.8 (Task 2.1): hard wall-clock cap. Distinct from the legacy
        // internal 25s budget — when exceeded we return a structured Timeout
        // envelope with partial hits, never a silently empty result.
        public int TimeoutMs { get; set; } = 30000;

        // Issue #27 item 4: scope the scan to specific object(s) by exact name
        // (comma/semicolon-separated, case-insensitive). When set, only those
        // objects are read — a search inside one known Procedure becomes
        // O(object) instead of O(KB), and bypasses the base type whitelist so any
        // searchable object type can be targeted directly.
        public string ObjectName { get; set; }

        // Issue #27 item 4: resume cursor. On Timeout the response returns a
        // nextCursor; passing it back as StartIndex resumes the scan where it
        // stopped instead of rescanning from the top.
        public int StartIndex { get; set; } = 0;
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
            return SearchAsJson(c, System.Threading.CancellationToken.None);
        }

        // v2.3.8 (post-Task 7.2 fix): worker-side cancellation. The gateway's
        // BackgroundJobRegistry.RegisterCancellation gives us a token that the
        // assistant trips via lifecycle action=cancel + job_id. Plumbing it
        // here means a slow regex over 24k entries actually stops mid-loop
        // instead of running to completion while the gateway poller exits.
        public string SearchAsJson(SourceSearchCriteria c, System.Threading.CancellationToken ct)
        {
            // v2.3.8 (Task 2.1): surface index readiness as a structured envelope
            // BEFORE touching the body of SearchCore — keeping the envelope check
            // in a separate method that doesn't reference KBObject types means
            // unit tests with a Cold index never trigger JIT-time resolution of
            // Artech.Architecture.Common, so they can run without the GeneXus
            // install on the probing path.
            var state = _index != null ? _index.GetState() : null;
            string status = state?.Status ?? "Ready";
            // issue #25 #1 / #3: only a Cold/Reindexing index is genuinely
            // unsearchable (nothing stable in memory yet). The lite-walk states
            // (LiteReady/UltraLiteReady/Enriching) hold a growing-but-stable set
            // of entries whose full source we can read on demand — search them
            // and flag the result partial rather than hard-blocking. That keeps
            // search usable while the walk runs, and a zero result on a partial
            // index is reported with a DISTINCT code (never a plain empty-success)
            // so the caller can't read it as "token does not exist".
            bool cold = string.Equals(status, "Cold", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase);
            if (cold)
            {
                string indexCode = string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase)
                    ? "Reindexing" : "IndexCold";
                var indexResult = new JObject { ["retryAfterMs"] = state?.EtaMs ?? 5000 };
                if (state?.Progress != null) indexResult["progress"] = state.Progress.Value;
                return Models.McpResponse.Ok(code: indexCode, result: indexResult);
            }
            bool partial = !string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase);
            return SearchCore(c, ct, partial, status);
        }

        private string SearchCore(SourceSearchCriteria c, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken),
            bool partialIndex = false, string indexStatus = "Ready")
        {
            try
            {
                if (string.IsNullOrEmpty(c.Callee) && string.IsNullOrEmpty(c.Pattern))
                    return Models.McpResponse.Err(code: "MissingCriteria", message: "Provide 'callee' (semantic) or 'pattern' (regex).");

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

                // The literal pre-filter only sees indexed text (SourceSnippet/Name/Keywords),
                // which never contains the WebForm XML. A WebForm scope scan would therefore
                // be pre-filtered away before its part is ever read, so skip the pre-filter
                // when the caller asked for the webForm/layout part.
                bool scopeTouchesWebForm = (c.Scope ?? new List<string> { "source" })
                    .Any(s => string.Equals(s, "webForm", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s, "layout", StringComparison.OrdinalIgnoreCase));

                // Issue #27 item 4: an explicit objectName scope restricts the scan to
                // those exact objects (bypassing both the base type whitelist and the
                // literal pre-filter), so a search inside one known object is O(object).
                var objectNameSet = ParseObjectNames(c.ObjectName);

                IEnumerable<Models.SearchIndex.IndexEntry> query = index.Objects.Values;
                if (objectNameSet != null)
                {
                    // issue #36.7 — tolerate module-qualified vs bare names in EITHER direction
                    // so passing "Foo" finds "MyModule.Foo" and vice-versa (exact match was too
                    // strict and quietly yielded an empty set that looked like a full-KB scan).
                    query = query.Where(e => ObjectNameMatches(objectNameSet, e.Name));
                }
                else
                {
                    query = query
                        .Where(e => e.Type == "Procedure" || e.Type == "DataProvider" || e.Type == "WebPanel" || e.Type == "Transaction")
                        .Where(e => scopeTouchesWebForm || MatchesAnyLiteral(e, literals));
                }
                var entries = query
                    .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // issue #36.7 — an objectName scope that resolved to zero entries must say so
                // explicitly, not silently return "no hits" (indistinguishable from "found
                // nothing in the object", and the symptom reporters read as "filter ignored").
                if (objectNameSet != null && entries.Count == 0)
                {
                    return Models.McpResponse.Ok(code: "ObjectNameNoMatch", result: new JObject
                    {
                        ["hits"] = new JArray(),
                        ["totalObjects"] = 0,
                        ["requestedObjectNames"] = new JArray(objectNameSet.ToArray()),
                        ["note"] = "objectName matched no objects in the index. Names match module-qualified OR bare; if you expected a hit, confirm the object exists and the index is Ready (genexus_lifecycle action=status)."
                    });
                }

                // v2.3.8 (Task 2.1): hard wall-clock timeout — emits a Timeout
                // envelope with partial hits, replacing the legacy budgetExceeded
                // flag. The 25s internal cap is now driven by c.TimeoutMs (default
                // 30s) so callers can tune the budget per-call.
                int timeoutMs = c.TimeoutMs > 0 ? c.TimeoutMs : 0;
                var swBudget = System.Diagnostics.Stopwatch.StartNew();

                int produced = 0;
                int scanned = 0;
                // Issue #27 item 4: index-addressable loop so a Timeout/Cancel can report a
                // resumable nextCursor (the absolute entry index reached).
                int startIndex = c.StartIndex > 0 ? c.StartIndex : 0;
                int cursor = startIndex;
                for (cursor = startIndex; cursor < entries.Count; cursor++)
                {
                    var e = entries[cursor];
                    if (produced >= c.MaxResults) break;
                    if (ct.IsCancellationRequested)
                    {
                        return Models.McpResponse.Ok(code: "Cancelled", result: new JObject
                        {
                            ["partialHits"] = hits,
                            ["totalScanned"] = scanned,
                            ["totalObjects"] = entries.Count,
                            ["nextCursor"] = cursor,
                            ["resumeHint"] = "Pass startIndex=nextCursor to resume this scan where it stopped."
                        });
                    }
                    if (swBudget.ElapsedMilliseconds > timeoutMs)
                    {
                        int pct = entries.Count > 0 ? (int)(100L * cursor / entries.Count) : 100;
                        return Models.McpResponse.Ok(code: "Timeout", result: new JObject
                        {
                            ["partialHits"] = hits,
                            ["totalScanned"] = scanned,
                            ["totalObjects"] = entries.Count,
                            ["coveragePercent"] = pct,
                            ["timeoutMs"] = timeoutMs,
                            ["nextCursor"] = cursor,
                            // Full-source scan reads each object's source via the SDK (~tens of ms
                            // each), so a whole-KB scan spans many budget windows. Prefer scoping
                            // for an instant search; resume only for an exhaustive sweep.
                            ["resumeHint"] = $"Scanned {pct}% ({cursor}/{entries.Count}). FASTEST: narrow the scan — objectName=\"A,B\" (searches only those, O(objects)), or typeFilter / pathPrefix. To keep sweeping the whole KB, resume with startIndex={cursor} (optionally a larger timeoutMs)."
                        });
                    }
                    scanned++;
                    KBObject obj;
                    // Pass the type so FindObject takes the O(1) typed fast path (Type:Name index
                    // hit → Objects.Get(guid)) instead of the global, type-iterating search — the
                    // scan already has the entry's exact type, so re-resolving by bare name per
                    // object was needless overhead across thousands of objects.
                    try { obj = _objectService.FindObject(e.Name, e.Type); } catch { continue; }
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

                // Item 22: fields=[caption,description,parmNames] — metadata-only search.
                // Only runs when Fields contains non-source values AND a pattern is supplied.
                var extraFields = c.Fields != null
                    ? c.Fields.Where(f => !string.Equals(f, "source", StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<string>();
                if (extraFields.Count > 0 && rx != null)
                {
                    var allEntries = index.Objects.Values
                        .Where(e => string.IsNullOrEmpty(c.TypeFilter) || string.Equals(e.Type, c.TypeFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var e in allEntries)
                    {
                        if (produced >= c.MaxResults) break;
                        if (ct.IsCancellationRequested) break;
                        if (swBudget.ElapsedMilliseconds > timeoutMs) break;

                        foreach (var field in extraFields)
                        {
                            if (produced >= c.MaxResults) break;
                            string fieldValue = null;
                            if (string.Equals(field, "description", StringComparison.OrdinalIgnoreCase))
                                fieldValue = e.Description;
                            else if (string.Equals(field, "caption", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(field, "parmNames", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(field, "webForm", StringComparison.OrdinalIgnoreCase))
                            {
                                // Caption / parmNames / webForm require SDK access
                                KBObject obj2 = null;
                                try { obj2 = _objectService.FindObject(e.Name); } catch { }
                                if (obj2 == null) continue;
                                if (string.Equals(field, "caption", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        dynamic dyn = obj2;
                                        fieldValue = dyn?.Form?.Caption?.ToString() ?? dyn?.Caption?.ToString() ?? "";
                                    }
                                    catch { fieldValue = ""; }
                                }
                                else if (string.Equals(field, "webForm", StringComparison.OrdinalIgnoreCase))
                                {
                                    // webForm — scan the WebForm XML (WebPanel / Transaction layouts).
                                    // Opt-in via fields=[webForm] because the XML can be large; we
                                    // never load it on the default code-search path. Reuses the
                                    // same read path as WriteService / PatchService via
                                    // WebFormXmlHelper.ReadEditableXml.
                                    try { fieldValue = GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj2) ?? ""; }
                                    catch { fieldValue = ""; }
                                }
                                else // parmNames — scan Rules part for 'parm(' signature
                                {
                                    try
                                    {
                                        string rulesSrc = TryGetPartSource(obj2, "rules");
                                        if (!string.IsNullOrEmpty(rulesSrc))
                                        {
                                            var parmMatch = System.Text.RegularExpressions.Regex.Match(
                                                rulesSrc, @"parm\s*\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                            fieldValue = parmMatch.Success ? parmMatch.Groups[1].Value : "";
                                        }
                                    }
                                    catch { fieldValue = ""; }
                                }
                            }
                            if (string.IsNullOrEmpty(fieldValue)) continue;
                            if (rx.IsMatch(fieldValue))
                            {
                                hits.Add(new JObject
                                {
                                    ["objectName"] = e.Name,
                                    ["type"] = e.Type,
                                    ["field"] = field,
                                    ["matchedValue"] = fieldValue
                                });
                                produced++;
                            }
                        }
                    }
                }

                bool truncated = produced >= c.MaxResults;
                // Issue #27 item 4: when maxResults truncated the scan mid-list, expose the
                // entry cursor so the caller can page forward through the same object set.
                bool hasMoreEntries = truncated && cursor < entries.Count;
                var resultPayload = new JObject
                {
                    ["count"] = produced,
                    ["truncated"] = truncated,
                    ["hits"] = hits,
                    // issue #25 #1/#3: make index coverage explicit so a zero count is
                    // never mistaken for "does not exist" when the walk is still running.
                    ["indexStatus"] = indexStatus,
                    ["partial"] = partialIndex,
                    ["scannedObjects"] = scanned,
                    ["totalObjects"] = entries.Count,
                    // v2.8.0: canonical pagination block — total is now the scoped object count.
                    ["pagination"] = new JObject
                    {
                        ["offset"]     = startIndex,
                        ["limit"]      = c.MaxResults,
                        ["returned"]   = produced,
                        ["total"]      = entries.Count,
                        ["hasMore"]    = hasMoreEntries,
                        ["nextOffset"] = hasMoreEntries ? (JToken)cursor : JValue.CreateNull()
                    }
                };
                if (hasMoreEntries) resultPayload["nextCursor"] = cursor;
                if (partialIndex)
                {
                    resultPayload["partialHint"] = "Index walk is still in progress; this scan covered only the objects walked so far. A zero or small count does NOT mean the token is absent — re-run when whoami reports indexStatus=Ready.";
                }
                if (hits.Count > 0 && hits[0] is JObject topHit)
                {
                    resultPayload["_meta"] = new JObject
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
                // A zero-hit result on a partial index gets its own code so callers
                // cannot read it as an authoritative "not found" empty-success.
                string completionCode = (partialIndex && produced == 0)
                    ? "PartialIndexNoMatch" : "SourceSearchCompleted";
                return Models.McpResponse.Ok(code: completionCode, result: resultPayload);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "SourceSearchFailed", message: ex.Message);
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

        // Issue #27 item 4: parse the objectName scope (comma/semicolon/newline-separated)
        // into a case-insensitive set, or null when no scope was supplied.
        private static HashSet<string> ParseObjectNames(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return null;
            var names = objectName
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0);
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return set.Count > 0 ? set : null;
        }

        // issue #36.7 — match an object name against the requested set, tolerant of
        // module qualification on EITHER side (index-qualified vs user-bare and vice-versa).
        private static bool ObjectNameMatches(HashSet<string> wanted, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (wanted.Contains(name)) return true;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && wanted.Contains(name.Substring(dot + 1))) return true; // index qualified, user bare
            foreach (var w in wanted)
            {
                int wd = w.LastIndexOf('.'); // user qualified, index bare
                if (wd >= 0 && string.Equals(w.Substring(wd + 1), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool MatchesAnyLiteral(Models.SearchIndex.IndexEntry e, System.Collections.Generic.List<string> literals)
        {
            if (literals == null || literals.Count == 0) return true;
            // issue #25 #3: the literal pre-filter is only SOUND when the index
            // actually holds this entry's body text. SourceSnippet/Keywords are
            // never populated for Procedure/DataProvider/WebPanel/Transaction
            // (the types searched here), so treating an empty snippet as
            // "no match" silently drops entries whose full source contains the
            // token — a false empty-success. When there is no indexed text to
            // prove absence, include the entry so its full source gets read.
            bool hasIndexedText = !string.IsNullOrEmpty(e.SourceSnippet)
                || (e.Keywords != null && e.Keywords.Count > 0);
            if (!hasIndexedText) return true;
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
                // WebForm / Layout — the visual XML of WebPanels/Transactions. Not an
                // ISource part, so it has to be read through WebFormXmlHelper. Searching it
                // via scope lets callers grep control names, captions, classes and bindings
                // with the same line-numbered context as a source scan, instead of the
                // whole-blob matchedValue the fields=[webForm] metadata path returns.
                if (string.Equals(partName, "webForm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "layout", StringComparison.OrdinalIgnoreCase))
                {
                    try { return GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj) ?? ""; } catch { return ""; }
                }
            }
            catch { }
            return "";
        }
    }
}
