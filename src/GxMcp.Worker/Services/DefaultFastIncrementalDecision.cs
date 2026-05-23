using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Services
{
    // Item 28 (Tier-S) — EXPERIMENTAL default implementation of
    // IFastIncrementalDecision. Combines EditDirtyTracker (session-scoped
    // dirty bookkeeping for MCP writes) with IndexCacheService (KB type
    // lookup) to decide what BuildService can safely skip when the agent
    // opts into fastIncremental=true.
    //
    // Heuristics (intentionally conservative — false positives here mean
    // the agent sees a stale .dll, which is worse than a slow build):
    //
    //   * Empty dirty set + every target has been built at least once
    //       → NothingDirty=true (BuildService short-circuits).
    //
    //   * Any dirty target is Theme / MasterPage / Domain / SDT
    //       → ForceFullBuild=true, FallbackReason="non-incremental-target-kind".
    //
    //   * Index lookup returns null for a dirty target (we can't classify it)
    //       → ForceFullBuild=true, FallbackReason="target-kind-unknown".
    //
    //   * Otherwise every dirty target is one of Procedure / WebPanel /
    //     Transaction / Web Component / WorkWithDevicesPanel
    //       → CanSkipDeploy=true. CanSkipSpecify lists every clean target.
    public sealed class DefaultFastIncrementalDecision : IFastIncrementalDecision
    {
        private static readonly HashSet<string> IncrementalSafeKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Procedure",
                "WebPanel",
                "Transaction",
                "WebComponent",
                "WorkWithDevicesPanel",
                "DataProvider"
            };

        private static readonly HashSet<string> RiskyKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Theme",
                "MasterPage",
                "Domain",
                "SDT",
                "StructuredDataType",
                "BusinessProcessDiagram"
            };

        private readonly IndexCacheService _index;

        public DefaultFastIncrementalDecision(IndexCacheService index)
        {
            _index = index;
        }

        public FastIncrementalDecision Decide(string kbPath, IReadOnlyList<string> targets)
        {
            var result = new FastIncrementalDecision();
            if (targets == null || targets.Count == 0)
            {
                result.NothingDirty = true;
                return result;
            }

            var dirty = new List<string>();
            var clean = new List<string>();
            foreach (var t in targets)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (EditDirtyTracker.IsDirty(kbPath, t)) dirty.Add(t);
                else clean.Add(t);
            }

            if (dirty.Count == 0)
            {
                result.NothingDirty = true;
                result.CanSkipSpecify = clean;
                return result;
            }

            // Classify dirty targets — any risky kind forces full build.
            foreach (var t in dirty)
            {
                string kind = TryGetKind(t);
                if (kind == null)
                {
                    result.ForceFullBuild = true;
                    result.FallbackReason = "target-kind-unknown";
                    return result;
                }
                if (RiskyKinds.Contains(kind))
                {
                    result.ForceFullBuild = true;
                    result.FallbackReason = "non-incremental-target-kind";
                    return result;
                }
                if (!IncrementalSafeKinds.Contains(kind))
                {
                    // Unknown-but-classified kind — bail safely.
                    result.ForceFullBuild = true;
                    result.FallbackReason = "non-incremental-target-kind";
                    return result;
                }
            }

            result.CanSkipDeploy = true;
            result.CanSkipSpecify = clean;
            return result;
        }

        private string TryGetKind(string name)
        {
            if (_index == null) return null;
            try
            {
                var entry = _index.TryGetEntryByName(name);
                return entry?.Type;
            }
            catch
            {
                return null;
            }
        }
    }
}
