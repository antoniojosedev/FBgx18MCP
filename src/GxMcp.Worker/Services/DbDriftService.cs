using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 41 (mcp-improvements-2026-05-22) — Transaction ↔ DB drift detection.
    ///
    /// Pragmatic implementation: the canonical drift signal in GeneXus is whatever
    /// reorg would emit. <see cref="BuildService.ReorgPreview"/> is the read-only
    /// surface for that (stubbed today but contract-stable). Direct DB connection
    /// from net48 worker would require an Oracle.ManagedDataAccess dependency and
    /// per-DBMS plumbing the GeneXus IDE already owns via CheckAndInstallDatabase
    /// — so we adopt the reorg plan as the authoritative drift source and document
    /// it in the response with <c>source: "reorg_plan"</c>.
    ///
    /// Once the SDK probe locates a non-mutating DB-introspection entry point the
    /// service can grow a <c>source: "live_db"</c> branch without changing the
    /// envelope shape.
    /// </summary>
    public class DbDriftService
    {
        public interface IReorgSource
        {
            string ReorgPreview(string target);
        }

        private sealed class BuildServiceReorgSource : IReorgSource
        {
            private readonly BuildService _build;
            public BuildServiceReorgSource(BuildService build) { _build = build; }
            public string ReorgPreview(string target) => _build.ReorgPreview(target);
        }

        private readonly IReorgSource _source;

        public DbDriftService(BuildService buildService)
            : this(new BuildServiceReorgSource(buildService)) { }

        // Test seam.
        public DbDriftService(IReorgSource source)
        {
            _source = source;
        }

        public string Check(string target)
        {
            return BuildEnvelope(target, includeReport: false);
        }

        public string Report(string target)
        {
            return BuildEnvelope(target, includeReport: true);
        }

        private string BuildEnvelope(string target, bool includeReport)
        {
            string reorgJson;
            try
            {
                reorgJson = _source.ReorgPreview(target);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "ReorgPreviewFailed",
                    ["message"] = ex.Message,
                    ["source"] = "reorg_plan"
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            JObject reorg;
            try { reorg = JObject.Parse(reorgJson ?? "{}"); }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "ReorgPreviewMalformed",
                    ["message"] = ex.Message,
                    ["source"] = "reorg_plan"
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            var ddl = reorg["ddl"] as JArray ?? new JArray();
            var perTable = AnalyzeDdl(ddl);

            int tablesWithDrift = 0;
            var tables = new JArray();
            foreach (var kv in perTable)
            {
                if (kv.Value.Count > 0) tablesWithDrift++;
                var driftArr = new JArray();
                string maxSeverity = "info";
                foreach (var d in kv.Value)
                {
                    driftArr.Add(new JObject
                    {
                        ["kind"] = d.Kind,
                        ["detail"] = d.Detail,
                        ["severity"] = d.Severity,
                        ["statement"] = d.Statement
                    });
                    if (SeverityRank(d.Severity) > SeverityRank(maxSeverity))
                        maxSeverity = d.Severity;
                }
                tables.Add(new JObject
                {
                    ["name"] = kv.Key,
                    ["drift"] = driftArr,
                    ["severity"] = kv.Value.Count == 0 ? "ok" : maxSeverity
                });
            }

            int tablesChecked = perTable.Count;
            // Empty plan with status=Stub means: no schema-level drift signal currently
            // available from the SDK surface — but also no proof of cleanliness. Report
            // that distinction so the agent doesn't assume "drift=0" means in-sync.
            bool reorgIsStub = string.Equals(reorg["status"]?.ToString(), "Stub", StringComparison.OrdinalIgnoreCase);

            var result = new JObject
            {
                ["status"] = "Success",
                ["target"] = target ?? string.Empty,
                ["source"] = "reorg_plan",
                ["tables"] = tables,
                ["summary"] = new JObject
                {
                    ["tables_checked"] = tablesChecked,
                    ["tables_with_drift"] = tablesWithDrift,
                    ["reorg_stub"] = reorgIsStub
                }
            };

            if (reorgIsStub)
            {
                result["note"] = "reorg_preview is currently a stub on this worker; drift detection cannot be confirmed without running action=reorg on a non-production environment. Empty 'tables' array means 'no signal' rather than 'no drift'.";
            }

            if (includeReport)
            {
                // Re-render as a markdown-friendly summary block — same data, easier to paste.
                var lines = new List<string>();
                lines.Add("# Transaction ↔ DB drift report");
                lines.Add(string.Format("- target: {0}", target ?? "(all)"));
                lines.Add("- source: reorg_plan");
                lines.Add(string.Format("- tables checked: {0}", tablesChecked));
                lines.Add(string.Format("- tables with drift: {0}", tablesWithDrift));
                if (reorgIsStub) lines.Add("- note: reorg_preview is a stub on this worker.");
                foreach (var t in tables)
                {
                    var tj = (JObject)t;
                    var driftArr = (JArray)tj["drift"];
                    if (driftArr.Count == 0) continue;
                    lines.Add(string.Empty);
                    lines.Add(string.Format("## {0} ({1})", tj["name"], tj["severity"]));
                    foreach (var d in driftArr)
                    {
                        lines.Add(string.Format("- [{0}] {1}: {2}", d["severity"], d["kind"], d["detail"]));
                    }
                }
                result["report"] = string.Join("\n", lines);
            }

            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static int SeverityRank(string severity)
        {
            switch ((severity ?? string.Empty).ToLowerInvariant())
            {
                case "error": return 3;
                case "warning": return 2;
                case "info": return 1;
                default: return 0;
            }
        }

        public struct DriftEntry
        {
            public string Kind;
            public string Detail;
            public string Severity;
            public string Statement;
        }

        /// <summary>
        /// Classify a list of DDL statements (typically from reorg_preview.ddl[]) into
        /// per-table drift entries. Statement-by-statement scan: CREATE TABLE → missing
        /// table, ADD COLUMN → missing column, ALTER COLUMN/MODIFY → type mismatch,
        /// CREATE INDEX → missing index, DROP COLUMN → orphan column.
        /// </summary>
        public static Dictionary<string, List<DriftEntry>> AnalyzeDdl(JArray ddl)
        {
            var result = new Dictionary<string, List<DriftEntry>>(StringComparer.OrdinalIgnoreCase);
            if (ddl == null) return result;

            foreach (var token in ddl)
            {
                string stmt = null;
                string table = null;
                if (token is JObject jo)
                {
                    stmt = jo["statement"]?.ToString() ?? jo["sql"]?.ToString() ?? jo["ddl"]?.ToString();
                    table = jo["table"]?.ToString() ?? jo["tableName"]?.ToString();
                }
                else
                {
                    stmt = token?.ToString();
                }
                if (string.IsNullOrWhiteSpace(stmt)) continue;
                if (string.IsNullOrWhiteSpace(table)) table = ExtractTableName(stmt);
                if (string.IsNullOrWhiteSpace(table)) table = "(unknown)";

                if (!result.TryGetValue(table, out var list))
                {
                    list = new List<DriftEntry>();
                    result[table] = list;
                }

                var entry = Classify(stmt);
                entry.Statement = stmt;
                list.Add(entry);
            }

            return result;
        }

        private static DriftEntry Classify(string stmt)
        {
            string upper = stmt.ToUpperInvariant();
            if (upper.Contains("CREATE TABLE"))
                return new DriftEntry { Kind = "missing_table", Detail = "Table does not exist in DB.", Severity = "error" };
            if (upper.Contains("ADD COLUMN") || (upper.Contains("ALTER TABLE") && upper.Contains(" ADD ")))
                return new DriftEntry { Kind = "missing_column", Detail = "Column declared on transaction but missing in DB.", Severity = "warning" };
            if (upper.Contains("DROP COLUMN") || (upper.Contains("ALTER TABLE") && upper.Contains(" DROP ")))
                return new DriftEntry { Kind = "orphan_column", Detail = "Column exists in DB but not on transaction.", Severity = "info" };
            if (upper.Contains("MODIFY") || upper.Contains("ALTER COLUMN"))
                return new DriftEntry { Kind = "type_mismatch", Detail = "Column type differs between transaction and DB.", Severity = "warning" };
            if (upper.Contains("CREATE INDEX") || upper.Contains("CREATE UNIQUE INDEX"))
                return new DriftEntry { Kind = "missing_index", Detail = "Index declared on transaction but missing in DB.", Severity = "warning" };
            if (upper.Contains("DROP INDEX"))
                return new DriftEntry { Kind = "orphan_index", Detail = "Index exists in DB but not on transaction.", Severity = "info" };
            return new DriftEntry { Kind = "other", Detail = "Other schema delta.", Severity = "info" };
        }

        private static string ExtractTableName(string stmt)
        {
            // Best-effort: find token after CREATE TABLE / ALTER TABLE / CREATE INDEX ... ON / DROP TABLE.
            string upper = stmt.ToUpperInvariant();
            string[] anchors = { "ALTER TABLE", "CREATE TABLE", "DROP TABLE" };
            foreach (var anchor in anchors)
            {
                int idx = upper.IndexOf(anchor, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int start = idx + anchor.Length;
                    return ReadIdentifier(stmt, start);
                }
            }
            int onIdx = upper.IndexOf(" ON ", StringComparison.Ordinal);
            if (onIdx >= 0) return ReadIdentifier(stmt, onIdx + 4);
            return null;
        }

        private static string ReadIdentifier(string stmt, int from)
        {
            int i = from;
            while (i < stmt.Length && char.IsWhiteSpace(stmt[i])) i++;
            int start = i;
            while (i < stmt.Length)
            {
                char c = stmt[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '"' || c == '.') i++;
                else break;
            }
            if (i == start) return null;
            return stmt.Substring(start, i - start).Trim('"');
        }
    }
}
