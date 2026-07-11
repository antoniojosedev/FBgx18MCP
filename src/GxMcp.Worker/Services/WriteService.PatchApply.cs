using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Semantic-ops (structured) and JSON-Patch write entry points extracted from
    // WriteService.cs (plan 007). Pure move, no logic changes — see
    // plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
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
                // v2.6.6 FR#13 — validate mode plumbing. Default "strict" preserves
                // the v2.6.5 abort-on-first-failure semantics so existing callers are
                // unaffected.
                string validate = req["validate"]?.ToString();

                if (string.IsNullOrEmpty(target))
                    throw new UsageException("usage_error", "target required");
                if (opsRaw == null || opsRaw.Count == 0)
                    throw new UsageException("usage_error", "ops[] required");
                if (string.IsNullOrEmpty(partName))
                    partName = "Structure";

                // Pre-flight: reject immediately when no KB is open, before JIT-loading GeneXus types.
                if (!_objectService.GetKbService().IsOpen)
                    throw new UsageException("usage_error", "object '" + target + "' not found");

                return ApplySemanticOpsCore(target, partName, opsRaw, dryRun, returnPostState, verbose, validate);
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
        private string ApplySemanticOpsCore(string target, string partName, JArray opsRaw, bool dryRun, bool returnPostState = true, bool verbose = false, string validate = null)
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

            // v2.6.6 FR#13 — validate mode dispatch. The legacy Apply() path is
            // preserved when validate is unset (or "strict") AND every op succeeds,
            // so the resulting XML is byte-identical to v2.6.5.
            string mode = SemanticOpsService.NormalizeMode(validate);
            SemanticOpsService.OpsApplyOutcome outcome;
            try
            {
                outcome = new SemanticOpsService().ApplyWithResults(currentXml, kind, ops, mode);
            }
            catch (UsageException) when (mode != "strict")
            {
                outcome = new SemanticOpsService.OpsApplyOutcome
                {
                    Xml = currentXml,
                    Results = new System.Collections.Generic.List<SemanticOpsService.OpResult>(),
                    Aborted = true,
                    Mode = mode
                };
            }
            string newXml = outcome.Xml;
            int okCount = outcome.Results.Count(r => r.Ok);

            // strict + aborted → bubble the original failure for backwards compat.
            if (mode == "strict" && outcome.Aborted)
            {
                var failed = outcome.Results.FirstOrDefault(r => !r.Ok);
                throw new UsageException(failed?.Code ?? "usage_error",
                    failed?.Reason ?? "op failed");
            }

            var opResultsJson = new JArray();
            foreach (var r in outcome.Results) opResultsJson.Add(r.ToJson());

            // validate=only → never persist; return diagnostics only.
            if (mode == "only" || dryRun)
            {
                var envelope = DryRunPlanBuilder.BuildEnvelope(target, currentXml, newXml, "ops");
                JObject env;
                try { env = JObject.Parse(envelope.ToString()); }
                catch { env = new JObject { ["raw"] = envelope.ToString() }; }
                env["validate"] = mode;
                env["opResults"] = opResultsJson;
                env["opsApplied"] = okCount;
                env["opsTotal"] = ops.Count;
                if (returnPostState)
                    env["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose);
                return env.ToString(Newtonsoft.Json.Formatting.None);
            }

            string writeResult = WriteObject(target, partName, newXml, null, false, false, false, false);
            JObject writeJson;
            try { writeJson = JObject.Parse(writeResult); }
            catch { writeJson = new JObject { ["raw"] = writeResult }; }

            // v2.6.6 FR#12 — re-read persisted bytes AFTER the SDK commits so
            // return_post_state slices reflect on-disk reality, not the
            // in-memory write buffer that the v2.6.4 regression captured.
            var writeStatus = writeJson["status"]?.ToString();
            bool writeOk = string.Equals(writeStatus, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "Ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(writeStatus, "partial", StringComparison.OrdinalIgnoreCase);
            string persistedAfter = writeOk ? ReadPersistedPartSafely(target, partName) : null;

            var resp = new JObject
            {
                ["isError"] = false,
                ["target"] = target,
                ["part"] = partName,
                ["mode"] = "ops",
                ["validate"] = mode,
                ["opsApplied"] = okCount,
                ["opsTotal"] = ops.Count,
                ["opResults"] = opResultsJson,
                ["write"] = writeJson
            };
            if (returnPostState)
                resp["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose, persistedAfter);
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

            // v2.6.6 FR#12 — see ApplySemanticOpsCore for the rationale.
            var patchWriteStatus = writeJson["status"]?.ToString();
            bool writeOk = string.Equals(patchWriteStatus, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(patchWriteStatus, "Ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(patchWriteStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(patchWriteStatus, "partial", StringComparison.OrdinalIgnoreCase);
            string persistedAfter = writeOk ? ReadPersistedPartSafely(target, partName) : null;

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
                resp["post_state"] = JsonPatchService.BuildPostState(currentXml, newXml, verbose, persistedAfter);
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// v2.6.6 FR#12 — read the persisted part bytes from the SDK with cache
        /// drop so callers see post-commit reality. Returns null on any failure
        /// (logged); callers fall back to the in-memory <c>after</c> value.
        /// </summary>
        private string ReadPersistedPartSafely(string target, string partName)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            try
            {
                var obj = _objectService.FindObject(target, null);
                if (obj != null) _objectService.MarkReadCacheDirty(obj, partName);
                string readJson = _objectService.ReadObjectSource(target, partName, null, null, "mcp", true, null);
                if (string.IsNullOrWhiteSpace(readJson)) return null;
                var parsed = JObject.Parse(readJson);
                return parsed["source"]?.ToString()
                    ?? parsed["content"]?.ToString()
                    ?? parsed["parts"]?[partName ?? "Source"]?.ToString();
            }
            catch (Exception ex)
            {
                Logger.Debug("[POST-STATE] persisted re-read failed for " + target + " (" + partName + "): " + ex.Message);
                return null;
            }
        }
    }
}
