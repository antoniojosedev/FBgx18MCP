using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // In-memory, per-tool latency aggregate. The gateway had no per-tool timing at all —
    // only the index build and KB-open logged elapsed, so read/write/edit latency was
    // invisible and "the worker is slow" was unmeasurable. This records the end-to-end
    // gateway→worker→gateway time for each tool call (excluding worker cold-start, which is
    // awaited before the sample starts), logs one [TOOL-LATENCY] line per call, and keeps a
    // rolling aggregate surfaced in whoami so slow paths are identifiable instead of guessed.
    public static class ToolLatencyStats
    {
        private sealed class Agg
        {
            public long Count;
            public long TotalMs;
            public long MaxMs;
            public long LastMs;
            public string? LastAtUtc;
        }

        private static readonly ConcurrentDictionary<string, Agg> _stats =
            new ConcurrentDictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);

        public static void Record(string? tool, double ms)
        {
            if (string.IsNullOrEmpty(tool)) tool = "unknown";
            // Ignore internal/heartbeat noise so the aggregate reflects real tool calls.
            if (string.Equals(tool, "ping", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, "heartbeat", StringComparison.OrdinalIgnoreCase))
                return;

            long m = ms <= 0 ? 0 : (long)Math.Round(ms);
            var a = _stats.GetOrAdd(tool!, _ => new Agg());
            lock (a)
            {
                a.Count++;
                a.TotalMs += m;
                if (m > a.MaxMs) a.MaxMs = m;
                a.LastMs = m;
                a.LastAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            }
        }

        // Top tools by total time spent (where the session's time actually went), with the
        // per-call count / avg / max / last so a single slow call and a chatty-but-cheap tool
        // are distinguishable.
        public static JObject Summarize(int topN = 10)
        {
            var result = new JObject();
            var arr = new JArray();
            var snapshot = _stats.ToArray();
            long grandTotal = 0;
            long grandCount = 0;

            var ranked = snapshot
                .Select(kv =>
                {
                    long count, total, max, last;
                    string? lastAt;
                    var a = kv.Value;
                    lock (a) { count = a.Count; total = a.TotalMs; max = a.MaxMs; last = a.LastMs; lastAt = a.LastAtUtc; }
                    return (tool: kv.Key, count, total, max, last, lastAt);
                })
                .OrderByDescending(x => x.total)
                .ToList();

            foreach (var x in ranked)
            {
                grandTotal += x.total;
                grandCount += x.count;
            }

            foreach (var x in ranked.Take(Math.Max(0, topN)))
            {
                arr.Add(new JObject
                {
                    ["tool"] = x.tool,
                    ["count"] = x.count,
                    ["avgMs"] = x.count > 0 ? (long)Math.Round((double)x.total / x.count) : 0,
                    ["maxMs"] = x.max,
                    ["lastMs"] = x.last,
                    ["totalMs"] = x.total,
                    ["lastAtUtc"] = x.lastAt
                });
            }

            result["totalCalls"] = grandCount;
            result["totalMs"] = grandTotal;
            result["byTool"] = arr;
            return result;
        }

        internal static void ResetForTest() => _stats.Clear();
    }
}
