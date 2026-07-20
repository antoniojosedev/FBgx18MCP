using System;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GenexusServices = Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_analyze mode=kb_stats — KB activity &amp; freshness (P1 #6).
    ///
    /// Read-only. Primary signal comes from <c>IModelInformationService</c> timestamps
    /// (cheap, always available on an open model): last object change, last table change,
    /// last reorg. From those it derives <c>reorgLikelyNeeded</c> (a table changed after the
    /// last reorg). Optional operation history from <c>IStatisticsService</c> is included
    /// only when a specific object-type <c>typeGuid</c> is supplied (the SDK API is keyed by
    /// object-class GUID, not a KB-wide feed).
    ///
    /// Both services are non-<c>IGxService</c>, so they resolve through
    /// <see cref="SdkServiceLocator"/> (interface-GUID locator), not the generic
    /// <see cref="SdkServiceResolver"/>. Missing service → clean <c>*Unavailable</c>.
    /// </summary>
    public class KbStatsService
    {
        private readonly KbService _kb;

        public KbStatsService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            KBModel model;
            try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; }
            catch { model = null; }

            if (model == null)
                return McpResponse.Err(
                    code: "NoKbOpen",
                    message: "No open KB / design model available.",
                    hint: "Open a KB first (genexus_kb action=open).");

            var info = SdkServiceLocator.TryResolve<GenexusServices.IModelInformationService>();
            if (info == null)
                return McpResponse.Err(
                    code: "ModelInformationServiceUnavailable",
                    message: "The GeneXus SDK's IModelInformationService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            var result = new JObject();
            DateTime lastObj = SafeDate(() => info.GetLastModifiedObjectTimestamp(model));
            DateTime lastTbl = SafeDate(() => info.GetLastModifiedTableTimestamp(model));
            DateTime lastReorg = SafeDate(() => info.GetLastReorgTimestamp(model));

            result["lastModifiedObject"] = Iso(lastObj);
            result["lastModifiedTable"] = Iso(lastTbl);
            result["lastReorg"] = Iso(lastReorg);
            // A table changed after the last reorg ⇒ the generated DB schema is likely stale.
            result["reorgLikelyNeeded"] = lastTbl != default && lastTbl > lastReorg;
            result["source"] = "sdk:IModelInformationService";

            // Optional per-object-type operation history (SDK API is object-class-GUID keyed).
            string typeGuidStr = args?["typeGuid"]?.ToString();
            int count = args?["count"]?.ToObject<int?>() ?? 10;
            if (!string.IsNullOrWhiteSpace(typeGuidStr) && Guid.TryParse(typeGuidStr, out var typeGuid))
            {
                var stats = SdkServiceLocator.TryResolve<IStatisticsService>();
                if (stats != null)
                {
                    try
                    {
                        var ops = new JArray();
                        foreach (var ev in stats.GetOperationsByDate(model, typeGuid, count))
                        {
                            if (ev == null) continue;
                            ops.Add(new JObject
                            {
                                ["date"] = Iso(SafeDate(() => ev.Date)),
                                ["user"] = SafeStr(() => ev.OperationUser?.Name),
                                ["value"] = SafeInt(() => ev.Value),
                                ["info"] = SafeStr(() => ev.AdditionalInfo)
                            });
                        }
                        result["operations"] = ops;
                    }
                    catch (Exception ex) { result["operationsError"] = ex.Message; }
                }
                else result["operationsError"] = "IStatisticsService unavailable";
            }

            return McpResponse.Ok(code: "KbStatsRetrieved", result: result);
        }

        private static DateTime SafeDate(Func<DateTime> f) { try { return f(); } catch { return default; } }
        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static JToken Iso(DateTime d) => d == default ? (JToken)JValue.CreateNull() : d.ToUniversalTime().ToString("o");
    }
}
