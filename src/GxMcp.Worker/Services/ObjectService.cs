using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class ObjectService
    {
        private sealed class ReadCacheEntry
        {
            public string Payload { get; set; } = string.Empty;
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, ReadCacheEntry> _readCache =
            new ConcurrentDictionary<string, ReadCacheEntry>(StringComparer.OrdinalIgnoreCase);
        // PERFORMANCE (W-M1): 20s was too tight for read-after-read patterns from LLM agents
        // that consult the same object multiple times in a single tool sequence. Invalidation
        // is event-driven elsewhere, so doubling the TTL is safe.
        private static readonly TimeSpan ReadCacheTtl = TimeSpan.FromSeconds(60);

        // v2.6.8 (review C8): crash-line detector. Anchored markers only —
        // bare "critical" inside a log message (e.g., "critical section",
        // "no critical errors") must NOT trip the matcher.
        private static readonly System.Text.RegularExpressions.Regex _crashLinePattern =
            new System.Text.RegularExpressions.Regex(
                @"\[(ERROR|CRITICAL|FATAL)\]|\bCRITICAL\s+(?:Init|Error|Failure|Exception)\b|\bUnhandled\s+exception\b",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private readonly KbService _kbService;
        private readonly BuildService _buildService;
        private DataInsightService _dataInsightService;
        private UIService _uiService;
        private PatternAnalysisService _patternAnalysisService;
        private WriteService _writeService;

        public ObjectService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _buildService = buildService;
        }

        public void SetDataInsightService(DataInsightService ds) { _dataInsightService = ds; }
        public void SetUIService(UIService ui) { _uiService = ui; }
        public void SetPatternAnalysisService(PatternAnalysisService patternAnalysisService) { _patternAnalysisService = patternAnalysisService; }
        public void SetWriteService(WriteService writeService) { _writeService = writeService; }
        public KbService GetKbService() { return _kbService; }

        public SearchIndex GetIndex() { return _kbService.GetIndexCache().GetIndex(); }

        /// <summary>
        /// Returns all index entries whose name matches <paramref name="name"/> (case-insensitive).
        /// Works without a KB open — the index may be pre-populated via IndexCacheService.UpdateIndex().
        /// Returns an empty list when the index is empty or no entries match.
        /// </summary>
        public List<SearchIndex.IndexEntry> FindCandidateEntries(string name)
        {
            if (string.IsNullOrEmpty(name)) return new List<SearchIndex.IndexEntry>();
            var index = GetIndex();
            if (index?.Objects == null) return new List<SearchIndex.IndexEntry>();
            var results = new List<SearchIndex.IndexEntry>();
            foreach (var entry in index.Objects.Values)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    results.Add(entry);
            }
            return results;
        }

        public string CreateObject(string type, string name)
        {
            return CreateObject(type, name, null);
        }

        public string CreateObject(string type, string name, JObject options)
        {
            var sw = Stopwatch.StartNew();
            // Item 21 (friction 2026-05-22) — universal dryRun: report planned shape
            // without calling Save(). Resolved type guid + pre-flight duplicate check
            // still run so the agent sees the same validation failures as the live call.
            bool dryRun = options?["dryRun"]?.ToObject<bool?>() ?? false;
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return "{\"status\":\"Error\", \"error\":\"No KB open\"}";

                Logger.Info(string.Format("Creating Object: {0} ({1})", name, type));

                // Map string type to Guid. First try the well-known descriptor table (covers
                // every type with a concrete wrapper class), then fall back to ObjClass.<Name>
                // reflection so types without a public wrapper (SDPanel, Dashboard, Query,
                // WorkflowDiagram, ConversationalFlows, TestSuite, WikiPage, WorkWithWeb,
                // WorkWithDevices, etc.) are still creatable by name.
                Guid typeGuid = ResolveObjectTypeGuid(type);
                if (typeGuid == Guid.Empty)
                {
                    return "{\"status\":\"Error\", \"error\":\"Unsupported object type: " + type +
                        "\", \"hint\":\"Known types: Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Image, Theme, ThemeClass, DesignSystem, ColorPalette, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, Dashboard, Query, WorkflowDiagram, ConversationalFlows, TestSuite, API, URLRewrite, MiniApp, SuperApp, OfflineDatabase, DataView, Group, Language, TranslationMessage, WorkWithDevices, WorkWithWeb.\"}";
                }

                // Pre-flight duplicate check: gives a clear, structured error before the SDK throws.
                try
                {
                    var existing = kb.DesignModel.Objects.Get(typeGuid, name);
                    if (existing != null)
                    {
                        return "{\"status\":\"Error\", \"code\":\"AlreadyExists\", \"error\":\"" + type + " '" + CommandDispatcher.EscapeJsonString(name) + "' already exists.\"}";
                    }
                }
                catch { /* lookup is best-effort; if it throws, Save will surface the duplicate error anyway */ }

                KBObject newObj = KBObject.Create(kb.DesignModel, typeGuid);
                newObj.Name = name;

                // Initialize with some default content if possible
                if (newObj.GetType().Name == "Procedure")
                {
                    var partProp = newObj.GetType().GetProperty("ProcedurePart");
                    if (partProp != null) {
                        object part = partProp.GetValue(newObj);
                        if (part != null) {
                            var sourceProp = part.GetType().GetProperty("Source");
                            if (sourceProp != null) sourceProp.SetValue(part, "// Procedure: " + name + "\n\n");
                        }
                    }
                }
                else if (newObj.GetType().Name == "DataProvider")
                {
                    var partsProp = newObj.GetType().GetProperty("Parts");
                    if (partsProp != null) {
                        var parts = (System.Collections.IEnumerable)partsProp.GetValue(newObj);
                        foreach (object p in parts)
                        {
                            if (p.GetType().Name == "SourcePart")
                            {
                                var sourceProp = p.GetType().GetProperty("Source");
                                if (sourceProp != null) sourceProp.SetValue(p, "// Data Provider: " + name + "\n\n");
                                break;
                            }
                        }
                    }
                }
                string seededDescription = null;
                JObject domainMeta = null;
                if (type.Equals("SDT", StringComparison.OrdinalIgnoreCase) || type.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase))
                {
                    InitializeSDTWithDefaultItem(newObj, name);
                    seededDescription = "Item1 : VARCHAR(40)";
                }
                else if (newObj is Artech.Genexus.Common.Objects.Transaction newTrn)
                {
                    InitializeTransactionWithDefaultKey(newTrn, name);
                    seededDescription = name + "Id : Numeric(8,0) [Key]";
                }
                else if (type.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                {
                    domainMeta = InitializeDomain(newObj, name, options, kb);
                }

                if (dryRun)
                {
                    // Item 21 (friction 2026-05-22) — return planned shape without persisting.
                    // SDK in-memory artefact is discarded (GC-collected) since we don't hold
                    // a reference past this method. Pre-flight checks (type resolution,
                    // duplicate name) already ran above so the LLM sees real validation.
                    return new JObject
                    {
                        ["status"] = "DryRun",
                        ["dryRun"] = true,
                        ["type"] = type,
                        ["name"] = name,
                        ["seededDescription"] = seededDescription,
                        ["hint"] = "Re-run without dryRun to call Save()."
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                newObj.Save();

                // Best-effort: refresh search index so subsequent list/query calls see this object
                // without waiting for a full reindex.
                try
                {
                    var idx = _kbService?.GetIndexCache();
                    if (idx != null) idx.UpdateEntry(newObj);
                }
                catch (Exception ex) { Logger.Error("CreateObject: index UpdateEntry failed for " + name + ": " + ex.Message); }

                Logger.Info(string.Format("Object created successfully in {0}ms", sw.ElapsedMilliseconds));
                string idStr = "";
                try { idStr = newObj.Key?.Id.ToString() ?? ""; } catch { try { idStr = newObj.Guid.ToString(); } catch { } }
                var response = new JObject
                {
                    ["status"] = "Success",
                    ["type"] = type,
                    ["name"] = name,
                    ["id"] = idStr
                };
                JObject metaObj = null;
                if (domainMeta != null)
                {
                    metaObj = domainMeta;
                }
                if (!string.IsNullOrEmpty(seededDescription))
                {
                    // Surface the auto-seeded payload so the agent knows the object isn't empty
                    // before calling read/edit. Agents that immediately overwrite Structure
                    // need this signal to avoid being surprised by the seed item appearing in
                    // round-trip reads.
                    metaObj = new JObject
                    {
                        ["seeded"] = new JArray { seededDescription },
                        ["seededHint"] = "An initial item was auto-added so the SDK accepts the empty Save. Overwrite via genexus_edit part=Structure (full mode) to replace it."
                    };
                }
                // WebPanel / SDPanel get a hint pointing at apply_pattern. Agents asked for
                // "a WebPanel with WorkWithPlus" otherwise tend to hand-build the layout via
                // WebForm edits, which compiles but produces a page with none of WWP's
                // grid/filter/action infrastructure. The hint short-circuits that drift.
                bool isWebPanel = type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("SDPanel", StringComparison.OrdinalIgnoreCase);
                if (isWebPanel)
                {
                    if (metaObj == null) metaObj = new JObject();
                    metaObj["patternHint"] =
                        "Empty " + type + " created. Two real paths to WorkWithPlus: " +
                        "(A) Apply WWP directly on this " + type + " — call genexus_apply_pattern name=" + name +
                        " pattern=WorkWithPlus settings={template:'<TemplateName>'} to attach a 'WorkWithPlus" + name +
                        "' host. The MCP runs the SDK's IPatternBuildProcess.UpdateParentObject so the projection " +
                        "lands on this " + type + "'s WebForm immediately. Subsequent genexus_edit on the host's " +
                        "PatternInstance auto-projects too. " +
                        "(B) Apply WWP to a Transaction — generates the full 'WW<Trn>' family (Selection list + " +
                        "View detail + exports). Pick (A) for custom WebPanel-based screens (queries, dashboards), " +
                        "(B) for CRUD-around-a-Transaction. " +
                        "Available templates: list via `genexus_list_objects typeFilter=\"WorkWithPlus for Web Template\"` " +
                        "(common: MatIsoTemplate, TransactionResp2, PopoverEmpty).";
                    metaObj["nextStep"] = new JObject
                    {
                        ["forWwpOnThisWebPanel"] = new JObject
                        {
                            ["tool"] = "genexus_apply_pattern",
                            ["arguments"] = new JObject
                            {
                                ["name"] = name,
                                ["pattern"] = "WorkWithPlus",
                                ["settings"] = new JObject { ["template"] = "<TemplateName>" }
                            }
                        },
                        ["forWwpFromTransaction"] = new JObject
                        {
                            ["step1"] = new JObject { ["tool"] = "genexus_create_object", ["arguments"] = new JObject { ["type"] = "Transaction", ["name"] = "<TrnName>" } },
                            ["step2"] = new JObject { ["tool"] = "genexus_apply_pattern", ["arguments"] = new JObject { ["name"] = "<TrnName>", ["pattern"] = "WorkWithPlus" } },
                            ["step3"] = new JObject { ["tool"] = "genexus_edit", ["arguments"] = new JObject { ["name"] = "WorkWithPlus<TrnName>", ["part"] = "PatternInstance" } }
                        }
                    };
                }
                if (metaObj != null) response["_meta"] = metaObj;
                return response.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                Logger.Error("CreateObject failed: " + ex.Message);
                return "{\"status\":\"Error\", \"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Item 32: objectFilter added. sinceMode now also accepts an ISO-8601 timestamp;
        // the legacy 'crash' sentinel is still honoured for backward compatibility.
        // logPathOverride: test-only seam so unit tests can inject a temp file path.
        public string ReadLogs(int lines, string filterCorrelation, string grepPattern, string sinceMode = null, string objectFilter = null, string logPathOverride = null)
        {
            try
            {
                if (lines <= 0) lines = 100;
                if (lines > 2000) lines = 2000;

                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? Process.GetCurrentProcess().MainModule.FileName) ?? "";
                string logPath = !string.IsNullOrEmpty(logPathOverride)
                    ? logPathOverride
                    : Path.Combine(exeDir, "worker_debug.log");
                if (!File.Exists(logPath))
                {
                    return "{\"status\":\"Error\", \"error\":\"Log file not found at " + CommandDispatcher.EscapeJsonString(logPath) + "\"}";
                }

                // Stream-read tail
                var allLines = new List<string>();
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string ln;
                    while ((ln = sr.ReadLine()) != null) allLines.Add(ln);
                }

                IEnumerable<string> filtered = allLines;

                // v2.6.8: since=crash slices the log starting at the most recent
                // [ERROR]/[CRITICAL] line — the agent (and the user reporting a
                // crash) gets the stack + immediate context without having to
                // hunt for it manually.
                bool sliceFromCrash = string.Equals(sinceMode, "crash", StringComparison.OrdinalIgnoreCase);
                int crashIndex = -1;
                if (sliceFromCrash)
                {
                    for (int i = allLines.Count - 1; i >= 0; i--)
                    {
                        string ln = allLines[i];
                        if (_crashLinePattern.IsMatch(ln))
                        {
                            crashIndex = i;
                            break;
                        }
                    }
                    if (crashIndex >= 0)
                    {
                        // Take 5 lines of context before + everything after.
                        int start = Math.Max(0, crashIndex - 5);
                        filtered = allLines.Skip(start);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(sinceMode))
                {
                    // Item 32: since=<ISO timestamp> — skip lines whose leading timestamp
                    // is before the requested cutoff. Log format: [yyyy-MM-dd HH:mm:ss.fff]
                    // Lines that don't carry a parseable timestamp are kept (defensive).
                    if (DateTime.TryParse(sinceMode, null, System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTime sinceDt))
                    {
                        // Normalize to UTC so a client-supplied "...Z" timestamp compares correctly
                        // against log-line timestamps (the worker writes them in local time).
                        DateTime sinceUtc = sinceDt.Kind == DateTimeKind.Utc ? sinceDt : sinceDt.ToUniversalTime();
                        filtered = allLines.Where(l =>
                        {
                            // Try to parse the leading [yyyy-MM-dd HH:mm:ss.fff] prefix.
                            if (l.Length > 2 && l[0] == '[')
                            {
                                int close = l.IndexOf(']');
                                if (close > 0)
                                {
                                    string ts = l.Substring(1, close - 1);
                                    if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.AssumeLocal, out DateTime lineTs))
                                        return lineTs.ToUniversalTime() >= sinceUtc;
                                }
                            }
                            return true; // unparseable timestamp — keep line
                        });
                    }
                }

                // Item 32: object-name filter — only lines mentioning the object.
                if (!string.IsNullOrWhiteSpace(objectFilter))
                    filtered = filtered.Where(l => l.IndexOf(objectFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!string.IsNullOrWhiteSpace(filterCorrelation))
                    filtered = filtered.Where(l => l.IndexOf(filterCorrelation, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(grepPattern))
                {
                    try
                    {
                        var rx = new System.Text.RegularExpressions.Regex(grepPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        filtered = filtered.Where(l => rx.IsMatch(l));
                    }
                    catch { /* invalid regex falls back to substring */ filtered = filtered.Where(l => l.IndexOf(grepPattern, StringComparison.OrdinalIgnoreCase) >= 0); }
                }

                var matchList = filtered.ToList();
                int skip = Math.Max(0, matchList.Count - lines);
                var tail = matchList.Skip(skip).ToList();
                var result = new JObject
                {
                    ["status"] = "Success",
                    // Item 32: surface the log path so the agent can read adjacent logs
                    // (gateway_debug.log, probe.log, etc.) directly via genexus_asset.
                    ["logPath"] = logPath,
                    // Back-compat alias: prior shape exposed the file location as "path".
                    ["path"] = logPath,
                    ["logDir"] = exeDir,
                    ["totalLines"] = allLines.Count,
                    ["matched"] = tail.Count,
                    ["lines"] = string.Join("\n", tail)
                };
                if (sliceFromCrash)
                {
                    result["sinceMode"] = "crash";
                    result["crashLineIndex"] = crashIndex;
                    if (crashIndex < 0)
                    {
                        result["hint"] = "No ERROR/CRITICAL markers found in the log — worker has not crashed (or the log has rotated).";
                    }
                }
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\", \"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string WorkerReload(string sourceDir)
        {
            try
            {
                string currentExe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(currentExe)) currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                string publishDir = Path.GetDirectoryName(currentExe) ?? "";
                int currentPid = Process.GetCurrentProcess().Id;

                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    return "{\"status\":\"Error\", \"error\":\"sourceDir must point to a directory with the new worker binaries (typically src/GxMcp.Worker/bin/Release).\"}";
                }

                // Spawn a detached PowerShell helper that:
                //   1) waits for THIS worker pid to exit (releases the .exe lock)
                //   2) copies sourceDir/* → publishDir/* with retries (the gateway can
                //      respawn the worker faster than we copy, re-locking the .exe — we
                //      then kill the respawned worker so the next gateway respawn picks
                //      up the new bits)
                //   3) writes worker_reload.last_result.json next to publishDir so
                //      callers can diagnose silent failures
                //
                // Previous version used `-ErrorAction SilentlyContinue` on a single
                // Copy-Item, which masked the lock race entirely — the reload returned
                // Success while the binary on disk was unchanged.
                string src = sourceDir.Replace("'", "''");
                string dst = publishDir.Replace("'", "''");
                string ps =
                    "$pid_target=" + currentPid + "; " +
                    "$src='" + src + "'; $dst='" + dst + "'; " +
                    "$log = Join-Path $dst 'worker_reload.last_result.json'; " +
                    "function Write-Status($status, $detail) { " +
                    "  @{ status = $status; detail = $detail; timestamp = (Get-Date).ToString('o'); src = $src; dst = $dst } | " +
                    "  ConvertTo-Json | Set-Content -Path $log -Encoding utf8 -ErrorAction SilentlyContinue " +
                    "} " +
                    "try { Wait-Process -Id $pid_target -Timeout 30 -ErrorAction SilentlyContinue } catch {} " +
                    "$copied=$false; $lastErr=''; " +
                    "for ($i=0; $i -lt 20 -and -not $copied; $i++) { " +
                    "  try { " +
                    "    Copy-Item \\\"$src\\*\\\" \\\"$dst\\\" -Recurse -Force -ErrorAction Stop; " +
                    "    $copied=$true " +
                    "  } catch { " +
                    "    $lastErr = $_.Exception.Message; " +
                    "    $w = Get-Process -Name GxMcp.Worker -ErrorAction SilentlyContinue; " +
                    "    if ($w) { try { $w | Stop-Process -Force -ErrorAction SilentlyContinue } catch {} } " +
                    "    Start-Sleep -Milliseconds 500 " +
                    "  } " +
                    "} " +
                    "if ($copied) { " +
                    "  $w = Get-Process -Name GxMcp.Worker -ErrorAction SilentlyContinue; " +
                    "  if ($w) { try { $w | Stop-Process -Force -ErrorAction SilentlyContinue } catch {} } " +
                    "  Write-Status 'Success' 'Binaries copied; respawned worker (if any) killed so gateway brings up a fresh one with new bits.' " +
                    "} else { " +
                    "  Write-Status 'Error' \\\"Copy failed after retries. Last error: $lastErr\\\" " +
                    "}; ";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                Logger.Info("WorkerReload: helper spawned (will copy " + sourceDir + " -> " + publishDir + " after exit). Exiting in 1s.");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1000);
                    Logger.Info("WorkerReload: exiting now for respawn.");
                    Environment.Exit(0);
                });
                return "{\"status\":\"Accepted\", \"sourceDir\":\"" + CommandDispatcher.EscapeJsonString(sourceDir) + "\", \"publishDir\":\"" + CommandDispatcher.EscapeJsonString(publishDir) + "\", \"note\":\"Worker exits in 1s; detached helper copies binaries with retries and kills any worker that respawned mid-copy so the gateway brings up a fresh one. Inspect '" + CommandDispatcher.EscapeJsonString(System.IO.Path.Combine(publishDir, "worker_reload.last_result.json")) + "' for the copy outcome.\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("WorkerReload failed: " + ex.Message);
                return "{\"status\":\"Error\", \"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string DeleteObject(string target, string typeFilter, bool confirm)
        {
            try
            {
                var kb = _kbService.GetKB();
                if (kb == null) return "{\"status\":\"Error\", \"error\":\"No KB open\"}";

                if (!confirm)
                {
                    return "{\"status\":\"Error\", \"error\":\"Delete requires explicit confirm=true (irreversible operation).\"}";
                }

                var obj = FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

                string objName = obj.Name;
                string objType = obj.TypeDescriptor?.Name ?? "Unknown";
                Guid objGuid = obj.Guid;

                Logger.Info(string.Format("Deleting Object: {0} ({1}, guid={2})", objName, objType, objGuid));

                obj.Delete();

                Logger.Info(string.Format("Object deleted: {0} ({1})", objName, objType));

                // Keep the search index honest: without this, list_objects keeps returning
                // the deleted object for several minutes until a full reindex. Same
                // mechanism CreateObject uses, but in reverse.
                try
                {
                    var idx = _kbService?.GetIndexCache();
                    if (idx != null) idx.RemoveEntry(objType, objName);
                }
                catch (Exception ex) { Logger.Error("DeleteObject: index RemoveEntry failed for " + objName + ": " + ex.Message); }

                return "{\"status\":\"Success\", \"deleted\":\"" + CommandDispatcher.EscapeJsonString(objName) + "\", \"type\":\"" + CommandDispatcher.EscapeJsonString(objType) + "\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("DeleteObject failed: " + ex.Message);
                return "{\"status\":\"Error\", \"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Mirrors the SDT init: a freshly created Transaction with zero attributes fails the
        // SDK validation on Save. We seed it with a Numeric(4) key attribute named
        // "<TrnName>Id" — same convention the GeneXus IDE uses when you create a new Trn.
        private static void InitializeTransactionWithDefaultKey(Artech.Genexus.Common.Objects.Transaction trn, string trnName)
        {
            try
            {
                dynamic root = null;
                try { root = trn.Structure?.Root; } catch { }
                if (root == null) { Logger.Error("InitializeTransactionWithDefaultKey: Structure.Root null for " + trnName); return; }

                // If, somehow, attributes already exist, leave the Trn alone.
                try { foreach (var _ in root.Attributes) return; } catch { }

                string keyName = trnName + "Id";

                // Reuse an existing global Attribute with the conventional "<TrnName>Id" name;
                // otherwise create one (Numeric(4)) — same convention the GeneXus IDE uses.
                Artech.Genexus.Common.Objects.Attribute globalAttr = null;
                try { globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(trn.Model, keyName); }
                catch (Exception ex) { Logger.Error("InitializeTransactionWithDefaultKey: global attr lookup failed: " + ex.Message); }

                if (globalAttr == null)
                {
                    try
                    {
                        var attrGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                        var newAttr = KBObject.Create(trn.Model, attrGuid);
                        newAttr.Name = keyName;
                        globalAttr = newAttr as Artech.Genexus.Common.Objects.Attribute;
                        if (globalAttr != null)
                        {
                            try { globalAttr.Type = Artech.Genexus.Common.eDBType.NUMERIC; } catch { }
                            try { globalAttr.Length = 4; } catch { }
                            try { globalAttr.Decimals = 0; } catch { }
                        }
                        newAttr.Save();
                        if (globalAttr == null) globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(trn.Model, keyName);
                        Logger.Info("InitializeTransactionWithDefaultKey: created global Attribute " + keyName);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("InitializeTransactionWithDefaultKey: global Attribute creation failed: " + (ex.InnerException?.Message ?? ex.Message));
                        return;
                    }
                }

                if (globalAttr == null)
                {
                    Logger.Error("InitializeTransactionWithDefaultKey: global Attribute still null for " + keyName);
                    return;
                }

                // TransactionLevel exposes a typed AddAttribute(Attribute) that returns the wrapped
                // TransactionAttribute. Use it directly via dynamic dispatch (root is dynamic).
                try
                {
                    var trnAttr = root.AddAttribute(globalAttr);
                    try { trnAttr.IsKey = true; } catch { }
                    Logger.Info("InitializeTransactionWithDefaultKey: added key '" + keyName + "' to " + trnName);
                }
                catch (Exception ex)
                {
                    Logger.Error("InitializeTransactionWithDefaultKey: AddAttribute failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("InitializeTransactionWithDefaultKey failed: " + ex.Message);
            }
        }

        private static readonly Guid SDT_STRUCTURE_PART_GUID = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        private static void InitializeSDTWithDefaultItem(KBObject sdt, string sdtName)
        {
            try
            {
                KBObjectPart structure = null;
                foreach (KBObjectPart p in sdt.Parts)
                {
                    if (p.Type == SDT_STRUCTURE_PART_GUID) { structure = p; break; }
                    try {
                        string descName = p.TypeDescriptor?.Name ?? "";
                        string className = p.GetType().Name;
                        if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                        { structure = p; break; }
                    } catch { }
                }

                if (structure == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: SDTStructurePart not found for " + sdtName);
                    return;
                }

                dynamic ds = structure;
                dynamic root = null;
                try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }
                if (root == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: Root not found for " + sdtName);
                    return;
                }

                dynamic items = null;
                try { items = root.Items; } catch { try { items = root.Children; } catch { } }
                if (items == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: items collection not found for " + sdtName);
                    return;
                }

                // Skip if structure is already populated
                try {
                    foreach (dynamic existing in items) { return; }
                } catch { }

                Type rootType = ((object)root).GetType();
                var asm = rootType.Assembly;

                // Preferred path: invoke the real SDK API root.AddItem(string, eDBType) — same
                // approach SdtDslParser uses. Ctor + items.Add doesn't work because SDTItem has
                // no public ctor we can satisfy; that path always returned null and left the
                // SDT empty, which then made Save() reject it.
                Type eDBTypeT = asm.GetType("Artech.Genexus.Common.eDBType");
                if (eDBTypeT != null)
                {
                    MethodInfo addItem = rootType.GetMethod("AddItem", new Type[] { typeof(string), eDBTypeT });
                    if (addItem != null)
                    {
                        try
                        {
                            object varchar = Enum.Parse(eDBTypeT, "VARCHAR");
                            object added = addItem.Invoke(root, new object[] { "Item1", varchar });
                            if (added != null)
                            {
                                Logger.Info("InitializeSDTWithDefaultItem: default 'Item1' added to " + sdtName + " via AddItem(string, eDBType)");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("InitializeSDTWithDefaultItem: AddItem(string, eDBType) threw for " + sdtName + ": " + (ex.InnerException?.Message ?? ex.Message));
                        }
                    }
                    else
                    {
                        var sigs = string.Join("; ", rootType.GetMethods().Where(m => m.Name == "AddItem").Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
                        Logger.Error("InitializeSDTWithDefaultItem: AddItem(string, eDBType) not found on " + rootType.FullName + ". Sigs=[" + sigs + "]");
                    }
                }
                else
                {
                    Logger.Error("InitializeSDTWithDefaultItem: Artech.Genexus.Common.eDBType not resolvable in " + asm.GetName().Name);
                }

                // Fallback: legacy ctor path (kept in case SDK API surface differs in some build).
                Type sdtItemType = null;
                string[] namespaces = { "Artech.Genexus.Common.Parts", "Artech.Genexus.Common.Objects", "Artech.Genexus.Common", "Artech.Genexus.Common.Parts.SDT", rootType.Namespace };
                foreach (var ns in namespaces)
                {
                    if (string.IsNullOrEmpty(ns)) continue;
                    sdtItemType = asm.GetType(ns + ".SDTItem") ?? asm.GetType(ns + ".SDTLevel") ?? asm.GetType(ns + ".StructureItem") ?? asm.GetType(ns + ".StructureLevel");
                    if (sdtItemType != null) break;
                }
                if (sdtItemType == null)
                {
                    foreach (var loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var t in loadedAsm.GetTypes())
                            {
                                if (!t.IsClass || t.IsAbstract) continue;
                                string n = t.Name;
                                if (n.Equals("SDTItem", StringComparison.OrdinalIgnoreCase) || n.Equals("SDTLevel", StringComparison.OrdinalIgnoreCase))
                                {
                                    sdtItemType = t;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (sdtItemType != null) break;
                    }
                }
                if (sdtItemType == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: SDTItem type not resolved for " + sdtName + " (fallback path).");
                    return;
                }

                dynamic newItem = null;
                Exception lastCtorEx = null;
                object[][] ctorArgVariants = new object[][] {
                    new object[] { root },
                    new object[] { structure },
                    new object[] { },
                    new object[] { sdt },
                    new object[] { structure, root }
                };
                foreach (var args in ctorArgVariants)
                {
                    try { newItem = Activator.CreateInstance(sdtItemType, args); if (newItem != null) break; }
                    catch (Exception ex) { lastCtorEx = ex; }
                }
                if (newItem == null)
                {
                    Logger.Error("InitializeSDTWithDefaultItem: ctor fallback failed for " + sdtName + ". LastEx: " + lastCtorEx?.Message);
                    return;
                }
                newItem.Name = "Item1";
                try
                {
                    if (eDBTypeT != null) newItem.Type = Enum.Parse(eDBTypeT, "VARCHAR");
                } catch { }
                items.Add(newItem);
                Logger.Info("InitializeSDTWithDefaultItem: default 'Item1' added to " + sdtName + " via ctor fallback");
            }
            catch (Exception ex)
            {
                Logger.Error("InitializeSDTWithDefaultItem failed: " + ex.Message);
            }
        }

        private static readonly ConcurrentDictionary<string, Guid> _typeGuidCache =
            new ConcurrentDictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a friendly object-type name (e.g. "WebPanel", "Dashboard", "WorkflowDiagram") to
        /// the KBObject type Guid. First consults a static table of typed-wrapper descriptors;
        /// then falls back to reading the matching static Guid field on
        /// <c>Artech.Genexus.Common.ObjClass</c> via reflection. Returns Guid.Empty when unknown.
        /// </summary>
        private static Guid ResolveObjectTypeGuid(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return Guid.Empty;
            string key = NormalizeTypeAlias(type);
            if (_typeGuidCache.TryGetValue(key, out var cached)) return cached;

            Guid g = ResolveFromTypedDescriptor(key);
            if (g == Guid.Empty) g = ResolveFromObjClassField(key);
            if (g != Guid.Empty) _typeGuidCache[key] = g;
            return g;
        }

        private static string NormalizeTypeAlias(string type)
        {
            string t = type.Trim();
            if (t.Equals("StructuredDataType", StringComparison.OrdinalIgnoreCase)) return "SDT";
            if (t.Equals("BusinessProcessDiagram", StringComparison.OrdinalIgnoreCase)) return "WorkflowDiagram";
            if (t.Equals("BPD", StringComparison.OrdinalIgnoreCase)) return "WorkflowDiagram";
            if (t.Equals("PanelForSD", StringComparison.OrdinalIgnoreCase)) return "SDPanel";
            return t;
        }

        private static Guid ResolveFromTypedDescriptor(string type)
        {
            try
            {
                if (type.Equals("Procedure", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Procedure>().Id;
                if (type.Equals("Transaction", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Transaction>().Id;
                if (type.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.WebPanel>().Id;
                if (type.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.SDT>().Id;
                if (type.Equals("DataProvider", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataProvider>().Id;
                if (type.Equals("Attribute", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                if (type.Equals("Table", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Table>().Id;
                if (type.Equals("Domain", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Domain>().Id;
                if (type.Equals("DataSelector", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataSelector>().Id;
                if (type.Equals("DataView", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DataView>().Id;
                if (type.Equals("Index", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Index>().Id;
                if (type.Equals("ExternalObject", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.ExternalObject>().Id;
                if (type.Equals("Theme", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Theme>().Id;
                if (type.Equals("Image", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Image>().Id;
                if (type.Equals("Menu", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Menu>().Id;
                if (type.Equals("Menubar", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Menubar>().Id;
                if (type.Equals("Stencil", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Stencil>().Id;
                if (type.Equals("UserControl", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.UserControl>().Id;
                if (type.Equals("WorkPanel", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.WorkPanel>().Id;
                if (type.Equals("Report", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Report>().Id;
                if (type.Equals("API", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.API>().Id;
                if (type.Equals("URLRewrite", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.URLRewrite>().Id;
                if (type.Equals("MiniApp", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.MiniApp>().Id;
                if (type.Equals("SuperApp", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.SuperApp>().Id;
                if (type.Equals("DesignSystem", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.DesignSystem>().Id;
                if (type.Equals("ColorPalette", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.ColorPalette>().Id;
                if (type.Equals("OfflineDatabase", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.OfflineDatabase>().Id;
                if (type.Equals("Group", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Group>().Id;
                if (type.Equals("Language", StringComparison.OrdinalIgnoreCase)) return KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Language>().Id;
            }
            catch (Exception ex)
            {
                Logger.Error("ResolveFromTypedDescriptor failed for " + type + ": " + ex.Message);
            }
            return Guid.Empty;
        }

        private static Type _objClassType;

        private static Guid ResolveFromObjClassField(string type)
        {
            // Static Guid fields on Artech.Genexus.Common.ObjClass cover Dashboard, SDPanel, Query,
            // WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, WorkWithDevices, etc. —
            // anything the IDE creates that doesn't have its own typed wrapper.
            Type t = _objClassType;
            if (t == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = asm.GetName().Name;
                    if (n == null || !n.StartsWith("Artech.Genexus.Common", StringComparison.Ordinal)) continue;
                    try { t = asm.GetType("Artech.Genexus.Common.ObjClass", throwOnError: false); }
                    catch { continue; }
                    if (t != null) { _objClassType = t; break; }
                }
            }
            if (t == null) return Guid.Empty;

            var fi = t.GetField(type, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (fi == null) return Guid.Empty;
            try
            {
                return fi.GetValue(null) is Guid g ? g : Guid.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error("ResolveFromObjClassField: read " + type + " failed: " + ex.Message);
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Initialize a freshly-created Domain with caller-supplied dataType/length/decimals/signed/enumValues/basedOn.
        /// Defaults to Character(20) when no dataType is provided — matches the IDE's "new domain" default
        /// and gives the SDK a valid type before Save.
        /// </summary>
        /// <returns>_meta JObject describing what was applied (echoed to caller), or null on hard error.</returns>
        private static JObject InitializeDomain(KBObject domainObj, string domainName, JObject options, Artech.Architecture.Common.Objects.KnowledgeBase kb)
        {
            var meta = new JObject();
            try
            {
                string dataType = options?["dataType"]?.ToString();
                int? length = options?["length"]?.ToObject<int?>();
                int? decimals = options?["decimals"]?.ToObject<int?>();
                bool? signed = options?["signed"]?.ToObject<bool?>();
                string description = options?["description"]?.ToString();
                string basedOnName = options?["basedOn"]?.ToString();
                var enumArr = options?["enumValues"] as JArray;

                // basedOn short-circuits dataType: a domain-based-on-domain inherits its type.
                bool basedOnApplied = false;
                if (!string.IsNullOrEmpty(basedOnName))
                {
                    object basedOn = null;
                    try
                    {
                        foreach (var obj in kb.DesignModel.Objects.GetByName(null, null, basedOnName))
                        {
                            if (obj is Artech.Genexus.Common.Objects.Domain d) { basedOn = d; break; }
                        }
                    }
                    catch (Exception ex) { Logger.Error("InitializeDomain: basedOn lookup failed: " + ex.Message); }

                    if (basedOn == null)
                    {
                        meta["basedOnError"] = "Domain '" + basedOnName + "' not found in KB. Created standalone Character(20) instead.";
                    }
                    else if (!DomainPropertyApplier.ApplyDomainBasedOn(domainObj, basedOn))
                    {
                        meta["basedOnError"] = "Failed to apply DomainBasedOn=" + basedOnName + ".";
                    }
                    else
                    {
                        meta["basedOn"] = basedOnName;
                        basedOnApplied = true;
                    }
                }

                if (!basedOnApplied)
                {
                    if (string.IsNullOrEmpty(dataType)) dataType = "Character";
                    if (!length.HasValue && dataType.Equals("Character", StringComparison.OrdinalIgnoreCase)) length = 20;
                    if (!length.HasValue && dataType.Equals("VarChar", StringComparison.OrdinalIgnoreCase)) length = 40;
                    if (!length.HasValue && dataType.Equals("Numeric", StringComparison.OrdinalIgnoreCase)) length = 8;

                    if (!DomainPropertyApplier.ApplyPrimitive(domainObj, dataType, length, decimals, signed))
                    {
                        Logger.Error("InitializeDomain: ApplyPrimitive failed for " + domainName + " (dataType=" + dataType + ")");
                        meta["typeError"] = "Could not apply dataType='" + dataType + "'. Supported: Character, VarChar, Numeric, Date, DateTime, Time, Boolean, LongVarChar, Blob, Image, GUID.";
                    }
                    else
                    {
                        meta["dataType"] = dataType;
                        if (length.HasValue) meta["length"] = length.Value;
                        if (decimals.HasValue) meta["decimals"] = decimals.Value;
                        if (signed.HasValue) meta["signed"] = signed.Value;
                    }
                }

                if (!string.IsNullOrEmpty(description))
                {
                    try { domainObj.Description = description; } catch { /* best-effort */ }
                }

                if (enumArr != null && enumArr.Count > 0)
                {
                    var specs = new List<DomainEnumValueSpec>();
                    foreach (var item in enumArr)
                    {
                        if (!(item is JObject jo)) continue;
                        var ev = new DomainEnumValueSpec
                        {
                            Name = jo["name"]?.ToString(),
                            Value = jo["value"]?.ToString(),
                            Description = jo["description"]?.ToString()
                        };
                        if (!string.IsNullOrEmpty(ev.Name)) specs.Add(ev);
                    }

                    int applied = DomainPropertyApplier.ApplyEnumValues(domainObj, specs);
                    if (applied < 0)
                    {
                        meta["enumError"] = "Could not write EnumValues — SDK helper not resolvable. Domain saved without enum values; set them via IDE.";
                    }
                    else if (applied > 0)
                    {
                        var arr = new JArray();
                        foreach (var s in specs.Take(applied)) arr.Add(new JObject { ["name"] = s.Name, ["value"] = s.Value });
                        meta["enumValues"] = arr;
                        meta["enumHint"] = "Enum values applied. For Character domains the 'value' should be a quoted literal, e.g. \"\\\"A\\\"\". Verify via genexus_analyze name=" + domainName + " mode=summary.";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("InitializeDomain failed for " + domainName + ": " + ex.Message);
                meta["initError"] = ex.Message;
            }
            return meta;
        }

        public KBObject FindObject(string target, string typeFilter = null)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var sw = Stopwatch.StartNew();
            var kb = _kbService.GetKB();
            if (kb == null) return null;

            string typePart = typeFilter;
            string namePart = target.Trim();

            if (target.Contains(":") && typeFilter == null)
            {
                var parts = target.Split(new[] { ':' }, 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    Logger.Warn("FindObject: malformed 'Type:Name' target: " + target);
                    return null;
                }
                typePart = parts[0].Trim();
                namePart = parts[1].Trim();
            }

            // 1. FAST PATH: Use Search Index
            var index = GetIndex();
            if (index != null && index.Objects != null)
            {
                if (typePart != null)
                {
                    string key = string.Format("{0}:{1}", typePart, namePart);
                    if (index.Objects.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.Guid))
                    {
                        var obj = kb.DesignModel.Objects.Get(new Guid(entry.Guid));
                        if (obj != null) {
                            Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Index-Typed) in {1}ms", target, sw.ElapsedMilliseconds));
                            return obj;
                        }
                    }
                }
                else
                {
                    // Global search in index
                    // OPTIMIZATION: Prioritize logic types if no filter is provided
                    var matches = new List<SearchIndex.IndexEntry>();
                    foreach (var entry in index.Objects.Values)
                    {
                        if (string.Equals(entry.Name, namePart, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(entry);
                        }
                    }

                    if (matches.Count > 0)
                    {
                        // Order: 1. Non-Folders/Modules, 2. Logic Types (Procedure, WP, Trn), 3. Files/Images (last)
                        var prioritizedMatch = matches
                            .OrderBy(m => (m.Type == "Folder" || m.Type == "Module") ? 100 : 0)
                            .ThenBy(m => (m.Type == "File" || m.Type == "Image") ? 50 : 0)
                            .FirstOrDefault();

                        if (prioritizedMatch != null && !string.IsNullOrEmpty(prioritizedMatch.Guid))
                        {
                             var obj = kb.DesignModel.Objects.Get(new Guid(prioritizedMatch.Guid));
                             if (obj != null) return obj;
                        }
                    }
                }
            }

            // 2. SLOW PATH: Fallback to SDK GetByName (for safety with new objects not yet indexed)
            var sdkMatches = kb.DesignModel.Objects.GetByName(null, null, namePart);
            if (typePart != null)
            {
                foreach (KBObject obj in sdkMatches)
                {
                    if (obj.TypeDescriptor != null && string.Equals(obj.TypeDescriptor.Name, typePart, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug(string.Format("FindObject '{0}' SUCCESS (Typed-SDK) in {1}ms", target, sw.ElapsedMilliseconds));
                        return obj;
                    }
                }
            }

            // Global search, prioritizing non-container objects and avoiding Files if others exist
            KBObject firstLogicMatch = null;
            KBObject firstMatch = null;

            foreach (KBObject obj in sdkMatches)
            {
                if (firstMatch == null) firstMatch = obj;
                
                string type = obj.TypeDescriptor?.Name;
                if (type != "Folder" && type != "Module" && type != "File" && type != "Image")
                {
                    if (firstLogicMatch == null) firstLogicMatch = obj;
                }
            }

            var result = firstLogicMatch ?? firstMatch;
            if (result != null)
            {
                Logger.Debug(string.Format("FindObject '{0}' SUCCESS (SDK-Fallback) in {1}ms", target, sw.ElapsedMilliseconds));
            }
            return result;
        }

        public string ExtractAllParts(string target, string client = "ide", string typeFilter = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var obj = FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

                var result = new JObject { ["name"] = obj.Name, ["parts"] = new JObject() };
                string[] partsToFetch = { "Source", "Rules", "Events", "Variables", "Documentation", "Help" };

                foreach (var pName in partsToFetch)
                {
                    string partJson = ReadObjectSourceInternal(obj, pName, null, null, client);
                    try {
                        var pObj = JObject.Parse(partJson);
                        if (pObj["source"] != null)
                        {
                            ((JObject)result["parts"])[pName] = pObj["source"];
                        }
                    } catch { }
                }

                Logger.Info(string.Format("ExtractAllParts for {0} complete in {1}ms", obj.Name, sw.ElapsedMilliseconds));
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string ReadObject(string target, string typeFilter = null)
        {
            var obj = FindObject(target, typeFilter);
            if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

            var parts = new JArray();
            foreach (KBObjectPart p in obj.Parts)
            {
                parts.Add(new JObject { 
                    ["name"] = p.TypeDescriptor?.Name ?? p.Type.ToString(), 
                    ["guid"] = p.Type.ToString() 
                });
            }

            string parentName = null;
            string moduleName = null;

            try { parentName = obj.Parent?.Name; } catch { }
            try { moduleName = obj.Module?.Name; } catch { }

            return new JObject { 
                ["name"] = obj.Name, 
                ["type"] = obj.TypeDescriptor.Name,
                ["parent"] = parentName,
                ["module"] = moduleName,
                ["parts"] = parts
            }.ToString();
        }

        public string ReadObjectSource(string target, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false, string typeFilter = null)
        {
            var obj = FindObject(target, typeFilter);
            if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

            string resolvedPart = ResolvePartName(obj, partName);
            if (ShouldUseReadCache(client, minimize))
            {
                string cacheKey = BuildReadCacheKey(obj.Guid, resolvedPart, offset, limit, client, minimize);
                if (TryGetReadCache(cacheKey, out string cachedPayload))
                {
                    return cachedPayload;
                }

                string payload = ReadObjectSourceInternal(obj, resolvedPart, offset, limit, client, minimize);
                if (CanCachePayload(payload))
                {
                    SetReadCache(cacheKey, payload);
                }

                return payload;
            }

            return ReadObjectSourceInternal(obj, resolvedPart, offset, limit, client, minimize);
        }

        /// <summary>
        /// Read multiple named parts of an object in one call.
        /// Returns a JSON object: { name, type, parts: { Source: "...", Variables: "..." } }
        /// Parts that are not found or produce no source are silently omitted.
        /// When requestedParts is null/empty the full default set is returned (backward-compatible).
        /// </summary>
        public string ReadObjectSourceParts(string target, IEnumerable<string> requestedParts, string typeFilter = null)
        {
            var obj = FindObject(target, typeFilter);
            if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

            string[] partsToFetch = (requestedParts != null && requestedParts.Any())
                ? requestedParts.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray()
                : new[] { "Source", "Rules", "Events", "Variables", "Documentation", "Help" };

            var partsObj = new JObject();
            foreach (var pName in partsToFetch)
            {
                try
                {
                    string partJson = ReadObjectSourceInternal(obj, pName, null, null, "mcp", false);
                    var pObj = JObject.Parse(partJson);
                    if (pObj["source"] != null)
                        partsObj[pName] = pObj["source"];
                    else if (pObj["error"] == null)
                        // For XML/binary parts, include the raw response
                        partsObj[pName] = pObj;
                }
                catch { /* skip parts that error */ }
            }

            return new JObject
            {
                ["name"] = obj.Name,
                ["type"] = obj.TypeDescriptor?.Name,
                ["parts"] = partsObj
            }.ToString();
        }

        public void MarkReadCacheDirty(KBObject obj, string partName = null)
        {
            if (obj == null)
            {
                return;
            }

            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? null : partName.Trim().ToLowerInvariant();
            string objectPrefix = obj.Guid.ToString("N").ToLowerInvariant() + "|";
            foreach (var key in _readCache.Keys)
            {
                if (!key.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalizedPart != null && !key.StartsWith(objectPrefix + normalizedPart + "|", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _readCache.TryRemove(key, out _);
            }

            // SDK object cache invalidation is expensive; do it only after writes.
            InvalidateCache(obj);
        }

        public string ExportObjectToText(string target, string outputPath, string partName = null, string typeFilter = null, bool overwrite = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputPath))
                    return Models.McpResponse.Error("Output path is required.", target);

                var obj = FindObject(target, typeFilter);
                if (obj == null) return HealingService.FormatNotFoundError(target, GetIndex());

                string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                string exportJson = ReadObjectSourceInternal(obj, normalizedPart, 0, int.MaxValue, "mcp", false);
                JObject exportResult = JObject.Parse(exportJson);
                string source = exportResult["source"]?.ToString();
                if (string.IsNullOrEmpty(source))
                {
                    return Models.McpResponse.Error(
                        "Export failed",
                        target,
                        normalizedPart,
                        exportResult["error"]?.ToString() ?? "The object part did not return text content.",
                        obj.Name,
                        obj.TypeDescriptor?.Name);
                }

                string fullPath = Path.GetFullPath(outputPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(fullPath) && !overwrite)
                    return Models.McpResponse.Error("Output file already exists. Set overwrite=true to replace it.", fullPath);

                File.WriteAllText(fullPath, source, new UTF8Encoding(false));
                return Models.McpResponse.Success("ExportText", target, new JObject
                {
                    ["part"] = normalizedPart,
                    ["path"] = fullPath,
                    ["bytes"] = new FileInfo(fullPath).Length
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Export failed", target, partName, ex.Message);
            }
        }

        public string ImportObjectFromText(string target, string inputPath, string partName = null, string typeFilter = null)
        {
            try
            {
                if (_writeService == null)
                    return Models.McpResponse.Error("Import failed", target, partName, "Write service is not available.");

                if (string.IsNullOrWhiteSpace(inputPath))
                    return Models.McpResponse.Error("Input path is required.", target);

                string fullPath = Path.GetFullPath(inputPath);
                if (!File.Exists(fullPath))
                    return Models.McpResponse.Error("Input file not found.", fullPath);

                string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                var obj = FindObject(target, typeFilter);
                if (obj == null)
                {
                    if (string.IsNullOrWhiteSpace(typeFilter))
                    {
                        return Models.McpResponse.Error(
                            "Import failed",
                            target,
                            normalizedPart,
                            "Object not found. Provide 'type' to create it before importing.");
                    }

                    string createResult = CreateObject(typeFilter, target);
                    JObject createJson = JObject.Parse(createResult);
                    if (!string.Equals(createJson["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                    {
                        return createResult;
                    }
                }

                string importedText = File.ReadAllText(fullPath);
                string writeResult = _writeService.WriteObject(target, normalizedPart, importedText, typeFilter, autoValidate: false);
                JObject writeJson = JObject.Parse(writeResult);

                if (string.Equals(writeJson["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                {
                    writeJson["path"] = fullPath;
                    writeJson["part"] = normalizedPart;
                    writeJson["importedBytes"] = new FileInfo(fullPath).Length;
                }

                return writeJson.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Import failed", target, partName, ex.Message);
            }
        }

        private string ReadObjectSourceInternal(KBObject obj, string partName, int? offset = null, int? limit = null, string client = "ide", bool minimize = false)
        {
            partName = ResolvePartName(obj, partName);

            Logger.Info($"ReadObjectSourceInternal: {obj.Name} (Part: {partName}, Client: {client})");
            var sw = Stopwatch.StartNew();
            try
            {
                string targetName = obj.Name;

                if (WebFormXmlHelper.IsVisualPart(partName))
                {
                    string xml = WebFormXmlHelper.ReadEditableXml(obj);
                    if (string.IsNullOrEmpty(xml))
                    {
                        var diagnosticPart = WebFormXmlHelper.GetWebFormPart(obj);
                        string details = diagnosticPart == null 
                            ? "No visual part (Layout/WebForm) found." 
                            : $"Rejected part {diagnosticPart.TypeDescriptor?.Name} (Class: {diagnosticPart.GetType().Name}, GUID: {diagnosticPart.Type}) as a valid visual part.";

                        return Models.McpResponse.Error(
                            "Visual XML not available",
                            targetName,
                            partName,
                            details,
                            obj.Name,
                            obj.TypeDescriptor?.Name,
                            new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)));
                    }

                    var visualResult = new JObject
                    {
                        ["part"] = partName,
                        ["contentType"] = "application/xml",
                        ["xmlKind"] = "GxMultiForm"
                    };
                    ProcessTextResponse(xml, visualResult, client);
                    return visualResult.ToString();
                }

                if (PatternAnalysisService.IsPatternPart(partName))
                {
                    global::Artech.Architecture.Common.Objects.KBObject resolvedObject = null;
                    string resolvedPartName = partName;
                    string patternXml = _patternAnalysisService?.ReadPatternPartXml(obj, partName, out resolvedObject, out resolvedPartName);
                    if (string.IsNullOrEmpty(patternXml))
                    {
                        // PatternVirtual fallback: serialise the matching part directly when the WWP+ analyser bails.
                        try
                        {
                            var rawPart = obj.Parts.Cast<global::Artech.Architecture.Common.Objects.KBObjectPart>()
                                .FirstOrDefault(p =>
                                    string.Equals(p.TypeDescriptor?.Name, partName, StringComparison.OrdinalIgnoreCase) ||
                                    p.GetType().Name.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (rawPart != null)
                            {
                                patternXml = rawPart.SerializeToXml();
                                resolvedPartName = rawPart.TypeDescriptor?.Name ?? rawPart.GetType().Name;
                            }
                        }
                        catch (Exception fbEx) { Logger.Debug("[PatternRead] raw-serialize fallback failed: " + fbEx.Message); }

                        if (string.IsNullOrEmpty(patternXml))
                            return Models.McpResponse.Error(
                                "Pattern XML not available",
                                targetName,
                                partName,
                                "The requested WorkWithPlus pattern part could not be resolved through the current SDK path.",
                                obj.Name,
                                obj.TypeDescriptor?.Name,
                                new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj)));
                    }

                    var patternResult = new JObject
                    {
                        ["part"] = partName,
                        ["contentType"] = "application/xml",
                        ["xmlKind"] = resolvedPartName
                    };

                    if (resolvedObject != null && resolvedObject.Guid != obj.Guid)
                    {
                        patternResult["resolvedObject"] = resolvedObject.Name;
                        patternResult["resolvedType"] = resolvedObject.TypeDescriptor?.Name;
                    }

                    ProcessTextResponse(patternXml, patternResult, client);
                    return patternResult.ToString();
                }

                Guid partGuid = GxMcp.Worker.Structure.PartAccessor.GetPartGuid(obj.TypeDescriptor.Name, partName);
                
                KBObjectPart part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);

                JObject result = new JObject();
                result["part"] = partName;

                // Virtual/DSL Parts (Structure for Trn/Table/SDT)
                // We process this BEFORE the generic part check because Tables might not have a physical Part GUID mapped,
                // and even if they do, we want our custom DSL representation.
                bool isStructurePartAlias = partName.Equals("Structure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("TableStructure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("SDTStructure", StringComparison.OrdinalIgnoreCase)
                    || partName.Equals("TrnStructure", StringComparison.OrdinalIgnoreCase);
                // Friction-report #9b: use the SDK type test (is Table) instead of comparing
                // GetType().Name as a string — subclassed/proxied Table instances were falling
                // through to the generic part.SerializeToXml() branch and returning <Properties />.
                bool isStructurableObject = obj is Transaction
                    || obj is Table
                    || string.Equals(obj.TypeDescriptor?.Name, "Table", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(obj.TypeDescriptor?.Name, "Transaction", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(obj.TypeDescriptor?.Name, "SDT", StringComparison.OrdinalIgnoreCase);
                if (isStructurePartAlias && isStructurableObject)
                {
                    string structureText = StructureParser.SerializeToText(obj);
                    ProcessTextResponse(structureText, result, client);
                    Logger.Info("ReadSource (Structure DSL) SUCCESS");
                    return result.ToString();
                }

                if (part == null)
                {
                    result["error"] = $"Part '{partName}' not found in {obj.Name}";
                    try
                    {
                        var avail = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                        if (avail != null && avail.Length > 0)
                        {
                            result["availableParts"] = new JArray(avail);
                            result["hint"] = $"Valid parts for {obj.TypeDescriptor?.Name ?? "object"}: {string.Join(", ", avail)}.";
                        }
                    }
                    catch { }
                    return result.ToString();
                }

                // Handle Variables Part specially
                if (part.GetType().Name.Equals("VariablesPart"))
                {
                    string varText = VariableInjector.GetVariablesAsText((dynamic)part);
                    ProcessTextResponse(varText, result, client);
                    Logger.Info("ReadSource (Variables) SUCCESS");
                }
                else if (part is ISource sourcePart)
                {
                    string content = sourcePart.Source ?? "";
                    if (minimize && content.Length > 5000)
                    {
                        content = content.Substring(0, 2500) + "\n... [TRUNCATED FOR BREVITY - USE PAGINATION] ...\n" + content.Substring(content.Length - 1000);
                    }
                    ProcessSourceContent(obj, content, offset, limit, result, client);
                    Logger.Info("ReadSource (ISource) SUCCESS");
                }
                else
                {
                    // Reflection Fallback for Data Providers and other parts that encapsulate a Source string but don't implement ISource natively.
                    var contentProp = part.GetType().GetProperty("Source", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                   ?? part.GetType().GetProperty("Content", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (contentProp != null && contentProp.CanRead && contentProp.PropertyType == typeof(string))
                    {
                        string content = (string)contentProp.GetValue(part) ?? "";
                        if (minimize && content.Length > 5000)
                        {
                            content = content.Substring(0, 2500) + "\n... [TRUNCATED FOR BREVITY] ...\n" + content.Substring(content.Length - 1000);
                        }
                        ProcessSourceContent(obj, content, offset, limit, result, client);
                        Logger.Info("ReadSource (Reflection) SUCCESS");
                    }
                    else
                    {
                        string xml = part.SerializeToXml();
                        ProcessTextResponse(xml, result, client);
                        Logger.Info("ReadSource (XML) SUCCESS");
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private static string ResolvePartName(KBObject obj, string partName)
        {
            if (!string.IsNullOrWhiteSpace(partName))
            {
                return partName;
            }

            if (obj is Procedure) return "Source";
            if (obj is Transaction || obj is WebPanel) return "Events";
            return "Source";
        }

        private static bool ShouldUseReadCache(string client, bool minimize)
        {
            return string.Equals(client, "mcp", StringComparison.OrdinalIgnoreCase) && !minimize;
        }

        private static string BuildReadCacheKey(Guid objectGuid, string partName, int? offset, int? limit, string client, bool minimize)
        {
            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "source" : partName.Trim().ToLowerInvariant();
            string normalizedClient = string.IsNullOrWhiteSpace(client) ? "mcp" : client.Trim().ToLowerInvariant();
            int normalizedOffset = offset ?? -1;
            int normalizedLimit = limit ?? -1;
            return objectGuid.ToString("N").ToLowerInvariant() + "|" + normalizedPart + "|" + normalizedOffset + "|" + normalizedLimit + "|" + normalizedClient + "|" + (minimize ? "1" : "0");
        }

        private static bool TryGetReadCache(string key, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!_readCache.TryGetValue(key, out var entry) || entry == null)
            {
                return false;
            }

            if (DateTime.UtcNow - entry.UpdatedUtc > ReadCacheTtl)
            {
                _readCache.TryRemove(key, out _);
                return false;
            }

            payload = entry.Payload;
            return !string.IsNullOrWhiteSpace(payload);
        }

        private static void SetReadCache(string key, string payload)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            _readCache[key] = new ReadCacheEntry
            {
                Payload = payload,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static bool CanCachePayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                var json = JObject.Parse(payload);
                return json["error"] == null;
            }
            catch
            {
                return false;
            }
        }

        private void ProcessSourceContent(KBObject obj, string content, int? offset, int? limit, JObject result, string client = "ide")
        {
            // v2.3.8 (Task 6.2) — delegate to ReadPagination so byte-budget + line-budget
            // are applied uniformly and suggestedNextOffset/Limit surface for chained reads.
            var page = GxMcp.Worker.Helpers.ReadPagination.ApplyDefault(content, offset, limit, client);
            bool mcpDefault = !offset.HasValue && !limit.HasValue && client == "mcp";
            bool includeDerivedMetadata = client != "mcp";

            result["isTruncatedByWorker"] = page.Truncated;
            result["truncated"] = page.Truncated;
            if (page.Truncated && mcpDefault)
                result["message"] = "MCP read defaulted to ~200 lines / 16 KB to control context size. Use offset/limit to paginate, or limit=0 to read in full.";

            string paginatedContent = page.Content;
            if (page.Truncated)
                paginatedContent += "\n\n// ... [CONTENT TRUNCATED. USE PAGINATION (offset/limit) TO READ FURTHER] ... //\n";

            ProcessTextResponse(paginatedContent, result, client);
            result["offset"] = page.Offset;
            result["limit"] = page.LinesReturned;
            result["totalLines"] = page.TotalLines;
            result["totalBytes"] = page.TotalBytes;
            if (page.SuggestedNextOffset.HasValue) result["suggestedNextOffset"] = page.SuggestedNextOffset.Value;
            if (page.SuggestedNextLimit.HasValue) result["suggestedNextLimit"] = page.SuggestedNextLimit.Value;

            if (includeDerivedMetadata)
            {
                AddVariableMetadata(obj, paginatedContent, result);
                AddCallSignatures(obj, paginatedContent, result);
            }
        }

        private void ProcessTextResponse(string text, JObject result, string client)
        {
            result["isEmpty"] = string.IsNullOrEmpty(text);

            if (client == "mcp")
            {
                Logger.Debug("ProcessTextResponse: Using Plain Text for MCP");
                result["source"] = text;
                result["isBase64"] = false;
            }
            else
            {
                Logger.Debug($"ProcessTextResponse: Using Base64 for {client}");
                result["source"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                result["isBase64"] = true;
            }
        }

        private void AddCallSignatures(KBObject obj, string source, JObject result)
        {
            try
            {
                var calledObjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Regex for common call patterns in GeneXus
                var callMatches = System.Text.RegularExpressions.Regex.Matches(source, 
                    @"\b(?:call|udp|submit)\s*\(\s*(\w+)|\b(\w+)\s*\.\s*(?:call|udp|submit)\b", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in callMatches)
                {
                    string name = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
                    calledObjectNames.Add(name);
                }

                if (calledObjectNames.Count > 0)
                {
                    var calls = new JArray();
                    foreach (var name in calledObjectNames)
                    {
                        var target = FindObject(name);
                        if (target != null && target.Guid != obj.Guid)
                        {
                            var (parmRule, parms) = GetParametersInternal(target);
                            if (!string.IsNullOrEmpty(parmRule))
                            {
                                var cObj = new JObject();
                                cObj["name"] = target.Name;
                                cObj["type"] = target.TypeDescriptor.Name;
                                cObj["parmRule"] = parmRule;
                                calls.Add(cObj);
                            }
                        }
                    }
                    if (calls.Count > 0) result["calls"] = calls;
                }
            }
            catch (Exception ex) { Logger.Debug("AddCallSignatures failed: " + ex.Message); }
        }

        private void AddVariableMetadata(KBObject obj, string source, JObject result)
        {
            try
            {
                // Nirvana v19.4: Auto-Inject Full Context (Variables + Data Schema + Pattern)
                var varPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (varPart != null)
                {
                    var referencedVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matches = System.Text.RegularExpressions.Regex.Matches(source, @"&(\w+)");
                    foreach (System.Text.RegularExpressions.Match match in matches) {
                        referencedVars.Add(match.Groups[1].Value);
                    }

                    if (referencedVars.Count > 0)
                    {
                        var variables = new JArray();
                        var varListProp = varPart.GetType().GetProperty("Variables");
                        if (varListProp != null)
                        {
                            var varList = varListProp.GetValue(varPart) as System.Collections.IEnumerable;
                            if (varList != null)
                            {
                                foreach (object vObj in varList)
                                {
                                    dynamic v = vObj;
                                    string vName = v.Name;
                                    if (referencedVars.Contains(vName))
                                    {
                                        variables.Add(new JObject {
                                            ["name"] = vName,
                                            ["type"] = v.Type.ToString(),
                                            ["length"] = Convert.ToInt32(v.Length),
                                            ["decimals"] = Convert.ToInt32(v.Decimals),
                                            ["isCollection"] = (bool)v.IsCollection
                                        });
                                    }
                                }
                            }
                        }
                        if (variables.Count > 0) result["variables"] = variables;
                    }
                }

                // Inject Data Context (Tables used in this object)
                if (_dataInsightService != null)
                {
                    try {
                        var dataContextJson = _dataInsightService.GetDataContext(obj.Name);
                        if (!string.IsNullOrEmpty(dataContextJson) && !dataContextJson.Contains("\"error\""))
                        {
                            var dataContext = JObject.Parse(dataContextJson);
                            if (dataContext["dataSchema"] != null) result["dataSchema"] = dataContext["dataSchema"];
                            if (dataContext["patternMetadata"] != null) result["patternMetadata"] = dataContext["patternMetadata"];
                        }
                    } catch { /* Silent fail to ensure read stability */ }
                }
            }
            catch { }
        }

        public class ParameterInfo
        {
            public string Name { get; set; }
            public string Accessor { get; set; }
            public string Type { get; set; }
        }

        public (string parmRule, List<ParameterInfo> parameters) GetParametersInternal(KBObject obj)
        {
            string parmRule = "";
            var parameters = new List<ParameterInfo>();

            try
            {
                if (obj is Procedure proc) parmRule = proc.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is Transaction trn) parmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is WebPanel wp) parmRule = wp.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase));
                else if (obj is DataProvider dp) parmRule = (string)dp.GetType().GetProperty("Rules")?.GetValue(dp)?.GetType().GetProperty("Source")?.GetValue(((dynamic)dp).Rules) ?? "";
                
                if (string.IsNullOrEmpty(parmRule) && obj is DataProvider dp2) {
                    // DataProvider might have parm in Source instead of Rules in some versions/objects
                    try { 
                        string sourceStr = ((dynamic)dp2).Source.Source;
                        foreach (string line in sourceStr.Split('\n'))
                        {
                            if (line.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase))
                            {
                                parmRule = line;
                                break;
                            }
                        }
                    } catch {}
                }

                if (!string.IsNullOrEmpty(parmRule))
                {
                    parmRule = parmRule.Trim();
                    var match = System.Text.RegularExpressions.Regex.Match(parmRule, @"parm\s*\((.*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var parmContent = match.Groups[1].Value;
                        var parts = parmContent.Split(',');
                        foreach (var part in parts)
                        {
                            var p = part.Trim();
                            var pInfo = new ParameterInfo { Name = p, Accessor = "in", Type = "Unknown" };
                            if (p.StartsWith("inout:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "inout"; pInfo.Name = p.Substring(6).Trim(); }
                            else if (p.StartsWith("in:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "in"; pInfo.Name = p.Substring(3).Trim(); }
                            else if (p.StartsWith("out:", StringComparison.OrdinalIgnoreCase)) { pInfo.Accessor = "out"; pInfo.Name = p.Substring(4).Trim(); }
                            
                            if (pInfo.Name.StartsWith("&")) pInfo.Name = pInfo.Name.Substring(1);
                            parameters.Add(pInfo);
                        }
                    }
                }

                TryResolveParameterTypes(obj, parameters);
            }
            catch { }

            return (parmRule, parameters);
        }

        private static void TryResolveParameterTypes(KBObject obj, List<ParameterInfo> parameters)
        {
            if (parameters == null || parameters.Count == 0) return;
            try
            {
                dynamic vPart = obj.Parts
                    .Cast<KBObjectPart>()
                    .FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart", StringComparison.OrdinalIgnoreCase));
                if (vPart == null) return;

                var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in vPart.Variables)
                {
                    string name = null;
                    string formatted = null;
                    try
                    {
                        name = (string)((dynamic)v).Name;
                        string baseType = ((dynamic)v).Type?.ToString() ?? "Unknown";
                        int len = 0, dec = 0;
                        try { len = (int)((dynamic)v).Length; } catch { }
                        try { dec = (int)((dynamic)v).Decimals; } catch { }

                        // SDT-typed: prefer SDT name when available
                        string sdtName = null;
                        try { sdtName = ((dynamic)v).PromptInformation?.SDTName as string; } catch { }
                        if (!string.IsNullOrEmpty(sdtName))
                        {
                            formatted = sdtName;
                        }
                        else if (len > 0)
                        {
                            formatted = dec > 0 ? $"{baseType}({len},{dec})" : $"{baseType}({len})";
                        }
                        else
                        {
                            formatted = baseType;
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(formatted))
                        byName[name] = formatted;
                }

                foreach (var p in parameters)
                {
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    if (byName.TryGetValue(p.Name, out var t) && !string.IsNullOrEmpty(t))
                        p.Type = t;
                }
            }
            catch { /* keep "Unknown" on failure */ }
        }

        private static void InvalidateCache(object obj)
        {
            try
            {
                var type = typeof(Artech.Architecture.Common.Objects.KBObject).Assembly.GetType("Artech.Architecture.Common.Cache.SingleInstanceModelObjectCache");
                if (type != null)
                {
                    var method = type.GetMethod("Invalidate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    method?.Invoke(null, new object[] { obj });
                    Logger.Debug("InvalidateCache: Object invalidated via reflection.");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("InvalidateCache reflection failed: " + ex.Message);
            }
        }
    }
}
