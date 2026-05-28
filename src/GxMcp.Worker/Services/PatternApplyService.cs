using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Applies (and re-applies) GeneXus patterns to KBObjects. Equivalent to the
    /// IDE's "Right-click → Apply Pattern" entry point.
    ///
    /// SDK surface lives in Artech.Packages.Patterns.dll inside the GeneXus
    /// install's Packages\ folder, which is NOT statically referenced by the
    /// worker. We load it on demand by reflection so the worker can build/run
    /// on machines without the pattern engine installed; in that case we return
    /// a graceful "pattern_unavailable" status instead of throwing.
    ///
    /// Tests inject a fake <see cref="IPatternEngineAdapter"/> so the unit
    /// suite does not depend on the live SDK / license / open KB.
    /// </summary>
    public class PatternApplyService
    {
        // Well-known pattern GUIDs. WorkWithPlus is the only one in scope for W2;
        // additional pattern keys can be registered here as we expose more tools.
        public static readonly Guid WorkWithPlusPatternId = new Guid("07135890-56fc-489b-b408-063722fa9f7d");

        private static readonly Dictionary<string, Guid> KnownPatterns = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            { "WorkWithPlus", WorkWithPlusPatternId },
            { "WWP", WorkWithPlusPatternId },
        };

        private readonly ObjectService _objectService;
        private readonly IPatternEngineAdapter _engine;
        // Test seam: when set, used instead of _objectService.FindObject to resolve
        // the parent KBObject. The live path always uses _objectService.
        private readonly Func<string, KBObject> _findObjectOverride;

        public PatternApplyService(ObjectService objectService)
            : this(objectService, new ReflectionPatternEngineAdapter(), null)
        {
        }

        public PatternApplyService(ObjectService objectService, IPatternEngineAdapter engine)
            : this(objectService, engine, null)
        {
        }

        // Test ctor: lets unit tests bypass the live SDK FindObject lookup.
        internal PatternApplyService(ObjectService objectService, IPatternEngineAdapter engine, Func<string, KBObject> findObjectOverride)
        {
            _objectService = objectService;
            _engine = engine;
            _findObjectOverride = findObjectOverride;
        }

        private KBObject ResolveObject(string objectName)
        {
            if (_findObjectOverride != null) return _findObjectOverride(objectName);
            return _objectService != null ? _objectService.FindObject(objectName) : null;
        }

        /// <summary>
        /// First-time apply of a pattern to a parent object.
        /// </summary>
        public string ApplyPattern(string objectName, string patternKey, JObject settings = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                    return McpResponse.Error("Object name is required.", objectName);
                if (string.IsNullOrWhiteSpace(patternKey))
                    return McpResponse.Error("Pattern key is required.", objectName);

                if (!TryResolvePatternId(patternKey, out Guid patternId))
                    return PatternUnavailable(patternKey, "Unknown pattern key. Pass 'WorkWithPlus' or a known GUID.");

                KBObject obj = ResolveObject(objectName);
                if (obj == null)
                {
                    // Reuse existing not-found shape (best-effort: tests may inject a null _objectService)
                    if (_objectService != null)
                        return HealingService.FormatNotFoundError(objectName, _objectService.GetLoadedIndexOrNull());
                    return McpResponse.Error("Object not found", objectName);
                }

                // Parent-type gate — extracted into TryBuildTypeGateRejection so
                // the rejection envelope shape is unit-testable without a real
                // KBObject. Live template enumeration stays in this scope because
                // it needs _objectService.
                {
                    string parentType = obj.TypeDescriptor?.Name ?? "";
                    string callerTemplate = settings != null ? settings["template"]?.ToString() : null;
                    List<string> availableTemplates = null;
                    bool isWebPanelKind = string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(parentType, "SDPanel", StringComparison.OrdinalIgnoreCase);
                    if (isWebPanelKind)
                    {
                        try { availableTemplates = ListWwpWebTemplates(); } catch { /* best-effort */ }
                    }
                    string reject = TryBuildTypeGateRejection(obj.Name, patternKey, parentType, callerTemplate, availableTemplates);
                    if (reject != null) return reject;
                }

                // IDE lock pre-check (parity with ReapplyPattern). The SDK
                // apply call deadlocks 10+ min when the GeneXus IDE holds the
                // object (or its WWP host) open. Fail fast with a structured
                // IdeHoldsLock error instead of hanging the worker thread.
                string lockReject = TryBuildIdeLockRejection(obj, objectName);
                if (lockReject != null) return lockReject;

                return ApplyPatternToObject(obj, patternId, patternKey, settings, reapply: false);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.ApplyPattern failed: " + ex);
                return McpResponse.Error(ex.Message, objectName);
            }
        }

        /// <summary>
        /// Re-apply (regenerate) an existing pattern instance on the object.
        /// </summary>
        public string ReapplyPattern(string objectName, JObject settings = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                    return McpResponse.Error("Object name is required.", objectName);

                KBObject obj = ResolveObject(objectName);
                if (obj == null)
                {
                    if (_objectService != null)
                        return HealingService.FormatNotFoundError(objectName, _objectService.GetLoadedIndexOrNull());
                    return McpResponse.Error("Object not found", objectName);
                }

                // Friction 2026-05-25 item #6 — IDE lock pre-check. The GeneXus
                // IDE writes <KB>/Locks/<object-guid>.lock when it opens an
                // object. The reapply SDK call deadlocks for >10min when the
                // IDE holds the lock (UpdateParentObject contends on the same
                // KBObject handle). Fail fast with a structured error rather
                // than hanging the worker thread indefinitely.
                string lockReject = TryBuildIdeLockRejection(obj, objectName);
                if (lockReject != null) return lockReject;

                // For reapply we default to WorkWithPlus until other patterns are wired.
                return ApplyPatternToObject(obj, WorkWithPlusPatternId, "WorkWithPlus", settings, reapply: true);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.ReapplyPattern failed: " + ex);
                return McpResponse.Error(ex.Message, objectName);
            }
        }

        /// <summary>
        /// Friction 2026-05-25 item #6 — returns a structured "IDE holds lock"
        /// rejection envelope when GeneXus IDE has the object open, otherwise
        /// null. Looks for <KB>/Locks/<guid>.lock for the parent AND its WWP
        /// host (WorkWithPlus&lt;Name&gt;). Best-effort: any I/O failure logs
        /// and returns null so the SDK call proceeds.
        /// </summary>
        internal string TryBuildIdeLockRejection(KBObject parent, string objectName)
        {
            try
            {
                string kbPath = null;
                try { kbPath = _objectService?.GetKbService()?.GetKbPath(); } catch { /* best-effort */ }
                if (string.IsNullOrWhiteSpace(kbPath)) return null;

                // GetKbPath returns either the .gkb file or the directory; normalise.
                string kbDir = System.IO.File.Exists(kbPath)
                    ? System.IO.Path.GetDirectoryName(kbPath)
                    : kbPath;
                if (string.IsNullOrWhiteSpace(kbDir)) return null;

                string locksDir = System.IO.Path.Combine(kbDir, "Locks");
                if (!System.IO.Directory.Exists(locksDir)) return null;

                var hits = new System.Collections.Generic.List<JObject>();
                void Probe(KBObject obj, string role)
                {
                    if (obj == null) return;
                    var guid = obj.Guid;
                    if (guid == Guid.Empty) return;
                    string lockFile = System.IO.Path.Combine(locksDir, guid.ToString("D") + ".lock");
                    if (!System.IO.File.Exists(lockFile)) return;

                    // Distinguish IDE locks from our own worker's locks. The
                    // worker writes its OWN .lock files when it opens objects;
                    // we should not refuse our own session. The IDE's lock
                    // files are typically created/touched at session start and
                    // remain throughout — the safest signal is "is the file
                    // currently held open by another process". F_OK is enough
                    // for now; refine via FileShare probe if false-positives
                    // appear.
                    hits.Add(new JObject
                    {
                        ["role"] = role,
                        ["object"] = obj.Name,
                        ["guid"] = guid.ToString("D"),
                        ["lockFile"] = lockFile,
                        ["lockedAtUtc"] = System.IO.File.GetLastWriteTimeUtc(lockFile).ToString("o")
                    });
                }

                Probe(parent, "parent");

                // Probe the WWP host too: WorkWithPlus<Name> is the conventional naming.
                if (_objectService != null && !string.IsNullOrEmpty(parent?.Name))
                {
                    var hostName = "WorkWithPlus" + parent.Name;
                    var host = _objectService.FindObject(hostName);
                    Probe(host, "wwpHost");
                }

                if (hits.Count == 0) return null;

                var err = new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "IdeHoldsLock",
                    ["message"] = "GeneXus IDE has " + (hits.Count == 1 ? "an object" : "objects") + " open that would deadlock the SDK reapply call. Close the tab(s) in the IDE (or save and switch to another object) and retry.",
                    ["target"] = objectName,
                    ["lockedObjects"] = new JArray(hits),
                    ["hint"] = "Close '" + (string)hits[0]["object"] + "' (and any other listed object) in the GeneXus IDE before calling reapply. The MCP worker and the IDE cannot hold the same KBObject handle simultaneously."
                };
                return err.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                Logger.Info("[IDE-LOCK-CHECK] best-effort failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Friction 2026-05-25 — invoke <c>DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface.SetPatternApplyOnSave(host)</c>
        /// so the IDE's "Apply this pattern on save" checkbox stays on after a
        /// reapply (or after a user manually unchecked it in the IDE). Pure
        /// reflection over WWP types; best-effort — any miss is logged and
        /// returns false instead of throwing. Returns true when the SDK
        /// method was found and invoked without exception.
        /// </summary>
        internal bool TryEnableApplyOnSave(KBObject host)
        {
            return GxMcp.Worker.Helpers.WwpApplyOnSaveHelper.TryEnable(host);
        }

        internal string ApplyPatternToObject(KBObject obj, Guid patternId, string patternKey, JObject settings, bool reapply, string objectNameForResponse = null)
        {
            var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
            var phases = new System.Collections.Generic.List<string>();
            void Phase(string name) { phases.Add($"{name}={phaseTimer.ElapsedMilliseconds}ms"); phaseTimer.Restart(); }

            // Surfaced on the response when reapply projection runs long — agents
            // need a structured signal (not just a log line) to suggest closing
            // an IDE tab or retrying later. See SLOW_REAPPLY_THRESHOLD_MS.
            long projectionElapsedMs = 0;

            // F17 (perf-gated): SdkSurfaceProbe.Run walks every loaded SDK assembly,
            // dumps all public types/methods/properties and writes a multi-MB raw.json.
            // It used to run on EVERY apply (~5-15s of pure waste in production calls).
            // It's a debugging artifact — opt in with GX_MCP_SDK_PROBE=1 when you need
            // the dump, or call genexus_sdk_probe explicitly.
            if (_objectService != null
                && string.Equals(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE"), "1", StringComparison.Ordinal))
            {
                try
                {
                    var fullProbe = SdkSurfaceProbe.Run(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE_DIR"));
                    Logger.Info("[SDK-PROBE] Wrote SDK surface: " + fullProbe.RawJsonPath +
                        " (assemblies=" + fullProbe.AssembliesScanned +
                        ", types=" + fullProbe.TypesScanned +
                        ", generators=" + fullProbe.GeneratorCandidates + ")");
                }
                catch (Exception ex) { Logger.Debug("[SDK-PROBE] skipped: " + ex.Message); }
            }

            Phase("sdkProbeGated");
            // Probe the engine; if license/package is missing we degrade gracefully.
            object patternDefinition = _engine.GetPatternDefinition(patternId);
            if (patternDefinition == null)
            {
                return PatternUnavailable(patternKey, "WorkWithPlus pattern not loaded — check license / package install");
            }

            // Detect existing instance to decide between first-apply and re-apply.
            object existingInstance = _engine.GetPatternInstance(obj, patternId);

            // Stale-metadata guard: GetPatternInstance can return non-null even after
            // the user deleted the generated host (WorkWithPlus<Name>) in a prior
            // session — the SDK keeps the PatternInstance metadata on the parent.
            // Without this probe, reapply would skip the engine apply and produce a
            // minimalist PatternInstance (empty <table/>). Treat a missing host as
            // first-apply so the engine regenerates the family.
            bool staleInstanceRecovered = false;
            if (existingInstance != null && _objectService != null && !string.IsNullOrEmpty(obj?.Name)
                && string.Equals(patternKey, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var wwpHostProbe = _objectService.FindObject("WorkWithPlus" + obj.Name);
                    if (wwpHostProbe == null)
                    {
                        Logger.Info("ApplyPattern: PatternInstance metadata present but WorkWithPlus" + obj.Name + " host missing — treating as first-apply (stale metadata recovery).");
                        existingInstance = null;
                        staleInstanceRecovered = true;
                    }
                }
                catch (Exception ex) { Logger.Debug("ApplyPattern: stale-host probe failed (best-effort): " + ex.Message); }
            }

            bool wasFirstApply = existingInstance == null;

            PatternApplyResult result;
            try
            {
                // The SDK's `PatternEngine.ApplyPattern(PatternInstance, ApplySettings)`
                // overload may not be present on every GeneXus install (observed missing on
                // 18.0.7.179127). When that happens, fall back to the void overload — the
                // SDK detects the existing instance and re-applies. Wrap each reapply call
                // so the fallback is transparent to callers.
                if (existingInstance != null)
                {
                    // Existing host detected. The engine's reapply overload throws NRE
                    // on this install (needs services we can't provide). Skip it and
                    // project via UpdateParentObject below — that's the only generator
                    // step that matters anyway.
                    result = new PatternApplyResult();
                    wasFirstApply = false;
                    Logger.Info("ApplyPattern: existing host detected — skipping engine reapply (NRE-prone), will project via UpdateParentObject.");
                }
                else
                {
                    result = _engine.ApplyPattern(obj, patternDefinition, settings);
                    wasFirstApply = true;
                }
                Phase("engineApply");
            }
            catch (Exception ex)
            {
                string errName = objectNameForResponse ?? obj?.Name ?? "";
                Logger.Error("PatternEngine apply failed for '" + errName + "': " + ex);
                var err = new JObject
                {
                    ["status"] = "Error",
                    ["target"] = errName,
                    ["patternKey"] = patternKey,
                    ["message"] = ex.Message
                };
                return err.ToString(Newtonsoft.Json.Formatting.None);
            }

            string targetName = objectNameForResponse ?? obj?.Name ?? "";

            // F17: when there's an existing PatternInstance host (re-apply case),
            // re-invoke UpdateParentObject so edits to the host's PatternInstance get
            // projected onto the parent's WebForm. We re-resolve the host KBObject
            // from disk first because the cached `existingInstance` may carry stale
            // PatternInstance state from before genexus_edit landed.
            if (existingInstance != null && _objectService != null)
            {
                KBObject reappliedHost = null;
                // Friction 2026-05-25 — projection step (`UpdateParentObject`)
                // can deadlock for 10+ minutes when the IDE has the host or
                // parent open in a tab. We can't safely abort an STA call,
                // but we CAN time it and surface elapsedMs in the response so
                // callers see how long it took. Combined with [APPLY-PATTERN]
                // log lines, anyone watching can identify hangs early.
                var projectionSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var freshHost = _objectService.FindObject("WorkWithPlus" + obj.Name);
                    if (freshHost != null)
                    {
                        TryInvokeBuildProcessUpdateParent(obj, freshHost);
                        reappliedHost = freshHost;
                    }
                    else if (existingInstance is KBObject existingHostObj)
                    {
                        TryInvokeBuildProcessUpdateParent(obj, existingHostObj);
                        reappliedHost = existingHostObj;
                    }
                }
                catch (Exception ex) { Logger.Info("Reapply UpdateParentObject best-effort: " + ex.Message); }
                projectionSw.Stop();
                projectionElapsedMs = projectionSw.ElapsedMilliseconds;
                if (projectionSw.ElapsedMilliseconds > 30000)
                {
                    // 30s threshold — IDE-hold-on-tab deadlocks were 10+ min.
                    // Clean reapply on a free object completes in ~1-3s. Log
                    // at warn so dev can correlate slow reapplies with IDE
                    // tab state. The .lock file pre-check (TryBuildIdeLockRejection)
                    // is a false-negative on per-tab opens — see project
                    // memory `feedback_mcp_friction_2026_05_25` item #6.
                    Logger.Warn("[APPLY-PATTERN] projection took " + projectionSw.ElapsedMilliseconds + "ms — likely IDE-tab-hold contention. Close any tab on '" + obj?.Name + "' or 'WorkWithPlus" + obj?.Name + "' if reapplies keep timing out.");
                }
                phases.Add($"projection={projectionSw.ElapsedMilliseconds}ms");

                // Friction 2026-05-25 — first-apply called SetPatternApplyOnSave
                // via PatternInstancePackageInterface so the IDE's "Apply this
                // pattern on save" checkbox lit up; reapply previously skipped
                // it, so a host whose checkbox was manually unchecked stayed
                // unchecked even after a successful reapply. Now always re-
                // assert the flag on reapply — best-effort, reflection-based,
                // matches the helper used in TryFirstApplyViaPackage above.
                if (reappliedHost != null)
                {
                    try { TryEnableApplyOnSave(reappliedHost); }
                    catch (Exception ex) { Logger.Info("Reapply SetPatternApplyOnSave best-effort: " + ex.Message); }
                }
            }

            // Compute the real generated-objects list. The adapter's GeneratedObjects
            // collection is normally empty (void overload returns no names), so we look
            // up the canonical WWP family by name pattern instead. Cheap: O(family_size)
            // FindObject lookups, vs O(model_size) for a pre/post diff. Also avoids the
            // race where the SDK registers new objects asynchronously after Invoke returns.
            var generated = LookupWwpFamilyByConvention(obj);
            Phase("lookupFamily");
            if (result?.GeneratedObjects != null)
            {
                foreach (var name in result.GeneratedObjects)
                {
                    if (!string.IsNullOrEmpty(name) && !generated.Contains(name))
                        generated.Add(name);
                }
            }

            // Keep the search index in sync with the generated family so the agent's next
            // list_objects/query reflects what the apply created. Without this, the host
            // and WW/Export* siblings remain invisible for minutes until a full reindex.
            // FindObject is O(1) on the typed index — avoid model.Objects.GetAll() loops
            // that would be O(generated × model_size) and torch large-KB performance.
            if (generated.Count > 0 && _objectService != null)
            {
                try
                {
                    var idx = _objectService.GetKbService()?.GetIndexCache();
                    if (idx != null)
                    {
                        foreach (var name in generated)
                        {
                            try
                            {
                                var o = _objectService.FindObject(name);
                                if (o != null) idx.UpdateEntry(o);
                            }
                            catch { /* per-name best-effort */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("ApplyPattern: index UpdateEntry sweep skipped: " + ex.Message);
                }
            }
            Phase("indexUpdate");

            string parentTypeName = obj?.TypeDescriptor?.Name ?? "";
            string bindingMode;
            if (string.Equals(parentTypeName, "Transaction", StringComparison.OrdinalIgnoreCase)) bindingMode = "transaction-family";
            else if (string.Equals(parentTypeName, "WebPanel", StringComparison.OrdinalIgnoreCase)) bindingMode = "webpanel-direct-attach";
            else if (string.Equals(parentTypeName, "SDPanel", StringComparison.OrdinalIgnoreCase)) bindingMode = "sdpanel-direct-attach";
            else bindingMode = "unknown";

            // Friction 2026-05-26 — apply_pattern reapply previously returned
            // status=Success even when the parent's Events-by-WorkWithPlus
            // generation produced src0265 ("Invalid attribute") / src0216
            // ("Visible invalid property") errors at save time (visible only
            // when the user tried to Ctrl+S in the IDE). Run the parent's
            // standard SDK validation here so any pre-existing references to
            // controls that the fresh PatternInstance no longer knows surface
            // in the response. Best-effort: if SdkDiagnosticsHelper throws
            // we still return Success — diagnostics aren't load-bearing.
            JArray patternValidationIssues = null;
            try
            {
                if (obj != null)
                {
                    var issues = GxMcp.Worker.Helpers.SdkDiagnosticsHelper.GetDiagnostics(obj);
                    if (issues != null && issues.Count > 0)
                    {
                        // Filter to ERROR-level diagnostics in pattern-generated
                        // events. We don't want to surface unrelated warnings
                        // or other-part issues here.
                        var filtered = new JArray();
                        foreach (var t in issues)
                        {
                            string sev = t["severity"]?.ToString();
                            string code = t["code"]?.ToString() ?? "";
                            // Surface known WWP-projection error codes plus
                            // any Error-severity issue on the parent's events.
                            if (string.Equals(sev, "Error", StringComparison.OrdinalIgnoreCase) ||
                                code.StartsWith("src0265", StringComparison.OrdinalIgnoreCase) ||
                                code.StartsWith("src0216", StringComparison.OrdinalIgnoreCase))
                            {
                                filtered.Add(t);
                            }
                        }
                        if (filtered.Count > 0) patternValidationIssues = filtered;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("ApplyPattern: post-projection validate best-effort: " + ex.Message); }
            Phase("postValidate");

            var response = new JObject
            {
                ["status"] = patternValidationIssues != null ? "PartialFailure" : "Success",
                ["target"] = targetName,
                ["parentType"] = parentTypeName,
                ["bindingMode"] = bindingMode,
                ["patternKey"] = patternKey,
                ["patternId"] = patternId.ToString(),
                ["wasFirstApply"] = wasFirstApply,
                ["generatedObjects"] = new JArray(generated),
                ["errors"] = new JArray(result?.Errors ?? Enumerable.Empty<string>())
            };
            if (staleInstanceRecovered)
            {
                response["staleInstanceRecovered"] = true;
                response["staleInstanceHint"] = "PatternInstance metadata was present on the parent but the generated WorkWithPlus host was missing (typically from a prior delete). Engine apply was re-run as if this were a fresh apply so the family regenerates instead of producing an empty PatternInstance.";
            }
            if (patternValidationIssues != null)
            {
                response["patternValidationIssues"] = patternValidationIssues;
                response["hint"] = "Pattern apply persisted, but the parent's Events code references controls the fresh PatternInstance doesn't expose. The next IDE 'Ctrl+S' will fail with these errors. Edit the parent's Events to remove or rename the referenced controls (typically " +
                                   "`GrpX.Visible = …` or similar) before saving in the IDE.";
            }

            // Reapply projection-time surfacing. The STA-bound SDK call can't be
            // hard-aborted from another thread, but a structured signal lets the
            // agent decide to close the IDE tab / retry without re-reading logs.
            // Threshold matches the warn-log at the projection site.
            if (reapply && projectionElapsedMs > 30000)
            {
                response["slowReapply"] = true;
                response["projectionMs"] = projectionElapsedMs;
                response["slowReapplyHint"] = $"Reapply projection took {projectionElapsedMs}ms (threshold 30000ms). " +
                                              $"The most common cause is the GeneXus IDE holding '{targetName}' or 'WorkWithPlus{targetName}' open in a tab — close it and retry. " +
                                              $"If no IDE is running, the SDK may be hung on a stale handle; restart the worker via genexus_worker_reload mode=hard.";
            }

            // Surface the WWP host (`WorkWithPlus<X>`) explicitly when present — agents
            // need this name to read/edit the PatternInstance after a first-apply. We
            // ONLY surface it when the SDK actually created/has it; deriving the name
            // from convention when it doesn't exist would mislead the agent into reading
            // a non-existent object (see WebPanel-direct case below).
            string host = generated.FirstOrDefault(n => n.StartsWith("WorkWithPlus", StringComparison.Ordinal));
            if (host == null && obj != null && _objectService != null)
            {
                try
                {
                    var hostObj = _objectService.FindObject("WorkWithPlus" + obj.Name);
                    if (hostObj != null) host = hostObj.Name;
                }
                catch { /* lookup best-effort */ }
            }
            if (!string.IsNullOrEmpty(host)) response["patternHost"] = host;

            // F23: surface available `WorkWithPlus for Web Template` names so agents
            // don't have to guess on the next call. Helpful especially on Web/SDPanel
            // direct-attach paths where `settings.template` is required.
            if (obj?.TypeDescriptor?.Name == "WebPanel" || obj?.TypeDescriptor?.Name == "SDPanel")
            {
                try
                {
                    var templates = ListWwpWebTemplates();
                    if (templates.Count > 0)
                    {
                        response["availableTemplates"] = new JArray(templates);
                    }
                }
                catch { /* best-effort */ }
            }

            // No-op detection: PatternEngine.ApplyPattern returns void; on certain target
            // shapes (notably a bare WebPanel against the default WorkWithPlus pattern in
            // GeneXus 18.0.7), the call completes cleanly but the SDK doesn't actually
            // attach a PatternInstance or generate the family. Without this check the
            // agent gets a misleading Success and goes on to read a non-existent host.
            //
            // Heuristic: no family generated AND target itself has no PatternInstance
            // part AND no host exists for it. We downgrade `status` to `NoOp` and emit
            // an actionable `recommendation` instead of pretending the apply worked.
            if (generated.Count == 0 && host == null && obj != null)
            {
                bool targetHasPatternInstance = false;
                try
                {
                    foreach (var part in obj.Parts)
                    {
                        var p = part as KBObjectPart;
                        if (p == null) continue;
                        if (string.Equals(p.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                            p.GetType().Name.IndexOf("PatternInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            targetHasPatternInstance = true;
                            break;
                        }
                    }
                }
                catch { /* best-effort */ }

                if (!targetHasPatternInstance)
                {
                    // F16: Use the OFFICIAL WWP package API discovered via deep reflection
                    // on DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface:
                    //   1. CreatePatternInstanceWithTemplate(KBModel, KBObject, String, out PatternInstance)
                    //      — creates a host bound to the parent with a registered Template
                    //   2. SetPatternApplyOnSave(KBObject)
                    //      — toggles "apply pattern on save" so derived objects regenerate
                    //   3. ValidateAndSave(KBObject)
                    //      — WWP custom validator + persist; fires the generators
                    //
                    // This is the IDE's canonical apply path (Right-click → Apply Pattern).
                    // Earlier shortcuts (private ctor → ghost host, auto-orchestrate →
                    // destructive) bypassed the WWP lifecycle wired by these helpers.
                    string sett_template = settings != null ? settings["template"]?.ToString() : null;
                    bool packageAttached = false;
                    string packageAttachError = null;
                    string createdHostName = null;
                    string usedTemplate = null;
                    try
                    {
                        packageAttached = TryPackageInterfaceAttach(obj, sett_template, out createdHostName, out usedTemplate, out packageAttachError);
                    }
                    catch (Exception ex)
                    {
                        packageAttachError = ex.GetType().Name + ": " + ex.Message;
                    }

                    if (packageAttached)
                    {
                        response["status"] = "Success";
                        response["wasFirstApply"] = true;
                        response["directAttach"] = true;
                        response["directAttachRoute"] = "PatternInstancePackageInterface";
                        response["template"] = usedTemplate;
                        response["directAttachNote"] = "Attached via the official WWP package API (CreatePatternInstanceWithTemplate + SetPatternApplyOnSave + ValidateAndSave). Host '" + createdHostName + "' is bound through the IDE's canonical lifecycle, so PatternInstance edits trigger regeneration on save.";
                        if (!string.IsNullOrEmpty(createdHostName))
                        {
                            response["patternHost"] = createdHostName;
                            response["generatedObjects"] = new JArray(createdHostName);
                            try
                            {
                                var idx = _objectService?.GetKbService()?.GetIndexCache();
                                if (idx != null)
                                {
                                    var hostObj = _objectService.FindObject(createdHostName);
                                    if (hostObj != null) idx.UpdateEntry(hostObj);
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        response["status"] = "NoOp";
                        response["noOpReason"] = "Engine ApplyPattern void overload no-op'd on this target, and the WWP package's CreatePatternInstanceWithTemplate fallback also failed: " + (packageAttachError ?? "unknown");
                        response["recommendation"] = obj.TypeDescriptor?.Name == "WebPanel"
                            ? "Either: (1) pass an explicit `settings.template` matching a `WorkWithPlus for Web Template` object in this KB (we tried auto-discovery first). (2) Apply WorkWithPlus to a Transaction — the engine generates 'WW<Trn>' as a wired WWP screen."
                            : "Apply WorkWithPlus to a Transaction to generate the WWP family.";

                        // Diagnostic probes (perf-gated): pattern + full SDK surface dump.
                        // These are useful when investigating a NoOp regression but
                        // expensive (full reflection sweep + multi-MB disk write). Opt in
                        // with GX_MCP_SDK_PROBE=1 — the recommendation/availableTemplates
                        // already give the agent enough to act on without the dump.
                        if (string.Equals(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE"), "1", StringComparison.Ordinal))
                        {
                            try
                            {
                                var dump = DumpSdkSurface(patternDefinition, patternId);
                                string dumpPath = Path.Combine(Path.GetTempPath(), "gxmcp_pattern_probe.json");
                                File.WriteAllText(dumpPath, dump.ToString(Newtonsoft.Json.Formatting.Indented));
                                response["sdkProbePath"] = dumpPath;

                                var fullProbe = SdkSurfaceProbe.Run(Environment.GetEnvironmentVariable("GX_MCP_SDK_PROBE_DIR"));
                                response["sdkSurfaceProbe"] = new JObject
                                {
                                    ["rawJsonPath"] = fullProbe.RawJsonPath,
                                    ["indexMdPath"] = fullProbe.IndexMdPath,
                                    ["generatorsMdPath"] = fullProbe.GeneratorsMdPath,
                                    ["rawSizeBytes"] = fullProbe.RawSizeBytes,
                                    ["assembliesScanned"] = fullProbe.AssembliesScanned,
                                    ["typesScanned"] = fullProbe.TypesScanned,
                                    ["generatorCandidates"] = fullProbe.GeneratorCandidates,
                                    ["warnings"] = new JArray(fullProbe.Warnings)
                                };
                            }
                            catch (Exception ex) { response["sdkProbeError"] = ex.Message; }
                        }
                    }
                }
            }

            Phase("tailEnvelope");
            Logger.Info("[ApplyPattern-PERF] target=" + targetName + " parent=" + parentTypeName + " phases=" + string.Join(",", phases));

            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(response, GxMcp.Worker.Helpers.WriteResultMeta.SdkPatternEngine);
            return response.ToString(Newtonsoft.Json.Formatting.None);
        }

        // F23: list all `WorkWithPlus for Web Template` KBObjects in the current KB —
        // used to surface options to the agent on apply_pattern responses.
        // Walking model.Objects.GetAll() is O(KB-size) and was clocking 10s+ on the
        // 50k-object KB. Templates rarely change at runtime, so memoize per KB for
        // v2.6.4 (#10): pure type-gate logic. Returns the rejection envelope
        // (serialized JSON) when the parent type / template combination is
        // ineligible for WorkWithPlus, or null if the apply may proceed.
        // - WorkWithPlus key only (other keys pass through unchanged)
        // - Transaction: always eligible (no template required)
        // - WebPanel/SDPanel: eligible; if callerTemplate provided AND
        //   availableTemplates is non-empty AND template not in list → reject
        // - Anything else → reject upfront (Procedure/SDT/Domain/etc.)
        // Extracted so the rejection contract is unit-testable without a live
        // KB or KBObject.
        internal static string TryBuildTypeGateRejection(
            string objName,
            string patternKey,
            string parentType,
            string callerTemplate,
            List<string> availableTemplates)
        {
            bool isWwp = string.Equals(patternKey, "WorkWithPlus", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(patternKey, "WWP", StringComparison.OrdinalIgnoreCase);
            if (!isWwp) return null;

            if (parentType == null) parentType = "";
            bool isTransaction = string.Equals(parentType, "Transaction", StringComparison.OrdinalIgnoreCase);
            bool isWebPanelKind = string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(parentType, "SDPanel", StringComparison.OrdinalIgnoreCase);

            if (!isTransaction && !isWebPanelKind)
            {
                var rej = new JObject
                {
                    ["status"] = "Error",
                    ["target"] = objName,
                    ["patternKey"] = patternKey,
                    ["parentType"] = parentType,
                    ["message"] = $"WorkWithPlus cannot be applied to a {parentType}.",
                    ["validParentTypes"] = new JArray("Transaction", "WebPanel", "SDPanel"),
                    ["hint"] = "Apply WorkWithPlus only to a Transaction (generates WW/View/Export family) or to a WebPanel/SDPanel (direct-attach with a Template; pass settings.template or let the MCP auto-discover one)."
                };
                return rej.ToString(Newtonsoft.Json.Formatting.None);
            }

            if (isWebPanelKind
                && !string.IsNullOrEmpty(callerTemplate)
                && availableTemplates != null
                && availableTemplates.Count > 0
                && !availableTemplates.Any(t => string.Equals(t, callerTemplate, StringComparison.OrdinalIgnoreCase)))
            {
                var bad = new JObject
                {
                    ["status"] = "Error",
                    ["target"] = objName,
                    ["patternKey"] = patternKey,
                    ["parentType"] = parentType,
                    ["message"] = $"Template '{callerTemplate}' is not a registered `WorkWithPlus for Web Template` in this KB.",
                    ["availableTemplates"] = new JArray(availableTemplates),
                    ["hint"] = "Pass settings.template equal to one of availableTemplates, or omit it to auto-discover."
                };
                return bad.ToString(Newtonsoft.Json.Formatting.None);
            }

            return null;
        }

        // 60s — fresh enough to pick up new templates, cheap on the upfront-guard
        // path where every apply on a WebPanel hits this.
        private static readonly Dictionary<string, (DateTime expiresAt, List<string> names)> _templateCache
            = new Dictionary<string, (DateTime, List<string>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _templateCacheLock = new object();
        private const int TemplateCacheTtlSeconds = 60;

        private List<string> ListWwpWebTemplates()
        {
            if (_objectService == null) return new List<string>();
            string kbKey = null;
            try { kbKey = _objectService.GetKbService()?.GetKB()?.Location ?? "(no-kb)"; } catch { kbKey = "(error)"; }
            lock (_templateCacheLock)
            {
                if (_templateCache.TryGetValue(kbKey, out var hit) && hit.expiresAt > DateTime.UtcNow)
                    return hit.names;
            }
            var names = new List<string>();
            // FAST PATH: walk the search index (in-memory, ~ms) instead of
            // model.Objects.GetAll() (~10s on 50k-object KBs). The index entries
            // carry Type, which is what we filter on. Fall back to SDK enumeration
            // only when the index isn't ready.
            try
            {
                var index = _objectService.GetIndex();
                if (index != null && index.Objects != null && index.Objects.Count > 0)
                {
                    foreach (var entry in index.Objects.Values)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                        if (string.Equals(entry.Type, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase))
                            names.Add(entry.Name);
                    }
                }
                else
                {
                    var kb = _objectService.GetKbService()?.GetKB();
                    if (kb != null)
                    {
                        foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                        {
                            if (o == null) continue;
                            if (string.Equals(o.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase))
                                names.Add(o.Name);
                        }
                    }
                }
            }
            catch { /* best-effort */ }
            names.Sort(StringComparer.Ordinal);
            lock (_templateCacheLock)
            {
                _templateCache[kbKey] = (DateTime.UtcNow.AddSeconds(TemplateCacheTtlSeconds), names);
            }
            return names;
        }

        // Discover a usable `WorkWithPlus for Web Template` KBObject in the current
        // KB to seed `<WPRoot Template="...">`. The validator rejects the save if the
        // Template attribute doesn't resolve to a real Template object, and the set
        // of registered templates is KB-specific (BaseXmlObjects.xml references
        // "Empty" but most KBs ship custom names like "MatIsoTemplate", "TransactionResp2",
        // "PopoverEmpty", etc.). We prefer the caller-provided value, then a non-Popover
        // template (Popovers are popup-specific and may have stricter required structure),
        // then any template, then fall back to "Empty".
        private string ResolveAvailableWwpTemplate(string preferred)
        {
            if (_objectService == null) return preferred ?? "Empty";
            try
            {
                var kb = _objectService.GetKbService()?.GetKB();
                if (kb == null) return preferred ?? "Empty";

                // Caller hint: if a real Template object exists with the requested name, use it.
                if (!string.IsNullOrWhiteSpace(preferred))
                {
                    var hit = _objectService.FindObject(preferred);
                    if (hit != null && string.Equals(hit.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase))
                        return hit.Name;
                }

                string firstNonPopover = null;
                string anyTemplate = null;
                foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                {
                    if (o == null) continue;
                    if (!string.Equals(o.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase)) continue;
                    if (anyTemplate == null) anyTemplate = o.Name;
                    if (firstNonPopover == null && !o.Name.StartsWith("Popover", StringComparison.OrdinalIgnoreCase))
                    {
                        firstNonPopover = o.Name;
                    }
                    if (firstNonPopover != null) break;
                }
                return firstNonPopover ?? anyTemplate ?? "Empty";
            }
            catch (Exception ex)
            {
                Logger.Debug("ResolveAvailableWwpTemplate failed: " + ex.Message);
                return preferred ?? "Empty";
            }
        }

        // Ensures the engine adapter has run its reflection probe. The probe is lazy
        // and gated by a private flag inside ReflectionPatternEngineAdapter — calling
        // any of its public methods triggers it. We just trigger via a cheap call.
        private void EnsureProbedEngine()
        {
            try { _engine?.GetPatternDefinition(WorkWithPlusPatternId); } catch { }
        }

        // OFFICIAL APPLY PATH for WebPanel/Procedure/SDPanel targets via the WWP
        // package's `PatternInstancePackageInterface` helper. This is the IDE's
        // canonical Right-click → Apply Pattern → WWP route. Three static methods:
        //   1. CreatePatternInstanceWithTemplate(KBModel, KBObject, String, out PatternInstance) -> Boolean
        //   2. SetPatternApplyOnSave(KBObject) -> Boolean
        //   3. ValidateAndSave(KBObject) -> Boolean
        //
        // Resolves a Template (caller hint via settings.template, else auto-discovers
        // a registered `WorkWithPlus for Web Template` in this KB). Falls through to
        // NoOp on any failure with the SDK error message attached.
        internal bool TryPackageInterfaceAttach(KBObject parent, string preferredTemplate, out string hostName, out string usedTemplate, out string errorMessage)
        {
            hostName = null;
            usedTemplate = null;
            errorMessage = null;
            if (parent == null) { errorMessage = "parent KBObject is null"; return false; }
            if (_objectService == null) { errorMessage = "ObjectService unavailable"; return false; }

            try
            {
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null)
                {
                    try
                    {
                        var gxPath = Environment.GetEnvironmentVariable("GX_PATH") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                        var wwpDllPath = Path.Combine(gxPath, "Packages", "Patterns", "WorkWithPlus", "DVelop.Patterns.WorkWithPlus.dll");
                        if (File.Exists(wwpDllPath)) wwpAsm = Assembly.LoadFrom(wwpDllPath);
                    }
                    catch { }
                }
                if (wwpAsm == null) { errorMessage = "DVelop.Patterns.WorkWithPlus not loaded"; return false; }

                // FIRST CHOICE: WWP_ApplyTemplate MSBuild task. This is the IDE's actual
                // "Apply Template" route — has WebPanelName / TemplateName / KB inputs
                // exactly fit for our case. Tried before the PackageInterface fallback.
                if (parent.TypeDescriptor?.Name == "WebPanel")
                {
                    usedTemplate = ResolveAvailableWwpTemplate(preferredTemplate);
                    if (string.IsNullOrEmpty(usedTemplate))
                    {
                        errorMessage = "No `WorkWithPlus for Web Template` found in KB.";
                        return false;
                    }

                    var taskResult = TryRunWwpApplyTemplateTask(wwpAsm, parent, usedTemplate, out hostName, out var taskErr);
                    if (taskResult) return true;
                    Logger.Info("WWP_ApplyTemplate task failed: " + taskErr + " — falling back to PackageInterface");
                    // continue to PackageInterface fallback below
                }

                var ifaceType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface", false);
                if (ifaceType == null) { errorMessage = "PatternInstancePackageInterface type not found"; return false; }

                var createMethod = ifaceType.GetMethod("CreatePatternInstanceWithTemplate", BindingFlags.Public | BindingFlags.Static);
                var setApplyMethod = ifaceType.GetMethod("SetPatternApplyOnSave", BindingFlags.Public | BindingFlags.Static);
                var validateSaveMethod = ifaceType.GetMethod("ValidateAndSave", BindingFlags.Public | BindingFlags.Static);
                if (createMethod == null || setApplyMethod == null || validateSaveMethod == null)
                {
                    errorMessage = "PatternInstancePackageInterface missing expected statics (CreatePatternInstanceWithTemplate/SetPatternApplyOnSave/ValidateAndSave)";
                    return false;
                }

                usedTemplate = ResolveAvailableWwpTemplate(preferredTemplate);
                if (string.IsNullOrEmpty(usedTemplate))
                {
                    errorMessage = "No `WorkWithPlus for Web Template` object found in this KB and no caller hint provided. Pass settings.template explicitly.";
                    return false;
                }

                var kb = _objectService.GetKbService()?.GetKB();
                if (kb == null) { errorMessage = "No KB open"; return false; }
                object model = kb.DesignModel;

                // Invoke: bool CreatePatternInstanceWithTemplate(model, parent, template, out instance)
                // Empirically the return value is unreliable — false has been observed even
                // when the host was created on disk (External change detected logs confirm).
                // So we ALSO check via FindObject(WorkWithPlus<parentName>) after the call.
                var args = new object[] { model, parent, usedTemplate, null };
                object createResult;
                bool createThrew = false;
                string createThrowMsg = null;
                try
                {
                    createResult = createMethod.Invoke(null, args);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    createThrew = true;
                    createThrowMsg = inner.GetType().Name + ": " + inner.Message;
                    createResult = null;
                }
                bool createSaid = createResult is bool b && b;
                var hostObj = args[3] as KBObject;
                if (hostObj == null)
                {
                    // Out-parameter null — check disk for the host (the API may have
                    // created+saved but returned false).
                    hostObj = _objectService.FindObject("WorkWithPlus" + parent.Name);
                }
                if (hostObj == null)
                {
                    errorMessage = createThrew
                        ? "CreatePatternInstanceWithTemplate threw: " + createThrowMsg
                        : "CreatePatternInstanceWithTemplate returned " + (createSaid ? "true" : "false") + " but host not present on disk (template='" + usedTemplate + "')";
                    return false;
                }
                hostName = hostObj.Name;

                // Enable apply-on-save so future PatternInstance edits regenerate.
                try { setApplyMethod.Invoke(null, new object[] { hostObj }); }
                catch (Exception ex) { Logger.Info("SetPatternApplyOnSave best-effort: " + ex.Message); }

                // Final validate+save — triggers the engine generators.
                try
                {
                    var saveResult = validateSaveMethod.Invoke(null, new object[] { hostObj });
                    if (saveResult is bool sb && !sb)
                    {
                        errorMessage = "ValidateAndSave returned false";
                        return false;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "ValidateAndSave threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }

                // F17: Trigger the projection step that the IDE does on apply. Found via
                // SDK probe (docs/sdk-probe/) — the lifecycle is:
                //   Pattern (PatternDefinition).PatternImplementation
                //     → .GetBuildProcess() returns IPatternBuildProcess
                //     → .UpdateParentObject(parent, instance) projects PatternInstance
                //       onto the bound KBObject's WebForm.
                //
                // This is what the IDE calls internally. We invoke via reflection so we
                // don't add a hard dependency. Errors don't fail the attach — the host
                // is already saved, this just materializes the projection.
                try
                {
                    TryInvokeBuildProcessUpdateParent(parent, hostObj);
                }
                catch (Exception ex)
                {
                    Logger.Info("Package-interface attach: UpdateParentObject best-effort failed: " + ex.Message);
                }

                Logger.Info("Package-interface attach succeeded: host='" + hostName + "' parent='" + parent.Name + "' template='" + usedTemplate + "'");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetType().Name + ": " + ex.Message;
                Logger.Warn("TryPackageInterfaceAttach unexpected: " + ex);
                return false;
            }
        }

        // F17 / F18: delegates to the shared helper. Kept as a thin wrapper so the
        // apply_pattern → projection flow keeps its log context. The actual reflection
        // lives in WwpProjectionHelper so WriteService can call it too.
        internal void TryInvokeBuildProcessUpdateParent(KBObject parent, KBObject host)
        {
            WwpProjectionHelper.TryProjectHostOntoParent(parent, host);
        }

        // Legacy implementation kept for reference; superseded by the call above.
        private void TryInvokeBuildProcessUpdateParent_Legacy(KBObject parent, KBObject host)
        {
            try
            {
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null) { Logger.Debug("[BUILD-PROC] DVelop.Patterns.WorkWithPlus not loaded"); return; }

                var workWithPatternType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPattern", false);
                if (workWithPatternType == null) { Logger.Debug("[BUILD-PROC] WorkWithPattern type not found"); return; }

                object impl;
                try { impl = Activator.CreateInstance(workWithPatternType); }
                catch (Exception ex) { Logger.Debug("[BUILD-PROC] WorkWithPattern ctor failed: " + ex.Message); return; }
                if (impl == null) { Logger.Debug("[BUILD-PROC] WorkWithPattern instance null"); return; }

                // PatternImplementation.Initialize() may be needed for the impl to be
                // functional. Best-effort call.
                try
                {
                    var initMethod = workWithPatternType.GetMethod("Initialize",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    initMethod?.Invoke(impl, null);
                }
                catch (Exception ex) { Logger.Debug("[BUILD-PROC] Initialize skipped: " + ex.Message); }

                var getBuildProcess = workWithPatternType.GetMethod("GetBuildProcess",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getBuildProcess == null) { Logger.Debug("[BUILD-PROC] GetBuildProcess() not found"); return; }

                object buildProcess = getBuildProcess.Invoke(impl, null);
                if (buildProcess == null) { Logger.Debug("[BUILD-PROC] GetBuildProcess returned null"); return; }

                var updateParent = buildProcess.GetType().GetMethod("UpdateParentObject",
                    BindingFlags.Public | BindingFlags.Instance);
                if (updateParent == null) { Logger.Debug("[BUILD-PROC] UpdateParentObject() not found on " + buildProcess.GetType().FullName); return; }

                Logger.Info("[BUILD-PROC] Invoking " + buildProcess.GetType().FullName + ".UpdateParentObject(parent=" + parent.Name + ", host=" + host.Name + ")");
                updateParent.Invoke(buildProcess, new object[] { parent, host });
                Logger.Info("[BUILD-PROC] UpdateParentObject returned successfully");

                // Save parent so projected changes persist. Use KBObjectSavePreferences
                // with ForceSave + SkipValidation because the projected WebForm can fail
                // WebPanel-level semantic validation while still being structurally
                // correct (same trick the IDE uses internally per WriteVisualPart).
                try
                {
                    var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                    {
                        ForceSave = true,
                        ForceSaveDefaultParts = true,
                        SkipValidation = true
                    };
                    parent.Save(prefs);
                    Logger.Info("[BUILD-PROC] Saved parent '" + parent.Name + "' (ForceSave+SkipValidation) after projection.");
                }
                catch (Exception saveEx)
                {
                    Logger.Info("[BUILD-PROC] ForceSave parent threw: " + saveEx.Message + " — falling back to EnsureSave(true).");
                    try { parent.EnsureSave(true); } catch (Exception ex2) { Logger.Info("[BUILD-PROC] EnsureSave fallback also failed: " + ex2.Message); }
                }
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Logger.Warn("[BUILD-PROC] UpdateParentObject threw: " + inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception ex)
            {
                Logger.Warn("[BUILD-PROC] UpdateParentObject reflection failed: " + ex.Message);
            }
        }

        // Run the WWP_ApplyTemplate MSBuild task in-process. This is the IDE's exact
        // path for "Apply Template to WebPanel". The task takes WebPanelName +
        // TemplateName + KB as inputs and Execute() returns Boolean.
        //
        // MSBuild tasks normally need BuildEngine / Log infrastructure. We stub
        // BuildEngine with a NullBuildEngine impl so Execute() can call Log.* methods
        // without NPE, then invoke Execute reflectively.
        internal bool TryRunWwpApplyTemplateTask(Assembly wwpAsm, KBObject webPanel, string templateName, out string hostName, out string errorMessage)
        {
            hostName = null;
            errorMessage = null;
            try
            {
                var taskType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.MSBuildTasks.WWP_ApplyTemplate", false);
                if (taskType == null) { errorMessage = "WWP_ApplyTemplate type not found"; return false; }

                object task;
                try { task = Activator.CreateInstance(taskType); }
                catch (Exception ex) { errorMessage = "WWP_ApplyTemplate ctor failed: " + ex.Message; return false; }

                var kb = _objectService.GetKbService()?.GetKB();
                if (kb == null) { errorMessage = "No KB open"; return false; }

                // Set the task inputs. KB property is the KB instance; WebPanelName +
                // TemplateName drive the apply target.
                taskType.GetProperty("KB")?.SetValue(task, kb);
                taskType.GetProperty("WebPanelName")?.SetValue(task, webPanel.Name);
                taskType.GetProperty("TemplateName")?.SetValue(task, templateName);
                taskType.GetProperty("WebPanelDescription")?.SetValue(task, webPanel.Description ?? webPanel.Name);
                taskType.GetProperty("AddWebPanelToListPrograms")?.SetValue(task, false);
                taskType.GetProperty("CaptureOutput")?.SetValue(task, true);

                // BuildEngine stub — Execute() uses Log methods which dereference BuildEngine.
                try
                {
                    var stubType = typeof(NullBuildEngine);
                    var stub = Activator.CreateInstance(stubType);
                    taskType.GetProperty("BuildEngine")?.SetValue(task, stub);
                }
                catch (Exception ex) { Logger.Debug("BuildEngine stub set skipped: " + ex.Message); }

                var executeMethod = taskType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (executeMethod == null) { errorMessage = "WWP_ApplyTemplate.Execute() not found"; return false; }

                bool ok;
                try { ok = (bool)executeMethod.Invoke(task, null); }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "Execute threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }

                if (!ok)
                {
                    var output = taskType.GetProperty("Output")?.GetValue(task) as string;
                    errorMessage = "Execute returned false. Output: " + (output ?? "<empty>");
                    return false;
                }

                // Look up the host the task created. Convention: WorkWithPlus<WebPanelName>.
                hostName = "WorkWithPlus" + webPanel.Name;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Stub IBuildEngine that swallows MSBuild logging calls — needed because the
        // WWP MSBuild tasks reference BuildEngine.LogMessage / LogError directly.
        private sealed class NullBuildEngine : Microsoft.Build.Framework.IBuildEngine
        {
            public bool ContinueOnError => true;
            public int LineNumberOfTaskNode => 0;
            public int ColumnNumberOfTaskNode => 0;
            public string ProjectFileOfTaskNode => string.Empty;
            public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;
            public void LogCustomEvent(Microsoft.Build.Framework.CustomBuildEventArgs e) { }
            public void LogErrorEvent(Microsoft.Build.Framework.BuildErrorEventArgs e) { Logger.Info("[WWP_TASK ERR] " + e.Message); }
            public void LogMessageEvent(Microsoft.Build.Framework.BuildMessageEventArgs e) { Logger.Debug("[WWP_TASK] " + e.Message); }
            public void LogWarningEvent(Microsoft.Build.Framework.BuildWarningEventArgs e) { Logger.Info("[WWP_TASK WARN] " + e.Message); }
        }

        // [OBSOLETE — kept for reference] Direct-attach via private ctor produced ghost
        // hosts whose PatternInstance edits had no projection. Replaced by
        // TryPackageInterfaceAttach above (uses the official WWP package API).
        internal bool TryDirectAttachPatternInstance(KBObject parent, Guid patternId, out string hostName, out string errorMessage)
        {
            hostName = null;
            errorMessage = null;
            if (parent == null) { errorMessage = "parent KBObject is null"; return false; }

            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Packages.Patterns", StringComparison.OrdinalIgnoreCase));
                if (asm == null) { errorMessage = "Artech.Packages.Patterns not loaded"; return false; }

                var piType = asm.GetType("Artech.Packages.Patterns.Objects.PatternInstance", false);
                if (piType == null) { errorMessage = "PatternInstance type not found"; return false; }

                // Locate the (KBObject, Guid) ctor — usually non-public.
                var ctor = piType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        if (ps.Length != 2) return false;
                        return typeof(KBObject).IsAssignableFrom(ps[0].ParameterType)
                            && ps[1].ParameterType == typeof(Guid);
                    });
                if (ctor == null) { errorMessage = "PatternInstance(KBObject, Guid) ctor not found"; return false; }

                object instance;
                try
                {
                    instance = ctor.Invoke(new object[] { parent, patternId });
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "ctor threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }
                if (instance == null) { errorMessage = "ctor returned null"; return false; }

                // The instance is a KBObject. Give it a sensible name and Save() so it
                // lands in the KB. Convention matches what the engine produces for
                // Transaction targets: "WorkWithPlus<parentName>".
                string desiredName = "WorkWithPlus" + parent.Name;
                try
                {
                    var nameProp = piType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null && nameProp.CanWrite)
                    {
                        nameProp.SetValue(instance, desiredName);
                    }
                }
                catch (Exception ex) { Logger.Debug("DirectAttach: set Name skipped: " + ex.Message); }

                // Seed the PatternInstance part with a minimal valid Panel-mode XML.
                // Without this, Save() throws ValidationException("A validação de
                // WorkWithPlus instância falhou.") because WWP rejects an empty <instance/>.
                // The XML must declare type="Panel" so the engine generates the panel
                // family (PanelView, PanelTabTabular etc) rather than the Transaction one.
                try
                {
                    var parts = piType.GetProperty("Parts", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as System.Collections.IEnumerable;
                    KBObjectPart targetPart = null;
                    if (parts != null)
                    {
                        foreach (var p in parts)
                        {
                            var part = p as KBObjectPart;
                            if (part == null) continue;
                            if (string.Equals(part.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                part.GetType().Name.IndexOf("PatternInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                targetPart = part;
                                break;
                            }
                        }
                    }

                    if (targetPart != null)
                    {
                        // Mode/Dirty bookkeeping mirrors WriteService.ApplyPatternEnvelope —
                        // without this the SDK skips persisting the seeded XML.
                        WriteService.ForcePatternPartDirty(targetPart);

                        // Minimal valid seed: an `<instance type="WebPanel">` with a
                        // `<WPRoot Template="<resolved>">` host that has a `<table>` (required,
                        // CanModifyCollection=false in the schema) and a `<steps/>` slot.
                        // Validated against real samples in WWP/Resources/BaseXmlObjects.xml.
                        //
                        // Template name MUST resolve to a `WorkWithPlus for Web Template`
                        // KBObject in THIS KB — the validator rejects unknown names. We
                        // discover one at runtime instead of hard-coding "Empty" (which is
                        // a WWP-ship name that most real KBs don't register).
                        string template = ResolveAvailableWwpTemplate(null);
                        string seedXml =
                            "<instance type=\"" + (parent.TypeDescriptor?.Name == "SDPanel" ? "Panel" : "WebPanel") + "\">\n" +
                            "  <WPRoot Template=\"" + System.Security.SecurityElement.Escape(template) + "\" defaultTemplate=\"" + System.Security.SecurityElement.Escape(template) + "\" childrenOrderedList=\"7;66;TableMain\">\n" +
                            "    <table name=\"TableMain\" themeClass=\"TableMain\" defaultThemeClass=\"TableMain\" type=\"Responsive\" defaultType=\"Responsive\" childrenOrderedList=\"7;28;ErrorViewer\">\n" +
                            "      <errorViewer defaultThemeClass=\"ErrorViewer\" />\n" +
                            "    </table>\n" +
                            "    <steps />\n" +
                            "  </WPRoot>\n" +
                            "</instance>";
                        if (!WriteService.ApplyPatternDataFromXml(targetPart, seedXml))
                        {
                            Logger.Warn("DirectAttach: seed XML failed to apply via DeserializeDataFrom — Save will likely fail validation.");
                        }
                    }
                }
                catch (Exception seedEx)
                {
                    Logger.Debug("DirectAttach: seed step skipped: " + seedEx.Message);
                }

                // Save the new host. KBObject.Save() is in the inherited base class.
                var saveMethod = piType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (saveMethod == null) { errorMessage = "PatternInstance.Save() not found"; return false; }
                try
                {
                    saveMethod.Invoke(instance, null);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    errorMessage = "Save threw: " + inner.GetType().Name + ": " + inner.Message;
                    return false;
                }

                hostName = desiredName;
                Logger.Info("PatternInstance direct-attach succeeded: host='" + desiredName + "' parent='" + parent.Name + "' (" + parent.TypeDescriptor?.Name + ")");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetType().Name + ": " + ex.Message;
                Logger.Warn("DirectAttach unexpected failure: " + ex);
                return false;
            }
        }

        // Diagnostic dump of the SDK surface we can reach from the pattern definition.
        // Enabled by GX_MCP_PATTERN_PROBE=1 to keep the response clean by default. Lists
        // public static methods on PatternEngine, public instance methods on the
        // PatternDefinition runtime type, and public methods on PatternInstance — so we
        // can iterate toward a direct-attach API for the WebPanel-target case.
        private JObject DumpSdkSurface(object patternDefinition, Guid patternId)
        {
            var dump = new JObject();
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Packages.Patterns", StringComparison.OrdinalIgnoreCase));
                if (asm == null) { dump["error"] = "Artech.Packages.Patterns not loaded"; return dump; }

                var patternEngine = asm.GetType("Artech.Packages.Patterns.PatternEngine", false);
                var patternInstance = asm.GetType("Artech.Packages.Patterns.Objects.PatternInstance", false);

                dump["engineStatics"] = DumpMethods(patternEngine, BindingFlags.Public | BindingFlags.Static);
                dump["instanceStatics"] = DumpMethods(patternInstance, BindingFlags.Public | BindingFlags.Static);
                dump["instanceInstance"] = DumpMethods(patternInstance, BindingFlags.Public | BindingFlags.Instance);

                // F16: deep-probe DVelop.Patterns.WorkWithPlus — the WWP-specific impl
                // assembly. Also: Artech.Template.Helper which exposes template-apply
                // surface, plus any 'WorkWithPlus for Web Template' KBObject descriptor.
                // Hunting for: WorkWithPlusInstance / WorkWithPlusForWebTemplate /
                // anything with "ApplyTemplate" / "Initialize" / "Generate".
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null)
                {
                    try
                    {
                        var gxPath = Environment.GetEnvironmentVariable("GX_PATH") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                        var wwpDllPath = Path.Combine(gxPath, "Packages", "Patterns", "WorkWithPlus", "DVelop.Patterns.WorkWithPlus.dll");
                        if (File.Exists(wwpDllPath)) wwpAsm = Assembly.LoadFrom(wwpDllPath);
                    }
                    catch (Exception ex) { dump["wwpAsmLoadErr"] = ex.Message; }
                }

                if (wwpAsm != null)
                {
                    var wwpTypes = new JArray();
                    foreach (var t in wwpAsm.GetTypes())
                    {
                        if (!t.IsPublic) continue;
                        if (t.Name.IndexOf("Template", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Instance", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Helper", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var entry = new JObject { ["fullName"] = t.FullName };
                        entry["publicStatic"] = DumpMethods(t, BindingFlags.Public | BindingFlags.Static);
                        entry["publicInstance"] = DumpMethods(t, BindingFlags.Public | BindingFlags.Instance);
                        // Add properties for tasks (MSBuild tasks expose inputs as props).
                        var propArr = new JArray();
                        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            propArr.Add(p.Name + ":" + p.PropertyType.Name);
                        }
                        entry["publicProperties"] = propArr;
                        wwpTypes.Add(entry);
                    }
                    dump["wwpTypes"] = wwpTypes;
                }

                // Probe TemplateService / template-apply route in Artech.Template.Helper.
                var tmplAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name?.IndexOf("Template", StringComparison.OrdinalIgnoreCase) >= 0);
                if (tmplAsm != null)
                {
                    dump["templateAsmName"] = tmplAsm.GetName().Name;
                    var tmplTypes = new JArray();
                    foreach (var t in tmplAsm.GetExportedTypes())
                    {
                        if (t.Name.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Service", StringComparison.OrdinalIgnoreCase) < 0 &&
                            t.Name.IndexOf("Template", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        tmplTypes.Add(new JObject
                        {
                            ["fullName"] = t.FullName,
                            ["methods"] = DumpMethods(t, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                        });
                    }
                    dump["templateTypes"] = tmplTypes;
                }

                // List `WorkWithPlus for Web Template` KBObject descriptor — the type
                // GUID lets us read template content + understand the apply route.
                try
                {
                    var kb = _objectService?.GetKbService()?.GetKB();
                    if (kb != null)
                    {
                        var tmplInKb = new JArray();
                        foreach (KBObject o in kb.DesignModel.Objects.GetAll())
                        {
                            if (o == null) continue;
                            if (!string.Equals(o.TypeDescriptor?.Name, "WorkWithPlus for Web Template", StringComparison.OrdinalIgnoreCase)) continue;
                            tmplInKb.Add(new JObject
                            {
                                ["name"] = o.Name,
                                ["typeGuid"] = o.TypeDescriptor.Id.ToString(),
                                ["parts"] = new JArray(o.Parts.Cast<KBObjectPart>().Select(p => p.Name).ToArray())
                            });
                            if (tmplInKb.Count >= 3) break; // sample only
                        }
                        dump["wwpWebTemplatesInKb"] = tmplInKb;
                    }
                }
                catch (Exception ex) { dump["wwpWebTemplatesErr"] = ex.Message; }

                if (patternDefinition != null)
                {
                    var t = patternDefinition.GetType();
                    dump["patternDefinitionType"] = t.FullName;

                    // Resolve ParentTypes — the list of KBObject types WWP accepts as
                    // parent. This is the key piece: if WebPanel isn't in this list, the
                    // engine silently no-ops the apply for WebPanel targets.
                    try
                    {
                        var prop = t.GetProperty("ParentTypes", BindingFlags.Public | BindingFlags.Instance);
                        var val = prop?.GetValue(patternDefinition) as System.Collections.IEnumerable;
                        var arr = new JArray();
                        if (val != null) foreach (var item in val) arr.Add(item?.ToString() ?? "null");
                        dump["parentTypes"] = arr;
                    }
                    catch (Exception ex) { dump["parentTypesError"] = ex.Message; }

                    // Dump pattern Objects with their type-scope so we can see which ones
                    // target WebPanel specifically.
                    try
                    {
                        var prop = t.GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
                        var val = prop?.GetValue(patternDefinition) as System.Collections.IEnumerable;
                        var arr = new JArray();
                        if (val != null)
                        {
                            foreach (var item in val)
                            {
                                if (item == null) continue;
                                var pt = item.GetType();
                                var entry = new JObject { ["type"] = pt.FullName };
                                foreach (var p in pt.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    if (p.GetIndexParameters().Length > 0) continue;
                                    try
                                    {
                                        var v = p.GetValue(item);
                                        if (v == null) { entry[p.Name] = null; continue; }
                                        if (v is string || v is int || v is bool || v is Guid)
                                            entry[p.Name] = v.ToString();
                                        else
                                            entry[p.Name] = v.GetType().Name;
                                    }
                                    catch { }
                                }
                                arr.Add(entry);
                            }
                        }
                        dump["patternObjects"] = arr;
                    }
                    catch { }

                    // Dump DefinitionFile path so we can read the raw XML manifest.
                    try
                    {
                        var defFile = t.GetProperty("DefinitionFile")?.GetValue(patternDefinition)?.ToString();
                        var basePath = t.GetProperty("DefinitionBasePath")?.GetValue(patternDefinition)?.ToString();
                        dump["definitionFile"] = defFile;
                        dump["definitionBasePath"] = basePath;
                    }
                    catch { }

                    // Probe Pattern.cs / PatternEngine internals — try to find a Pattern
                    // instance constructor or a private "create instance for type" helper.
                    try
                    {
                        var piType = asm.GetType("Artech.Packages.Patterns.Objects.PatternInstance");
                        if (piType != null)
                        {
                            var ctors = piType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var arr = new JArray();
                            foreach (var c in ctors)
                            {
                                string sig = (c.IsPublic ? "public " : "private ") +
                                    "ctor(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
                                arr.Add(sig);
                            }
                            dump["patternInstanceCtors"] = arr;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                dump["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return dump;
        }

        private static JArray DumpMethods(Type t, BindingFlags flags)
        {
            var arr = new JArray();
            if (t == null) return arr;
            try
            {
                foreach (var m in t.GetMethods(flags))
                {
                    if (m.IsSpecialName) continue; // skip property getters/setters
                    if (m.DeclaringType == typeof(object)) continue;
                    string sig = m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name;
                    arr.Add(sig);
                }
            }
            catch { }
            return arr;
        }

        // Probes the KB for the canonical WorkWithPlus family generated by a first-apply
        // on a Transaction: the host (`WorkWithPlus<X>`) plus the WW/View/Export* siblings.
        // We don't iterate the whole model — that's slow on large KBs (38k+ objects) and
        // races against async SDK persistence. Targeted FindObject lookups by name are
        // O(family_size) regardless of KB size.
        //
        // Naming reference (GeneXus 18 WWP default): `WorkWithPlus<X>` host, `WW<X>`
        // selection panel, `View<X>` detail, `ExportWW<X>`/`ExportReportWW<X>` exports,
        // `Prompt<X>` prompt. WebPanel targets don't generate siblings — they get the
        // host attached directly.
        private List<string> LookupWwpFamilyByConvention(KBObject parent)
        {
            var found = new List<string>();
            if (parent == null || _objectService == null) return found;
            string baseName = parent.Name;
            if (string.IsNullOrEmpty(baseName)) return found;

            string[] candidates = new[]
            {
                "WorkWithPlus" + baseName,
                "WW" + baseName,
                "View" + baseName,
                "ExportWW" + baseName,
                "ExportReportWW" + baseName,
                "Prompt" + baseName
            };

            foreach (var name in candidates)
            {
                try
                {
                    var o = _objectService.FindObject(name);
                    if (o != null && !string.IsNullOrEmpty(o.Name))
                    {
                        found.Add(o.Name);
                    }
                }
                catch { /* lookup best-effort */ }
            }
            return found;
        }

        private PatternApplyResult TryReapplyWithFallback(
            object existingInstance,
            KBObject parent,
            object patternDefinition,
            JObject settings,
            out bool wasFirstApply)
        {
            try
            {
                var result = _engine.ReapplyPattern(existingInstance, settings);
                wasFirstApply = false;
                return result;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.IndexOf("ApplyPattern(PatternInstance, ApplySettings)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // SDK lacks the reapply overload on this install — replay the first-apply,
                // which the engine treats as a re-apply when an instance already exists.
                Logger.Info("PatternEngine reapply overload missing — falling back to first-apply overload.");
                if (settings != null && settings.Count > 0)
                {
                    Logger.Info("Reapply fallback: settings ignored on the void overload — defaults applied.");
                }
                var result = _engine.ApplyPattern(parent, patternDefinition, settings);
                wasFirstApply = false;
                return result;
            }
        }

        private static bool TryResolvePatternId(string patternKey, out Guid id)
        {
            if (KnownPatterns.TryGetValue(patternKey.Trim(), out id)) return true;
            return Guid.TryParse(patternKey.Trim(), out id);
        }

        private static string PatternUnavailable(string patternKey, string message)
        {
            var j = new JObject
            {
                ["status"] = "pattern_unavailable",
                ["patternKey"] = patternKey,
                ["message"] = message
            };
            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── Item 45: Pattern Diagnose ────────────────────────────────────────────
        // Read-only preflight: resolve target + pattern, then run every validation
        // gate that ApplyPattern would run, but return structured reasons instead of
        // mutating anything. No SDK apply is called.
        //
        // Returned reasons (one or more may fire):
        //   parentTypeMismatch   — target type is not supported by the pattern
        //   overrideConflict     — an existing PatternInstance host already exists
        //   templateInvalid      — caller-supplied template not found in KB
        //   missingRequiredAttribute — target is null / unresolvable
        //   ok                   — all checks pass; apply would proceed
        //
        // Each finding object: { reason, severity, detail, remediation }
        public string DiagnosePattern(string objectName, string patternKey, JObject settings = null)
        {
            try
            {
                var findings = new JArray();

                // ── 1. Parameter validation ──────────────────────────────────────
                if (string.IsNullOrWhiteSpace(objectName))
                {
                    findings.Add(Finding("missingRequiredAttribute", "critical",
                        "objectName is empty or null.",
                        "Pass name=<KBObject name>."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }
                if (string.IsNullOrWhiteSpace(patternKey))
                {
                    findings.Add(Finding("missingRequiredAttribute", "critical",
                        "pattern key is empty or null.",
                        "Pass pattern='WorkWithPlus' or a known GUID."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }

                // ── 2. Pattern resolution ────────────────────────────────────────
                if (!TryResolvePatternId(patternKey, out Guid patternId))
                {
                    findings.Add(Finding("templateInvalid", "critical",
                        $"Unknown pattern key '{patternKey}'. Not in known-patterns registry and not a valid GUID.",
                        "Use 'WorkWithPlus' (or alias 'WWP') or supply a raw pattern GUID."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }

                // ── 3. Engine / license availability ────────────────────────────
                object patternDef = null;
                try { patternDef = _engine.GetPatternDefinition(patternId); } catch { }
                if (patternDef == null)
                {
                    findings.Add(Finding("templateInvalid", "critical",
                        "Pattern engine returned null for the given pattern GUID — package probably not installed or license inactive.",
                        "Verify the WorkWithPlus package is present in GeneXus\\Packages\\Patterns\\WorkWithPlus\\. Check license activation."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }

                // ── 4. Object resolution ─────────────────────────────────────────
                KBObject obj = ResolveObject(objectName);
                if (obj == null)
                {
                    findings.Add(Finding("missingRequiredAttribute", "critical",
                        $"Object '{objectName}' not found in the KB.",
                        "Verify the name with genexus_query or genexus_list_objects."));
                    return DiagnoseResponse(objectName, patternKey, findings);
                }

                string parentType = obj.TypeDescriptor?.Name ?? "";

                // ── 5. Parent-type gate ──────────────────────────────────────────
                string callerTemplate = settings?["template"]?.ToString();
                List<string> availableTemplates = null;
                bool isWebPanelKind = string.Equals(parentType, "WebPanel", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(parentType, "SDPanel", StringComparison.OrdinalIgnoreCase);
                if (isWebPanelKind)
                {
                    try { availableTemplates = ListWwpWebTemplates(); } catch { availableTemplates = new List<string>(); }
                }

                string typeGateReject = TryBuildTypeGateRejection(obj.Name, patternKey, parentType, callerTemplate, availableTemplates);
                if (typeGateReject != null)
                {
                    var rejectEnv = JObject.Parse(typeGateReject);
                    bool isTemplateIssue = rejectEnv["error"]?.ToString()?.Contains("Template") == true
                                       || rejectEnv["error"]?.ToString()?.Contains("template") == true;
                    string reason = isTemplateIssue ? "templateInvalid" : "parentTypeMismatch";
                    string detail = rejectEnv["error"]?.ToString() ?? "Type gate rejected apply.";
                    string remediation = rejectEnv["hint"]?.ToString()
                        ?? (rejectEnv["availableTemplates"] != null
                            ? "Pass settings.template equal to one of availableTemplates: " + rejectEnv["availableTemplates"]
                            : "Apply WorkWithPlus only to Transaction, WebPanel or SDPanel.");
                    findings.Add(Finding(reason, "critical", detail, remediation));
                }

                // ── 6. Override / existing instance conflict ─────────────────────
                object existingInstance = null;
                try { existingInstance = _engine.GetPatternInstance(obj, patternId); } catch { }
                if (existingInstance != null)
                {
                    findings.Add(Finding("overrideConflict", "warn",
                        $"An existing PatternInstance for '{patternKey}' was found on '{objectName}'. A first-apply will be a no-op; use reapply=true.",
                        "Call genexus_apply_pattern with reapply=true to regenerate the existing pattern instance."));
                }

                // ── 7. Missing required attribute: template for WebPanel targets ──
                if (isWebPanelKind && string.IsNullOrEmpty(callerTemplate) && typeGateReject == null)
                {
                    if (availableTemplates != null && availableTemplates.Count > 0)
                    {
                        findings.Add(Finding("missingRequiredAttribute", "info",
                            $"No settings.template supplied for {parentType} target. MCP will auto-discover one ({availableTemplates[0]}).",
                            $"Pass settings.template explicitly to pin the template. Available: {string.Join(", ", availableTemplates)}."));
                    }
                    else
                    {
                        findings.Add(Finding("missingRequiredAttribute", "warn",
                            $"No settings.template supplied and no 'WorkWithPlus for Web Template' objects found in this KB.",
                            "Import or create a 'WorkWithPlus for Web Template' object before applying to a WebPanel."));
                    }
                }

                // ── 8. ok — all critical checks passed ──────────────────────────
                if (!findings.Any(f => f["severity"]?.ToString() == "critical"))
                {
                    findings.Add(Finding("ok", "info",
                        "All pre-apply checks passed. The pattern should apply cleanly.",
                        existingInstance != null
                            ? "Remember to pass reapply=true (an instance already exists)."
                            : "Call genexus_apply_pattern to proceed."));
                }

                return DiagnoseResponse(obj.Name, patternKey, findings);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.DiagnosePattern failed: " + ex);
                return McpResponse.Error(ex.Message, objectName);
            }
        }

        // ── helpers used only by DiagnosePattern ────────────────────────────────

        private static JObject Finding(string reason, string severity, string detail, string remediation)
        {
            return new JObject
            {
                ["reason"] = reason,
                ["severity"] = severity,
                ["detail"] = detail,
                ["remediation"] = remediation
            };
        }

        private static string DiagnoseResponse(string target, string patternKey, JArray findings)
        {
            var hasAnyOk = findings.Any(f => f["reason"]?.ToString() == "ok");
            var hasCritical = findings.Any(f => f["severity"]?.ToString() == "critical");
            string overallStatus = hasCritical ? "blocked" : (hasAnyOk ? "ok" : "warnings");
            var resp = new JObject
            {
                ["status"] = overallStatus,
                ["target"] = target ?? "",
                ["patternKey"] = patternKey ?? "",
                ["findings"] = findings
            };
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>
    /// Result of an apply/re-apply call — kept minimal because the SDK does not
    /// surface a structured return for first-time apply (void) and only a bool
    /// for re-apply. Both are normalized into this shape.
    /// </summary>
    public class PatternApplyResult
    {
        public IList<string> GeneratedObjects { get; set; } = new List<string>();
        public IList<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Engine boundary so tests can mock the live SDK without a KB or license.
    /// </summary>
    public interface IPatternEngineAdapter
    {
        /// <summary>Returns a PatternDefinition (as object) or null when unavailable.</summary>
        object GetPatternDefinition(Guid patternId);

        /// <summary>Returns the existing PatternInstance for (object, patternId), or null.</summary>
        object GetPatternInstance(KBObject parent, Guid patternId);

        /// <summary>First-time apply. SDK signature is void; throws are propagated.</summary>
        PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings);

        /// <summary>Re-apply against an existing PatternInstance.</summary>
        PatternApplyResult ReapplyPattern(object patternInstance, JObject settings);
    }

    /// <summary>
    /// Live implementation. Loads Artech.Packages.Patterns.dll by reflection so
    /// the worker compiles without a hard reference. All calls return null /
    /// throw a controlled NotAvailable status when the assembly is missing,
    /// which the service surfaces as "pattern_unavailable".
    /// </summary>
    internal class ReflectionPatternEngineAdapter : IPatternEngineAdapter
    {
        private const string PatternsAssemblyName = "Artech.Packages.Patterns";
        private const string PatternEngineTypeName = "Artech.Packages.Patterns.PatternEngine";
        private const string PatternInstanceTypeName = "Artech.Packages.Patterns.Objects.PatternInstance";

        private readonly object _lock = new object();
        private bool _probed;
        private Assembly _patternsAssembly;
        private Type _patternEngineType;
        private Type _patternInstanceType;
        private MethodInfo _getPatternDefinition;
        private MethodInfo _applyPatternFirst;     // ApplyPattern(KBObject, PatternDefinition)
        private MethodInfo _applyPatternReapply;   // ApplyPattern(PatternInstance, ApplySettings)
        private MethodInfo _patternInstanceGet;    // PatternInstance.Get(KBObject, Guid)
        private Type _applySettingsType;           // second parameter of _applyPatternReapply

        private bool EnsureProbed()
        {
            if (_probed) return _patternEngineType != null;
            lock (_lock)
            {
                if (_probed) return _patternEngineType != null;
                try
                {
                    _patternsAssembly = LoadPatternsAssembly();
                    if (_patternsAssembly != null)
                    {
                        _patternEngineType = _patternsAssembly.GetType(PatternEngineTypeName, false);
                        _patternInstanceType = _patternsAssembly.GetType(PatternInstanceTypeName, false);
                    }

                    if (_patternEngineType != null)
                    {
                        _getPatternDefinition = _patternEngineType.GetMethod(
                            "GetPatternDefinition",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(Guid) }, null);

                        // Disambiguate by parameter count; concrete parameter types are
                        // resolved at call time via assembly-loaded Type references.
                        // Disambiguation bug fix (F16): `IsAssignableFrom(KBObject)` matched
                        // BOTH overloads because PatternInstance INHERITS from KBObject. Result:
                        // both methods bound to _applyPatternFirst, _applyPatternReapply stayed
                        // null, and reapply silently failed. Disambiguate by EXACT type now —
                        // the first overload is (KBObject, PatternDefinition), the reapply is
                        // (PatternInstance, ApplySettings).
                        foreach (var m in _patternEngineType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name != "ApplyPattern") continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 2) continue;
                            // Reapply: first param is PatternInstance (or a subtype).
                            if (_patternInstanceType != null && _patternInstanceType.IsAssignableFrom(ps[0].ParameterType))
                            {
                                _applyPatternReapply = m;
                                _applySettingsType = ps[1].ParameterType;
                            }
                            // First-apply: first param is KBObject EXACTLY (or generic KBObject
                            // base, not a PatternInstance-derived subtype).
                            else if (ps[0].ParameterType == typeof(KBObject))
                            {
                                _applyPatternFirst = m;
                            }
                        }
                    }

                    if (_patternInstanceType != null)
                    {
                        _patternInstanceGet = _patternInstanceType.GetMethod(
                            "Get",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(KBObject), typeof(Guid) }, null);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("PatternEngine probe failed: " + ex.Message);
                }
                _probed = true;
                return _patternEngineType != null;
            }
        }

        private static Assembly LoadPatternsAssembly()
        {
            // 1. Already loaded?
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, PatternsAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null) return loaded;

            // 2. Try the standard install path under Packages\.
            string gxPath = Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrEmpty(gxPath))
                gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";

            string candidate = Path.Combine(gxPath, "Packages", PatternsAssemblyName + ".dll");
            if (File.Exists(candidate))
            {
                try { return Assembly.LoadFrom(candidate); }
                catch (Exception ex) { Logger.Warn("LoadFrom " + candidate + " failed: " + ex.Message); }
            }

            // 3. Last-resort: by name (will hit fusion's probing paths).
            try { return Assembly.Load(PatternsAssemblyName); }
            catch { return null; }
        }

        public object GetPatternDefinition(Guid patternId)
        {
            if (!EnsureProbed() || _getPatternDefinition == null) return null;
            try { return _getPatternDefinition.Invoke(null, new object[] { patternId }); }
            catch (TargetInvocationException tie)
            {
                Logger.Warn("GetPatternDefinition threw: " + (tie.InnerException?.Message ?? tie.Message));
                return null;
            }
        }

        public object GetPatternInstance(KBObject parent, Guid patternId)
        {
            if (!EnsureProbed() || _patternInstanceGet == null) return null;
            try { return _patternInstanceGet.Invoke(null, new object[] { parent, patternId }); }
            catch (TargetInvocationException) { return null; }
        }

        public PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings)
        {
            if (!EnsureProbed() || _applyPatternFirst == null)
                throw new InvalidOperationException("PatternEngine.ApplyPattern(KBObject, PatternDefinition) not found");

            // First-apply overload is void ApplyPattern(KBObject, PatternDefinition) — no
            // ApplySettings slot. Settings only flow on re-apply. If the caller supplied
            // settings on first-apply, surface a log line so they know the call defaulted.
            if (settings != null && settings.Count > 0)
            {
                Logger.Info("ApplyPattern (first-apply): settings ignored on this overload — defaults applied. Use reapply=true after apply to project settings.");
            }

            try
            {
                _applyPatternFirst.Invoke(null, new[] { parent, patternDefinition });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            // The SDK doesn't surface generated-object names from the void overload.
            // Best-effort: scan the new PatternInstance for owned objects after apply.
            return new PatternApplyResult();
        }

        // Public surface so PatternApplyService can re-apply on a host that was just
        // attached via PatternInstancePackageInterface (no probe re-run needed).
        public bool HasReapplyOverload()
        {
            EnsureProbed();
            return _applyPatternReapply != null;
        }

        public void InvokeReapply(object patternInstanceObj, JObject settings)
        {
            EnsureProbed();
            if (_applyPatternReapply == null) throw new InvalidOperationException("Reapply overload not bound");
            // ApplySettings is needed by the SDK (passing null causes NRE inside).
            // Materialise an empty instance via parameterless ctor and project caller
            // settings on top if provided.
            object applySettings = null;
            if (_applySettingsType != null)
            {
                try
                {
                    var ctor = _applySettingsType.GetConstructor(Type.EmptyTypes);
                    applySettings = ctor != null
                        ? ctor.Invoke(null)
                        : Activator.CreateInstance(_applySettingsType, nonPublic: true);
                    if (settings != null && settings.Count > 0)
                    {
                        ProjectJObjectOntoInstance(settings, applySettings, new List<string>(), 0);
                    }
                }
                catch (Exception ex) { Logger.Debug("InvokeReapply: ApplySettings build skipped: " + ex.Message); }
            }
            try
            {
                _applyPatternReapply.Invoke(null, new[] { patternInstanceObj, applySettings });
            }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        public PatternApplyResult ReapplyPattern(object patternInstance, JObject settings)
        {
            if (!EnsureProbed() || _applyPatternReapply == null)
                throw new InvalidOperationException("PatternEngine.ApplyPattern(PatternInstance, ApplySettings) not found");

            // ApplySettings is a pattern-internal type. We materialise it by reflection
            // (parameterless ctor) and best-effort-project the caller's JObject onto its
            // writable instance properties. Null/empty settings → pass null, which the
            // SDK treats as "use defaults" (preserving the historical behaviour).
            object applySettings = null;
            var unmapped = new List<string>();
            if (settings != null && settings.Count > 0)
            {
                applySettings = TryBuildApplySettings(settings, unmapped);
                if (applySettings == null)
                {
                    Logger.Warn("ReapplyPattern: failed to materialise ApplySettings; falling back to defaults. Unmapped keys: " + string.Join(", ", unmapped));
                }
                else if (unmapped.Count > 0)
                {
                    Logger.Info("ReapplyPattern: ApplySettings projected with " + unmapped.Count + " unmapped key(s): " + string.Join(", ", unmapped));
                }
            }

            try
            {
                _applyPatternReapply.Invoke(null, new[] { patternInstance, applySettings });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            return new PatternApplyResult();
        }

        // Builds an ApplySettings instance and walks the JObject onto its writable
        // properties (case-insensitive). Best-effort: any miss is collected in
        // `unmapped` rather than thrown, so a partial projection still beats null.
        internal object TryBuildApplySettings(JObject settings, IList<string> unmapped)
        {
            if (_applySettingsType == null) return null;
            object instance;
            try
            {
                var ctor = _applySettingsType.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    // No parameterless ctor — try the activator anyway, in case the
                    // type exposes a non-public default.
                    instance = Activator.CreateInstance(_applySettingsType, nonPublic: true);
                }
                else
                {
                    instance = ctor.Invoke(null);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("ApplySettings ctor failed for " + _applySettingsType.FullName + ": " + ex.Message);
                return null;
            }

            ProjectJObjectOntoInstance(settings, instance, unmapped, depth: 0);
            return instance;
        }

        private static void ProjectJObjectOntoInstance(JObject src, object dst, IList<string> unmapped, int depth)
        {
            if (src == null || dst == null) return;
            // Defensive: pattern engine settings trees should not be deep. Avoid
            // pathological recursion if a nested type self-references.
            if (depth > 6) return;

            var type = dst.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in src)
            {
                if (!props.TryGetValue(kv.Key, out var prop))
                {
                    unmapped.Add(kv.Key);
                    continue;
                }
                try
                {
                    object value = CoerceJTokenToType(kv.Value, prop.PropertyType, unmapped, depth);
                    if (value == DBNull.Value) // sentinel: coercion declined
                    {
                        unmapped.Add(kv.Key);
                        continue;
                    }
                    prop.SetValue(dst, value, null);
                }
                catch (Exception ex)
                {
                    Logger.Debug("ApplySettings set " + kv.Key + " failed: " + ex.Message);
                    unmapped.Add(kv.Key);
                }
            }
        }

        private static object CoerceJTokenToType(JToken token, Type target, IList<string> unmapped, int depth)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var underlying = Nullable.GetUnderlyingType(target) ?? target;

            if (underlying == typeof(string)) return token.ToObject<string>();
            if (underlying == typeof(bool)) return token.ToObject<bool>();
            if (underlying == typeof(int)) return token.ToObject<int>();
            if (underlying == typeof(long)) return token.ToObject<long>();
            if (underlying == typeof(double)) return token.ToObject<double>();
            if (underlying == typeof(Guid)) return Guid.Parse(token.ToObject<string>() ?? string.Empty);
            if (underlying.IsEnum)
            {
                var s = token.ToObject<string>();
                if (string.IsNullOrEmpty(s))
                {
                    // numeric enum value
                    return Enum.ToObject(underlying, token.ToObject<int>());
                }
                return Enum.Parse(underlying, s, ignoreCase: true);
            }

            // Nested object → recurse (best-effort: requires parameterless ctor).
            if (token.Type == JTokenType.Object)
            {
                try
                {
                    var ctor = underlying.GetConstructor(Type.EmptyTypes);
                    var nested = ctor != null
                        ? ctor.Invoke(null)
                        : Activator.CreateInstance(underlying, nonPublic: true);
                    if (nested == null) return DBNull.Value;
                    ProjectJObjectOntoInstance((JObject)token, nested, unmapped, depth + 1);
                    return nested;
                }
                catch
                {
                    return DBNull.Value;
                }
            }

            // Last resort: let Newtonsoft figure it out (handles arrays/lists for known shapes).
            try { return token.ToObject(target); }
            catch { return DBNull.Value; }
        }
    }
}
