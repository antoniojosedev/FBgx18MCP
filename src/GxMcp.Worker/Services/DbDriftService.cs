using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

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
        // B15: the reorg *plan* DDL (exact ALTER delta vs the live DB) requires a DB
        // connection the net48 worker deliberately doesn't open, so ReorgPreview stays a
        // stub. But the SDK DOES expose an authoritative "does the model diverge from the
        // last reorg?" signal via ISpecifierService.ImpactDatabase (ReorgImpactService).
        // Wire it in so drift_check returns a real reorg-needed verdict instead of an empty
        // "no signal" envelope, and point the agent at the DDL that IS available (sql_ddl).
        private ReorgImpactService _reorgImpact;
        public void SetReorgImpact(ReorgImpactService reorgImpact) { _reorgImpact = reorgImpact; }

        public DbDriftService(BuildService buildService)
            : this(new BuildServiceReorgSource(buildService)) { }

        // Test seam.
        public DbDriftService(IReorgSource source)
        {
            _source = source;
        }

        // deep=false (default): cheap timestamp heuristic only (IModelInformationService).
        // deep=true (opt-in): also runs ISpecifierService.ImpactDatabase (specification,
        // build-heavy, minutes). Bug #2: drift_check MUST NOT force the deep path — it used
        // to hardcode deep=true here, so a plain drift_check ran a full ImpactDatabase and
        // held the worker's single SDK thread hostage for minutes.
        public string Check(string target, bool deep = false)
        {
            return BuildEnvelope(target, includeReport: false, deep: deep);
        }

        public string Report(string target, bool deep = false)
        {
            return BuildEnvelope(target, includeReport: true, deep: deep);
        }

        private string BuildEnvelope(string target, bool includeReport, bool deep = false)
        {
            string reorgJson;
            try
            {
                reorgJson = _source.ReorgPreview(target);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "ReorgPreviewFailed",
                    message: ex.Message,
                    hint: "The reorg preview call threw. Check that the SDK build service is available.",
                    extra: new JObject { ["source"] = "reorg_plan" });
            }

            JObject reorg;
            try { reorg = JObject.Parse(reorgJson ?? "{}"); }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "ReorgPreviewMalformed",
                    message: "Failed to parse reorg preview JSON: " + ex.Message,
                    hint: "The BuildService.ReorgPreview returned malformed JSON.",
                    extra: new JObject { ["source"] = "reorg_plan" });
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

            var resultPayload = new JObject
            {
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
                // B15: the fs/model reorg plan is unavailable, but ask the SDK whether the
                // model has diverged since the last reorg (authoritative, no DB write).
                bool signalAdded = false;
                if (_reorgImpact != null)
                {
                    try
                    {
                        var impactJson = _reorgImpact.Run(new JObject { ["deep"] = deep, ["target"] = target ?? string.Empty });
                        var impact = JObject.Parse(impactJson ?? "{}");
                        var ir = impact["result"] as JObject ?? impact;
                        var driftSignal = new JObject
                        {
                            ["reorgLikelyNeeded"] = ir["reorgLikelyNeeded"],
                            ["reorgNeeded"] = ir["reorgNeeded"],
                            ["deepAnalysis"] = ir["deepAnalysis"],
                            ["lastModifiedTable"] = ir["lastModifiedTable"],
                            ["lastReorg"] = ir["lastReorg"],
                            ["source"] = "sdk:ISpecifierService.ImpactDatabase"
                        };
                        resultPayload["reorgSignal"] = driftSignal;
                        signalAdded = true;
                    }
                    catch (Exception ex)
                    {
                        resultPayload["reorgSignalError"] = ex.Message;
                    }
                }
                if (!signalAdded)
                    resultPayload["note"] = "reorg_preview is a stub on this worker and the SDK impact signal is unavailable; drift cannot be confirmed here. Use genexus_db action=reorg_impact deep=true for the reorg-needed verdict and genexus_db action=sql_ddl for the schema DDL. Empty 'tables' means 'no signal', not 'no drift'.";
                else if (deep)
                    resultPayload["note"] = "The exact table-level DDL delta (ALTER/ADD/DROP) requires a live DB connection the worker doesn't open, so 'tables' is not itemized here. 'reorgSignal' above is the authoritative SDK verdict (ISpecifierService.ImpactDatabase) on whether the model diverged from the last reorg. For the desired-schema DDL use genexus_db action=sql_ddl; to apply the delta run genexus_lifecycle action=reorg on a non-production environment.";
                else
                    resultPayload["note"] = "The exact table-level DDL delta requires a live DB connection the worker doesn't open, so 'tables' is not itemized here. 'reorgSignal' above is the CHEAP timestamp heuristic (reorgLikelyNeeded = a table changed after the last reorg); it does NOT run specification. For the authoritative verdict pass deep=true (runs ISpecifierService.ImpactDatabase: specification, build-heavy, minutes). For the desired-schema DDL use genexus_db action=sql_ddl.";
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
                resultPayload["report"] = string.Join("\n", lines);
            }

            return McpResponse.Ok(target: target, code: "DbDriftDetected", result: resultPayload);
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
