using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_compare — read-only "Compare Objects" parity over the GeneXus SDK's
    /// <see cref="IComparerService"/>. Resolves two KB objects by name (optionally
    /// type-scoped via <c>type</c>) and reports whether they're equal in content
    /// (mode=content, default) or top-level properties (mode=properties).
    ///
    /// Follows the same SDK-service-resolution idiom as
    /// <see cref="GxServerSyncService"/>: resolve via <c>SdkServices.TryGetService</c>,
    /// guard every null/throw path, never crash the worker. If the Comparer package
    /// isn't loaded in this session, returns a clean <c>ComparerServiceUnavailable</c>
    /// error instead of a partial/garbled result.
    ///
    /// Read-only — no Merge/write path is exercised here (see
    /// docs/sdk_coverage_gap_matrix.md P0 #2 for the Merge follow-up).
    /// </summary>
    public class CompareService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public CompareService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string objectAName = args?["objectA"]?.ToString();
            string objectBName = args?["objectB"]?.ToString();
            string type = args?["type"]?.ToString();
            string mode = args?["mode"]?.ToString();
            if (string.IsNullOrWhiteSpace(mode)) mode = "content";
            mode = mode.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(objectAName) || string.IsNullOrWhiteSpace(objectBName))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "objectA and objectB are required.",
                    hint: "Pass both objectA and objectB as existing object names.");
            }

            if (mode != "content" && mode != "properties")
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "Unknown mode '" + mode + "'. Expected 'content' or 'properties'.",
                    hint: "Pass mode=content or mode=properties.");
            }

            // issue #32 item 2 (generalized): resilient resolve — retries a lazy/late SDK
            // registration and force-resolves before hard-failing (self-heals a respawn).
            IComparerService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IComparerService>();

            if (svc == null)
            {
                return McpResponse.Err(
                    code: "ComparerServiceUnavailable",
                    message: "The GeneXus SDK's IComparerService is not registered in this worker session (self-heal retries were exhausted).",
                    hint: "The Comparer package may not be loaded in this worker. Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            KBObject objA;
            KBObject objB;
            try
            {
                objA = _objects?.FindObject(objectAName, type);
                objB = _objects?.FindObject(objectBName, type);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CompareFailed", message: ex.Message, hint: "Check the worker log for details.");
            }

            if (objA == null)
            {
                return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object '" + objectAName + "' not found.",
                    hint: "Confirm objectA exists (and matches type, if provided).",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query",
                        new JObject { ["name"] = objectAName },
                        "Search for objectA by name to confirm it exists and get its exact name/type.")));
            }
            if (objB == null)
            {
                return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object '" + objectBName + "' not found.",
                    hint: "Confirm objectB exists (and matches type, if provided).",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query",
                        new JObject { ["name"] = objectBName },
                        "Search for objectB by name to confirm it exists and get its exact name/type.")));
            }

            try
            {
                if (mode == "properties")
                {
                    bool eqProps = svc.AreEqualInProperties(objA, objB, ComparePropertiesOptions.Default);
                    return McpResponse.Ok(
                        code: "CompareCompleted",
                        result: new JObject
                        {
                            ["equal"] = eqProps,
                            ["objectA"] = objA.Name,
                            ["objectB"] = objB.Name,
                            ["mode"] = "properties",
                            ["source"] = "sdk:IComparerService"
                        });
                }

                bool equal = svc.AreEqualInContent(objA, objB, CompareObjectOptions.Default);
                var result = new JObject
                {
                    ["equal"] = equal,
                    ["objectA"] = objA.Name,
                    ["objectB"] = objB.Name,
                    ["mode"] = "content",
                    ["source"] = "sdk:IComparerService"
                };

                if (!equal)
                {
                    result["differences"] = DiffPartNames(svc, objA, objB);
                }

                return McpResponse.Ok(code: "CompareCompleted", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CompareFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        /// <summary>
        /// Best-effort part-level breakdown: for each part TYPE present on both
        /// objects (Rules, Layout, Events, Source, ...), reports the type name
        /// when that pair's content differs. Not a full DiffNodes edit script —
        /// just enough for the agent to know where to look next via genexus_read.
        /// Any per-part failure is swallowed; the top-level 'equal:false' already
        /// stands regardless of whether this breakdown succeeds.
        /// </summary>
        private static JArray DiffPartNames(IComparerService svc, KBObject objA, KBObject objB)
        {
            var differences = new JArray();
            try
            {
                var partsB = new Dictionary<string, KBObjectPart>(StringComparer.OrdinalIgnoreCase);
                foreach (KBObjectPart pb in objB.Parts)
                {
                    string key = pb?.TypeDescriptor?.Name;
                    if (!string.IsNullOrEmpty(key) && !partsB.ContainsKey(key)) partsB[key] = pb;
                }

                foreach (KBObjectPart pa in objA.Parts)
                {
                    string key = pa?.TypeDescriptor?.Name;
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!partsB.TryGetValue(key, out var pb)) continue;

                    bool partsEqual;
                    try { partsEqual = svc.AreEqualInContent(pa, pb, false, CompareObjectOptions.Default); }
                    catch { continue; }

                    if (!partsEqual) differences.Add(key);
                }
            }
            catch { /* best-effort — content-level equal:false already surfaced */ }
            return differences;
        }
    }
}
