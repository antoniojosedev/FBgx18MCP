using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class WriteService : IWriteServiceFacade
    {
        private readonly ObjectService _objectService;
        private readonly PatternAnalysisService _patternAnalysisService;
        private ValidationService _validationService;
        private static readonly object _persistenceWarmupLock = new object();
        private static bool _persistenceWarmupDone = false;
        private static readonly object _flushLock = new object();
        private static System.Timers.Timer _flushTimer;
        private static bool _pendingCommit = false;

        public WriteService(ObjectService objectService)
        {
            _objectService = objectService;
            _patternAnalysisService = new PatternAnalysisService(objectService);
            InitializeFlushTimer();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushBackground();
        }

        public void SetValidationService(ValidationService vs) { _validationService = vs; }

        public string ApplySemanticOps(JObject req)
        {
            string target = req?["target"]?.ToString();
            string partName = req?["part"]?.ToString();
            if (string.IsNullOrEmpty(partName)) partName = "Structure";
            return WrapWithPersistedState(ApplySemanticOpsImpl(req), target, partName, GxMcp.Worker.Helpers.WriteResultMeta.Ops);
        }

        private string ApplySemanticOpsImpl(JObject req)
        {
            // Validation runs here — no GeneXus types referenced in this method body.
            // GeneXus SDK types are isolated in ApplySemanticOpsCore so JIT can load
            // this method even when GeneXus assemblies are absent (unit-test environment).
            try
            {
                if (req == null)
                    throw new UsageException("usage_error", "request required");

                string target = req["target"]?.ToString();
                string partName = req["part"]?.ToString();
                JArray opsRaw = req["ops"] as JArray;
                bool dryRun = req["dryRun"]?.ToObject<bool?>() ?? false;
                bool returnPostState = req["return_post_state"]?.ToObject<bool?>() ?? true;
                bool verbose = req["verbose"]?.ToObject<bool?>() ?? false;

                if (string.IsNullOrEmpty(target))
                    throw new UsageException("usage_error", "target required");
                if (opsRaw == null || opsRaw.Count == 0)
                    throw new UsageException("usage_error", "ops[] required");
                if (string.IsNullOrEmpty(partName))
                    partName = "Structure";

                // Pre-flight: reject immediately when no KB is open, before JIT-loading GeneXus types.
                if (!_objectService.GetKbService().IsOpen)
                    throw new UsageException("usage_error", "object '" + target + "' not found");

                return ApplySemanticOpsCore(target, partName, opsRaw, dryRun, returnPostState, verbose);
            }
            catch (UsageException ux)
            {
                return new JObject
                {
                    ["isError"] = true,
                    ["error"] = new JObject
                    {
                        ["code"] = ux.Code,
                        ["message"] = ux.Message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["isError"] = true,
                    ["error"] = new JObject
                    {
                        ["code"] = "internal_error",
                        ["message"] = ex.Message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private string ApplySemanticOpsCore(string target, string partName, JArray opsRaw, bool dryRun, bool returnPostState = true, bool verbose = false)
        {
            var obj = _objectService.FindObject(target, null);
            if (obj == null)
                throw new UsageException("usage_error", "object '" + target + "' not found");

            string kind = obj.TypeDescriptor?.Name ?? "";

            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);
            if (part == null)
                throw new UsageException("usage_error",
                    "part '" + partName + "' not found in " + kind);

            string currentXml = part.SerializeToXml();
            if (string.IsNullOrEmpty(currentXml))
                throw new UsageException("usage_error",
                    "part '" + partName + "' produced empty XML");

            var ops = opsRaw.OfType<JObject>().Select(SemanticOp.From).ToList();

            string newXml = new SemanticOpsService().Apply(currentXml, kind, ops);

            if (dryRun)
                return DryRunPlanBuilder.BuildEnvelope(target, currentXml, newXml, "ops").ToString(Newtonsoft.Json.Formatting.None);

            string writeResult = WriteObject(target, partName, newXml, null, false, false, false, false);
            JObject writeJson;
            try { writeJson = JObject.Parse(writeResult); }
            catch { writeJson = new JObject { ["raw"] = writeResult }; }

            var resp = new JObject
            {
                ["isError"] = false,
                ["target"] = target,
                ["part"] = partName,
                ["mode"] = "ops",
                ["opsApplied"] = ops.Count,
                ["write"] = writeJson
            };
            if (returnPostState)
                resp["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose);
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        public string ApplyJsonPatch(JObject req)
        {
            string target = req?["target"]?.ToString();
            string partName = req?["part"]?.ToString();
            return WrapWithPersistedState(ApplyJsonPatchImpl(req), target, partName, GxMcp.Worker.Helpers.WriteResultMeta.Ops);
        }

        private string ApplyJsonPatchImpl(JObject req)
        {
            // Validation runs here — no GeneXus types referenced in this method body.
            // GeneXus SDK types are isolated in ApplyJsonPatchCore so JIT can load
            // this method even when GeneXus assemblies are absent (unit-test environment).
            try
            {
                if (req == null)
                    throw new UsageException("usage_error", "request required");

                string target = req["target"]?.ToString();
                string partName = req["part"]?.ToString();
                JArray patchArr = req["patch"] as JArray;
                bool dryRun = req["dryRun"]?.ToObject<bool?>() ?? false;
                bool returnPostState = req["return_post_state"]?.ToObject<bool?>() ?? true;
                bool verbose = req["verbose"]?.ToObject<bool?>() ?? false;

                if (string.IsNullOrEmpty(target))
                    throw new UsageException("usage_error", "target required");
                if (string.IsNullOrEmpty(partName))
                    throw new UsageException("usage_error", "part required for mode:patch");
                if (patchArr == null)
                    throw new UsageException("usage_error", "patch[] required");

                // Pre-flight: reject immediately when no KB is open, before JIT-loading GeneXus types.
                if (!_objectService.GetKbService().IsOpen)
                    throw new UsageException("usage_error", "object '" + target + "' not found");

                return ApplyJsonPatchCore(target, partName, patchArr, dryRun, returnPostState, verbose);
            }
            catch (UsageException ux)
            {
                return new JObject
                {
                    ["isError"] = true,
                    ["error"] = new JObject
                    {
                        ["code"] = ux.Code,
                        ["message"] = ux.Message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["isError"] = true,
                    ["error"] = new JObject
                    {
                        ["code"] = "internal_error",
                        ["message"] = ex.Message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private string ApplyJsonPatchCore(string target, string partName, JArray patchArr, bool dryRun, bool returnPostState = true, bool verbose = false)
        {
            var obj = _objectService.FindObject(target, null);
            if (obj == null)
                throw new UsageException("usage_error", "object '" + target + "' not found");

            string kind = obj.TypeDescriptor?.Name ?? "";

            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);
            if (part == null)
                throw new UsageException("usage_error",
                    "part '" + partName + "' not found in " + kind);

            string currentXml = part.SerializeToXml();
            if (string.IsNullOrEmpty(currentXml))
                throw new UsageException("usage_error",
                    "part '" + partName + "' produced empty XML");

            string newXml = new JsonPatchService().Apply(currentXml, kind, patchArr);

            if (dryRun)
                return DryRunPlanBuilder.BuildEnvelope(target, currentXml, newXml, "patch").ToString(Newtonsoft.Json.Formatting.None);

            string writeResult = WriteObject(target, partName, newXml, null, false, false, false, false);
            JObject writeJson;
            try { writeJson = JObject.Parse(writeResult); }
            catch { writeJson = new JObject { ["raw"] = writeResult }; }

            var resp = new JObject
            {
                ["isError"] = false,
                ["target"] = target,
                ["part"] = partName,
                ["mode"] = "patch",
                ["opsApplied"] = patchArr.Count,
                ["write"] = writeJson
            };
            if (returnPostState)
                resp["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose);
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        private void InitializeFlushTimer()
        {
            if (_flushTimer != null) return;
            lock (_flushLock)
            {
                if (_flushTimer != null) return;
                _flushTimer = new System.Timers.Timer(2000); // 2 seconds debounce
                _flushTimer.AutoReset = false;
                _flushTimer.Elapsed += (s, e) => FlushBackground();
            }
        }

        private void FlushBackground()
        {
            if (!_pendingCommit) return;
            
            lock (_flushLock)
            {
                if (!_pendingCommit) return;
                try
                {
                    Logger.Info("[BACKGROUND-FLUSH] Starting commits...");
                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) return;

                    // Commits
                    var model = kb.DesignModel;
                    if (model != null) {
                        try {
                            var modelCommit = model.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance);
                            modelCommit?.Invoke(model, null);
                            Logger.Info("[BACKGROUND-FLUSH] Model.Commit() successful.");
                        } catch (Exception ex) { Logger.Debug("[BACKGROUND-FLUSH] Model.Commit skipped: " + ex.Message); }
                    }
                    
                    try {
                        var kbCommit = kb.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.Instance);
                        kbCommit?.Invoke(kb, null);
                        Logger.Info("[BACKGROUND-FLUSH] KB.Commit() successful.");
                    } catch (Exception ex) { Logger.Debug("[BACKGROUND-FLUSH] KB.Commit skipped: " + ex.Message); }

                    _pendingCommit = false;
                    Logger.Info("[BACKGROUND-FLUSH] Full commit cycle complete.");
                }
                catch (Exception ex)
                {
                    Logger.Error("[BACKGROUND-FLUSH] ERROR: " + ex.Message);
                }
            }
        }

        private void EnsurePersistenceWarmup()
        {
            if (_persistenceWarmupDone) return;

            lock (_persistenceWarmupLock)
            {
                if (_persistenceWarmupDone) return;
                try
                {
                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) return;
                    Logger.Info("[DEBUG-SAVE] Warming up persistence pipeline...");
                    dynamic tx = kb.BeginTransaction();
                    bool committed = false;
                    try
                    {
                        tx.Commit();
                        committed = true;
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { tx.Rollback(); } catch (Exception rbEx) { Logger.Debug("[DEBUG-SAVE] Warmup rollback failed: " + rbEx.Message); }
                        }
                        try { (tx as IDisposable)?.Dispose(); } catch { }
                    }
                    _persistenceWarmupDone = true;
                    Logger.Info("[DEBUG-SAVE] Persistence warmup complete.");
                }
                catch (Exception ex)
                {
                    Logger.Warn("[DEBUG-SAVE] Persistence warmup failed: " + ex.Message);
                }
            }
        }

        private void ScheduleFlush(bool force = false)
        {
            _pendingCommit = true;
            if (force)
            {
                FlushBackground();
                return;
            }

            lock (_flushLock)
            {
                if (_flushTimer == null) return;
                _flushTimer.Stop();
                _flushTimer.Start();
            }
        }

        private static string GetSdkMessagesSafe(object target)
        {
            try
            {
                return target?.GetSdkMessages();
            }
            catch
            {
                return string.Empty;
            }
        }

        // Documentation / Help parts (DocumentationPart, HelpPart) wrap a WikiPage and do not
        // implement ISource. Their Content/EditableContent properties are read-only on the part,
        // so the generic Source/Content reflection fallback above silently misses them. HelpPart
        // does expose a writable HtmlContent; both expose a writable Page (WikiPage) whose own
        // Content / EditableContent / StorableContent setters accept the new text.
        private static bool TrySetDocumentationContent(
            global::Artech.Architecture.Common.Objects.KBObjectPart part,
            string content,
            out string diagnostic)
        {
            diagnostic = null;
            if (part == null) return false;

            var partType = part.GetType();
            bool isDocumentation =
                partType.GetInterface("Artech.Genexus.Common.Parts.IDocumentation") != null ||
                partType.Name.Equals("DocumentationPart", StringComparison.OrdinalIgnoreCase) ||
                partType.Name.Equals("HelpPart", StringComparison.OrdinalIgnoreCase);
            if (!isDocumentation) return false;

            // 1. HelpPart exposes a writable HtmlContent — preferred when present.
            var htmlProp = partType.GetProperty("HtmlContent", BindingFlags.Public | BindingFlags.Instance);
            if (htmlProp != null && htmlProp.CanWrite && htmlProp.PropertyType == typeof(string))
            {
                try
                {
                    htmlProp.SetValue(part, content ?? string.Empty);
                    diagnostic = "part.HtmlContent";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Debug("[DEBUG-SAVE] Setting HtmlContent failed: " + ex.Message);
                }
            }

            // 2. Push through the underlying WikiPage. Page may be null on a never-edited part;
            // attempt to materialize one from the module so the first write isn't a no-op.
            var pageProp = partType.GetProperty("Page", BindingFlags.Public | BindingFlags.Instance);
            if (pageProp == null || !pageProp.CanRead) return false;

            object page = null;
            try { page = pageProp.GetValue(part); }
            catch (Exception ex) { Logger.Debug("[DEBUG-SAVE] Reading Page failed: " + ex.Message); }

            if (page == null && pageProp.CanWrite)
            {
                try
                {
                    var wikiPageType = pageProp.PropertyType;
                    object module = null;
                    var moduleProp = partType.GetProperty("Module", BindingFlags.Public | BindingFlags.Instance);
                    if (moduleProp != null && moduleProp.CanRead)
                    {
                        try { module = moduleProp.GetValue(part); } catch { }
                    }

                    if (module != null)
                    {
                        var ctor = wikiPageType.GetConstructor(new[] { module.GetType() });
                        if (ctor != null) page = ctor.Invoke(new[] { module });
                    }
                    if (page == null)
                    {
                        var ctor0 = wikiPageType.GetConstructor(Type.EmptyTypes);
                        if (ctor0 != null) page = ctor0.Invoke(null);
                    }

                    if (page != null)
                    {
                        pageProp.SetValue(part, page);
                        page = pageProp.GetValue(part); // re-read in case the setter wrapped it
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("[DEBUG-SAVE] Instantiating Page failed: " + ex.Message);
                }
            }

            if (page == null) return false;

            foreach (var name in new[] { "EditableContent", "Content", "StorableContent", "InvariantContent" })
            {
                var prop = page.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) continue;
                try
                {
                    prop.SetValue(page, content ?? string.Empty);
                    diagnostic = "part.Page." + name;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Debug("[DEBUG-SAVE] Setting page." + name + " failed: " + ex.Message);
                }
            }

            return false;
        }

        private static bool ShouldRetryWithoutPartSave(string partName, global::Artech.Architecture.Common.Objects.KBObjectPart part, Exception ex, string partMessages, JArray issues)
        {
            if (!(part is global::Artech.Architecture.Common.Objects.ISource)) return false;
            if (!WritePolicy.IsLogicalSourcePart(partName))
            {
                return false;
            }

            string exceptionMessage = ex?.Message ?? string.Empty;
            string diagnosticText = WritePolicy.BuildFailureDetails(partMessages, issues);
            return WritePolicy.ShouldRetryWithoutPartSave(partName, exceptionMessage, diagnosticText);
        }

        private static JObject CreateTransactionErrorResponse(string target, string partName, string stage, Exception ex, JArray issues, string retryStrategy, string sdkMessages, global::Artech.Architecture.Common.Objects.KBObject obj = null, string decodedCode = null)
        {
            // Friction-report #2: if the SDK threw bare "Erro" but GetSdkMessages / GetDiagnostics
            // produced the real message (e.g. "src0059: Esperando 'EndFor'..."), surface it as the
            // top-level error instead of the uninformative exception text.
            string enrichedError = WritePolicy.PreferDetailedMessage(ex.Message, sdkMessages, issues);
            var errorRes = new JObject
            {
                ["status"] = "Error",
                ["error"] = enrichedError
            };
            if (!string.Equals(enrichedError, (ex.Message ?? string.Empty).Trim(), System.StringComparison.Ordinal))
            {
                errorRes["originalError"] = ex.Message;
            }

            // Friction-report 05-13 #3: when the SDK fires src0216 ("'Foo' propriedade inválida")
            // and the variable on the dotted accessor was never declared, the real fix is
            // genexus_add_variable, not changing the field name. Detect this and surface a
            // structured hint so the agent doesn't chase the wrong rabbit.
            try
            {
                string corpus = (enrichedError ?? string.Empty) + "\n" + (sdkMessages ?? string.Empty);
                if (obj != null && !string.IsNullOrEmpty(decodedCode) &&
                    corpus.IndexOf("src0216", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var declared = CollectDeclaredVariableNames(obj);
                    var undeclared = WritePolicy.FindUndeclaredVariablesForSrc0216(corpus, decodedCode, declared);
                    string hint = WritePolicy.BuildUndeclaredVariableHint(undeclared);
                    if (!string.IsNullOrEmpty(hint))
                    {
                        errorRes["hint"] = hint;
                        var arr = new JArray();
                        foreach (var v in undeclared) arr.Add("&" + v);
                        errorRes["undeclaredVariables"] = arr;
                    }
                }
            }
            catch (Exception hintEx)
            {
                Logger.Debug("[CreateTransactionErrorResponse] undeclared-var hint failed: " + hintEx.Message);
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                errorRes["target"] = target;
            }

            if (!string.IsNullOrWhiteSpace(partName))
            {
                errorRes["part"] = partName;
            }

            if (!string.IsNullOrWhiteSpace(stage))
            {
                errorRes["stage"] = stage;
            }

            if (!string.IsNullOrWhiteSpace(retryStrategy))
            {
                errorRes["retryStrategy"] = retryStrategy;
            }

            string detailText = WritePolicy.BuildFailureDetails(sdkMessages, issues);
            if (!string.IsNullOrWhiteSpace(detailText))
            {
                errorRes["details"] = detailText;
            }

            if (!string.IsNullOrWhiteSpace(sdkMessages))
            {
                errorRes["sdkMessages"] = sdkMessages;
            }

            errorRes["stackTrace"] = ex.StackTrace;
            errorRes["issues"] = issues;
            return errorRes;
        }

        // Loose equality on the Structure DSL: compare on the set of `Name : Type` tokens,
        // not exact whitespace/length round-trip. We just want to know whether the items we
        // requested are actually present in the persisted state.
        private static bool StructureDslMatches(string expected, string actual)
        {
            var e = ExtractStructureLineSet(expected);
            var a = ExtractStructureLineSet(actual);
            if (e.Count == 0) return true; // nothing was requested; treat as ok
            // Every requested line must appear (by name) in the persisted result.
            return e.IsSubsetOf(a);
        }

        private static HashSet<string> ExtractStructureLineSet(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return set;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("{") || line.StartsWith("}")) continue;
                // Reduce "AluCod : NUMERIC(8,0)" → "AluCod : NUMERIC" so length/decimals drift
                // doesn't cause a false negative when the DSL renderer drops them.
                int colon = line.IndexOf(':');
                if (colon < 0) { set.Add(line); continue; }
                string name = line.Substring(0, colon).Trim();
                string rest = line.Substring(colon + 1).Trim();
                int paren = rest.IndexOf('(');
                if (paren >= 0) rest = rest.Substring(0, paren).Trim();
                set.Add(name + " : " + rest);
            }
            return set;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // Reads the declared variable names from the object's Variables part. Used to
        // distinguish "user wrote &Var.Foo without declaring &Var" (real bug) from
        // "&Var is declared but the SDT has no Foo" (different real bug — leave alone).
        private static IEnumerable<string> CollectDeclaredVariableNames(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            if (obj == null) yield break;
            global::Artech.Genexus.Common.Parts.VariablesPart varPart = null;
            try
            {
                foreach (var p in obj.Parts)
                {
                    if (p is global::Artech.Genexus.Common.Parts.VariablesPart vp) { varPart = vp; break; }
                }
            }
            catch { }
            if (varPart == null) yield break;

            System.Collections.IEnumerable vars = null;
            try { vars = varPart.Variables as System.Collections.IEnumerable; } catch { }
            if (vars == null) yield break;
            foreach (dynamic v in vars)
            {
                string name = null;
                try { name = v.Name; } catch { }
                if (!string.IsNullOrWhiteSpace(name)) yield return name;
            }
        }

        // IWriteServiceFacade adapter — translates JObject args into the canonical WriteObject call.
        public string WriteObject(string target, JObject args)
        {
            string mode = args?["mode"]?.ToString();
            string action = string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase) ? "WritePatch" : "WriteObject";
            JObject payload = args?["content"] as JObject;
            if (payload == null && args?["content"] != null)
            {
                payload = new JObject { ["text"] = args["content"].ToString() };
            }
            if (payload == null) payload = new JObject();

            return WriteObject(
                target,
                action,
                payload?.ToString(),
                args?["type"]?.ToString(),
                true,
                false,
                true,
                args?["dryRun"]?.ToObject<bool?>() ?? false);
        }

        public string WriteObject(string target, string partName, string code, string typeFilter = null, bool autoValidate = true, bool preferFastSourceSave = false, bool autoInjectVariables = true, bool dryRun = false)
        {
            string raw = WriteObjectInternal(target, partName, code, typeFilter, autoValidate, preferFastSourceSave, autoInjectVariables, dryRun);
            // v2.3.8 Task 3.4: every edit response carries persistedHash + persistedSnippet
            // (success, no-change, dry-run, rollback, or error).
            // Default sdkPath = typed-sdk; deeper writers (LayoutService raw-XML) tag their own
            // sdkPath first and WrapWithPersistedState is idempotent so it preserves that.
            return WrapWithPersistedState(raw, target, string.IsNullOrWhiteSpace(partName) ? "Source" : partName, GxMcp.Worker.Helpers.WriteResultMeta.TypedSdk);
        }

        private string WriteObjectInternal(string target, string partName, string code, string typeFilter = null, bool autoValidate = true, bool preferFastSourceSave = false, bool autoInjectVariables = true, bool dryRun = false)
        {
            try
            {
                partName = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;

                // DEBUG ENCODING: Detect and decode Base64 if needed
                string decodedCode = code;
                if (!string.IsNullOrEmpty(code) && (code.EndsWith("=") || code.Length > 100)) {
                    try {
                        byte[] data = Convert.FromBase64String(code);
                        decodedCode = System.Text.Encoding.UTF8.GetString(data);
                        Logger.Info("[DEBUG-SAVE] Payload decoded from Base64.");
                    } catch { /* Not base64, use as is */ }
                }

                Logger.Info(string.Format("[DEBUG-SAVE] Request received for {0} (Part: {1}, Code Length: {2})", target, partName, decodedCode?.Length ?? 0));

                // SP4.T1: When no type filter is given and the index contains multiple entries
                // with the same name (different types), return an inline alternatives array so
                // the caller can disambiguate without a separate list_objects round-trip.
                if (typeFilter == null && !target.Contains(":"))
                {
                    var candidates = _objectService.FindCandidateEntries(target);
                    // Only signal ambiguity when there are genuinely different types; a single
                    // entry (or all entries of the same type) is not ambiguous.
                    var distinctTypes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in candidates) { if (!string.IsNullOrEmpty(c.Type)) distinctTypes.Add(c.Type); }
                    if (distinctTypes.Count > 1)
                    {
                        var alternatives = new JArray();
                        foreach (var c in candidates)
                        {
                            alternatives.Add(new JObject
                            {
                                ["name"] = c.Name,
                                ["type"] = c.Type,
                                ["parentPath"] = c.ParentPath ?? c.Path ?? string.Empty
                            });
                        }
                        return new JObject
                        {
                            ["status"] = "Error",
                            ["error"] = "Ambiguous object name",
                            ["target"] = target,
                            ["suggestion"] = "Disambiguate by passing 'type' or by using a fully-qualified parentPath.",
                            ["alternatives"] = alternatives,
                            ["hint"] = "Retry with one of the alternatives' (name, type) pairs."
                        }.ToString();
                    }
                }

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) {
                    Logger.Error("[DEBUG-SAVE] Object NOT FOUND: " + target);
                    return CreateWriteError(
                        "Object not found",
                        target,
                        partName,
                        "The shadow file points to an object that is not available in the active Knowledge Base."
                    );
                }

                Logger.Debug(string.Format("[DEBUG-SAVE] Object Found: {0} ({1})", obj.Name, obj.TypeDescriptor.Name));

                if (PatternAnalysisService.IsPatternPart(partName))
                {
                    return WritePatternPart(obj, target, partName, decodedCode, dryRun);
                }

                if (WebFormXmlHelper.IsVisualPart(partName))
                {
                    return WriteVisualPart(obj, target, partName, decodedCode, dryRun);
                }

                if (dryRun)
                {
                    return Models.McpResponse.Success("Write", target, new JObject
                    {
                        ["status"] = "DryRun",
                        ["part"] = partName,
                        ["details"] = "Dry-run for non-pattern/visual parts: input received; not validated against SDK. Save skipped."
                    });
                }

                // ... (rest of the log)
                // 1. VIRTUAL/DSL PARTS INTERCEPTOR (Prioritize over physical part resolution for Structure)
                if (partName.Equals("Structure", StringComparison.OrdinalIgnoreCase))
                {
                    var objToUpdate = _objectService.FindObject(target, typeFilter);
                    if (objToUpdate != null && (objToUpdate is global::Artech.Genexus.Common.Objects.Transaction || objToUpdate.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)))
                    {
                        try {
                            StructureParser.ParseFromText(objToUpdate, decodedCode);
                            objToUpdate.EnsureSave();
                            // Friction-report 05-13 #2: a Structure write on an SDT or Transaction
                            // must commit synchronously, not via the debounced ScheduleFlush()
                            // timer. Subsequent requests (e.g. a Procedure that references
                            // &Var.Field) re-validate against the persisted KB. If the SDT items
                            // are still only in-memory when the next save runs, the validator
                            // reloads from disk (still seed-only) and fires `src0216 propriedade
                            // inválida` on the field access. Force the flush here so disk reflects
                            // the new items before we return Success.
                            ScheduleFlush(force: true);

                            // Friction-report 05-13 #2 deeper finding: the SDK persists the
                            // SDT to the Design model and to disk, but a parallel Prototype
                            // model (model id 2) — used by the validator when other objects
                            // consume this SDT — never gets the corresponding
                            // ModelEntityVersion rows for the SDTLevelEntity/SDTItemEntity
                            // names. IDE-created SDTs have those rows; SDK-create-via-MCP
                            // doesn't propagate them. Mirror Model 1 → Model 2 for our
                            // newly-saved SDT directly in SQL so the validator can resolve
                            // `&Var.<Field>` from the prototype model after this Structure
                            // write.
                            if (objToUpdate.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    string kbPath = _objectService.GetKbService().GetKbPath();
                                    GxMcp.Worker.Helpers.SdtModelPropagation.TryPropagateToPrototypeModel(objToUpdate, kbPath);
                                }
                                catch (Exception propEx)
                                {
                                    Logger.Warn("[SDT-PROP] propagation failed for " + target + ": " + propEx.Message);
                                }
                            }

                            _objectService.MarkReadCacheDirty(objToUpdate, partName);

                            // Friction-report 05-13 #2: confirm the Save actually persisted the
                            // new items by re-reading the structure from a forced cache miss.
                            // If the DSL we just wrote doesn't round-trip, the SDT structure
                            // part is in the stale-tree failure mode and the validator on the
                            // next consumer (Procedure that references &Var.Field) will report
                            // `src0216` even though our Success response said otherwise.
                            string roundTripError = null;
                            try
                            {
                                var verifyObj = _objectService.FindObject(target, typeFilter);
                                if (verifyObj != null)
                                {
                                    _objectService.MarkReadCacheDirty(verifyObj, partName);
                                    string persisted = GxMcp.Worker.Helpers.StructureParser.SerializeToText(verifyObj);
                                    if (!StructureDslMatches(decodedCode, persisted))
                                    {
                                        roundTripError = "Structure DSL applied in-memory but post-Save read-back didn't include all items. The SDK may have persisted the prior version. Re-read with genexus_read part=Structure and retry; if still wrong, the SDT's persisted EntityVersion is stale (see WebFormCompositionRepair pattern).";
                                        Logger.Warn("[DEBUG-SAVE] SDT Structure round-trip mismatch for " + target + ". expected=\"" + Truncate(decodedCode, 200) + "\" persisted=\"" + Truncate(persisted, 200) + "\"");
                                    }
                                }
                            }
                            catch (Exception verEx)
                            {
                                Logger.Debug("[DEBUG-SAVE] SDT round-trip verify threw: " + verEx.Message);
                            }

                            var okPayload = new JObject { ["details"] = "Structure DSL successfully applied" };
                            if (!string.IsNullOrEmpty(roundTripError))
                            {
                                okPayload["persistedVerified"] = false;
                                okPayload["persistedVerifyError"] = roundTripError;
                            }
                            else
                            {
                                okPayload["persistedVerified"] = true;
                            }
                            return Models.McpResponse.Success("Write", target, okPayload);
                        } catch (Exception ex) {
                            Logger.Error("[DEBUG-SAVE] Error parsing Structure DSL: " + ex.Message);
                            return Models.McpResponse.Error($"Invalid Structure Syntax: {ex.Message}", target);
                        }
                    }
                }

                global::Artech.Architecture.Common.Objects.KBObjectPart part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);

                if (part == null) {
                    Logger.Error("[DEBUG-SAVE] Part NOT FOUND in object: " + partName);
                    return CreateWriteError(
                        $"Part '{partName}' not found in {obj.TypeDescriptor.Name}",
                        target,
                        partName,
                        "The object does not expose the requested part.",
                        obj
                    );
                }

                if (part is global::Artech.Architecture.Common.Objects.ISource existingSourcePart &&
                    WritePolicy.IsUnchangedSourceWrite(existingSourcePart.Source, decodedCode))
                {
                    Logger.Info("[DEBUG-SAVE] Content is identical. Skipping validation and Save.");
                    return Models.McpResponse.Success("Write", target, new JObject { ["details"] = "No change" });
                }

                // Nirvana v19.4: Auto-Healing (Pre-save validation)
                if (autoValidate && _validationService != null && !partName.Equals("Variables", StringComparison.OrdinalIgnoreCase) && !partName.Equals("Structure", StringComparison.OrdinalIgnoreCase))
                {
                    string validationRes = _validationService.ValidateCode(target, partName, decodedCode);
                    var valJson = JObject.Parse(validationRes);
                    if (valJson["status"]?.ToString() == "Error")
                    {
                        string firstError = valJson["errors"]?[0]?["description"]?.ToString()
                            ?? valJson["error"]?.ToString()
                            ?? "Validation failed.";

                        // If the SDK only returned a generic "Erro" with no diagnostics, the pre-flight
                        // is uninformative. Fall through to the real save so the EnsureSave path can
                        // throw with the full SDK message ("src0059: …", etc).
                        bool isUninformative = string.Equals(firstError?.Trim(), "Erro", StringComparison.OrdinalIgnoreCase);
                        if (isUninformative)
                        {
                            Logger.Warn($"[AUTO-HEALING] Pre-flight returned generic 'Erro' for {target} ({partName}); proceeding to real save for detailed diagnostics.");
                        }
                        else
                        {
                            Logger.Warn($"[AUTO-HEALING] Blocked invalid code for {target} ({partName}): {firstError}");
                            return validationRes; // Return the error immediately to the LLM
                        }
                    }
                }

                // 1. SET CONTENT
                bool contentSet = false;
                if (part is global::Artech.Genexus.Common.Parts.VariablesPart varPart)
                {
                    VariableInjector.SetVariablesFromText(varPart, decodedCode);
                    contentSet = true;
                }
                else if (part is global::Artech.Architecture.Common.Objects.ISource sourcePart)
                {
                    sourcePart.Source = decodedCode;
                    
                    // Auto-inject variables based on the new code (Optimized with Index)
                    if (autoInjectVariables)
                    {
                        try {
                            var index = _objectService.GetKbService().GetIndexCache().GetIndex();
                            VariableInjector.InjectVariables(obj, decodedCode, index);
                        } catch (Exception ex) {
                            Logger.Warn("[DEBUG-SAVE] Auto-inject variables failed: " + ex.Message);
                        }
                    }
                    contentSet = true;
                }
                else if (TrySetDocumentationContent(part, decodedCode, out string docDiagnostic))
                {
                    Logger.Info("[DEBUG-SAVE] Documentation content set via " + docDiagnostic);
                    contentSet = true;
                }
                else
                {
                    try {
                        if (decodedCode.Trim().StartsWith("<") && !partName.Equals("Structure", StringComparison.OrdinalIgnoreCase)) {
                            part.DeserializeFromXml(decodedCode);
                            contentSet = true;
                        } else {
                            var contentProp = part.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance)
                                           ?? part.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
                            if (contentProp != null && contentProp.CanWrite) {
                                contentProp.SetValue(part, decodedCode);
                                contentSet = true;
                            }
                        }
                    } catch (Exception ex) {
                        // Surface the underlying error so the agent gets actionable feedback
                        // instead of a generic "could not set content" downstream.
                        Logger.Warn($"[DEBUG-SAVE] Content set failed for {target} ({partName}): {ex.Message}");
                    }
                }

                if (!contentSet) {
                    Logger.Warn("[DEBUG-SAVE] No suitable method found to update part content.");
                }

                // 2. FORCE DIRTY (Crucial)
                try {
                    // Mark Part as Dirty
                    var pType = part.GetType();
                    var pDirtyProp = pType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.Instance) 
                                  ?? pType.GetProperty("IsDirty", BindingFlags.Public | BindingFlags.Instance);
                    if (pDirtyProp != null) {
                        pDirtyProp.SetValue(part, true);
                        Logger.Debug("[DEBUG-SAVE] Part property '" + pDirtyProp.Name + "' set to TRUE");
                    }

                    // Mark Header Object as Dirty (Essential for Save)
                    var oType = obj.GetType();
                    var oDirtyProp = oType.GetProperty("Dirty", BindingFlags.Public | BindingFlags.Instance)
                                  ?? oType.GetProperty("IsDirty", BindingFlags.Public | BindingFlags.Instance);
                    if (oDirtyProp != null) {
                        oDirtyProp.SetValue(obj, true);
                        Logger.Debug("[DEBUG-SAVE] Object property '" + oDirtyProp.Name + "' set to TRUE");
                    }
                } catch (Exception ex) { Logger.Debug("[DEBUG-SAVE] Force Dirty failed: " + ex.Message); }

                // 3. PERSISTENCE SEQUENCE
                try
                {
                    EnsurePersistenceWarmup();

                    if (preferFastSourceSave &&
                        part is global::Artech.Architecture.Common.Objects.ISource &&
                        WritePolicy.IsLogicalSourcePart(partName))
                    {
                        try
                        {
                            var saveMethod = obj.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (saveMethod != null)
                            {
                                Logger.Info("[DEBUG-SAVE] Fast persistence path: obj.Save() without explicit transaction.");
                                saveMethod.Invoke(obj, null);
                                ScheduleFlush();
                                _objectService.MarkReadCacheDirty(obj, partName);
                                return Models.McpResponse.Success("Write", target, new JObject
                                {
                                    ["fastPath"] = "save_without_transaction"
                                });
                            }

                            Logger.Info("[DEBUG-SAVE] Fast persistence path fallback: obj.EnsureSave(false) without explicit transaction.");
                            obj.EnsureSave(false);
                            ScheduleFlush();
                            _objectService.MarkReadCacheDirty(obj, partName);
                            return Models.McpResponse.Success("Write", target, new JObject
                            {
                                ["fastPath"] = "ensure_save_without_transaction"
                            });
                        }
                        catch (Exception fastEx)
                        {
                            Logger.Warn("[DEBUG-SAVE] Fast persistence path failed. Falling back to full transaction path. Reason: " + fastEx.Message);
                        }
                    }

                    var kb = _objectService.GetKbService().GetKB();
                    if (kb == null) throw new Exception("KB not opened");

                    // 1. Start Transaction
                    Logger.Info("[DEBUG-SAVE] Starting SDK Transaction...");
                    var transaction = kb.BeginTransaction();
                    string failureStage = "transaction";
                    string retryStrategy = "standard";
                    string lastSdkMessages = string.Empty;
                    bool transactionCommitted = false;
                    bool transactionFinished = false;
                    string finalizeError = null;
                    // Block KbWatcher from polling DesignModel.Objects while we're inside the tx.
                    var writeGate = KbWatcherService.AcquireWriteGate();

                    try {
                        // 2. Checkout
                        try {
                            var checkoutMethod = obj.GetType().GetMethod("Checkout", BindingFlags.Public | BindingFlags.Instance);
                            checkoutMethod?.Invoke(obj, null);
                            Logger.Debug("[DEBUG-SAVE] SDK Checkout invoked.");
                        } catch (Exception coEx) {
                            // Checkout failure is usually benign (object not under VC), but
                            // a hard error here can cascade into "object not editable" later.
                            Logger.Debug("[DEBUG-SAVE] SDK Checkout skipped: " + coEx.Message);
                        }

                        // 3. Save Part (CRITICAL: Save the part explicitly first)
                        failureStage = "part_save";
                        Logger.Info(string.Format("[DEBUG-SAVE] Invoking part.Save() for {0}...", part.TypeDescriptor?.Name));
                        bool skippedPartSave = false;
                        if (preferFastSourceSave &&
                            part is global::Artech.Architecture.Common.Objects.ISource &&
                            WritePolicy.IsLogicalSourcePart(partName))
                        {
                            skippedPartSave = true;
                            retryStrategy = "object_save_only_fast_path";
                            Logger.Info($"[DEBUG-SAVE] Fast source save path enabled for {target} ({partName}). Skipping part.Save().");
                        }
                        else
                        {
                            try {
                                part.Save();
                                Logger.Info("[DEBUG-SAVE] part.Save() completed.");
                            } catch (Exception exPart) {
                                string partMsgs = GetSdkMessagesSafe(part);
                                lastSdkMessages = partMsgs;
                                var saveIssues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                                if (ShouldRetryWithoutPartSave(partName, part, exPart, partMsgs, saveIssues))
                                {
                                    skippedPartSave = true;
                                    retryStrategy = "object_save_only";
                                    Logger.Warn($"[DEBUG-SAVE] part.Save() failed generically for {target} ({partName}). Retrying with object-level save only.");
                                }
                                else
                                {
                                    string detailText = WritePolicy.BuildFailureDetails(partMsgs, saveIssues);
                                    Logger.Warn($"[DEBUG-SAVE] part.Save() threw exception: {exPart.Message}. Details: {detailText}");
                                    throw new Exception(
                                        string.IsNullOrWhiteSpace(detailText)
                                            ? $"Part save failed: {exPart.Message}"
                                            : $"Part save failed: {exPart.Message}. Details: {detailText}",
                                        exPart);
                                }
                            }
                        }

                        // Check for messages even if it didn't throw (some SDK errors are non-throwing)
                        string checkMsgs = GetSdkMessagesSafe(part);
                        lastSdkMessages = checkMsgs;
                        if (!skippedPartSave && !string.IsNullOrEmpty(checkMsgs) && (checkMsgs.Contains("Erro") || checkMsgs.Contains("Error"))) {
                            Logger.Warn($"[DEBUG-SAVE] part.Save() reported internal errors: {checkMsgs}");
                            throw new Exception($"Part save reported errors: {checkMsgs}");
                        }

                        // 4. Save Object (Unified approach)
                        try 
                        {
                            failureStage = "object_save";
                            Logger.Info("[DEBUG-SAVE] Invoking obj.EnsureSave(check: true)...");
                            obj.EnsureSave(true);
                            Logger.Info("[DEBUG-SAVE] obj.EnsureSave(true) completed.");
                        }
                        catch (Exception ex) when (ex.Message.Contains("Validation failed") || ex.Message.Contains("Save failed"))
                        {
                            Logger.Warn($"[DEBUG-SAVE] Standard save failed: {ex.Message}. Retrying with check=false...");
                            // RETRY WITHOUT VALIDATION (User request)
                            retryStrategy = retryStrategy == "standard" ? "ensure_save_without_validation" : $"{retryStrategy}+ensure_save_without_validation";
                            obj.EnsureSave(false);
                            Logger.Info("[DEBUG-SAVE] obj.EnsureSave(false) completed successfully.");
                        }
                        
                        // 5. Transaction Commit
                        failureStage = "commit";
                        Logger.Info("[DEBUG-SAVE] Committing SDK Transaction...");
                        transaction.Commit();
                        transactionCommitted = true;
                        transactionFinished = true;
                        Logger.Info("[DEBUG-SAVE] SDK Transaction Committed.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("[DEBUG-SAVE] SDK TRANSACTION ERROR: " + ex.ToString());
                        var issues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                        // After a Commit-stage failure, the transaction may already be in a
                        // half-finalized state where Rollback throws. Guard it so we still
                        // return the structured error JSON instead of crashing the worker.
                        if (!transactionCommitted)
                        {
                            try { transaction.Rollback(); }
                            catch (Exception rbEx)
                            {
                                Logger.Warn("[DEBUG-SAVE] Rollback after error also failed: " + rbEx.Message);
                                finalizeError = rbEx.Message;
                            }
                        }
                        transactionFinished = true;
                        lastSdkMessages = string.IsNullOrWhiteSpace(lastSdkMessages) ? GetSdkMessagesSafe(part) : lastSdkMessages;
                        return CreateTransactionErrorResponse(target, partName, failureStage, ex, issues, retryStrategy, lastSdkMessages, obj, decodedCode).ToString();
                    }
                    finally
                    {
                        // Defense in depth: ensure the transaction is never left undisposed,
                        // even if Commit/Rollback escaped via a path we didn't anticipate.
                        if (!transactionFinished && !transactionCommitted)
                        {
                            try { transaction.Rollback(); } catch { }
                        }
                        try { (transaction as IDisposable)?.Dispose(); } catch { }
                        try { writeGate.Dispose(); } catch { }
                        if (finalizeError != null)
                        {
                            Logger.Debug("[DEBUG-SAVE] Transaction finalize note: " + finalizeError);
                        }
                    }

                    // FAST SAVE: Run heavy indexing in background
                    Task.Run(() => {
                        try {
                            _objectService.GetKbService().GetIndexCache().UpdateEntry(obj);
                        } catch (Exception ex) { Logger.Error("[DEBUG-SAVE] Background Index update failed: " + ex.Message); }
                    });
                    
                    // Final persistence with debounce to avoid sync commit on every write.
                    ScheduleFlush();

                    Logger.Info("[DEBUG-SAVE] SAVE & COMMIT COMPLETE.");
                    _objectService.MarkReadCacheDirty(obj, partName);
                    return Models.McpResponse.Success("Write", target);
                }
                catch (Exception saveEx)
                {
                    Logger.Error("[DEBUG-SAVE] CRITICAL SDK EXCEPTION: " + saveEx.ToString());
                    return BuildEnrichedSaveError("SDK Save failed", saveEx, obj, target, partName).ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[DEBUG-SAVE] OUTER EXCEPTION: " + ex.ToString());
                // obj may not have been bound yet (exception before FindObject).
                return BuildEnrichedSaveError(null, ex, null, target, partName).ToString();
            }
        }

        // Friction-report #2: when the bare-error catches fire (outside the transaction's own
        // catch), still consult SdkDiagnosticsHelper + GetSdkMessages so an "Erro" exception
        // doesn't escape to the agent unenriched.
        private static JObject BuildEnrichedSaveError(string prefix, Exception ex, global::Artech.Architecture.Common.Objects.KBObject obj, string target, string partName)
        {
            JArray issues = null;
            string sdkMsgs = null;
            if (obj != null)
            {
                try { issues = SdkDiagnosticsHelper.GetDiagnostics(obj); } catch { }
                try { sdkMsgs = obj.GetSdkMessages(); } catch { }
            }
            string baseMessage = ex?.Message ?? string.Empty;
            string enriched = WritePolicy.PreferDetailedMessage(baseMessage, sdkMsgs, issues);
            string display = string.IsNullOrEmpty(prefix) ? enriched : prefix + ": " + enriched;
            var payload = new JObject
            {
                ["status"] = "Error",
                ["error"] = display
            };
            if (!string.Equals(enriched, baseMessage.Trim(), System.StringComparison.Ordinal))
            {
                payload["originalError"] = baseMessage;
            }
            if (!string.IsNullOrWhiteSpace(target)) payload["target"] = target;
            if (!string.IsNullOrWhiteSpace(partName)) payload["part"] = partName;
            if (!string.IsNullOrWhiteSpace(sdkMsgs)) payload["sdkMessages"] = sdkMsgs;
            if (issues != null && issues.Count > 0) payload["issues"] = issues;
            return payload;
        }

        private string CreateWriteError(
            string error,
            string target,
            string partName,
            string details,
            global::Artech.Architecture.Common.Objects.KBObject obj = null,
            GxMcp.Worker.Helpers.XmlEquivalenceDiff structuredDiff = null)
        {
            var response = new JObject
            {
                ["status"] = "Error",
                ["error"] = error
            };

            if (structuredDiff != null)
            {
                var d = new JObject();
                if (!string.IsNullOrEmpty(structuredDiff.ElementName)) d["element"] = structuredDiff.ElementName;
                if (!string.IsNullOrEmpty(structuredDiff.Path)) d["path"] = structuredDiff.Path;
                if (structuredDiff.RejectedAttributes != null && structuredDiff.RejectedAttributes.Length > 0)
                    d["rejectedAttributes"] = new JArray(structuredDiff.RejectedAttributes);
                if (structuredDiff.AddedAttributes != null && structuredDiff.AddedAttributes.Length > 0)
                    d["addedAttributes"] = new JArray(structuredDiff.AddedAttributes);
                if (structuredDiff.LeftAttributes != null) d["persistedAttributes"] = new JArray(structuredDiff.LeftAttributes);
                if (structuredDiff.RightAttributes != null) d["requestedAttributes"] = new JArray(structuredDiff.RightAttributes);
                response["verifyDiff"] = d;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                response["target"] = target;
            }

            if (!string.IsNullOrWhiteSpace(partName))
            {
                response["part"] = partName;
            }

            if (!string.IsNullOrWhiteSpace(details))
            {
                response["details"] = details;
            }

            if (obj != null)
            {
                response["objectName"] = obj.Name;
                response["objectType"] = obj.TypeDescriptor?.Name;

                var availableParts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
                if (availableParts.Length > 0)
                {
                    response["availableParts"] = new JArray(availableParts);
                }
            }

            var suggestion = BuildSuggestion(error, partName);
            if (!string.IsNullOrEmpty(suggestion)) response["suggestion"] = suggestion;

            return response.ToString();
        }

        private static string BuildSuggestion(string error, string partName)
        {
            if (string.IsNullOrEmpty(error)) return null;
            if (error.IndexOf("Part not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Try mode='full', or read the object first to see availableParts.";
            if (error.IndexOf("does not expose text source", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Resolve via parent Transaction (e.g., type='Transaction') or use mode='full'.";
            if (error.IndexOf("verification failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return "See 'verifyDiff' for rejected/added attrs. SDK sanitises attrs outside its element schema (e.g. 'style' on classref-bound elements). Drop those attrs or move them to a Theme class.";
            if (error.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Check XML well-formedness; verify single root element and quoted attribute values.";
            if (error.IndexOf("Object not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Disambiguate with type=<Transaction|Procedure|...> or list_objects to confirm the name.";
            return null;
        }

        // Item shape: { name, part?, content, type?, dryRun? }. stopOnError halts at first failure.
        public string BulkWrite(JObject args)
        {
            var items = args?["targets"] as JArray;
            if (items == null || items.Count == 0)
                return new JObject { ["status"] = "Error", ["error"] = "targets[] required" }.ToString();

            bool stopOnError = args?["stopOnError"]?.ToObject<bool?>() ?? true;
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
            var results = new JArray();
            int success = 0, failure = 0, skipped = 0;

            foreach (var it in items)
            {
                if (failure > 0 && stopOnError)
                {
                    results.Add(new JObject { ["status"] = "Skipped", ["target"] = it?["name"]?.ToString() });
                    skipped++;
                    continue;
                }
                var name = it?["name"]?.ToString();
                var part = it?["part"]?.ToString() ?? "";
                var content = it?["content"]?.ToString();
                var itemDryRun = it?["dryRun"]?.ToObject<bool?>() ?? dryRun;
                if (string.IsNullOrEmpty(name) || content == null)
                {
                    results.Add(new JObject { ["status"] = "Error", ["error"] = "missing name or content", ["target"] = name });
                    failure++;
                    continue;
                }
                string raw = WriteObject(name, part, content, it?["type"]?.ToString(), true, false, true, itemDryRun);
                var parsed = GxMcp.Worker.Helpers.JsonUtil.SafeParse(raw);
                results.Add(parsed);

                var status = (parsed as JObject)?["status"]?.ToString();
                if (string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)) failure++;
                else success++;
            }

            var bulkEnvelope = new JObject
            {
                ["status"] = failure == 0 ? "Success" : "PartialFailure",
                ["counts"] = new JObject { ["success"] = success, ["failure"] = failure, ["skipped"] = skipped },
                ["results"] = results,
            };
            // Bulk inherits whatever each item's sdkPath was: when all match, tag the bulk
            // with that value; when they differ, tag "hybrid" so observability captures the mix.
            string bulkSdkPath = SummarizeBulkSdkPath(results);
            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(bulkEnvelope, bulkSdkPath);
            return bulkEnvelope.ToString();
        }

        private static string SummarizeBulkSdkPath(JArray results)
        {
            string seen = null;
            foreach (var r in results)
            {
                if (!(r is JObject jo)) continue;
                string p = jo["_meta"]?["sdkPath"]?.ToString();
                if (string.IsNullOrEmpty(p)) continue;
                if (seen == null) seen = p;
                else if (!string.Equals(seen, p, StringComparison.Ordinal)) return GxMcp.Worker.Helpers.WriteResultMeta.Hybrid;
            }
            return seen ?? GxMcp.Worker.Helpers.WriteResultMeta.TypedSdk;
        }

        private string ResolveVariableTarget(string target, ref string varName,
            out global::Artech.Architecture.Common.Objects.KBObject obj,
            out global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            out global::Artech.Genexus.Common.Variable existing)
        {
            obj = null; varPart = null; existing = null;
            if (string.IsNullOrEmpty(varName)) return "{\"error\": \"Variable name is required.\"}";
            varName = varName.TrimStart('&');

            obj = _objectService.FindObject(target);
            if (obj == null) return CreateWriteError("Object not found", target, "Variables", "The requested object is not available in the active Knowledge Base.");

            // v2.3.8 Task 4.4 — kind-aware accessor. Falls back through typed Get<>,
            // name-based candidates, and reflective Variables-property discovery so that
            // WebPanel / Transaction / WorkPanel / DataProvider resolve symmetrically.
            varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
            if (varPart == null) return CreateWriteError("Variables part not found", target, "Variables", "The object does not expose a Variables part.", obj);

            string searchName = varName;
            existing = varPart.Variables.FirstOrDefault(v => string.Equals(v.Name, searchName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        /// Batch variant: removes all `varNames` from `target`, calling EnsureSave / ScheduleFlush once.
        /// Skips framework-managed names. Returns per-name outcomes plus aggregate counts.
        public string DeleteVariables(string target, System.Collections.Generic.IEnumerable<string> varNames)
        {
            return WrapWithPersistedState(DeleteVariablesInternal(target, varNames), target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string DeleteVariablesInternal(string target, System.Collections.Generic.IEnumerable<string> varNames)
        {
            try
            {
                if (varNames == null) return "{\"status\":\"NoChange\"}";
                string firstName = null;
                foreach (var n in varNames) { firstName = n; break; }
                if (firstName == null) return "{\"status\":\"NoChange\"}";

                string scratch = firstName;
                var err = ResolveVariableTarget(target, ref scratch, out var obj, out var varPart, out _);
                if (err != null) return err;

                var outcomes = new JArray();
                int removed = 0, refused = 0, missing = 0;
                foreach (var raw in varNames)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    var name = raw.TrimStart('&');
                    if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(name))
                    {
                        outcomes.Add(new JObject { ["name"] = name, ["status"] = "Refused", ["reason"] = "framework-managed" });
                        refused++;
                        continue;
                    }
                    var hit = varPart.Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) { outcomes.Add(new JObject { ["name"] = name, ["status"] = "NoChange" }); missing++; continue; }
                    varPart.Variables.Remove(hit);
                    outcomes.Add(new JObject { ["name"] = name, ["status"] = "Removed" });
                    removed++;
                }

                if (removed > 0)
                {
                    obj.EnsureSave();
                    ScheduleFlush();
                }

                return new JObject
                {
                    ["status"] = removed > 0 ? "Success" : "NoChange",
                    ["counts"] = new JObject { ["removed"] = removed, ["refused"] = refused, ["missing"] = missing },
                    ["outcomes"] = outcomes,
                }.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string DeleteVariable(string target, string varName)
        {
            return WrapWithPersistedState(DeleteVariableInternal(target, varName), target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string DeleteVariableInternal(string target, string varName)
        {
            try
            {
                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing == null)
                    return "{\"status\": \"NoChange\", \"details\": \"Variable not present; nothing to delete.\"}";

                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(varName))
                {
                    return new JObject
                    {
                        ["status"] = "Refused",
                        ["error"] = "Framework-managed variable",
                        ["details"] = "Variable '&" + varName + "' is managed by " + GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(varName) + " and will be re-injected on save."
                    }.ToString();
                }

                // Snapshot the var's internal id BEFORE Remove() — some SDK
                // builds null out the parent reference once a variable is
                // detached, which would otherwise lose the id needed to scan
                // for ghost bindings if the save throws.
                int? existingId = null;
                try
                {
                    int idx = 1;
                    foreach (var v in varPart.Variables)
                    {
                        if (ReferenceEquals(v, existing))
                        {
                            existingId = GxMcp.Worker.Helpers.VariableInjector.GetVariableInternalId(v, idx);
                            break;
                        }
                        idx++;
                    }
                }
                catch { /* best-effort */ }

                try
                {
                    varPart.Variables.Remove(existing);
                    obj.EnsureSave();
                    ScheduleFlush();
                    return "{\"status\": \"Success\"}";
                }
                catch (Exception saveEx)
                {
                    var boundResp = TryBuildBoundToControlsError(saveEx, obj, varName, existingId);
                    if (boundResp != null) return boundResp;
                    throw;
                }
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Task 4.5 — When the SDK rejects a delete/modify because the variable
        // is still bound to a control, surface a structured envelope instead of
        // a raw error string. We use a heuristic message match because the
        // concrete SDK exception type that signals this varies across GeneXus
        // builds and isn't documented; the regex catches both EN and PT-BR
        // phrasings observed in friction reports.
        private static readonly System.Text.RegularExpressions.Regex _boundToControlsRegex =
            new System.Text.RegularExpressions.Regex(
                @"(\[var:\d+\])|(control reference)|(referência de controle)|(bound to control)|(is being used)|(está sendo (usada|utilizada))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal string TryBuildBoundToControlsError(Exception ex, global::Artech.Architecture.Common.Objects.KBObject obj, string varName, int? variableId)
        {
            if (ex == null) return null;
            string flat = FlattenExceptionMessages(ex);
            if (string.IsNullOrEmpty(flat) || !_boundToControlsRegex.IsMatch(flat)) return null;

            string resolved = GxMcp.Worker.Helpers.WebFormSchemaHints.ResolveVarBindings(flat, obj);

            var bindings = new JArray();
            try
            {
                if (variableId.HasValue && variableId.Value > 0)
                {
                    string xml = GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj);
                    var hits = GxMcp.Worker.Helpers.WebFormSchemaHints.FindVarBindings(xml, variableId.Value);
                    foreach (var b in hits)
                    {
                        bindings.Add(new JObject
                        {
                            ["element"] = b.Element,
                            ["attribute"] = b.Attribute,
                            ["controlId"] = b.ControlId,
                            ["controlName"] = b.ControlName,
                        });
                    }
                }
            }
            catch { /* best-effort — bindings list is advisory */ }

            return new JObject
            {
                ["status"] = "Error",
                ["code"] = "BoundToControls",
                ["message"] = $"Variable '&{varName}' is bound to one or more controls; remove the bindings before deleting/modifying.",
                ["details"] = resolved,
                ["bindings"] = bindings,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string FlattenExceptionMessages(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(cur.Message);
            }
            return sb.ToString();
        }

        public string AddVariable(string target, string varName, string typeName = null)
        {
            return WrapWithPersistedState(AddVariableInternal(target, varName, typeName), target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string AddVariableInternal(string target, string varName, string typeName = null)
        {
            try
            {
                // Task 4.2 — validate typeName via VariableTypeResolver before any SDK work,
                // so unknown types never silently default to NUMERIC.
                GxMcp.Worker.Helpers.TypeResolution resolution = null;
                string resolvedTypeForSdk = typeName;
                int? resolvedLength = null;
                int? resolvedDecimals = null;
                if (!string.IsNullOrEmpty(typeName))
                {
                    resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(typeName);
                    if (!resolution.Recognized)
                    {
                        var accepted = new JArray();
                        if (resolution.AcceptedList != null)
                            foreach (var a in resolution.AcceptedList) accepted.Add(a);
                        return new JObject
                        {
                            ["status"] = "Error",
                            ["code"] = "UnknownType",
                            ["message"] = $"Unknown typeName '{typeName}'. Did you mean '{resolution.Suggestion}'?",
                            ["suggestion"] = resolution.Suggestion,
                            ["accepted"] = accepted
                        }.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
                    {
                        // Pass the raw name to the existing ResolveTypeObject path (SDT / BC / Domain).
                        resolvedTypeForSdk = resolution.DomainName;
                    }
                    else
                    {
                        // Canonicalise — e.g. VarChar(120) → Character(120) — so TryParseDbType picks
                        // up the canonical eDBType instead of an alias that may not round-trip.
                        resolvedLength = resolution.Length;
                        resolvedDecimals = resolution.Decimals;
                        resolvedTypeForSdk = resolution.CanonicalType;
                    }
                }

                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing != null)
                    return "{\"status\": \"Variable already exists\"}";

                if (!string.IsNullOrEmpty(typeName))
                {
                    global::Artech.Genexus.Common.Variable newVar = new global::Artech.Genexus.Common.Variable(varPart);
                    newVar.Name = varName;

                    if (resolution != null && resolution.CanonicalType != "DomainReference"
                        && VariableInjector.TryParseDbType(resolvedTypeForSdk, out var dbType))
                    {
                        newVar.Type = dbType;
                        try
                        {
                            if (resolvedLength.HasValue) newVar.Length = resolvedLength.Value;
                            if (resolvedDecimals.HasValue) newVar.Decimals = resolvedDecimals.Value;
                        }
                        catch { /* best-effort — SDK may reject for some types */ }
                    }
                    else
                    {
                        var targetObj = VariableInjector.ResolveTypeObject(varPart.Model, resolvedTypeForSdk);
                        if (targetObj != null)
                        {
                            if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                                newVar.DomainBasedOn = dom;
                            else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                VariableInjector.BindVariableToSdt(newVar, targetObj);
                            }
                            else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                            {
                                VariableInjector.BindVariableToBC(newVar, targetObj);
                            }
                        }
                        else if (resolution != null && resolution.CanonicalType == "DomainReference"
                                 && !string.IsNullOrEmpty(typeName) && !typeName.StartsWith("&"))
                        {
                            // FR#4 (friction-report 2026-05-19): resolver accepted the bare name as a
                            // potential SDT/BC/Domain reference but SDK couldn't find it in the KB.
                            // Surface a clear UnknownType so the agent knows to check the spelling.
                            // Skip when input had explicit "&" prefix (legacy domain ref behavior).
                            return new JObject
                            {
                                ["status"] = "Error",
                                ["code"] = "UnknownType",
                                ["message"] = $"Type '{typeName}' not found in KB. Expected primitive (Character/Numeric/etc), SDT name (e.g. SdtFoo), BC, or Domain.",
                                ["typeName"] = typeName
                            }.ToString(Newtonsoft.Json.Formatting.None);
                        }
                    }
                    varPart.Variables.Add(newVar);
                }
                else
                {
                    var newVar = VariableInjector.CreateVariable(varPart, varName);
                    varPart.Variables.Add(newVar);
                }

                obj.EnsureSave();
                ScheduleFlush();

                return "{\"status\": \"Success\"}";
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // ── Task 4.3 (v2.3.8) — genexus_modify_variable ──────────────────────────
        // Atomically change a variable's type while preserving its name (and
        // description when possible). Implemented as delete+add over the same
        // VariablesPart, with a snapshot of the pre-change variable set so we
        // can roll back if obj.Save() throws.
        public string ModifyVariable(string target, string varName, string newTypeName, string basedOn = null)
        {
            return WrapWithPersistedState(ModifyVariableInternal(target, varName, newTypeName, basedOn), target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string ModifyVariableInternal(string target, string varName, string newTypeName, string basedOn)
        {
            // Gate 1 — resolve newTypeName up front, before any SDK / KB call.
            // Mirrors AddVariable's Task 4.2 envelope shape exactly.
            GxMcp.Worker.Helpers.TypeResolution resolution = null;
            string resolvedTypeForSdk = newTypeName;
            int? resolvedLength = null;
            int? resolvedDecimals = null;
            if (string.IsNullOrEmpty(newTypeName))
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "UnknownType",
                    ["message"] = "newTypeName is required for genexus_modify_variable.",
                    ["suggestion"] = "Character(40)",
                    ["accepted"] = new JArray { "Character(N)", "Numeric(N.D)", "Date", "DateTime", "Boolean", "VarChar(N)", "<DomainName>" }
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(newTypeName);
            if (!resolution.Recognized)
            {
                var accepted = new JArray();
                if (resolution.AcceptedList != null)
                    foreach (var a in resolution.AcceptedList) accepted.Add(a);
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "UnknownType",
                    ["message"] = $"Unknown typeName '{newTypeName}'. Did you mean '{resolution.Suggestion}'?",
                    ["suggestion"] = resolution.Suggestion,
                    ["accepted"] = accepted
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
            {
                resolvedTypeForSdk = resolution.DomainName;
            }
            else
            {
                resolvedLength = resolution.Length;
                resolvedDecimals = resolution.Decimals;
                resolvedTypeForSdk = resolution.CanonicalType;
            }
            // `basedOn` (optional) takes precedence over a parsed DomainReference —
            // gives the caller explicit control when the typeName is ambiguous.
            if (!string.IsNullOrWhiteSpace(basedOn))
            {
                resolvedTypeForSdk = basedOn;
                resolution = new GxMcp.Worker.Helpers.TypeResolution
                {
                    Recognized = true,
                    CanonicalType = "DomainReference",
                    DomainName = basedOn,
                    Suggestion = basedOn,
                    AcceptedList = resolution?.AcceptedList
                };
            }

            try
            {
                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing == null)
                {
                    return new JObject
                    {
                        ["status"] = "Error",
                        ["code"] = "VariableNotFound",
                        ["message"] = $"Variable '&{varName}' not found on '{target}'."
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(varName))
                {
                    return new JObject
                    {
                        ["status"] = "Refused",
                        ["error"] = "Framework-managed variable",
                        ["details"] = "Variable '&" + varName + "' is managed by " + GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(varName) + " and will be re-injected on save."
                    }.ToString();
                }

                // Snapshot for rollback: capture every variable's identity + shape so we
                // can re-add the original if obj.Save() throws halfway through.
                string preservedDescription = null;
                try { preservedDescription = existing.Description; } catch { /* SDK may not expose */ }

                // Task 4.5 — capture internal id before Remove() so a
                // BoundToControls rejection can still scan the layout XML.
                int? existingVarId = null;
                try
                {
                    int idx = 1;
                    foreach (var v in varPart.Variables)
                    {
                        if (ReferenceEquals(v, existing))
                        {
                            existingVarId = GxMcp.Worker.Helpers.VariableInjector.GetVariableInternalId(v, idx);
                            break;
                        }
                        idx++;
                    }
                }
                catch { /* best-effort */ }

                // Atomic delete + add: keep the VariablesPart change in memory until
                // obj.Save() either succeeds or we restore the original variable.
                global::Artech.Genexus.Common.Variable originalSnapshot = existing;
                try
                {
                    varPart.Variables.Remove(existing);

                    var newVar = new global::Artech.Genexus.Common.Variable(varPart);
                    newVar.Name = varName;
                    if (!string.IsNullOrEmpty(preservedDescription))
                    {
                        try { newVar.Description = preservedDescription; } catch { /* best-effort */ }
                    }

                    if (resolution.CanonicalType != "DomainReference"
                        && VariableInjector.TryParseDbType(resolvedTypeForSdk, out var dbType))
                    {
                        newVar.Type = dbType;
                        try
                        {
                            if (resolvedLength.HasValue) newVar.Length = resolvedLength.Value;
                            if (resolvedDecimals.HasValue) newVar.Decimals = resolvedDecimals.Value;
                        }
                        catch { /* SDK may reject for some types */ }
                    }
                    else
                    {
                        var targetObj = VariableInjector.ResolveTypeObject(varPart.Model, resolvedTypeForSdk);
                        if (targetObj != null)
                        {
                            if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                                newVar.DomainBasedOn = dom;
                            else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                                VariableInjector.BindVariableToSdt(newVar, targetObj);
                            else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                                VariableInjector.BindVariableToBC(newVar, targetObj);
                        }
                    }

                    varPart.Variables.Add(newVar);

                    obj.EnsureSave();
                    ScheduleFlush();

                    return new JObject
                    {
                        ["status"] = "Success",
                        ["details"] = $"Variable '&{varName}' retyped to '{resolution.CanonicalType}" +
                                      (resolvedLength.HasValue ? "(" + resolvedLength.Value + (resolvedDecimals.HasValue && resolvedDecimals.Value > 0 ? "." + resolvedDecimals.Value : "") + ")" : "") + "'."
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }
                catch (Exception ex)
                {
                    // Best-effort rollback: re-add the original variable if it was
                    // removed but the new one failed to save. We can't re-insert the
                    // captured `originalSnapshot` directly (SDK may consider it
                    // detached after Remove), so reconstruct from preserved fields.
                    try
                    {
                        if (!varPart.Variables.Any(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var restored = new global::Artech.Genexus.Common.Variable(varPart);
                            restored.Name = varName;
                            try { if (preservedDescription != null) restored.Description = preservedDescription; } catch { }
                            try { restored.Type = originalSnapshot.Type; } catch { }
                            try { restored.Length = originalSnapshot.Length; } catch { }
                            try { restored.Decimals = originalSnapshot.Decimals; } catch { }
                            try { if (originalSnapshot.DomainBasedOn != null) restored.DomainBasedOn = originalSnapshot.DomainBasedOn; } catch { }
                            varPart.Variables.Add(restored);
                        }
                    }
                    catch { /* swallow — rollback is best-effort */ }
                    // Task 4.5 — prefer a structured BoundToControls envelope
                    // when the SDK rejection message looks like a ghost-binding
                    // failure; falls back to the legacy raw error envelope
                    // when the message doesn't match the heuristic.
                    var boundResp = TryBuildBoundToControlsError(ex, obj, varName, existingVarId);
                    if (boundResp != null) return boundResp;
                    return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
                }
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        // Detects the case where an agent is about to edit WebForm/Layout on an object
        // that already has a populated PatternInstance (the canonical surface for that
        // screen). On the next pattern apply/save, WWP regenerates the WebForm from the
        // pattern, silently overwriting the agent's hand edits. The warning steers the
        // agent toward `genexus_edit part=PatternInstance` without blocking the write —
        // power users still need the WebForm escape hatch for non-WWP customizations.
        private JArray BuildPatternShadowWarningsIfAny(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            string partName)
        {
            try
            {
                if (obj == null) return null;
                if (!WebFormXmlHelper.IsVisualPart(partName)) return null;

                var resolved = _patternAnalysisService.ResolveWWPInstance(obj);
                // Fallback for generated WW family (WW<Trn>, View<Trn>, etc.) whose host
                // is `WorkWithPlus<TrnBaseName>` rather than `WorkWithPlus<obj.Name>`.
                if (resolved == null && !string.IsNullOrEmpty(obj.Name))
                {
                    string[] candidatePrefixes = { "WW", "View", "ViewWW", "Prompt" };
                    foreach (var pre in candidatePrefixes)
                    {
                        if (!obj.Name.StartsWith(pre, StringComparison.Ordinal)) continue;
                        string baseName = obj.Name.Substring(pre.Length);
                        if (string.IsNullOrEmpty(baseName)) continue;
                        var host = _objectService.FindObject("WorkWithPlus" + baseName);
                        if (host != null && string.Equals(host.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                        {
                            resolved = host;
                            break;
                        }
                    }
                }
                if (resolved == null) return null;

                var part = _patternAnalysisService.FindPatternPart(resolved, "PatternInstance");
                if (part == null) return null;

                return new JArray
                {
                    new JObject
                    {
                        ["code"] = "EditingWebFormUnderPattern",
                        ["severity"] = "warning",
                        ["message"] =
                            "This object is covered by a WorkWithPlus PatternInstance ('" + resolved.Name +
                            "'). Hand edits to " + partName + " can be overwritten on the next pattern apply/save. " +
                            "Consider editing part=PatternInstance instead (genexus_edit name=" + resolved.Name +
                            " part=PatternInstance ...). Toggle SDPlus_Editor_Apply_On_Save=False on " + resolved.Name +
                            " if you must keep a hard override on the visual part.",
                        ["patternInstance"] = resolved.Name
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Debug("[WriteVisualPart] PatternShadow warning probe skipped: " + ex.Message);
                return null;
            }
        }

        private static void AttachWarnings(JObject payload, JArray warnings)
        {
            if (payload == null || warnings == null || warnings.Count == 0) return;
            payload["warnings"] = warnings;
        }

        private string WriteVisualPart(global::Artech.Architecture.Common.Objects.KBObject obj, string target, string partName, string xml, bool dryRun = false)
        {
            var webFormPart = WebFormXmlHelper.GetWebFormPart(obj);
            if (webFormPart == null)
            {
                return CreateWriteError(
                    "Visual part not found",
                    target,
                    partName,
                    "The object does not expose a WebForm part for visual editing.",
                    obj);
            }

            // Probe ONCE up front so every response branch (NoChange / DryRun / Success)
            // can attach the same warning set without duplicating the resolution work.
            JArray patternShadowWarnings = BuildPatternShadowWarningsIfAny(obj, partName);

            string normalizedInput;
            try
            {
                normalizedInput = WebFormXmlHelper.NormalizeEditableXmlInput(xml, partName);
            }
            catch (Exception ex)
            {
                return CreateWriteError("Invalid visual XML", target, partName, ex.Message, obj);
            }

            try
            {
                string currentXml = WebFormXmlHelper.ReadEditableXml(obj);
                if (XmlEquivalence.AreEquivalent(currentXml, normalizedInput, out _))
                {
                    var noChangeResp = new JObject
                    {
                        ["status"] = "NoChange",
                        ["part"] = partName,
                        ["details"] = dryRun ? "Dry-run: no change would be applied." : "No change"
                    };
                    AttachWarnings(noChangeResp, patternShadowWarnings);
                    return Models.McpResponse.Success("Write", target, noChangeResp);
                }
                if (dryRun)
                {
                    var dryResp = new JObject
                    {
                        ["status"] = "DryRun",
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed and would update visual XML. Save skipped."
                    };
                    AttachWarnings(dryResp, patternShadowWarnings);
                    var suspects = GxMcp.Worker.Helpers.WebFormSchemaHints.ScanForRejectedAttributes(normalizedInput);
                    if (suspects.Count > 0)
                    {
                        var arr = new JArray();
                        foreach (var s in suspects)
                            arr.Add(new JObject { ["element"] = s.Element, ["attribute"] = s.Attribute, ["reason"] = s.Reason });
                        dryResp["preflightWarnings"] = arr;
                        dryResp["warning"] = "Dry-run detected " + suspects.Count + " attribute(s) likely to be sanitised by the SDK on save. See preflightWarnings.";
                    }
                    return Models.McpResponse.Success("Write", target, dryResp);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[DEBUG-SAVE] Visual no-change precheck skipped: " + ex.Message);
                if (dryRun)
                {
                    return Models.McpResponse.Success("Write", target, new JObject
                    {
                        ["status"] = "DryRun",
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed; current visual read failed (" + ex.Message + "). Save skipped."
                    });
                }
            }

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return CreateWriteError("KB not opened", target, partName, "Open a Knowledge Base before writing visual metadata.", obj);
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    WebFormXmlHelper.ApplyEditableXml(webFormPart, normalizedInput);

                    // ── DIAGNOSTIC: byte-level state RIGHT BEFORE obj.Save ────────────────
                    Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "BEFORE-SAVE");

                    try
                    {
                        webFormPart.Save();
                    }
                    catch
                    {
                    }

                    // KBObjectManager.PrepareSave skips persistence silently when kbObject.Mode ==
                    // Mode.Unchanged. Our XmlNode/Properties mutations don't propagate to obj.Mode
                    // in the headless worker, so the default obj.Save() path always no-ops. Pass
                    // KBObjectSavePreferences { ForceSave = true } to bypass that check — same
                    // mechanism the SDK uses internally for generated objects (Layers.BL line 1025).
                    try
                    {
                        var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                        {
                            ForceSave = true,
                            ForceSaveDefaultParts = true,
                            SkipValidation = true
                        };
                        obj.Save(prefs);
                        Logger.Info("[VisualWrite] obj.Save(KBObjectSavePreferences{ForceSave=true}) completed.");
                    }
                    catch (Exception fsEx)
                    {
                        var inner = fsEx.InnerException ?? fsEx;
                        Logger.Info("[VisualWrite] obj.Save(ForceSave) threw: " + inner.GetType().Name + ": " + inner.Message + " — falling back to EnsureSave.");
                        obj.EnsureSave(true);
                    }

                    // ── DIAGNOSTIC: byte-level state RIGHT AFTER obj.Save ─────────────────
                    Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "AFTER-SAVE");

                    // ── BYPASS: Direct SaveModelEntityOutput ─────────────────────────────
                    // The SDK's SaveWithParent path completes successfully and SerializeData()
                    // returns the right bytes, but they don't reach disk. Try writing bytes
                    // directly via Entity.SaveModelEntityOutput(outputTypeId, version, ts, bytes).
                    Helpers.WebFormSaveDiagnostics.TryDirectSaveModelEntityOutput(webFormPart, obj);

                    // ── BYPASS 2: EntityManager.SaveWithParent direct call ───────────────
                    // If PerformSave's iteration of kbObject.Parts is skipping our part
                    // (IsVirtualPart=true, ShouldIgnorePart=true, or wrong instance), this
                    // sidesteps the loop entirely and forces SaveWithParent with our ref.
                    Helpers.WebFormSaveDiagnostics.TryDirectSaveWithParent(webFormPart, obj);

                    // ── BYPASS 3: SaveHeader() — bucket scan proved WebForm bytes are NOT
                    //   in any (typeId, version) output bucket. SaveHeader writes the entity's
                    //   primary row, which likely contains the data BLOB column.
                    Helpers.WebFormSaveDiagnostics.TryDirectSaveHeader(webFormPart);

                    // ── DIAGNOSTIC: state after all bypasses, before transaction.Commit ──
                    Helpers.WebFormSaveDiagnostics.DumpState(webFormPart, obj, "AFTER-BYPASSES");

                    transaction.Commit();
                    // Force synchronous flush so the data actually lands on disk before we re-read
                    // for verification. ScheduleFlush() default is timer-based and async — if the
                    // worker is killed before the timer fires (or before ProcessExit), unflushed
                    // KB writes are lost.
                    ScheduleFlush(force: true);
                    Logger.Info("[VisualWrite] ScheduleFlush(force=true) completed.");

                    var refreshedObj = _objectService.FindObject(target);
                    string persistedXml = WebFormXmlHelper.ReadEditableXml(refreshedObj ?? obj);
                    if (!XmlEquivalence.AreEquivalent(persistedXml, normalizedInput, out var visualDiff, out var visualStructured))
                    {
                        return CreateWriteError(
                            "Visual write verification failed",
                            target,
                            partName,
                            "The SDK save path completed, but the persisted WebForm XML does not match the requested content. Diff: " + (visualDiff ?? "n/a"),
                            obj,
                            visualStructured);
                    }

                    var okResp = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = "Visual XML updated and verified."
                    };
                    AttachWarnings(okResp, patternShadowWarnings);
                    return Models.McpResponse.Success("Write", target, okResp);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return CreateWriteError("Visual write failed", target, partName, ex.Message, obj);
                }
            }
        }

        private string WritePatternPart(global::Artech.Architecture.Common.Objects.KBObject obj, string target, string partName, string xml, bool dryRun = false)
        {
            string normalizedInput;
            GxMcp.Worker.Helpers.PatternChildOrderReconciler.Report reconcileReport;
            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                // Auto-reconcile childrenOrderedList so callers (LLMs) can add/remove/reorder
                // children purely by editing XML — the helper rebuilds each parent's
                // ordering attribute from the live child tree. Without this, an LLM that
                // adds a <textBlock> but forgets to update childrenOrderedList ships a
                // technically-valid XML that the IDE renders incorrectly. See
                // src/GxMcp.Worker/Helpers/PatternChildOrderReconciler.cs for the rules.
                reconcileReport = GxMcp.Worker.Helpers.PatternChildOrderReconciler.Reconcile(doc);
                if (reconcileReport.ParentsUpdated > 0)
                {
                    Logger.Info("[PATTERN-WRITE] Auto-reconciled childrenOrderedList on " + reconcileReport.ParentsUpdated + " parent(s).");
                    foreach (var change in reconcileReport.Changes)
                    {
                        Logger.Info("[PATTERN-WRITE]   " + change);
                    }
                }
                foreach (var skip in reconcileReport.Skips)
                {
                    Logger.Warn("[PATTERN-WRITE]   skipped: " + skip);
                }
                normalizedInput = doc.ToString();
            }
            catch (Exception ex)
            {
                return CreateWriteError("Invalid pattern XML", target, partName, ex.Message, obj);
            }

            try
            {
                string currentXml = _patternAnalysisService.ReadPatternPartXml(obj, partName, out _, out _);
                if (XmlEquivalence.AreEquivalent(currentXml, normalizedInput, out _))
                {
                    return Models.McpResponse.Success("Write", target, new JObject
                    {
                        ["status"] = "NoChange",
                        ["part"] = partName,
                        ["details"] = dryRun ? "Dry-run: no change would be applied." : "No change"
                    });
                }
                if (dryRun)
                {
                    return Models.McpResponse.Success("Write", target, new JObject
                    {
                        ["status"] = "DryRun",
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed and would update pattern XML. Save skipped."
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[DEBUG-SAVE] Pattern no-change precheck skipped: " + ex.Message);
                if (dryRun)
                {
                    return Models.McpResponse.Success("Write", target, new JObject
                    {
                        ["status"] = "DryRun",
                        ["part"] = partName,
                        ["details"] = "Dry-run: input parsed; current pattern read failed (" + ex.Message + "). Save skipped."
                    });
                }
            }

            LogRequestedPatternPayloadIfEnabled(normalizedInput);

            try
            {
                var preXml = _patternAnalysisService.ReadPatternPartXml(obj, partName, out _, out _);
                if (!string.IsNullOrWhiteSpace(preXml))
                {
                    var snap = PatternSnapshotStore.SaveSnapshot(obj.Guid.ToString(), partName, preXml);
                    if (!string.IsNullOrEmpty(snap)) Logger.Debug("[PatternSnapshot] Saved pre-write snapshot: " + snap);
                }
            }
            catch (Exception ex) { Logger.Debug("[PatternSnapshot] skipped: " + ex.Message); }

            var envelope = _patternAnalysisService.BuildPatternPartEnvelope(obj, partName, normalizedInput, out var resolvedObject, out var resolvedPart);
            if (resolvedObject == null || resolvedPart == null || string.IsNullOrWhiteSpace(envelope))
            {
                return CreateWriteError(
                    "Pattern part not found",
                    target,
                    partName,
                    "The authoritative WorkWithPlus pattern part could not be resolved for writing.",
                    obj);
            }

            LogPatternDiagnosticsIfEnabled(obj, resolvedObject, resolvedPart, normalizedInput);

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                return CreateWriteError("KB not opened", target, partName, "Open a Knowledge Base before writing pattern metadata.", obj);
            }

            using (var transaction = kb.BeginTransaction())
            {
                try
                {
                    LogPatternValidationState("before apply", resolvedObject);
                    // The SDK round-trip is: SerializeToXml() -> <Part><Data><![CDATA[<instance>...]]></Data></Part>.
                    // DeserializeFromXml expects the same wrapper. Passing the bare <instance>...</instance>
                    // (innerXml / normalizedInput) caused the SDK to ignore the payload, leaving the part
                    // unchanged in memory — Save() was a no-op and persistedHash kept matching the pre-write
                    // value. BuildPatternPartEnvelope already produced the wrapped form; route it through.
                    ApplyPatternEnvelope(resolvedPart, envelope, normalizedInput);
                    LogPatternInMemoryStateIfEnabled(obj, resolvedPart, partName, normalizedInput);
                    LogPatternValidationState("after apply before presave", resolvedObject);
                    RunPatternPreSaveExperimentIfEnabled(resolvedObject, resolvedPart, normalizedInput);

                    try
                    {
                        resolvedPart.Save();
                    }
                    catch
                    {
                    }

                    // KBObjectManager.PrepareSave silently skips persistence when kbObject.Mode == Mode.Unchanged.
                    // DeserializeFromXml mutates internal state but does NOT propagate to obj.Mode in the
                    // headless worker, so EnsureSave(true) was a no-op (persistedHash stayed identical across
                    // runs). Mirror the WriteVisualPart fix (line ~1924): invalidate the part's last-modification
                    // snapshot so dirty tracking notices, then call obj.Save(ForceSave=true) which bypasses the
                    // Mode.Unchanged short-circuit — same path the SDK uses for generated objects.
                    InvalidatePartLastModification(resolvedPart);

                    if (!TryPatternDirectSaveExperiment(resolvedObject))
                    {
                        bool forceSaved = false;
                        try
                        {
                            var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                            {
                                ForceSave = true,
                                ForceSaveDefaultParts = true,
                                SkipValidation = true
                            };
                            resolvedObject.Save(prefs);
                            forceSaved = true;
                            Logger.Info("[PATTERN-WRITE] resolvedObject.Save(KBObjectSavePreferences{ForceSave=true}) completed.");
                        }
                        catch (Exception fsEx)
                        {
                            var inner = fsEx.InnerException ?? fsEx;
                            Logger.Info("[PATTERN-WRITE] ForceSave threw: " + inner.GetType().Name + ": " + inner.Message + " — falling back to EnsureSave(true).");
                        }

                        if (!forceSaved)
                        {
                            resolvedObject.EnsureSave(true);
                        }
                    }
                    transaction.Commit();
                    // Force synchronous flush so the bytes hit disk before the verification read; the default
                    // timer-based ScheduleFlush() can lose writes if the worker is recycled before it fires.
                    ScheduleFlush(force: true);

                    string persistedXml = _patternAnalysisService.ReadPatternPartXml(obj, partName, out var refreshedObject, out _);

                    if (!XmlEquivalence.AreEquivalent(persistedXml, normalizedInput, out var patternDiff, out var patternStructured))
                    {
                        return CreateWriteError(
                            "Pattern write verification failed",
                            target,
                            partName,
                            "The SDK save path completed, but the persisted WorkWithPlus pattern XML does not match the requested content. Diff: " + (patternDiff ?? "n/a"),
                            refreshedObject ?? resolvedObject,
                            patternStructured);
                    }

                    var success = new JObject
                    {
                        ["part"] = partName,
                        ["details"] = "Pattern XML updated and verified."
                    };

                    if (resolvedObject.Guid != obj.Guid)
                    {
                        success["resolvedObject"] = resolvedObject.Name;
                        success["resolvedType"] = resolvedObject.TypeDescriptor?.Name;
                    }

                    // F18 (auto-project): when we just wrote a PatternInstance on a
                    // WorkWithPlus host, run the IDE's projection step so the bound
                    // parent's WebForm reflects the edit. Without this, the edit
                    // persists but the WebPanel layout never updates until the next
                    // explicit apply_pattern call. Best-effort — projection failures
                    // don't roll back the edit (the part is already saved).
                    if (string.Equals(resolvedObject.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var parentForProjection = WwpProjectionHelper.ResolveHostParent(resolvedObject, _objectService);
                            if (parentForProjection != null)
                            {
                                bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parentForProjection, resolvedObject);
                                if (projected)
                                {
                                    success["projection"] = new JObject
                                    {
                                        ["status"] = "Projected",
                                        ["parent"] = parentForProjection.Name,
                                        ["parentType"] = parentForProjection.TypeDescriptor?.Name,
                                        ["note"] = "PatternInstance edit was auto-projected onto the parent's WebForm via IPatternBuildProcess.UpdateParentObject."
                                    };

                                    // F21: refresh index cache so list_objects/inspect/query
                                    // see the parent's new state immediately (avoid the
                                    // stale-index window that bit us in F9). Best-effort.
                                    try
                                    {
                                        var idx = _objectService?.GetKbService()?.GetIndexCache();
                                        if (idx != null)
                                        {
                                            var refreshed = _objectService.FindObject(parentForProjection.Name);
                                            if (refreshed != null) idx.UpdateEntry(refreshed);
                                        }
                                    }
                                    catch (Exception idxEx) { Logger.Debug("[WWP-PROJECT] post-project index sync skipped: " + idxEx.Message); }
                                }
                                else
                                {
                                    success["projection"] = new JObject
                                    {
                                        ["status"] = "Skipped",
                                        ["parent"] = parentForProjection.Name,
                                        ["note"] = "Projection helper declined — see worker log for details. Edit was persisted; WebForm may need a manual apply_pattern to refresh."
                                    };
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug("[WWP-PROJECT] auto-project on edit skipped: " + ex.Message);
                        }
                    }

                    // Echo the auto-reconcile report so LLM callers can see exactly
                    // which childrenOrderedList values the MCP rewrote and why. The IDE
                    // hides children that aren't listed; if a caller forgot to update
                    // the attribute by hand, this block tells them the MCP did it for
                    // them (or, with `skips`, that it could NOT and the layout may not
                    // render — that's an actionable signal).
                    if (reconcileReport.HasContent)
                    {
                        var ordering = new JObject
                        {
                            ["parentsUpdated"] = reconcileReport.ParentsUpdated,
                            ["explanation"] = "WorkWithPlus uses `childrenOrderedList` on each container element to drive IDE rendering order. The MCP rebuilds it from the XML's actual child order so callers only need to place elements where they want them in the tree."
                        };
                        if (reconcileReport.Changes.Count > 0)
                        {
                            ordering["changes"] = new JArray(reconcileReport.Changes);
                        }
                        if (reconcileReport.Skips.Count > 0)
                        {
                            ordering["skipped"] = new JArray(reconcileReport.Skips);
                            ordering["skipNote"] = "These parents were left untouched because their childrenOrderedList could not be inferred safely; the affected children may not render in the IDE until the list is corrected manually.";
                        }
                        success["childrenOrderedListReconciliation"] = ordering;
                    }

                    return Models.McpResponse.Success("Write", target, success);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return CreateWriteError("Pattern write failed", target, partName, ex.Message, resolvedObject ?? obj);
                }
            }
        }

        // Public so PatternApplyService.TryDirectAttachPatternInstance can reuse the
        // same Dirty/Mode bookkeeping when seeding a freshly-constructed PatternInstance.
        public static void ForcePatternPartDirty(global::Artech.Architecture.Common.Objects.KBObjectPart part) => InvalidatePartLastModification(part);

        // Public for the same reason — direct-attach needs to seed the pattern data
        // via the same DeserializeDataFrom(XmlElement) path used by the regular write.
        public static bool ApplyPatternDataFromXml(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string innerXml) =>
            TryApplyPatternDataFromXml(resolvedPart, innerXml);

        private static void InvalidatePartLastModification(global::Artech.Architecture.Common.Objects.KBObjectPart part)
        {
            if (part == null) return;

            // PatternInstancePart does not implement InvalidateLastModification (the WebForm
            // dirty-tracking hook) and does not expose any string setter ("Source",
            // "EditableContent", "InstanceXml") — only DeserializeFromXml(string), which
            // mutates internal state but leaves the part's Mode = Unchanged. Without setting
            // Dirty/Mode the SDK's KBObjectManager.PrepareSave short-circuits even under
            // KBObjectSavePreferences.ForceSave because the entity-level dirty check on the
            // PART (not the object) wins. Force both flags explicitly.
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var invalidate = part.GetType().GetMethod("InvalidateLastModification", flags, null, Type.EmptyTypes, null);
                if (invalidate != null)
                {
                    invalidate.Invoke(part, null);
                    Logger.Info("[PATTERN-WRITE] Invoked InvalidateLastModification on pattern part.");
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-WRITE] InvalidateLastModification skipped: " + ex.Message);
            }

            try
            {
                var dirtyProp = part.GetType().GetProperty("Dirty", flags);
                if (dirtyProp != null && dirtyProp.CanWrite && dirtyProp.PropertyType == typeof(bool))
                {
                    dirtyProp.SetValue(part, true);
                    Logger.Info("[PATTERN-WRITE] Forced part.Dirty = true.");
                }
            }
            catch (Exception ex) { Logger.Info("[PATTERN-WRITE] Set Dirty=true skipped: " + ex.Message); }

            try
            {
                var modeProp = part.GetType().GetProperty("Mode", flags);
                if (modeProp != null && modeProp.CanWrite)
                {
                    // Mode is an enum: Unchanged | Added | Modified | Deleted. Lift to Modified.
                    var modified = Enum.Parse(modeProp.PropertyType, "Modified", ignoreCase: true);
                    modeProp.SetValue(part, modified);
                    Logger.Info("[PATTERN-WRITE] Forced part.Mode = Modified.");
                }
            }
            catch (Exception ex) { Logger.Info("[PATTERN-WRITE] Set Mode=Modified skipped: " + ex.Message); }
        }

        private static bool TryApplyPatternDataFromXml(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string innerXml)
        {
            if (resolvedPart == null || string.IsNullOrWhiteSpace(innerXml)) return false;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var method = resolvedPart.GetType()
                    .GetMethods(flags)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "DeserializeDataFrom", StringComparison.Ordinal) &&
                        m.GetParameters().Length == 1 &&
                        typeof(System.Xml.XmlElement).IsAssignableFrom(m.GetParameters()[0].ParameterType));
                if (method == null)
                {
                    Logger.Info("[PATTERN-WRITE] DeserializeDataFrom(XmlElement) not found on " + resolvedPart.GetType().FullName);
                    return false;
                }

                // SerializeDataTo(XmlElement target) writes the pattern data AS CHILDREN of target.
                // The inverse, DeserializeDataFrom, reads from the same shape — i.e. expects to be
                // passed the PARENT element whose first/only child is the <instance>. Passing
                // <instance> directly causes the SDK to read its (non-existent) <instance> child
                // and persist an empty <instance/>. Wrap the innerXml in a transient parent;
                // use a temp doc for parsing then ImportNode (preserving whitespace would inject
                // XmlWhitespace nodes that fail the SDK's strict XmlElement cast).
                var sourceDoc = new System.Xml.XmlDocument { PreserveWhitespace = false };
                sourceDoc.LoadXml(innerXml);

                var hostDoc = new System.Xml.XmlDocument { PreserveWhitespace = false };
                var parent = hostDoc.CreateElement("Data");
                hostDoc.AppendChild(parent);
                var imported = hostDoc.ImportNode(sourceDoc.DocumentElement, deep: true);
                parent.AppendChild(imported);

                method.Invoke(resolvedPart, new object[] { parent });
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Warn("[PATTERN-WRITE] DeserializeDataFrom(XmlElement) threw: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }

        private void ApplyPatternEnvelope(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string envelopeXml, string innerXml)
        {
            // The canonical pattern-part data mutation is DeserializeDataFrom(XmlElement).
            // SerializeToXml/DeserializeFromXml only round-trip the <Properties> bag (IsDefault etc),
            // NOT the actual pattern XML. Discovered via runtime reflection — see
            // src/GxMcp.Worker/Services/WriteService.cs commit history for the inspection trail.
            if (TryApplyPatternDataFromXml(resolvedPart, innerXml))
            {
                Logger.Info("[PATTERN-WRITE] DeserializeDataFrom(XmlElement) applied with " + (innerXml?.Length ?? 0) + " chars.");
                return;
            }

            if (TryApplyNativePatternMutationExperiment(resolvedPart, innerXml))
            {
                Logger.Info("[PATTERN-DEBUG] Native pattern mutation experiment applied.");
                return;
            }

            // SerializeToXml/DeserializeFromXml on KBObjectPart round-trip the wrapped envelope
            // (<Part type="..."><Data><![CDATA[...]]></Data></Part>). Passing the bare inner XML
            // is silently ignored, so prefer the envelope and fall back to innerXml only if
            // envelope construction failed upstream.
            string payload = !string.IsNullOrWhiteSpace(envelopeXml) ? envelopeXml : innerXml;

            var executeUpdateMethod = resolvedPart.GetType().GetMethod(
                "ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Action) },
                null);

            if (executeUpdateMethod != null)
            {
                Action updateAction = () => resolvedPart.DeserializeFromXml(payload);
                executeUpdateMethod.Invoke(resolvedPart, new object[] { "MCP pattern update", updateAction });
                return;
            }

            resolvedPart.DeserializeFromXml(payload);
        }

        private void LogPatternInMemoryStateIfEnabled(
            global::Artech.Architecture.Common.Objects.KBObject requestedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string partName,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                if (resolvedPart != null)
                {
                    string serializedPart = _patternAnalysisService.ExtractEditablePatternXmlForDiagnostics(resolvedPart);
                    if (!string.IsNullOrWhiteSpace(serializedPart))
                    {
                        string normalizedSerializedPart = XDocument.Parse(serializedPart, LoadOptions.PreserveWhitespace).ToString();
                        Logger.Info("[PATTERN-DEBUG] Resolved part equals requested after apply: " + string.Equals(normalizedSerializedPart, normalizedInput, StringComparison.Ordinal));
                        Logger.Info("[PATTERN-DEBUG] Resolved part hash after apply: " + normalizedSerializedPart.GetHashCode() + "; requested hash=" + normalizedInput.GetHashCode());
                    }
                }

                string currentXml = _patternAnalysisService.ReadPatternPartXml(requestedObject, partName, out _, out _);
                string normalizedCurrent = string.IsNullOrWhiteSpace(currentXml)
                    ? string.Empty
                    : XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace).ToString();
                Logger.Info("[PATTERN-DEBUG] In-memory equals requested after apply: " + string.Equals(normalizedCurrent, normalizedInput, StringComparison.Ordinal));
                Logger.Info("[PATTERN-DEBUG] In-memory hash after apply: " + normalizedCurrent.GetHashCode() + "; requested hash=" + normalizedInput.GetHashCode());
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] In-memory state inspection failed: " + ex.Message);
            }
        }

        private bool TryApplyNativePatternMutationExperiment(global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart, string innerXml)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_NATIVE_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (resolvedPart == null || string.IsNullOrWhiteSpace(innerXml)) return false;

            try
            {
                var requestedValues = ExtractRequestedGridVariableValues(innerXml);
                if (requestedValues.Count == 0)
                {
                    Logger.Info("[PATTERN-DEBUG] Native mutation experiment skipped: no target gridVariable changes found in requested XML.");
                    return false;
                }

                object rootElement = GetReadablePropertyValue(resolvedPart, "RootElement");
                if (rootElement == null)
                {
                    Logger.Warn("[PATTERN-DEBUG] Native mutation experiment skipped: RootElement not available.");
                    return false;
                }

                var targetElements = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                CollectPatternElementsByName(rootElement, targetElements);

                bool changed = false;
                foreach (var entry in requestedValues)
                {
                    if (!targetElements.TryGetValue(entry.Key, out object element) || element == null)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Native mutation experiment could not locate gridVariable '" + entry.Key + "'.");
                        continue;
                    }

                    object attributes = GetReadablePropertyValue(element, "Attributes");
                    if (attributes == null)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Native mutation experiment found '" + entry.Key + "' without Attributes.");
                        continue;
                    }

                    foreach (var attributeValue in entry.Value)
                    {
                        bool applied = TryApplyPatternDeltaCommand(element, attributes, attributeValue.Key, attributeValue.Value);
                        if (!applied)
                        {
                            applied = TrySetPatternAttributeValue(attributes, attributeValue.Key, attributeValue.Value);
                        }

                        if (applied)
                        {
                            changed = true;
                            Logger.Info("[PATTERN-DEBUG] Native mutation applied: " + entry.Key + "." + attributeValue.Key + "=" + attributeValue.Value);
                        }
                        else
                        {
                            Logger.Warn("[PATTERN-DEBUG] Native mutation failed for " + entry.Key + "." + attributeValue.Key + "=" + attributeValue.Value);
                        }
                    }
                }

                return changed;
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Native mutation experiment failed: " + ex.Message);
                return false;
            }
        }

        private Dictionary<string, Dictionary<string, string>> ExtractRequestedGridVariableValues(string innerXml)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var document = XDocument.Parse(innerXml, LoadOptions.PreserveWhitespace);
                var seenOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in document
                    .Descendants()
                    .Where(e => e.Name.LocalName.Equals("gridVariable", StringComparison.OrdinalIgnoreCase)))
                {
                    string name = (string)element.Attribute("name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!string.Equals(name, "HorasDebito", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "SedCPHor", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddRequestedAttribute(values, element, "description");
                    AddRequestedAttribute(values, element, "defaultDescription");
                    AddRequestedAttribute(values, element, "visible");
                    AddRequestedAttribute(values, element, "defaultVisible");

                    int occurrence = seenOccurrences.TryGetValue(name, out int current) ? current + 1 : 1;
                    seenOccurrences[name] = occurrence;
                    LogRequestedGridVariableOccurrence(name, occurrence, element, values);

                    if (values.Count > 0)
                    {
                        result[name] = values;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Failed to parse requested PatternInstance XML for native mutation: " + ex.Message);
            }

            return result;
        }

        private void LogRequestedPatternPayloadIfEnabled(string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(normalizedInput)) return;

            try
            {
                foreach (string name in new[] { "HorasDebito", "SedCPHor" })
                {
                    string snippet = ExtractGridVariableSnippet(normalizedInput, name);
                    if (string.IsNullOrWhiteSpace(snippet))
                    {
                        Logger.Info("[PATTERN-DEBUG] REQUESTED-PAYLOAD name=" + name + " snippet=<missing>");
                        continue;
                    }

                    Logger.Info("[PATTERN-DEBUG] REQUESTED-PAYLOAD name=" + name + " hash=" + snippet.GetHashCode() + " snippet=" + snippet);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] REQUESTED-PAYLOAD logging failed: " + ex.Message);
            }
        }

        private string ExtractGridVariableSnippet(string xml, string name)
        {
            if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(name)) return null;

            string marker = "name=\"" + name + "\"";
            int markerIndex = xml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;

            int start = xml.LastIndexOf("<gridVariable", markerIndex, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            int end = xml.IndexOf("/>", markerIndex, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;

            end += 2;
            if (end <= start || end > xml.Length) return null;

            return xml.Substring(start, end - start);
        }

        private void LogRequestedGridVariableOccurrence(string name, int occurrence, XElement element, Dictionary<string, string> values)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                string path = BuildRequestedElementPath(element);
                string attrs = string.Join(", ", values.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => kvp.Key + "=" + kvp.Value));
                Logger.Info("[PATTERN-DEBUG] REQUESTED-GRID occurrence=" + occurrence + " name=" + name + " path=" + path + " values=[" + attrs + "]");
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] REQUESTED-GRID logging failed for " + name + ": " + ex.Message);
            }
        }

        private string BuildRequestedElementPath(XElement element)
        {
            if (element == null) return "<null>";

            var segments = new Stack<string>();
            XElement current = element;
            while (current != null)
            {
                string name = current.Name.LocalName;
                string identifier = (string)current.Attribute("name");
                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    segments.Push(name + "[" + identifier + "]");
                }
                else
                {
                    int index = current.Parent == null
                        ? 1
                        : current.Parent.Elements(current.Name).TakeWhile(e => e != current).Count() + 1;
                    segments.Push(name + "[" + index + "]");
                }

                current = current.Parent;
            }

            return "/" + string.Join("/", segments);
        }

        private void AddRequestedAttribute(Dictionary<string, string> values, XElement element, string attributeName)
        {
            string value = (string)element.Attribute(attributeName);
            if (value != null)
            {
                values[attributeName] = value;
            }
        }

        private void CollectPatternElementsByName(object element, Dictionary<string, object> matches)
        {
            if (element == null) return;

            Type type = element.GetType();
            string elementType = ReadStringProperty(type, element, "Name");
            string keyValue = ReadStringProperty(type, element, "KeyValueString");
            string propertyTitle = ReadStringProperty(type, element, "PropertyTitle");
            string path = ReadStringProperty(type, element, "Path");
            object attributes = GetReadablePropertyValue(element, "Attributes");
            string attributeName = TryGetPatternAttributeValue(attributes, "name");

            if (string.Equals(elementType, "gridVariable", StringComparison.OrdinalIgnoreCase))
            {
                string candidateName = FirstNonEmpty(attributeName, keyValue, propertyTitle, ExtractNameFromPath(path));
                if (!string.IsNullOrWhiteSpace(candidateName) && !matches.ContainsKey(candidateName))
                {
                    matches[candidateName] = element;
                }
            }

            object children = GetReadablePropertyValue(element, "Children");
            if (!(children is System.Collections.IEnumerable enumerable)) return;

            foreach (object child in enumerable)
            {
                if (child == null) continue;
                CollectPatternElementsByName(child, matches);
            }
        }

        private string ExtractNameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            int atIndex = path.LastIndexOf("[@name=\"", StringComparison.OrdinalIgnoreCase);
            if (atIndex >= 0)
            {
                int start = atIndex + 8;
                int end = path.IndexOf("\"]", start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    return path.Substring(start, end - start);
                }
            }

            int bracketIndex = path.LastIndexOf('[', path.Length - 1);
            int closeIndex = path.LastIndexOf(']');
            if (bracketIndex >= 0 && closeIndex > bracketIndex)
            {
                string token = path.Substring(bracketIndex + 1, closeIndex - bracketIndex - 1);
                if (!int.TryParse(token, out _) && token.IndexOf('"') < 0)
                {
                    return token;
                }
            }

            return null;
        }

        private string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private object GetReadablePropertyValue(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length > 0) return null;
                return property.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        private bool TrySetPatternAttributeValue(object attributes, string propertyName, string value)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(propertyName)) return false;

            Type type = attributes.GetType();
            string before = TryGetPatternAttributeValue(attributes, propertyName);

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, "SetPropertyValueString", StringComparison.OrdinalIgnoreCase)))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string))
                {
                    try
                    {
                        method.Invoke(attributes, new object[] { propertyName, value });
                        string after = TryGetPatternAttributeValue(attributes, propertyName);
                        if (string.Equals(after, value, StringComparison.Ordinal))
                        {
                            return !string.Equals(before, after, StringComparison.Ordinal);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            MethodInfo setPropertyValue = type.GetMethod("SetPropertyValue", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(object) }, null);
            if (setPropertyValue != null)
            {
                try
                {
                    setPropertyValue.Invoke(attributes, new object[] { propertyName, value });
                    string after = TryGetPatternAttributeValue(attributes, propertyName);
                    if (string.Equals(after, value, StringComparison.Ordinal))
                    {
                        return !string.Equals(before, after, StringComparison.Ordinal);
                    }
                }
                catch
                {
                }
            }

            var indexer = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, typeof(object), new[] { typeof(string) }, null);
            if (indexer != null)
            {
                try
                {
                    indexer.SetValue(attributes, value, new object[] { propertyName });
                    string after = TryGetPatternAttributeValue(attributes, propertyName);
                    if (string.Equals(after, value, StringComparison.Ordinal))
                    {
                        return !string.Equals(before, after, StringComparison.Ordinal);
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private bool TryApplyPatternDeltaCommand(object element, object attributes, string propertyName, string value)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DELTA_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (element == null || attributes == null || string.IsNullOrWhiteSpace(propertyName)) return false;

            try
            {
                string before = TryGetPatternAttributeValue(attributes, propertyName);
                if (string.Equals(before, value, StringComparison.Ordinal))
                {
                    return false;
                }

                Type commandType = element.GetType().Assembly.GetType("Artech.Packages.Patterns.Objects.ChangeAttributeValueCommand", false, true);
                if (commandType == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Delta command type not found.");
                    return false;
                }

                ConstructorInfo ctor = commandType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var parameters = c.GetParameters();
                        return parameters.Length == 4 &&
                               parameters[0].ParameterType.IsInstanceOfType(element) &&
                               parameters[1].ParameterType == typeof(string);
                    });
                if (ctor == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Delta command ctor not found for " + propertyName + ".");
                    return false;
                }

                object command = ctor.Invoke(new object[] { element, propertyName, before, value });
                MethodInfo isSafe = commandType.GetMethod("IsSafeToExecute", BindingFlags.Public | BindingFlags.Instance);
                if (isSafe != null)
                {
                    object safeResult = isSafe.Invoke(command, null);
                    Logger.Info("[PATTERN-DEBUG] Delta command IsSafeToExecute for " + propertyName + " => " + DescribeValue(safeResult));
                    if (safeResult is bool safe && !safe)
                    {
                        return false;
                    }
                }

                MethodInfo execute = commandType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Delta command Execute missing for " + propertyName + ".");
                    return false;
                }

                execute.Invoke(command, null);
                string after = TryGetPatternAttributeValue(attributes, propertyName);
                Logger.Info("[PATTERN-DEBUG] Delta command applied " + propertyName + ": before=" + before + "; after=" + after + "; target=" + value);
                return string.Equals(after, value, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Delta command failed for " + propertyName + ": " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private string TryGetPatternAttributeValue(object attributes, string propertyName)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            Type type = attributes.GetType();

            MethodInfo getter = type.GetMethod("GetPropertyValueString", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (getter != null)
            {
                try
                {
                    return getter.Invoke(attributes, new object[] { propertyName })?.ToString();
                }
                catch
                {
                }
            }

            var indexer = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, typeof(object), new[] { typeof(string) }, null);
            if (indexer != null)
            {
                try
                {
                    return indexer.GetValue(attributes, new object[] { propertyName })?.ToString();
                }
                catch
                {
                }
            }

            return null;
        }

        private void LogPatternDiagnosticsIfEnabled(
            global::Artech.Architecture.Common.Objects.KBObject requestedObject,
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                var partType = resolvedPart.GetType();
                string executeUpdateSignature = TryGetMethodSignature(partType, "ExecuteUpdate");
                string deserializeSignature = string.Join(" | ", partType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "DeserializeFromXml", StringComparison.OrdinalIgnoreCase))
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());

                string serializeSignature = TryGetMethodSignature(partType, "SerializeToXml");

                Logger.Info("[PATTERN-DEBUG] Requested object: " + requestedObject.Name + " (" + requestedObject.TypeDescriptor?.Name + ")");
                Logger.Info("[PATTERN-DEBUG] Resolved object: " + resolvedObject.Name + " (" + resolvedObject.TypeDescriptor?.Name + ")");
                LogResolvedObjectDiagnostics(resolvedObject);
                Logger.Info("[PATTERN-DEBUG] Part type: " + partType.FullName);
                Logger.Info("[PATTERN-DEBUG] Part name/type descriptor: " + (resolvedPart.Name ?? "<null>") + " / " + (resolvedPart.TypeDescriptor?.Name ?? "<null>"));
                Logger.Info("[PATTERN-DEBUG] ExecuteUpdate: " + executeUpdateSignature);
                Logger.Info("[PATTERN-DEBUG] DeserializeFromXml overloads: " + deserializeSignature);
                Logger.Info("[PATTERN-DEBUG] SerializeToXml: " + serializeSignature);
                Logger.Info("[PATTERN-DEBUG] Type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(partType)));
                Logger.Info("[PATTERN-DEBUG] Interesting properties: " + string.Join(" | ", GetInterestingPropertySignatures(partType)));
                Logger.Info("[PATTERN-DEBUG] Interesting fields: " + string.Join(" | ", GetInterestingFieldSignatures(partType)));
                Logger.Info("[PATTERN-DEBUG] Interesting methods: " + string.Join(" | ", GetInterestingMethodSignatures(partType)));
                LogInterestingPropertyValues(resolvedPart, partType, resolvedObject);
                TryLogMethodResult("Part", resolvedPart, partType, "GetDataUpdateProcess");
                TryLogMethodResult("Part", resolvedPart, partType, "GetDataVersionAdapter");
                TryLogSemanticWorkWithPlusInstance(resolvedObject);
                Logger.Info("[PATTERN-DEBUG] Input hash: " + normalizedInput.GetHashCode() + "; length=" + normalizedInput.Length);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Diagnostic logging failed: " + ex.Message);
            }
        }

        private void RunPatternPreSaveExperimentIfEnabled(
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_PRESAVE_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;
            if (resolvedObject == null) return;

            try
            {
                LogPatternValidationState("pre-save baseline", resolvedObject);
                LogPatternStateMethods(resolvedObject);
                LogNamedInterfaceProperties("pre-save baseline", resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");
                RunPatternPartHooks("pre-save baseline", resolvedObject, resolvedPart);
                TryPatternSemanticGridSaveExperiment(resolvedObject, resolvedPart, normalizedInput);

                TryInvokeInterfaceMethodByName(resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget", "PreserveDefaultLock", Array.Empty<object>());
                LogPatternValidationState("after PreserveDefaultLock", resolvedObject);
                LogNamedInterfaceProperties("after PreserveDefaultLock", resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");

                string[] noArgMethods =
                {
                    "LoadInstancePropertyDefinition",
                    "RefreshDefaultDependentParts",
                    "CalculateDefault",
                    "CanCalculateDefault",
                    "ShouldRegenerate"
                };

                foreach (string methodName in noArgMethods)
                {
                    TryInvokePatternMethod(resolvedObject, methodName, Array.Empty<object>());
                    LogPatternValidationState("after " + methodName, resolvedObject);
                    LogNamedInterfaceProperties("after " + methodName, resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");
                    RunPatternPartHooks("after " + methodName, resolvedObject, resolvedPart);
                }

                TryInvokeInterfaceMethodByName(resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget", "PreserveDefaultUnlock", Array.Empty<object>());
                LogPatternValidationState("after PreserveDefaultUnlock", resolvedObject);
                LogNamedInterfaceProperties("after PreserveDefaultUnlock", resolvedObject, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");

                TryInvokePatternMethod(resolvedObject, "SaveUpdates", new object[] { false });
                LogPatternValidationState("after SaveUpdates(false)", resolvedObject);
                RunPatternPartHooks("after SaveUpdates(false)", resolvedObject, resolvedPart);

                TryInvokePatternMethod(resolvedObject, "SaveUpdates", new object[] { true });
                LogPatternValidationState("after SaveUpdates(true)", resolvedObject);
                RunPatternPartHooks("after SaveUpdates(true)", resolvedObject, resolvedPart);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Pre-save experiment failed: " + ex.Message);
            }
        }

        private bool TryPatternSemanticGridSaveExperiment(
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart,
            string normalizedInput)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_SEMANTIC_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (resolvedObject == null || resolvedPart == null || string.IsNullOrWhiteSpace(normalizedInput)) return false;

            try
            {
                var requestedValues = ExtractRequestedGridVariableValues(normalizedInput);
                var targetNames = new[] { "HorasDebito", "SedCPHor" };
                var requestedTargets = targetNames
                    .Where(name => requestedValues.TryGetValue(name, out var values) && values.Count > 0)
                    .Select(name => new
                    {
                        Name = name,
                        Values = requestedValues[name]
                    })
                    .ToList();

                if (requestedTargets.Count == 0)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: no HorasDebito or SedCPHor changes requested.");
                    return false;
                }

                Type semanticInstanceType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly => assembly.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPlusInstance", false, true))
                    .FirstOrDefault(type => type != null);
                if (semanticInstanceType == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: WorkWithPlusInstance type not loaded.");
                    return false;
                }

                ConstructorInfo ctor = semanticInstanceType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var parameters = c.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(resolvedObject);
                    });
                if (ctor == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: constructor not compatible.");
                    return false;
                }

                object semanticInstance = ctor.Invoke(new object[] { resolvedObject });
                object settings = GetReadablePropertyValue(semanticInstance, "Settings");
                if (settings == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: Settings not available.");
                    return false;
                }

                object gridSettings = null;
                MethodInfo getAllChildren = settings.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren != null)
                {
                    var allChildren = getAllChildren.Invoke(settings, null) as System.Collections.IEnumerable;
                    if (allChildren != null)
                    {
                        foreach (object child in EnumerateSemanticItems(allChildren))
                        {
                            if (child == null) continue;
                            if (string.Equals(child.GetType().FullName, "DVelop.Patterns.WorkWithPlus.SettingsGridElement", StringComparison.Ordinal))
                            {
                                gridSettings = child;
                                break;
                            }
                        }
                    }
                }

                if (gridSettings == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: SettingsGridElement not found.");
                    return false;
                }

                object rootElement = GetReadablePropertyValue(resolvedPart, "RootElement");
                if (rootElement == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: RootElement not available.");
                    return false;
                }

                var targetElements = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                CollectPatternElementsByName(rootElement, targetElements);

                MethodInfo saveAttribute = gridSettings.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "SaveAttribute", StringComparison.OrdinalIgnoreCase) &&
                        method.GetParameters().Length == 4);
                if (saveAttribute == null)
                {
                    WritePatternDebugTrace("Semantic grid experiment skipped: SaveAttribute unavailable.");
                    return false;
                }

                bool changed = false;
                foreach (var target in requestedTargets)
                {
                    if (!targetElements.TryGetValue(target.Name, out object targetElement) || targetElement == null)
                    {
                        WritePatternDebugTrace("Semantic grid experiment skipped: " + target.Name + " element not found.");
                        continue;
                    }

                    WritePatternDebugTrace("Semantic grid target before " + target.Name + "=" + DescribeSemanticElement(targetElement));

                    foreach (string attName in new[] { "description", "Description", "defaultDescription", "DefaultDescription" })
                    {
                        string attValue;
                        if (!target.Values.TryGetValue(attName, out attValue))
                        {
                            string fallbackKey = attName.StartsWith("default", StringComparison.OrdinalIgnoreCase) ? "defaultDescription" : "description";
                            if (!target.Values.TryGetValue(fallbackKey, out attValue))
                            {
                                continue;
                            }
                        }

                        object result = saveAttribute.Invoke(gridSettings, new object[] { targetElement, attName, attValue, attName.StartsWith("default", StringComparison.OrdinalIgnoreCase) });
                        WritePatternDebugTrace("Semantic grid SaveAttribute " + target.Name + "." + attName + " => " + DescribeValue(result));
                        changed = true;
                    }

                    WritePatternDebugTrace("Semantic grid target after " + target.Name + "=" + DescribeSemanticElement(targetElement));
                }

                if (changed)
                {
                    WritePatternDebugTrace("Semantic grid experiment applied requested changes.");
                }

                return changed;
            }
            catch (Exception ex)
            {
                WritePatternDebugTrace("Semantic grid experiment failed: " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private void RunPatternPartHooks(
            string stage,
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject,
            global::Artech.Architecture.Common.Objects.KBObjectPart resolvedPart)
        {
            if (resolvedObject == null || resolvedPart == null) return;

            try
            {
                MethodInfo getValidator = resolvedPart.GetType().GetMethod("GetValidator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getValidator != null)
                {
                    object validator = getValidator.Invoke(resolvedPart, null);
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " GetValidator()=" + DescribeValue(validator));
                    TryRunPatternValidator(stage, validator, resolvedObject);
                }

                MethodInfo getUpdateProcess = resolvedPart.GetType().GetMethod("GetDataUpdateProcess", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getUpdateProcess != null)
                {
                    object updateProcess = getUpdateProcess.Invoke(resolvedPart, null);
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " GetDataUpdateProcess()=" + DescribeValue(updateProcess));
                    TryRunPatternUpdateProcess(stage, updateProcess, resolvedObject);
                }

                TryLogPatternDefinitionHooks(stage, resolvedPart);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hooks failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryRunPatternValidator(string stage, object validator, object patternObject)
        {
            if (validator == null || patternObject == null) return;

            try
            {
                MethodInfo validateMethod = validator.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Validate", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 2);
                if (validateMethod == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " validator has no compatible Validate method.");
                    return;
                }

                var output = new Artech.Common.Diagnostics.OutputMessages();
                object result = validateMethod.Invoke(validator, new object[] { patternObject, output });
                string summary = output.ErrorText;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = output.FullText;
                }

                Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " validator.Validate => " + DescribeValue(result) + "; hasErrors=" + output.HasErrors + "; messages=" + (summary ?? string.Empty));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hook validator failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryRunPatternUpdateProcess(string stage, object updateProcess, object patternObject)
        {
            if (updateProcess == null || patternObject == null) return;

            try
            {
                MethodInfo updateMethod = updateProcess.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "UpdateObject", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 1);
                if (updateMethod == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " update process has no compatible UpdateObject method.");
                    return;
                }

                object result = updateMethod.Invoke(updateProcess, new object[] { patternObject });
                Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " updateProcess.UpdateObject => " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hook update process failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogPatternDefinitionHooks(string stage, object resolvedPart)
        {
            if (resolvedPart == null) return;

            try
            {
                Type iface = resolvedPart.GetType().GetInterfaces()
                    .FirstOrDefault(i => string.Equals(i.FullName, "Artech.Packages.Patterns.Engine.IPatternXPathNavigable", StringComparison.OrdinalIgnoreCase));
                if (iface == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " IPatternXPathNavigable not implemented.");
                    return;
                }

                PropertyInfo patternProp = iface.GetProperty("Pattern", BindingFlags.Public | BindingFlags.Instance);
                object pattern = patternProp?.GetValue(resolvedPart, null);
                Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " Pattern=" + DescribeValue(pattern));
                if (pattern == null) return;

                foreach (string methodName in new[] { "GetInstanceValidator", "GetInstanceUpdateProcess", "GetInstanceVersionAdapter" })
                {
                    try
                    {
                        MethodInfo method = pattern.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                        if (method == null)
                        {
                            Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " Pattern." + methodName + "()=<missing>");
                            continue;
                        }

                        object result = method.Invoke(pattern, null);
                        Logger.Info("[PATTERN-DEBUG] Part hook " + stage + " Pattern." + methodName + "()=" + DescribeValue(result));
                    }
                    catch (Exception exMethod)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Part hook " + stage + " Pattern." + methodName + "() failed: " + (exMethod.InnerException?.Message ?? exMethod.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Part hook pattern definition failed at " + stage + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private bool TryPatternDirectSaveExperiment(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DIRECT_SAVE_EXPERIMENT");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return false;
            if (resolvedObject == null) return false;

            try
            {
                MethodInfo saveMethod = resolvedObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Save", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (saveMethod != null)
                {
                    object saveResult = saveMethod.Invoke(resolvedObject, Array.Empty<object>());
                    Logger.Info("[PATTERN-DEBUG] Direct save experiment invoked " + FormatMethodSignature(saveMethod) + " => " + DescribeValue(saveResult));
                    return true;
                }

                MethodInfo saveWithPreferences = resolvedObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Save", StringComparison.OrdinalIgnoreCase) &&
                                         m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.Name.IndexOf("SavePreferences", StringComparison.OrdinalIgnoreCase) >= 0);

                if (saveWithPreferences != null)
                {
                    object preferences = null;
                    try
                    {
                        preferences = Activator.CreateInstance(saveWithPreferences.GetParameters()[0].ParameterType);
                    }
                    catch (Exception exCtor)
                    {
                        Logger.Warn("[PATTERN-DEBUG] Direct save experiment could not create save preferences: " + exCtor.Message);
                    }

                    if (preferences != null)
                    {
                        object saveResult = saveWithPreferences.Invoke(resolvedObject, new[] { preferences });
                        Logger.Info("[PATTERN-DEBUG] Direct save experiment invoked " + FormatMethodSignature(saveWithPreferences) + " => " + DescribeValue(saveResult));
                        return true;
                    }
                }

                Logger.Warn("[PATTERN-DEBUG] Direct save experiment could not locate a parameterless Save method.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Direct save experiment failed: " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private void TryInvokePatternMethod(object target, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName)) return;

            try
            {
                MethodInfo method = FindCompatibleMethod(target.GetType(), methodName, args);
                if (method == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Pre-save method not found: " + methodName);
                    return;
                }

                object result = method.Invoke(target, args);
                Logger.Info("[PATTERN-DEBUG] Pre-save invoked " + FormatMethodSignature(method) + " => " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Pre-save method failed " + methodName + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private MethodInfo FindCompatibleMethod(Type type, string methodName, object[] args)
        {
            if (type == null || string.IsNullOrWhiteSpace(methodName)) return null;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) ||
                            m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length) continue;

                bool compatible = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (args[i] == null) continue;
                    if (!parameters[i].ParameterType.IsInstanceOfType(args[i]) &&
                        !(parameters[i].ParameterType.IsValueType && parameters[i].ParameterType == args[i].GetType()))
                    {
                        compatible = false;
                        break;
                    }
                }

                if (compatible) return method;
            }

            return null;
        }

        private void LogPatternValidationState(string stage, global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            try
            {
                var output = new Artech.Common.Diagnostics.OutputMessages();
                bool isValid = resolvedObject.Validate(output);
                string summary = output.ErrorText;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = output.FullText;
                }
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = resolvedObject.GetSdkMessages();
                }

                Logger.Info("[PATTERN-DEBUG] Validation state " + stage + ": isValid=" + isValid + "; hasErrors=" + output.HasErrors + "; messages=" + (summary ?? string.Empty));
                LogPatternValidationFlags(stage, resolvedObject);

                MethodInfo validateStateMethod = FindCompatibleMethod(resolvedObject.GetType(), "ValidateState", new object[] { output });
                if (validateStateMethod != null)
                {
                    try
                    {
                        var stateOutput = new Artech.Common.Diagnostics.OutputMessages();
                        object stateResult = validateStateMethod.Invoke(resolvedObject, new object[] { stateOutput });
                        string stateSummary = stateOutput.ErrorText;
                        if (string.IsNullOrWhiteSpace(stateSummary))
                        {
                            stateSummary = stateOutput.FullText;
                        }
                        Logger.Info("[PATTERN-DEBUG] ValidateState " + stage + ": result=" + DescribeValue(stateResult) + "; hasErrors=" + stateOutput.HasErrors + "; messages=" + (stateSummary ?? string.Empty));
                    }
                    catch (Exception exState)
                    {
                        Logger.Warn("[PATTERN-DEBUG] ValidateState failed at " + stage + ": " + (exState.InnerException?.Message ?? exState.Message));
                    }
                }

                MethodInfo validateDataMethod = FindCompatibleMethod(resolvedObject.GetType(), "ValidateData", new object[] { output });
                if (validateDataMethod != null)
                {
                    try
                    {
                        var dataOutput = new Artech.Common.Diagnostics.OutputMessages();
                        object dataResult = validateDataMethod.Invoke(resolvedObject, new object[] { dataOutput });
                        string dataSummary = dataOutput.ErrorText;
                        if (string.IsNullOrWhiteSpace(dataSummary))
                        {
                            dataSummary = dataOutput.FullText;
                        }
                        Logger.Info("[PATTERN-DEBUG] ValidateData " + stage + ": result=" + DescribeValue(dataResult) + "; hasErrors=" + dataOutput.HasErrors + "; messages=" + (dataSummary ?? string.Empty));
                    }
                    catch (Exception exData)
                    {
                        Logger.Warn("[PATTERN-DEBUG] ValidateData failed at " + stage + ": " + (exData.InnerException?.Message ?? exData.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Validation state logging failed at " + stage + ": " + ex.Message);
            }
        }

        private void LogPatternValidationFlags(string stage, object target)
        {
            if (target == null) return;

            try
            {
                Type type = target.GetType();
                string[] interfaces = type.GetInterfaces()
                    .Select(i => i.FullName ?? i.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .ToArray();

                Logger.Info("[PATTERN-DEBUG] Interfaces " + stage + ": " + string.Join(" | ", interfaces));

                var candidates = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Where(p => p.PropertyType == typeof(bool) ||
                                p.PropertyType == typeof(bool?) ||
                                p.PropertyType == typeof(string) ||
                                p.PropertyType.IsEnum)
                    .Where(p => MatchesValidationSignal(p.Name))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToArray();

                foreach (var prop in candidates)
                {
                    try
                    {
                        object value = prop.GetValue(target, null);
                        Logger.Info("[PATTERN-DEBUG] Flag " + stage + " " + prop.Name + "=" + DescribeValue(value));
                    }
                    catch (Exception exProp)
                    {
                        Logger.Info("[PATTERN-DEBUG] Flag " + stage + " " + prop.Name + "=<error " + exProp.GetType().Name + ": " + exProp.Message + ">");
                    }
                }

                LogNamedInterfaceProperties(stage, target, "Artech.Architecture.Common.Defaults.IApplyDefaultTarget");
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Validation flag logging failed at " + stage + ": " + ex.Message);
            }
        }

        private bool MatchesValidationSignal(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            string[] tokens =
            {
                "valid",
                "invalid",
                "dirty",
                "modified",
                "change",
                "generate",
                "regenerate",
                "update",
                "save",
                "default",
                "error",
                "open",
                "load",
                "state",
                "sync"
            };

            return tokens.Any(token => name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LogPatternStateMethods(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            if (resolvedObject == null) return;

            string[] propertyNames = { "LastInstanceGeneration", "LastInstanceUpdate", "LastInstanceCalculateDefault", "SaveOutput" };
            foreach (string propertyName in propertyNames)
            {
                object value = GetReadablePropertyValue(resolvedObject, propertyName);
                Logger.Info("[PATTERN-DEBUG] State property " + propertyName + "=" + DescribeValue(value));
            }
        }

        private void LogResolvedObjectDiagnostics(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            if (resolvedObject == null) return;

            try
            {
                Type objectType = resolvedObject.GetType();
                Logger.Info("[PATTERN-DEBUG] Resolved object type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(objectType)));
                Logger.Info("[PATTERN-DEBUG] Resolved object ctors: " + string.Join(" | ", objectType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(ctor => FormatConstructorSignature(ctor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
                Logger.Info("[PATTERN-DEBUG] Resolved object interesting properties: " + string.Join(" | ", GetInterestingPropertySignatures(objectType)));
                Logger.Info("[PATTERN-DEBUG] Resolved object interesting methods: " + string.Join(" | ", GetInterestingMethodSignatures(objectType)));

                string[] interestingMethodNames =
                {
                    "Validate",
                    "Save",
                    "Apply",
                    "Generate",
                    "Regenerate",
                    "Update",
                    "Synchronize",
                    "Refresh",
                    "CalculateDefault",
                    "LoadInstancePropertyDefinition"
                };

                foreach (string methodName in interestingMethodNames)
                {
                    var matches = objectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(m => m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(FormatMethodSignature)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(12)
                        .ToArray();

                    if (matches.Length > 0)
                    {
                        Logger.Info("[PATTERN-DEBUG] Resolved object methods matching '" + methodName + "': " + string.Join(" | ", matches));
                    }
                }

                TryLogPatternDefinitionObject(resolvedObject);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Resolved object diagnostic logging failed: " + ex.Message);
            }
        }

        private string TryGetMethodSignature(Type type, string methodName)
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
            return method == null ? "<missing>" : FormatMethodSignature(method);
        }

        private string FormatMethodSignature(MethodInfo method)
        {
            string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
            return method.ReturnType.Name + " " + method.Name + "(" + parameters + ")";
        }

        private string FormatConstructorSignature(ConstructorInfo ctor)
        {
            string parameters = string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
            return "CTOR(" + parameters + ")";
        }

        private IEnumerable<string> GetTypeHierarchy(Type type)
        {
            var current = type;
            while (current != null)
            {
                yield return current.FullName ?? current.Name;
                current = current.BaseType;
            }
        }

        private IEnumerable<string> GetInterestingPropertySignatures(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => IsInterestingMemberName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
        }

        private IEnumerable<string> GetInterestingFieldSignatures(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => IsInterestingMemberName(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => (f.FieldType?.Name ?? "<unknown>") + " " + f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
        }

        private IEnumerable<string> GetInterestingMethodSignatures(Type type)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => IsInterestingMemberName(m.Name))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(FormatMethodSignature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToArray();
        }

        private bool IsInterestingMemberName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            string[] tokens =
            {
                "pattern",
                "instance",
                "grid",
                "variable",
                "node",
                "item",
                "property",
                "model",
                "root",
                "xml",
                "data",
                "attribute",
                "control"
                ,"children"
            };

            return tokens.Any(token => name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LogInterestingPropertyValues(
            object instance,
            Type type,
            global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => IsInterestingMemberName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object value = prop.GetValue(instance, null);
                    Logger.Info("[PATTERN-DEBUG] Property value " + prop.Name + ": " + DescribeValue(value));
                    if (value != null && (string.Equals(prop.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "RootElement", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Attributes", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Children", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Objects", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(prop.Name, "Parent", StringComparison.OrdinalIgnoreCase)))
                    {
                        LogNestedPatternObjectDiagnostics(prop.Name, value, resolvedObject);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info("[PATTERN-DEBUG] Property value " + prop.Name + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
                }
            }
        }

        private string DescribeValue(object value)
        {
            if (value == null) return "<null>";

            if (value is string text)
            {
                string compact = text.Replace("\r", "\\r").Replace("\n", "\\n");
                return "String(len=" + text.Length + "): " + (compact.Length > 160 ? compact.Substring(0, 160) + "..." : compact);
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var sb = new StringBuilder();
                int count = 0;
                foreach (object item in enumerable)
                {
                    if (count >= 5) break;
                    if (count > 0) sb.Append(", ");
                    sb.Append(item == null ? "<null>" : item.GetType().FullName ?? item.GetType().Name);
                    count++;
                }
                return (value.GetType().FullName ?? value.GetType().Name) + "(sampleCount=" + count + (count > 0 ? "; items=" + sb : string.Empty) + ")";
            }

            return (value.GetType().FullName ?? value.GetType().Name) + ": " + value;
        }

        private void LogNestedPatternObjectDiagnostics(string label, object value, global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            var nestedType = value.GetType();
            Logger.Info("[PATTERN-DEBUG] " + label + " type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " properties: " + string.Join(" | ", GetInterestingPropertySignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " fields: " + string.Join(" | ", GetInterestingFieldSignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " methods: " + string.Join(" | ", GetInterestingMethodSignatures(nestedType)));
            if (string.Equals(label, "RootElement", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("[PATTERN-DEBUG] RootElement all properties: " + string.Join(" | ", nestedType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(p => p.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(60)));
                Logger.Info("[PATTERN-DEBUG] RootElement all fields: " + string.Join(" | ", nestedType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(60)));
            }

            foreach (var prop in nestedType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => IsInterestingMemberName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object nestedValue = prop.GetValue(value, null);
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": " + DescribeValue(nestedValue));
                    if (nestedValue != null && (string.Equals(prop.Name, "Attributes", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(prop.Name, "Children", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(prop.Name, "Objects", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(prop.Name, "Parent", StringComparison.OrdinalIgnoreCase)))
                    {
                        LogNestedAttributesDiagnostics(label + "." + prop.Name, nestedValue);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
                }
            }

            LogInterestingMethodResults(label, value, nestedType, resolvedObject);
        }

        private void LogInterestingMethodResults(string label, object value, Type nestedType, global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            TryLogMethodResult(label, value, nestedType, "GetDataUpdateProcess");
            TryLogMethodResult(label, value, nestedType, "GetPatternDefinition");

            var getPanelControls = nestedType.GetMethod("GetPanelControls", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getPanelControls != null)
            {
                TryLogMethodResult(label, value, getPanelControls, new object[] { resolvedObject });
            }

            var getVariablesSpec = nestedType.GetMethod("GetVariablesSpec", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getVariablesSpec != null)
            {
                TryLogMethodResult(label, value, getVariablesSpec, new object[] { resolvedObject });
            }
        }

        private void LogNestedAttributesDiagnostics(string label, object value)
        {
            var nestedType = value.GetType();
            Logger.Info("[PATTERN-DEBUG] " + label + " type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " properties: " + string.Join(" | ", GetInterestingPropertySignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " fields: " + string.Join(" | ", GetInterestingFieldSignatures(nestedType)));
            Logger.Info("[PATTERN-DEBUG] " + label + " methods: " + string.Join(" | ", GetInterestingMethodSignatures(nestedType)));

            foreach (var prop in nestedType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(25))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object nestedValue = prop.GetValue(value, null);
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": " + DescribeValue(nestedValue));
                }
                catch (Exception ex)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + prop.Name + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
                }
            }

            foreach (var method in nestedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.GetParameters().Length == 0)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Take(20))
            {
                if (method.ReturnType == typeof(void)) continue;
                try
                {
                    object result = method.Invoke(value, Array.Empty<object>());
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + method.Name + "(): " + DescribeValue(result));
                }
                catch
                {
                }
            }

            if (string.Equals(nestedType.FullName, "Artech.Packages.Patterns.Objects.PatternInstanceElementChildren", StringComparison.Ordinal))
            {
                LogChildrenSample(label, value);
            }
        }

        private void LogChildrenSample(string label, object value)
        {
            try
            {
                var enumerable = value as System.Collections.IEnumerable;
                if (enumerable == null) return;
                var items = new List<object>();
                foreach (object item in enumerable)
                {
                    if (item != null) items.Add(item);
                }

                int index = 0;
                foreach (object item in items)
                {
                    LogPatternChildElement(label + "[" + index + "]", item, 0);
                    index++;
                    if (index >= 5) break;
                }

                foreach (object item in items)
                {
                    SearchPatternChildElementForTargets(label, item, 0);
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] " + label + " sample logging failed: <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogPatternChildElement(string label, object element, int depth)
        {
            if (depth > 6 || element == null) return;

            Type type = element.GetType();
            Logger.Info("[PATTERN-DEBUG] " + label + " type: " + (type.FullName ?? type.Name));

            foreach (string propName in new[] { "Name", "TypeName", "Caption", "PropertyTitle", "Path", "InternalPath", "KeyValueString" })
            {
                try
                {
                    var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop == null || prop.GetIndexParameters().Length > 0) continue;
                    object propValue = prop.GetValue(element, null);
                    Logger.Info("[PATTERN-DEBUG] " + label + "." + propName + ": " + DescribeValue(propValue));
                }
                catch
                {
                }
            }

            if (ShouldLogElementAttributes(type, element))
            {
                TryLogTargetAttributes(label, type, element);
            }

            try
            {
                var childrenProp = type.GetProperty("Children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (childrenProp == null || childrenProp.GetIndexParameters().Length > 0) return;
                object children = childrenProp.GetValue(element, null);
                Logger.Info("[PATTERN-DEBUG] " + label + ".Children: " + DescribeValue(children));
                if (children is System.Collections.IEnumerable enumerable)
                {
                    int childIndex = 0;
                    foreach (object child in enumerable)
                    {
                        if (child == null) continue;
                        LogPatternChildElement(label + ".Children[" + childIndex + "]", child, depth + 1);
                        childIndex++;
                        if (childIndex >= 5) break;
                    }
                }

                string path = ReadStringProperty(type, element, "Path");
                string name = ReadStringProperty(type, element, "Name");
                if (string.Equals(name, "grid", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(path) && path.IndexOf("/grid[1]", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    LogAllGridChildren(label, children);
                }
            }
            catch
            {
            }
        }

        private bool ShouldLogElementAttributes(Type type, object element)
        {
            string name = ReadStringProperty(type, element, "Name");
            string path = ReadStringProperty(type, element, "Path");

            if (!string.IsNullOrEmpty(path) &&
                (path.IndexOf("/table", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 path.IndexOf("/grid", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(name, "table", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "grid", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "gridVariable", StringComparison.OrdinalIgnoreCase);
        }

        private void SearchPatternChildElementForTargets(string label, object element, int depth)
        {
            if (depth > 8 || element == null) return;

            Type type = element.GetType();
            string name = ReadStringProperty(type, element, "Name");
            string path = ReadStringProperty(type, element, "Path");
            string propertyTitle = ReadStringProperty(type, element, "PropertyTitle");
            string keyValue = ReadStringProperty(type, element, "KeyValueString");
            object attributes = GetReadablePropertyValue(element, "Attributes");
            string attributeName = TryGetPatternAttributeValue(attributes, "name");

            if (string.Equals(name, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attributeName, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attributeName, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyValue, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyValue, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyTitle, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(propertyTitle, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(path) && (path.IndexOf("HorasDebito", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 path.IndexOf("SedCPHor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 path.IndexOf("TableGrid", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-NODE " + label + " depth=" + depth + " type=" + (type.FullName ?? type.Name));
                Logger.Info("[PATTERN-DEBUG] TARGET-NODE Name=" + name + "; AttributeName=" + attributeName + "; PropertyTitle=" + propertyTitle + "; KeyValueString=" + keyValue + "; Path=" + path);
                LogPatternElementHierarchy(label, element);
                TryLogTargetAttributes(label, type, element);
                LogPatternElementObjects(label, type, element);
            }

            try
            {
                var childrenProp = type.GetProperty("Children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (childrenProp == null || childrenProp.GetIndexParameters().Length > 0) return;
                object children = childrenProp.GetValue(element, null);
                if (children is System.Collections.IEnumerable enumerable)
                {
                    int childIndex = 0;
                    foreach (object child in enumerable)
                    {
                        if (child == null) continue;
                        SearchPatternChildElementForTargets(label + ".Children[" + childIndex + "]", child, depth + 1);
                        childIndex++;
                    }
                }
            }
            catch
            {
            }
        }

        private void TryLogTargetAttributes(string label, Type type, object element)
        {
            try
            {
                var attributesProp = type.GetProperty("Attributes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (attributesProp == null || attributesProp.GetIndexParameters().Length > 0) return;
                object attributes = attributesProp.GetValue(element, null);
                if (attributes == null) return;
                Logger.Info("[PATTERN-DEBUG] TARGET-ATTRIBUTES " + label + ": " + DescribeValue(attributes));
                LogNestedAttributesDiagnostics(label + ".Attributes", attributes);
                LogAttributeProperties(label + ".Attributes", attributes);
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-ATTRIBUTES " + label + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogPatternElementHierarchy(string label, object element)
        {
            try
            {
                var segments = new List<string>();
                object current = element;
                int depth = 0;
                while (current != null && depth < 12)
                {
                    Type currentType = current.GetType();
                    string currentName = FirstNonEmpty(
                        ReadStringProperty(currentType, current, "Name"),
                        ReadStringProperty(currentType, current, "PropertyTitle"),
                        ReadStringProperty(currentType, current, "KeyValueString"),
                        currentType.Name);
                    string currentPath = ReadStringProperty(currentType, current, "Path");
                    segments.Add(currentName + " {" + currentPath + "}");

                    var parentProp = currentType.GetProperty("Parent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (parentProp == null || parentProp.GetIndexParameters().Length > 0)
                    {
                        break;
                    }

                    current = parentProp.GetValue(current, null);
                    depth++;
                }

                if (segments.Count > 0)
                {
                    Logger.Info("[PATTERN-DEBUG] TARGET-HIERARCHY " + label + ": " + string.Join(" <= ", segments));
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-HIERARCHY " + label + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogPatternElementObjects(string label, Type type, object element)
        {
            try
            {
                var objectsProp = type.GetProperty("Objects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (objectsProp == null || objectsProp.GetIndexParameters().Length > 0) return;

                object objects = objectsProp.GetValue(element, null);
                Logger.Info("[PATTERN-DEBUG] TARGET-OBJECTS " + label + ": " + DescribeValue(objects));
                if (!(objects is System.Collections.IEnumerable enumerable)) return;

                int index = 0;
                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    Logger.Info("[PATTERN-DEBUG] TARGET-OBJECTS " + label + "[" + index + "]=" + DescribeValue(item));
                    index++;
                    if (index >= 10) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] TARGET-OBJECTS " + label + ": <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogAllGridChildren(string label, object children)
        {
            if (!(children is System.Collections.IEnumerable enumerable)) return;

            int index = 0;
            foreach (object child in enumerable)
            {
                if (child == null) continue;
                Type childType = child.GetType();
                string childPath = ReadStringProperty(childType, child, "Path");
                string childName = ReadStringProperty(childType, child, "Name");
                string childTitle = ReadStringProperty(childType, child, "PropertyTitle");
                Logger.Info("[PATTERN-DEBUG] GRID-CHILD " + label + "[" + index + "] Name=" + childName + "; PropertyTitle=" + childTitle + "; Path=" + childPath);
                TryLogTargetAttributes(label + "[" + index + "]", childType, child);
                index++;
                if (index >= 40) break;
            }
        }

        private void LogAttributeProperties(string label, object attributes)
        {
            try
            {
                var attrType = attributes.GetType();
                var propertiesProp = attrType.GetProperty("Properties", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (propertiesProp == null || propertiesProp.GetIndexParameters().Length > 0) return;
                object properties = propertiesProp.GetValue(attributes, null);
                if (!(properties is System.Collections.IEnumerable enumerable)) return;

                int count = 0;
                foreach (object prop in enumerable)
                {
                    if (prop == null) continue;
                    Type propType = prop.GetType();
                    string name = ReadStringProperty(propType, prop, "Name");
                    string value = ReadStringProperty(propType, prop, "Value");
                    Logger.Info("[PATTERN-DEBUG] ATTR-PROP " + label + " " + name + "=" + value);
                    count++;
                    if (count >= 40) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] ATTR-PROP " + label + " <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private string ReadStringProperty(Type type, object instance, string propertyName)
        {
            try
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null || prop.GetIndexParameters().Length > 0) return null;
                object value = prop.GetValue(instance, null);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void TryLogMethodResult(string label, object value, Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return;
            TryLogMethodResult(label, value, method, Array.Empty<object>());
        }

        private void TryLogMethodResult(string label, object value, MethodInfo method, object[] args)
        {
            try
            {
                object result = method.Invoke(value, args);
                Logger.Info("[PATTERN-DEBUG] " + label + "." + method.Name + "(): " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Info("[PATTERN-DEBUG] " + label + "." + method.Name + "(): <error " + ex.GetType().Name + ": " + ex.Message + ">");
            }
        }

        private void LogNamedInterfaceProperties(string stage, object target, string interfaceFullName)
        {
            if (target == null || string.IsNullOrWhiteSpace(interfaceFullName)) return;

            try
            {
                Type iface = target.GetType().GetInterfaces()
                    .FirstOrDefault(i => string.Equals(i.FullName, interfaceFullName, StringComparison.OrdinalIgnoreCase));
                if (iface == null) return;

                foreach (var prop in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        object value = prop.GetValue(target, null);
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + prop.Name + "=" + DescribeValue(value));
                    }
                    catch (Exception exProp)
                    {
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + prop.Name + "=<error " + exProp.GetType().Name + ": " + exProp.Message + ">");
                    }
                }

                foreach (var method in iface.GetMethods()
                    .Where(m => m.GetParameters().Length == 0 && m.ReturnType != typeof(void))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        object result = method.Invoke(target, null);
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + method.Name + "()=" + DescribeValue(result));
                    }
                    catch (Exception exMethod)
                    {
                        Logger.Info("[PATTERN-DEBUG] Interface " + stage + " " + iface.Name + "." + method.Name + "()=<error " + exMethod.GetType().Name + ": " + exMethod.Message + ">");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Interface logging failed for " + interfaceFullName + " at " + stage + ": " + ex.Message);
            }
        }

        private void TryInvokeInterfaceMethodByName(object target, string interfaceFullName, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(interfaceFullName) || string.IsNullOrWhiteSpace(methodName)) return;

            try
            {
                Type iface = target.GetType().GetInterfaces()
                    .FirstOrDefault(i => string.Equals(i.FullName, interfaceFullName, StringComparison.OrdinalIgnoreCase));
                if (iface == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Interface method not found because interface is missing: " + interfaceFullName + "." + methodName);
                    return;
                }

                MethodInfo method = iface.GetMethods()
                    .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == args.Length);
                if (method == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Interface method not found: " + interfaceFullName + "." + methodName);
                    return;
                }

                object result = method.Invoke(target, args);
                Logger.Info("[PATTERN-DEBUG] Interface invoke " + iface.Name + "." + method.Name + "() => " + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Interface method failed " + interfaceFullName + "." + methodName + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogPatternDefinitionObject(object patternInstance)
        {
            if (patternInstance == null) return;

            try
            {
                MethodInfo getPatternDefinition = patternInstance.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "GetPatternDefinition", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (getPatternDefinition == null) return;

                object definition = getPatternDefinition.Invoke(patternInstance, null);
                if (definition == null)
                {
                    Logger.Info("[PATTERN-DEBUG] GetPatternDefinition()=<null>");
                    return;
                }

                Type defType = definition.GetType();
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(defType)));
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() ctors: " + string.Join(" | ", defType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(ctor => FormatConstructorSignature(ctor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() properties: " + string.Join(" | ", defType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(40)));
                Logger.Info("[PATTERN-DEBUG] GetPatternDefinition() methods: " + string.Join(" | ", defType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("validator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("implement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(60)));

                TryLogPatternImplementationObject(definition);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] GetPatternDefinition() logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogPatternImplementationObject(object definition)
        {
            if (definition == null) return;

            try
            {
                PropertyInfo implProp = definition.GetType().GetProperty("PatternImplementation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object implementation = implProp?.GetValue(definition, null);
                if (implementation == null)
                {
                    Logger.Info("[PATTERN-DEBUG] PatternImplementation=<null>");
                    return;
                }

                Type implType = implementation.GetType();
                Logger.Info("[PATTERN-DEBUG] PatternImplementation type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(implType)));
                Logger.Info("[PATTERN-DEBUG] PatternImplementation ctors: " + string.Join(" | ", implType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(ctor => FormatConstructorSignature(ctor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
                Logger.Info("[PATTERN-DEBUG] PatternImplementation properties: " + string.Join(" | ", implType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(40)));
                Logger.Info("[PATTERN-DEBUG] PatternImplementation methods: " + string.Join(" | ", implType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("validator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("setting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(80)));

                foreach (string methodName in new[]
                {
                    "GetInstanceValidator",
                    "GetInstanceUpdateProcess",
                    "GetInstanceVersionAdapter",
                    "GetInstanceOneSource",
                    "GetInstanceSources",
                    "GetInstanceEditorHelper",
                    "GetSettingsValidator",
                    "GetSettingsUpdateProcess",
                    "GetSettingsVersionAdapter",
                    "GetSettingsEditorHelper"
                })
                {
                    try
                    {
                        MethodInfo method = implType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                        if (method == null)
                        {
                            Logger.Info("[PATTERN-DEBUG] PatternImplementation." + methodName + "()=<missing>");
                            continue;
                        }

                        object result = method.Invoke(implementation, null);
                        Logger.Info("[PATTERN-DEBUG] PatternImplementation." + methodName + "()=" + DescribeValue(result));
                        if (result != null &&
                            (methodName.IndexOf("EditorHelper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             methodName.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            LogPatternImplementationResultObject("PatternImplementation." + methodName + "()", result);
                        }
                    }
                    catch (Exception exMethod)
                    {
                        Logger.Warn("[PATTERN-DEBUG] PatternImplementation." + methodName + "() failed: " + (exMethod.InnerException?.Message ?? exMethod.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] PatternImplementation logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogPatternImplementationResultObject(string label, object value)
        {
            if (value == null) return;

            try
            {
                Type type = value.GetType();
                Logger.Info("[PATTERN-DEBUG] " + label + " type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(type)));
                Logger.Info("[PATTERN-DEBUG] " + label + " properties: " + string.Join(" | ", type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(p => p.Name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(60)));
                Logger.Info("[PATTERN-DEBUG] " + label + " methods: " + string.Join(" | ", type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(FormatMethodSignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(80)));

                MethodInfo createEditors = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "CreateEditors", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
                if (createEditors != null)
                {
                    try
                    {
                        object editors = createEditors.Invoke(value, null);
                        Logger.Info("[PATTERN-DEBUG] " + label + ".CreateEditors()=" + DescribeValue(editors));
                        if (editors is System.Collections.IEnumerable enumerable)
                        {
                            int index = 0;
                            foreach (object item in enumerable)
                            {
                                if (item == null) continue;
                                Type itemType = item.GetType();
                                Logger.Info("[PATTERN-DEBUG] " + label + ".CreateEditors()[" + index + "] type hierarchy: " + string.Join(" -> ", GetTypeHierarchy(itemType)));
                                Logger.Info("[PATTERN-DEBUG] " + label + ".CreateEditors()[" + index + "] properties: " + string.Join(" | ", itemType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    .Where(p => p.Name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                p.Name.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
                                    .Select(p => (p.PropertyType?.Name ?? "<unknown>") + " " + p.Name)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                    .Take(60)));
                                index++;
                                if (index >= 15) break;
                            }
                        }
                    }
                    catch (Exception exCreate)
                    {
                        Exception root = exCreate is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : exCreate;
                        Logger.Warn("[PATTERN-DEBUG] " + label + ".CreateEditors() failed: " + root.GetType().FullName + ": " + root.Message);
                        Logger.Warn("[PATTERN-DEBUG] " + label + ".CreateEditors() stack: " + (root.StackTrace ?? "<no-stack>"));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + " diagnostics failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryLogSemanticWorkWithPlusInstance(global::Artech.Architecture.Common.Objects.KBObject resolvedObject)
        {
            if (resolvedObject == null) return;

            WritePatternDebugTrace("TryLogSemanticWorkWithPlusInstance object=" + resolvedObject.Name + " type=" + (resolvedObject.TypeDescriptor?.Name ?? "<null>"));

            try
            {
                Type semanticInstanceType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly => assembly.GetType("DVelop.Patterns.WorkWithPlus.WorkWithPlusInstance", false, true))
                    .FirstOrDefault(type => type != null);
                if (semanticInstanceType == null)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic instance type not loaded.");
                    return;
                }

                ConstructorInfo ctor = semanticInstanceType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var parameters = c.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(resolvedObject);
                    });
                if (ctor == null)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic instance ctor not compatible with resolved object type.");
                    return;
                }

                object semanticInstance = ctor.Invoke(new object[] { resolvedObject });
                Logger.Info("[PATTERN-DEBUG] Semantic instance created: " + DescribeValue(semanticInstance));
                WritePatternDebugTrace("Semantic instance created=" + DescribeValue(semanticInstance));
                TryInvokeSemanticInitialize("Semantic instance", semanticInstance);

                LogSemanticGridSettings(semanticInstance);
                LogSemanticGridTargets(semanticInstance, "Instance");
                LogWorkWithPlusSemanticTypeCandidates();

                object settings = GetReadablePropertyValue(semanticInstance, "Settings");
                if (settings != null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings object: " + DescribeValue(settings));
                    WritePatternDebugTrace("Semantic settings object=" + DescribeValue(settings));
                    TryInvokeSemanticInitialize("Semantic settings", settings);
                    LogSemanticGridTargets(settings, "Settings");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Semantic WorkWithPlus logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogSemanticGridSettings(object semanticInstance)
        {
            if (semanticInstance == null) return;

            try
            {
                object settings = GetReadablePropertyValue(semanticInstance, "Settings");
                if (settings == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings unavailable.");
                    return;
                }

                MethodInfo getAllChildren = settings.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings GetAllChildren() unavailable.");
                    return;
                }

                var allChildren = getAllChildren.Invoke(settings, null) as System.Collections.IEnumerable;
                Logger.Info("[PATTERN-DEBUG] Semantic settings GetAllChildren count=" + CountEnumerable(allChildren));
                WritePatternDebugTrace("Semantic settings GetAllChildren count=" + CountEnumerable(allChildren));
                LogSemanticChildTypeSummary("Semantic settings", allChildren);
                if (allChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic settings returned no children.");
                    WritePatternDebugTrace("Semantic settings returned no children.");
                    return;
                }

                foreach (object child in EnumerateSemanticItems(allChildren))
                {
                    if (child == null) continue;
                    if (!string.Equals(child.GetType().FullName, "DVelop.Patterns.WorkWithPlus.SettingsGridElement", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Logger.Info("[PATTERN-DEBUG] Semantic settings grid: " + DescribeSemanticElement(child));
                    Logger.Info("[PATTERN-DEBUG] Semantic settings grid AlwaysUseColumnTitleProperty=" + DescribeValue(GetReadablePropertyValue(child, "AlwaysUseColumnTitleProperty")));
                    WritePatternDebugTrace("Semantic settings grid=" + DescribeSemanticElement(child));
                    WritePatternDebugTrace("Semantic settings grid AlwaysUseColumnTitleProperty=" + DescribeValue(GetReadablePropertyValue(child, "AlwaysUseColumnTitleProperty")));
                    LogSemanticTypeSurface("Semantic settings grid surface", child);
                    TryProbeSemanticGridLookup("Semantic settings grid lookup", child);
                    LogSemanticNestedChildren("Semantic settings grid", child);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Semantic settings logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogSemanticGridTargets(object semanticRoot, string label)
        {
            if (semanticRoot == null) return;

            try
            {
                MethodInfo getAllChildren = semanticRoot.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " GetAllChildren() unavailable.");
                    return;
                }

                var allChildren = getAllChildren.Invoke(semanticRoot, null) as System.Collections.IEnumerable;
                Logger.Info("[PATTERN-DEBUG] Semantic " + label + " GetAllChildren count=" + CountEnumerable(allChildren));
                WritePatternDebugTrace("Semantic " + label + " GetAllChildren count=" + CountEnumerable(allChildren));
                LogSemanticChildTypeSummary("Semantic " + label, allChildren);
                if (allChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " returned no children.");
                    WritePatternDebugTrace("Semantic " + label + " returned no children.");
                    return;
                }

                foreach (object child in EnumerateSemanticItems(allChildren))
                {
                    if (child == null) continue;
                    Type childType = child.GetType();
                    if (string.Equals(childType.FullName, "DVelop.Patterns.WorkWithPlus.WPGridElement", StringComparison.Ordinal) ||
                        string.Equals(childType.FullName, "DVelop.Patterns.WorkWithPlus.SettingsGridWPElement", StringComparison.Ordinal))
                    {
                        Logger.Info("[PATTERN-DEBUG] Semantic " + label + " grid: " + DescribeSemanticElement(child));
                        WritePatternDebugTrace("Semantic " + label + " grid=" + DescribeSemanticElement(child));
                        LogSemanticTypeSurface("Semantic " + label + " grid surface", child);
                        TryProbeSemanticGridLookup("Semantic " + label + " grid lookup", child);
                    }

                    string name = GetReadablePropertyValue(child, "Name")?.ToString();
                    string attributeName = GetReadablePropertyValue(child, "AttributeName")?.ToString();
                    bool isTarget = string.Equals(name, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(name, "SedCPHor", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(attributeName, "HorasDebito", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(attributeName, "SedCPHor", StringComparison.OrdinalIgnoreCase);
                    if (!isTarget)
                    {
                        continue;
                    }

                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " target: " + DescribeSemanticElement(child));
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " target Description=" + DescribeValue(GetReadablePropertyValue(child, "Description")));
                    Logger.Info("[PATTERN-DEBUG] Semantic " + label + " target Visible=" + DescribeValue(GetReadablePropertyValue(child, "Visible")));
                    WritePatternDebugTrace("Semantic " + label + " target=" + DescribeSemanticElement(child));
                    WritePatternDebugTrace("Semantic " + label + " target Description=" + DescribeValue(GetReadablePropertyValue(child, "Description")));
                    WritePatternDebugTrace("Semantic " + label + " target Visible=" + DescribeValue(GetReadablePropertyValue(child, "Visible")));
                    LogSemanticTypeSurface("Semantic " + label + " target surface", child);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] Semantic " + label + " logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogSemanticNestedChildren(string label, object semanticElement)
        {
            if (semanticElement == null) return;

            try
            {
                MethodInfo getAllChildren = semanticElement.GetType().GetMethod("GetAllChildren", BindingFlags.Public | BindingFlags.Instance);
                if (getAllChildren == null)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + " GetAllChildren() unavailable.");
                    return;
                }

                var nestedChildren = getAllChildren.Invoke(semanticElement, null) as System.Collections.IEnumerable;
                Logger.Info("[PATTERN-DEBUG] " + label + " GetAllChildren count=" + CountEnumerable(nestedChildren));
                WritePatternDebugTrace(label + " GetAllChildren count=" + CountEnumerable(nestedChildren));
                LogSemanticChildTypeSummary(label, nestedChildren);
                LogSemanticGridTargets(semanticElement, label);
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + " nested logging failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void LogWorkWithPlusSemanticTypeCandidates()
        {
            try
            {
                Assembly workWithPlusAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(assembly =>
                        assembly.GetName().Name != null &&
                        assembly.GetName().Name.IndexOf("WorkWithPlus", StringComparison.OrdinalIgnoreCase) >= 0);

                if (workWithPlusAssembly == null)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus assembly not loaded for candidate scan.");
                    return;
                }

                var interesting = workWithPlusAssembly.GetTypes()
                    .Where(type =>
                        type.FullName != null &&
                        (type.FullName.IndexOf("Change", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Merge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Comparer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Command", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.FullName.IndexOf("Grid", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(type => type.FullName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Take(120)
                    .ToList();

                Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic candidate types count=" + interesting.Count);
                foreach (string typeName in interesting)
                {
                    Logger.Info("[PATTERN-DEBUG] WorkWithPlus semantic candidate type=" + typeName);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] WorkWithPlus candidate scan failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void TryInvokeSemanticInitialize(string label, object instance)
        {
            if (instance == null) return;

            try
            {
                MethodInfo initialize = instance.GetType().GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (initialize == null)
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + " Initialize() unavailable.");
                    return;
                }

                object result = initialize.Invoke(instance, null);
                Logger.Info("[PATTERN-DEBUG] " + label + ".Initialize()=" + DescribeValue(result));
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + ".Initialize() failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private int CountEnumerable(System.Collections.IEnumerable enumerable)
        {
            if (enumerable == null) return -1;

            int count = 0;
            foreach (object _ in EnumerateSemanticItems(enumerable))
            {
                count++;
                if (count >= 5000) break;
            }

            return count;
        }

        private void LogSemanticChildTypeSummary(string label, System.Collections.IEnumerable enumerable)
        {
            if (enumerable == null) return;

            try
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                int seen = 0;
                foreach (object item in EnumerateSemanticItems(enumerable))
                {
                    if (item == null) continue;
                    string typeName = item.GetType().FullName ?? item.GetType().Name;
                    counts[typeName] = counts.TryGetValue(typeName, out int current) ? current + 1 : 1;
                    seen++;
                    if (seen >= 5000) break;
                }

                foreach (var entry in counts.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                {
                    Logger.Info("[PATTERN-DEBUG] " + label + " child-type " + entry.Key + " count=" + entry.Value);
                    WritePatternDebugTrace(label + " child-type " + entry.Key + " count=" + entry.Value);
                }

                int sampleIndex = 0;
                foreach (object item in EnumerateSemanticItems(enumerable))
                {
                    if (item == null) continue;
                    Logger.Info("[PATTERN-DEBUG] " + label + " child-sample[" + sampleIndex + "] " + DescribeSemanticElement(item));
                    WritePatternDebugTrace(label + " child-sample[" + sampleIndex + "] " + DescribeSemanticElement(item));
                    sampleIndex++;
                    if (sampleIndex >= 20) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[PATTERN-DEBUG] " + label + " child-type summary failed: " + ex.Message);
            }
        }

        private IEnumerable<object> EnumerateSemanticItems(System.Collections.IEnumerable enumerable)
        {
            if (enumerable == null) yield break;

            foreach (object item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                if (item is string)
                {
                    yield return item;
                    continue;
                }

                if (item is System.Collections.IEnumerable nested)
                {
                    foreach (object nestedItem in EnumerateSemanticItems(nested))
                    {
                        if (nestedItem == null) continue;
                        yield return nestedItem;
                    }

                    continue;
                }

                yield return item;
            }
        }

        private string DescribeSemanticElement(object instance)
        {
            if (instance == null) return "<null>";

            try
            {
                string typeName = instance.GetType().FullName ?? instance.GetType().Name;
                string name = GetReadablePropertyValue(instance, "Name")?.ToString();
                string attributeName = GetReadablePropertyValue(instance, "AttributeName")?.ToString();
                string description = GetReadablePropertyValue(instance, "Description")?.ToString();
                string visible = DescribeValue(GetReadablePropertyValue(instance, "Visible"));
                string element = DescribeValue(GetReadablePropertyValue(instance, "Element"));
                return typeName + " Name=" + (name ?? "<null>") + " AttributeName=" + (attributeName ?? "<null>") + " Description=" + (description ?? "<null>") + " Visible=" + visible + " Element=" + element;
            }
            catch (Exception ex)
            {
                return "<error " + ex.GetType().Name + ": " + ex.Message + ">";
            }
        }

        private void LogSemanticTypeSurface(string label, object target)
        {
            if (target == null) return;

            try
            {
                Type type = target.GetType();
                string typeName = type.FullName ?? type.Name;
                WritePatternDebugTrace(label + " type=" + typeName);
                WritePatternDebugTrace(label + " properties=" + string.Join(" | ",
                    type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Select(prop => prop.Name + ":" + (prop.PropertyType.Name ?? "<null>"))
                        .Take(80)));
                WritePatternDebugTrace(label + " methods=" + string.Join(" | ",
                    type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(method => !method.IsSpecialName)
                        .Select(method => FormatMethodSignature(method))
                        .Take(80)));
            }
            catch (Exception ex)
            {
                WritePatternDebugTrace(label + " surface error=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TryProbeSemanticGridLookup(string label, object target)
        {
            if (target == null) return;

            try
            {
                Type type = target.GetType();
                MethodInfo findMethod = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "FindWPGridAttribute", StringComparison.OrdinalIgnoreCase) &&
                        method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType == typeof(string));

                if (findMethod == null)
                {
                    WritePatternDebugTrace(label + " FindWPGridAttribute=<missing>");
                    return;
                }

                foreach (string targetName in new[] { "HorasDebito", "SedCPHor" })
                {
                    object result = findMethod.Invoke(target, new object[] { targetName });
                    WritePatternDebugTrace(label + " FindWPGridAttribute(" + targetName + ")=" + DescribeValue(result));
                    if (result != null)
                    {
                        LogSemanticTypeSurface(label + " result " + targetName, result);
                    }
                }
            }
            catch (Exception ex)
            {
                WritePatternDebugTrace(label + " lookup error=" + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private void WritePatternDebugTrace(string message)
        {
            string flag = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG");
            if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(message)) return;

            try
            {
                string directory = ResolvePatternDebugDirectory();
                Directory.CreateDirectory(directory);
                string filePath = Path.Combine(directory, "wwp_semantic_debug.txt");
                File.AppendAllText(filePath, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private string ResolvePatternDebugDirectory()
        {
            string configuredDirectory = Environment.GetEnvironmentVariable("GX_MCP_PATTERN_DEBUG_DIR");
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                return configuredDirectory;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".tmp");
        }

        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.1 — EOL-normalized matching helpers (friction-report #4)
        // ----------------------------------------------------------------------
        // Source bytes are preserved on disk; only the comparison is normalized.
        // CRLF/LF are unified and per-line trailing whitespace is trimmed before
        // matching. TryMatch returns indices into the ORIGINAL (non-normalized)
        // source so callers can splice in replacements without corrupting EOLs.

        internal static string NormalizeForCompare(string s)
        {
            if (s == null) return null;
            var lines = s.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            return string.Join("\n", lines);
        }

        internal static bool TryMatch(string source, string context, out int startIdx, out int endIdx)
        {
            startIdx = endIdx = -1;
            if (source == null || context == null) return false;
            var normSource = NormalizeForCompare(source);
            var normCtx = NormalizeForCompare(context);
            if (normCtx.Length == 0) return false;
            int normIdx = normSource.IndexOf(normCtx, StringComparison.Ordinal);
            if (normIdx < 0) return false;

            int targetLineStart = CountLinesBefore(normSource, normIdx);
            // Walk to the start of the target line in the original source.
            int origPos = 0;
            for (int line = 0; line < targetLineStart && origPos < source.Length; line++)
            {
                int nl = source.IndexOfAny(new[] { '\r', '\n' }, origPos);
                if (nl < 0) { origPos = source.Length; break; }
                origPos = nl + ((source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n') ? 2 : 1);
            }

            // Compute column within the normalized line where match starts.
            int prevNL = normSource.LastIndexOf('\n', Math.Max(0, normIdx - 1));
            int normLineStart = prevNL < 0 ? 0 : prevNL + 1;
            int colOffset = normIdx - normLineStart;
            startIdx = Math.Min(source.Length, origPos + colOffset);

            // Walk forward over (ctxLineCount) lines to find the end position in the original source.
            int ctxLineCount = CountLinesBefore(normCtx, normCtx.Length);
            int walker = startIdx;
            for (int i = 0; i < ctxLineCount && walker < source.Length; i++)
            {
                int nl = source.IndexOfAny(new[] { '\r', '\n' }, walker);
                if (nl < 0) { walker = source.Length; break; }
                walker = nl + ((source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n') ? 2 : 1);
            }

            // Add the residual column length on the last context line.
            int lastNL = normCtx.LastIndexOf('\n');
            int lastLineLen = lastNL < 0 ? normCtx.Length : (normCtx.Length - lastNL - 1);
            endIdx = Math.Min(source.Length, walker + lastLineLen);
            if (endIdx < startIdx) endIdx = startIdx;
            return true;
        }

        private static int CountLinesBefore(string s, int idx)
        {
            int c = 0;
            int limit = Math.Min(idx, s.Length);
            for (int i = 0; i < limit; i++) if (s[i] == '\n') c++;
            return c;
        }

        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.4 — persistedHash + persistedSnippet on every response
        // ----------------------------------------------------------------------
        // Every write/edit response is wrapped with the SHA256 of the final
        // on-disk source plus a ~10-line snippet, so callers can confirm
        // post-write state without a follow-up read. Applies uniformly to
        // success, no-change, dry-run, rollback, and error responses.

        internal static string ComputeSha256(string content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? ""));
                return "sha256:" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static string ExtractSnippet(string source, int lineHint, int contextLines = 10)
        {
            if (string.IsNullOrEmpty(source)) return "";
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var start = Math.Max(0, lineHint - contextLines);
            var end = Math.Min(lines.Length, lineHint + contextLines + 1);
            if (end <= start) return "";
            return string.Join("\n", lines.Skip(start).Take(end - start));
        }

        internal static JObject AppendPersistedState(JObject response, string finalSource, int? editLine)
        {
            if (response == null) response = new JObject();
            response["persistedHash"] = ComputeSha256(finalSource ?? "");
            response["persistedSnippet"] = ExtractSnippet(finalSource ?? "", editLine ?? 0, 10);
            return response;
        }

        /// <summary>
        /// Wraps a write-response JSON string with persistedHash + persistedSnippet derived
        /// from the on-disk source after the write attempt (success, partial, or rollback).
        /// Failures to re-read are swallowed — the original envelope is still augmented with
        /// an empty hash/snippet so downstream parsers always find the keys.
        /// </summary>
        private string WrapWithPersistedState(string responseJson, string target, string partName, string sdkPath = null)
        {
            JObject parsed = null;
            try { parsed = JObject.Parse(responseJson); }
            catch
            {
                parsed = new JObject { ["raw"] = responseJson ?? "" };
            }

            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(parsed, sdkPath);

            // Skip if the response is already decorated (e.g. nested call).
            if (parsed["persistedHash"] != null && parsed["persistedSnippet"] != null)
                return parsed.ToString();

            string finalSource = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(target) && _objectService != null)
                {
                    string readJson = _objectService.ReadObjectSource(target, partName, null, null, "mcp", true, null);
                    if (!string.IsNullOrWhiteSpace(readJson))
                    {
                        var readObj = JObject.Parse(readJson);
                        finalSource = readObj["source"]?.ToString()
                            ?? readObj["content"]?.ToString()
                            ?? readObj["parts"]?[partName ?? "Source"]?.ToString()
                            ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[PERSISTED-STATE] Re-read failed for " + target + " (" + partName + "): " + ex.Message);
            }

            AppendPersistedState(parsed, finalSource, null);
            return parsed.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
