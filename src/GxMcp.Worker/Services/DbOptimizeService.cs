using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// SOTA tool — genexus_db_optimize. Static index advisor for GeneXus KBs.
    ///
    /// Walks every Procedure / WebPanel / DataProvider Source + Events part, regex-parses
    /// For each blocks, derives (Transaction × where-signature × sort) access patterns,
    /// then for a given Transaction proposes covering indexes and flags redundancy.
    ///
    /// Heuristic, not compiler-grade: every finding carries a `confidence` score so the
    /// caller can decide whether to act on it directly or escalate to human review.
    /// </summary>
    public class DbOptimizeService
    {
        // ---- Test seams ---------------------------------------------------------

        public interface IObjectEnumerator
        {
            /// <summary>Returns all objects whose Source/Events part can hold For each blocks.</summary>
            IEnumerable<ObjectRef> EnumerateCallers();
            /// <summary>Returns the names of all Transactions known to the KB (for caller resolution).</summary>
            IEnumerable<string> EnumerateTransactionNames();
        }

        public interface ISourceReader
        {
            /// <summary>Returns combined Source+Events text for a single object. Empty when nothing.</summary>
            string ReadCallerSource(ObjectRef obj);
        }

        public interface IIndexReader
        {
            /// <summary>Returns existing indexes for a Transaction (or null if not resolvable).</summary>
            IList<ExistingIndex> ReadIndexes(string transactionName);
        }

        public sealed class ObjectRef
        {
            public string Name;
            public string Type;
        }

        public sealed class ExistingIndex
        {
            public string Name;
            public bool IsPrimary;
            public bool IsUnique;
            public List<string> Columns = new List<string>();
        }

        private readonly IObjectEnumerator _objects;
        private readonly ISourceReader _reader;
        private readonly IIndexReader _indexes;

        // ---- Production wiring --------------------------------------------------

        public DbOptimizeService(KbService kbService, ObjectService objectService, IndexCacheService indexCache)
            : this(new IndexEnumerator(indexCache),
                   new ObjectSourceReader(objectService),
                   new TransactionIndexReader(objectService))
        { }

        // Direct ctor for tests.
        public DbOptimizeService(IObjectEnumerator objects, ISourceReader reader, IIndexReader indexes)
        {
            _objects = objects;
            _reader = reader;
            _indexes = indexes;
        }

        // ---- Public action entry points ----------------------------------------

        public string Analyze(string target)
        {
            try
            {
                var patternsByTx = ScanKb(target);
                var txArr = new JArray();
                foreach (var kv in patternsByTx.OrderByDescending(k => k.Value.Sum(p => p.Samples.Count)))
                {
                    txArr.Add(SerializeTransaction(kv.Key, kv.Value));
                }
                return new JObject
                {
                    ["status"] = "Success",
                    ["target"] = target ?? "(all)",
                    ["transactions"] = txArr,
                    ["summary"] = new JObject
                    {
                        ["transactionsCovered"] = patternsByTx.Count,
                        ["totalAccessPatterns"] = patternsByTx.Values.Sum(v => v.Count)
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return ErrorEnv("AnalyzeFailed", ex.Message);
            }
        }

        public string SuggestIndexes(string transactionName)
        {
            if (string.IsNullOrWhiteSpace(transactionName))
                return ErrorEnv("MissingTarget", "target=<transactionName> is required for suggest_indexes.");

            try
            {
                var patternsByTx = ScanKb(transactionName);
                if (!patternsByTx.TryGetValue(transactionName, out var patterns))
                {
                    patterns = new List<AccessPattern>();
                }

                IList<ExistingIndex> existing = SafeReadIndexes(transactionName);
                var existingArr = SerializeExistingIndexes(existing);

                var suggested = new JArray();
                var redundant = new JArray();

                foreach (var pattern in patterns.OrderByDescending(p => p.Samples.Count))
                {
                    if (pattern.WhereAttributes.Count == 0) continue;
                    var ddlCols = new List<string>(pattern.WhereAttributes);
                    foreach (var sortAttr in pattern.SortAttributes)
                    {
                        if (!ddlCols.Contains(sortAttr, StringComparer.OrdinalIgnoreCase))
                            ddlCols.Add(sortAttr);
                    }

                    if (IsCoveredByExisting(ddlCols, existing)) continue;

                    string benefit = pattern.Samples.Count >= 5 ? "high"
                                   : pattern.Samples.Count >= 2 ? "medium"
                                   : "low";
                    string indexName = "IX_" + transactionName + "_" + string.Join("_", ddlCols);
                    string ddl = string.Format(
                        "CREATE INDEX {0} ON {1} ({2});",
                        indexName,
                        transactionName,
                        string.Join(", ", ddlCols));

                    suggested.Add(new JObject
                    {
                        ["columns"] = new JArray(ddlCols.Cast<object>().ToArray()),
                        ["rationale"] = string.Format(
                            "Where signature '{0}' appears in {1} For each block(s) without a covering index.",
                            pattern.WhereSignature, pattern.Samples.Count),
                        ["coveredQueries"] = pattern.Samples.Count,
                        ["estimatedBenefit"] = benefit,
                        ["confidence"] = pattern.Confidence,
                        ["ddl"] = ddl
                    });
                }

                // Redundancy: existing index X is a strict prefix of another existing index Y.
                if (existing != null)
                {
                    for (int i = 0; i < existing.Count; i++)
                    {
                        for (int j = 0; j < existing.Count; j++)
                        {
                            if (i == j) continue;
                            var a = existing[i]; var b = existing[j];
                            if (a.IsPrimary || a.IsUnique) continue;
                            if (a.Columns.Count >= b.Columns.Count) continue;
                            if (IsPrefix(a.Columns, b.Columns))
                            {
                                redundant.Add(new JObject
                                {
                                    ["name"] = a.Name,
                                    ["reason"] = string.Format(
                                        "Columns [{0}] are a strict prefix of index '{1}' ([{2}]).",
                                        string.Join(", ", a.Columns), b.Name, string.Join(", ", b.Columns))
                                });
                                break;
                            }
                        }
                    }
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["transaction"] = transactionName,
                    ["existingIndexes"] = existingArr,
                    ["suggestedIndexes"] = suggested,
                    ["redundantIndexes"] = redundant
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return ErrorEnv("SuggestIndexesFailed", ex.Message);
            }
        }

        public string Report(string format)
        {
            try
            {
                var patternsByTx = ScanKb(null);
                var ranked = new List<JObject>();
                foreach (var kv in patternsByTx)
                {
                    var existing = SafeReadIndexes(kv.Key);
                    foreach (var p in kv.Value)
                    {
                        if (p.WhereAttributes.Count == 0) continue;
                        var ddlCols = new List<string>(p.WhereAttributes);
                        foreach (var s in p.SortAttributes)
                            if (!ddlCols.Contains(s, StringComparer.OrdinalIgnoreCase)) ddlCols.Add(s);
                        if (IsCoveredByExisting(ddlCols, existing)) continue;
                        ranked.Add(new JObject
                        {
                            ["transaction"] = kv.Key,
                            ["whereSignature"] = p.WhereSignature,
                            ["sortAttributes"] = new JArray(p.SortAttributes.Cast<object>().ToArray()),
                            ["callerCount"] = p.Samples.Count,
                            ["confidence"] = p.Confidence,
                            ["suggestedDdl"] = string.Format(
                                "CREATE INDEX IX_{0}_{1} ON {0} ({2});",
                                kv.Key, string.Join("_", ddlCols), string.Join(", ", ddlCols))
                        });
                    }
                }
                ranked = ranked.OrderByDescending(j => j["callerCount"]!.ToObject<int>())
                               .Take(10)
                               .ToList();

                var top = new JArray(ranked.Cast<object>().ToArray());
                bool wantMd = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase);
                var env = new JObject
                {
                    ["status"] = "Success",
                    ["top10"] = top,
                    ["summary"] = new JObject
                    {
                        ["unindexedHotPaths"] = ranked.Count,
                        ["transactionsScanned"] = patternsByTx.Count
                    }
                };
                if (wantMd) env["report"] = RenderMarkdown(ranked);
                return env.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return ErrorEnv("ReportFailed", ex.Message);
            }
        }

        // ---- Core scan ----------------------------------------------------------

        /// <summary>
        /// Walk every caller, parse For each blocks, group by Transaction.
        /// When <paramref name="onlyTransaction"/> is non-null, patterns for other
        /// transactions are still recorded only if their source mentions the target —
        /// callers can then ignore them. We keep it simple: full scan, caller decides.
        /// </summary>
        private Dictionary<string, List<AccessPattern>> ScanKb(string onlyTransaction)
        {
            var txNames = new HashSet<string>(_objects.EnumerateTransactionNames(), StringComparer.OrdinalIgnoreCase);
            // When the index doesn't surface a Transactions list yet, fall back to
            // accepting any uppercased identifier — better than emitting zero hits.
            bool emptyTxList = txNames.Count == 0;

            var byTx = new Dictionary<string, Dictionary<string, AccessPattern>>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in _objects.EnumerateCallers())
            {
                string source;
                try { source = _reader.ReadCallerSource(obj); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(source)) continue;

                foreach (var fe in ParseForEachBlocks(source))
                {
                    string tx = fe.Transaction;
                    if (string.IsNullOrEmpty(tx)) continue;
                    if (!emptyTxList && !txNames.Contains(tx)) continue;
                    if (onlyTransaction != null && !string.Equals(tx, onlyTransaction, StringComparison.OrdinalIgnoreCase))
                    {
                        // Still index it — analyze without target filter returns all,
                        // suggest_indexes filters by lookup. Skipping here would mask
                        // findings when callers ask for the whole KB.
                    }

                    if (!byTx.TryGetValue(tx, out var perSig))
                    {
                        perSig = new Dictionary<string, AccessPattern>(StringComparer.OrdinalIgnoreCase);
                        byTx[tx] = perSig;
                    }
                    if (!perSig.TryGetValue(fe.WhereSignature, out var pat))
                    {
                        pat = new AccessPattern
                        {
                            Transaction = tx,
                            WhereSignature = fe.WhereSignature,
                            WhereAttributes = fe.WhereAttributes,
                            SortAttributes = fe.SortAttributes,
                            Confidence = fe.Confidence
                        };
                        perSig[fe.WhereSignature] = pat;
                    }
                    pat.Samples.Add(new ForEachSample
                    {
                        Object = obj.Name,
                        Line = fe.Line,
                        Snippet = fe.Snippet
                    });
                    // Confidence: degrade to lowest seen so callers see worst-case.
                    if (ConfidenceRank(fe.Confidence) < ConfidenceRank(pat.Confidence))
                        pat.Confidence = fe.Confidence;
                }
            }

            return byTx.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Values.ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        // ---- Parser -------------------------------------------------------------

        // Anchor regex — finds every "For each" opener. We then walk forward
        // to its matching endfor manually so nested blocks each yield a
        // ParsedForEach (regex Matches() returns non-overlapping spans, which
        // would swallow an inner block under its outer endfor).
        private static readonly Regex ForEachOpenRegex = new Regex(
            @"\bFor\s+each\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ForEachOpenOrCloseRegex = new Regex(
            @"\bFor\s+each\b|\bend(?:for)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public sealed class ParsedForEach
        {
            public string Transaction;
            public string WhereSignature;
            public List<string> WhereAttributes = new List<string>();
            public List<string> SortAttributes = new List<string>();
            public int Line;
            public string Snippet;
            public string Confidence;
        }

        /// <summary>
        /// Public for tests. Walks the source text and yields one ParsedForEach
        /// per encountered For each block. Handles single-line, multi-line, line
        /// comments (//...), and nested blocks (each yields independently — the
        /// regex matches innermost-then-outermost via lazy quantifier).
        /// </summary>
        public static IEnumerable<ParsedForEach> ParseForEachBlocks(string source)
        {
            if (string.IsNullOrEmpty(source)) yield break;
            string clean = StripLineComments(source);

            foreach (Match open in ForEachOpenRegex.Matches(clean))
            {
                int openEnd = open.Index + open.Length;
                int endforIdx = FindMatchingEndfor(clean, openEnd);
                int bodyEnd = endforIdx >= 0 ? endforIdx : clean.Length;
                string body = clean.Substring(openEnd, bodyEnd - openEnd);
                // Strip nested For-each blocks from the body so the outer's Where
                // signature doesn't capture inner attributes.
                body = RemoveNestedForEachBlocks(body);
                string wholeBlock = clean.Substring(open.Index,
                    (endforIdx >= 0 ? endforIdx + 6 : clean.Length) - open.Index);
                int line = LineOf(clean, open.Index);

                // Transaction name: first whitespace-trimmed identifier after "For each".
                // Tolerant: GeneXus also allows "For each <Tx> Order ..." and "For each <Tx> Where ...".
                string tx = ExtractFirstIdentifier(body);
                // The body starts AFTER "For each", so the first identifier is the
                // transaction. Trim it off before walking Where / Order clauses.
                string bodyAfterTx = string.IsNullOrEmpty(tx)
                    ? body
                    : body.Substring(body.IndexOf(tx, StringComparison.OrdinalIgnoreCase) + tx.Length);

                var whereAttrs = ExtractAttributeRefs(bodyAfterTx, anchor: "where");
                var sortAttrs = ExtractAttributeRefs(bodyAfterTx, anchor: "order");
                whereAttrs = whereAttrs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sortAttrs = sortAttrs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                string sig = whereAttrs.Count == 0
                    ? "(no-where)"
                    : string.Join(",", whereAttrs.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));

                string confidence = "high";
                if (string.IsNullOrEmpty(tx)) confidence = "low";
                else if (whereAttrs.Count == 0 && sortAttrs.Count == 0) confidence = "medium";

                yield return new ParsedForEach
                {
                    Transaction = tx,
                    WhereAttributes = whereAttrs,
                    SortAttributes = sortAttrs,
                    WhereSignature = sig,
                    Line = line,
                    Snippet = Truncate(wholeBlock, 200),
                    Confidence = confidence
                };
            }
        }

        /// <summary>
        /// Replaces every nested "For each ... endfor" inside <paramref name="body"/>
        /// with whitespace of the same length so attribute extraction sees only
        /// the current block's Where / Order clauses.
        /// </summary>
        private static string RemoveNestedForEachBlocks(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            var sb = new StringBuilder(body);
            int searchFrom = 0;
            while (searchFrom < sb.Length)
            {
                var m = ForEachOpenRegex.Match(sb.ToString(), searchFrom);
                if (!m.Success) break;
                int end = FindMatchingEndfor(sb.ToString(), m.Index + m.Length);
                int spanEnd = end >= 0 ? end + 6 : sb.Length;
                for (int i = m.Index; i < spanEnd && i < sb.Length; i++) sb[i] = ' ';
                searchFrom = spanEnd;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Walks tokens forward from <paramref name="from"/> counting nested
        /// "For each" openers and "endfor" closers. Returns the start index of
        /// the matching endfor, or -1 when no balanced close is found.
        /// </summary>
        private static int FindMatchingEndfor(string text, int from)
        {
            int depth = 1;
            int pos = from;
            while (pos < text.Length)
            {
                var m = ForEachOpenOrCloseRegex.Match(text, pos);
                if (!m.Success) return -1;
                bool isOpen = m.Value.IndexOf("for", System.StringComparison.OrdinalIgnoreCase) >= 0
                              && m.Value.IndexOf("each", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (isOpen) depth++;
                else
                {
                    depth--;
                    if (depth == 0) return m.Index;
                }
                pos = m.Index + m.Length;
            }
            return -1;
        }

        /// <summary>Line-comments (//...) only — GeneXus does not use /* */.</summary>
        private static string StripLineComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                }
                else
                {
                    sb.Append(text[i]); i++;
                }
            }
            return sb.ToString();
        }

        private static string ExtractFirstIdentifier(string body)
        {
            int i = 0;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            int start = i;
            while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_')) i++;
            if (i == start) return null;
            string token = body.Substring(start, i - start);
            // Reject reserved clause words masquerading as a Tx (e.g. when source begins
            // with "Where ..." because the writer omitted the Tx name).
            if (IsClauseKeyword(token)) return null;
            return token;
        }

        private static bool IsClauseKeyword(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            switch (token.ToLowerInvariant())
            {
                case "where":
                case "order":
                case "defined":
                case "by":
                case "when":
                case "in":
                case "using":
                case "if":
                case "for":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Walk every "Where" / "Order" clause in the body and harvest attribute
        /// references. An "attribute reference" here is any bare identifier (no
        /// leading &amp;, not a numeric literal, not a quoted string, not a clause
        /// keyword). Heuristic — we treat anything that survives that filter as a
        /// candidate column name.
        /// </summary>
        private static List<string> ExtractAttributeRefs(string body, string anchor)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(body)) return hits;
            // Anchor regex: "where" or "order" at a word boundary, captures up to the
            // next line that starts another clause (when/order/where) or "endfor".
            var rx = new Regex(@"\b" + Regex.Escape(anchor) + @"\b(?<expr>[^\r\n]*)",
                RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(body))
            {
                string expr = m.Groups["expr"].Value;
                foreach (string token in TokeniseExpression(expr))
                {
                    if (IsClauseKeyword(token)) continue;
                    hits.Add(token);
                }
            }
            return hits;
        }

        private static IEnumerable<string> TokeniseExpression(string expr)
        {
            // Strip quoted strings.
            var noStrings = Regex.Replace(expr ?? string.Empty, "\"[^\"]*\"|'[^']*'", " ");
            // Identifiers: alpha[alnum_]*. Variables (&Foo) are excluded by &.
            foreach (Match m in Regex.Matches(noStrings, @"(?<!&)\b[A-Za-z][A-Za-z0-9_]*\b"))
            {
                string t = m.Value;
                // Drop numerics (impossible per regex) and trivial 1-char names.
                if (t.Length < 2) continue;
                yield return t;
            }
        }

        private static int LineOf(string text, int charIndex)
        {
            int line = 1;
            for (int i = 0; i < charIndex && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        // ---- Index coverage helpers --------------------------------------------

        /// <summary>
        /// Public for tests. Returns true when at least one existing index covers
        /// the where-signature attributes as a prefix match (order-sensitive).
        /// </summary>
        public static bool IsCoveredByExisting(IList<string> ddlCols, IList<ExistingIndex> existing)
        {
            if (existing == null || existing.Count == 0) return false;
            if (ddlCols == null || ddlCols.Count == 0) return false;
            foreach (var idx in existing)
            {
                if (IsPrefix(ddlCols, idx.Columns)) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when needle is a (case-insensitive, order-sensitive)
        /// prefix of haystack.
        /// </summary>
        public static bool IsPrefix(IList<string> needle, IList<string> haystack)
        {
            if (needle == null || haystack == null) return false;
            if (needle.Count > haystack.Count) return false;
            for (int i = 0; i < needle.Count; i++)
            {
                if (!string.Equals(needle[i], haystack[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // ---- Serialisation ------------------------------------------------------

        private static JObject SerializeTransaction(string tx, List<AccessPattern> patterns)
        {
            var arr = new JArray();
            foreach (var p in patterns.OrderByDescending(p => p.Samples.Count))
            {
                var samples = new JArray();
                foreach (var s in p.Samples.Take(5))
                {
                    samples.Add(new JObject
                    {
                        ["object"] = s.Object,
                        ["line"] = s.Line,
                        ["snippet"] = s.Snippet
                    });
                }
                arr.Add(new JObject
                {
                    ["whereSignature"] = p.WhereSignature,
                    ["whereAttributes"] = new JArray(p.WhereAttributes.Cast<object>().ToArray()),
                    ["sortAttributes"] = new JArray(p.SortAttributes.Cast<object>().ToArray()),
                    ["callerCount"] = p.Samples.Count,
                    ["confidence"] = p.Confidence,
                    ["samples"] = samples
                });
            }
            return new JObject
            {
                ["name"] = tx,
                ["accessPatterns"] = arr
            };
        }

        private static JArray SerializeExistingIndexes(IList<ExistingIndex> existing)
        {
            var arr = new JArray();
            if (existing == null) return arr;
            foreach (var idx in existing)
            {
                arr.Add(new JObject
                {
                    ["name"] = idx.Name,
                    ["isPrimary"] = idx.IsPrimary,
                    ["isUnique"] = idx.IsUnique,
                    ["columns"] = new JArray(idx.Columns.Cast<object>().ToArray())
                });
            }
            return arr;
        }

        private IList<ExistingIndex> SafeReadIndexes(string transactionName)
        {
            try { return _indexes.ReadIndexes(transactionName) ?? new List<ExistingIndex>(); }
            catch { return new List<ExistingIndex>(); }
        }

        private static string RenderMarkdown(List<JObject> ranked)
        {
            var lines = new List<string>();
            lines.Add("# Top unindexed hot paths");
            lines.Add(string.Empty);
            lines.Add("| Transaction | Where signature | Callers | Confidence | DDL |");
            lines.Add("|---|---|---|---|---|");
            foreach (var j in ranked)
            {
                lines.Add(string.Format("| {0} | `{1}` | {2} | {3} | `{4}` |",
                    j["transaction"], j["whereSignature"], j["callerCount"],
                    j["confidence"], j["suggestedDdl"]));
            }
            return string.Join("\n", lines);
        }

        private static string ErrorEnv(string code, string message)
        {
            return new JObject
            {
                ["status"] = "Error",
                ["code"] = code,
                ["message"] = message ?? string.Empty
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static int ConfidenceRank(string c)
        {
            switch ((c ?? "").ToLowerInvariant())
            {
                case "high": return 3;
                case "medium": return 2;
                case "low": return 1;
                default: return 0;
            }
        }

        // ---- Aggregated model ---------------------------------------------------

        private sealed class AccessPattern
        {
            public string Transaction;
            public string WhereSignature;
            public List<string> WhereAttributes = new List<string>();
            public List<string> SortAttributes = new List<string>();
            public List<ForEachSample> Samples = new List<ForEachSample>();
            public string Confidence = "high";
        }

        private sealed class ForEachSample
        {
            public string Object;
            public int Line;
            public string Snippet;
        }

        // ---- Production seams ---------------------------------------------------

        private sealed class IndexEnumerator : IObjectEnumerator
        {
            private readonly IndexCacheService _cache;
            public IndexEnumerator(IndexCacheService cache) { _cache = cache; }

            public IEnumerable<ObjectRef> EnumerateCallers()
            {
                var index = _cache?.GetIndex();
                if (index == null) yield break;
                foreach (var entry in index.Objects.Values)
                {
                    if (string.IsNullOrEmpty(entry.Type)) continue;
                    string t = entry.Type;
                    if (t.Equals("Procedure", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("DataProvider", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("WorkPanel", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("SDPanel", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new ObjectRef { Name = entry.Name, Type = entry.Type };
                    }
                }
            }

            public IEnumerable<string> EnumerateTransactionNames()
            {
                var index = _cache?.GetIndex();
                if (index == null) yield break;
                foreach (var entry in index.Objects.Values)
                {
                    if (entry.Type != null
                        && entry.Type.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return entry.Name;
                    }
                }
            }
        }

        private sealed class ObjectSourceReader : ISourceReader
        {
            private readonly ObjectService _objects;
            public ObjectSourceReader(ObjectService objects) { _objects = objects; }

            public string ReadCallerSource(ObjectRef obj)
            {
                if (obj == null || string.IsNullOrEmpty(obj.Name)) return null;
                var sb = new StringBuilder();
                foreach (var part in new[] { "Source", "Events" })
                {
                    try
                    {
                        string json = _objects.ReadObjectSource(obj.Name, part, null, null, "mcp", false, obj.Type);
                        if (string.IsNullOrEmpty(json)) continue;
                        var jo = JObject.Parse(json);
                        string text = jo["source"]?.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            sb.AppendLine(text);
                        }
                    }
                    catch { /* skip parts that fail to read */ }
                }
                return sb.ToString();
            }
        }

        private sealed class TransactionIndexReader : IIndexReader
        {
            private readonly ObjectService _objects;
            public TransactionIndexReader(ObjectService objects) { _objects = objects; }

            public IList<ExistingIndex> ReadIndexes(string transactionName)
            {
                var result = new List<ExistingIndex>();
                if (string.IsNullOrWhiteSpace(transactionName)) return result;
                try
                {
                    var structSvc = new Structure.IndexService(_objects);
                    string json = structSvc.GetVisualIndexes(transactionName);
                    if (string.IsNullOrEmpty(json)) return result;
                    var jo = JObject.Parse(json);
                    var arr = jo["indexes"] as JArray;
                    if (arr == null) return result;
                    foreach (var t in arr.OfType<JObject>())
                    {
                        var idx = new ExistingIndex
                        {
                            Name = t["name"]?.ToString() ?? "(unnamed)",
                            IsPrimary = t["isPrimary"]?.ToObject<bool?>() ?? false,
                            IsUnique = t["isUnique"]?.ToObject<bool?>() ?? false
                        };
                        var attrs = t["attributes"] as JArray;
                        if (attrs != null)
                        {
                            foreach (var a in attrs.OfType<JObject>())
                            {
                                string name = a["name"]?.ToString();
                                if (!string.IsNullOrEmpty(name)) idx.Columns.Add(name);
                            }
                        }
                        result.Add(idx);
                    }
                }
                catch { /* SDK not available or transaction missing — return empty */ }
                return result;
            }
        }
    }
}
