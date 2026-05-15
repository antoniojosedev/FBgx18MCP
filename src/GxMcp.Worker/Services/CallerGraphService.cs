using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // v2.3.8 (Task 1.3): unified caller/callee graph navigation backed by the
    // search index. Previously the impact-analysis BFS lived inline in
    // AnalyzeService.ImpactAnalysis and a parallel KB-level scan lived in
    // AnalyzeService.Inspect (via obj.GetReferencesTo()). This service is the
    // single source of truth for index-based graph queries; AnalyzeService and
    // BuildService delegate into it in later tasks (1.4, 5.1).
    public class TransitiveResult
    {
        public List<string> Nodes { get; set; } = new List<string>();
        public bool Truncated { get; set; }
        public int Depth { get; set; }
    }

    public class CallerGraphService
    {
        private readonly IndexCacheService _index;

        public CallerGraphService(IndexCacheService index)
        {
            _index = index;
        }

        // Backwards-compatible ctor matching the plan's signature
        // (ObjectService is currently unused for index-based callers; kept for
        // future expansion when we may want a KB fallback for unindexed objects).
        public CallerGraphService(IndexCacheService index, ObjectService objectService)
        {
            _index = index;
        }

        // Returns the names of objects whose SourceSnippet contains a call site
        // to targetName, OR whose pre-computed CalledBy entry references it.
        // Prefers the inverted index (CalledBy) when present (fast path) and
        // falls back to a regex scan over SourceSnippet (covers cases where the
        // SDK reference walker missed the callsite — see FR#3 in IndexCacheService).
        public List<string> GetCallers(string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || _index == null) return new List<string>();
            var idx = _index.GetIndex();
            if (idx == null) return new List<string>();

            var callers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Fast path: inverted index already populated by IndexCacheService.
            foreach (var e in idx.Objects.Values)
            {
                if (e == null || string.Equals(e.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.CalledBy != null && e.Name != null)
                {
                    // (no-op here; CalledBy is on the target, not the caller)
                }
            }

            if (idx.Objects.Values.Any(v => v != null && string.Equals(v.Name, targetName, StringComparison.OrdinalIgnoreCase)))
            {
                var target = idx.Objects.Values.FirstOrDefault(v => v != null && string.Equals(v.Name, targetName, StringComparison.OrdinalIgnoreCase));
                if (target != null && target.CalledBy != null)
                {
                    foreach (var c in target.CalledBy) callers.Add(c);
                }
            }

            // Slow path / augmentation: regex over SourceSnippet. This catches the
            // case where Calls/CalledBy wasn't populated yet (e.g. tests using
            // LoadFromEntries) or where the SDK reference walker missed a call.
            var pattern = new Regex(@"\b" + Regex.Escape(targetName) + @"\s*\(", RegexOptions.IgnoreCase);
            foreach (var e in idx.Objects.Values)
            {
                if (e == null) continue;
                if (string.Equals(e.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(e.SourceSnippet) && pattern.IsMatch(e.SourceSnippet))
                    callers.Add(e.Name);

                // Also honour the entry's own Calls list (forward edge).
                if (e.Calls != null && e.Calls.Any(c => string.Equals(c, targetName, StringComparison.OrdinalIgnoreCase)))
                    callers.Add(e.Name);
            }

            return callers.ToList();
        }

        // Direct callees of objectName. Uses the entry's Calls list (the unified
        // forward edge — populated both from SDK references and from textual
        // scanning in IndexCacheService).
        public List<string> GetCallees(string objectName)
        {
            if (string.IsNullOrEmpty(objectName) || _index == null) return new List<string>();
            var idx = _index.GetIndex();
            if (idx == null) return new List<string>();

            var entry = idx.Objects.Values.FirstOrDefault(
                v => v != null && string.Equals(v.Name, objectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return new List<string>();

            var callees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entry.Calls != null)
            {
                foreach (var c in entry.Calls)
                    if (!string.IsNullOrEmpty(c)) callees.Add(c);
            }

            // Fallback for objects whose Calls hasn't been populated yet: scan
            // the snippet for identifiers that match known objects in the index.
            if (callees.Count == 0 && !string.IsNullOrEmpty(entry.SourceSnippet))
            {
                foreach (var other in idx.Objects.Values)
                {
                    if (other == null || other == entry || string.IsNullOrEmpty(other.Name)) continue;
                    var pat = new Regex(@"\b" + Regex.Escape(other.Name) + @"\s*\(", RegexOptions.IgnoreCase);
                    if (pat.IsMatch(entry.SourceSnippet)) callees.Add(other.Name);
                }
            }

            return callees.ToList();
        }

        // BFS over callers (reverse edges), capped at maxNodes (exclusive of the root). Cycle-safe.
        // v2.3.8 (Task 1.4): symmetric to GetCalleesTransitive. AnalyzeService.ImpactAnalysis
        // previously inlined this BFS over CalledBy; it now delegates here.
        public TransitiveResult GetCallersTransitive(string root, int maxNodes = 200)
            => GetCallersTransitive(root, maxNodes, System.Threading.CancellationToken.None);

        public TransitiveResult GetCallersTransitive(string root, int maxNodes, System.Threading.CancellationToken ct)
        {
            var result = new TransitiveResult();
            if (string.IsNullOrEmpty(root) || maxNodes <= 0) return result;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((root, 0));
            visited.Add(root);

            int maxDepth = 0;

            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) { result.Truncated = true; result.Depth = maxDepth; return result; }
                var (name, d) = queue.Dequeue();
                maxDepth = Math.Max(maxDepth, d);

                foreach (var caller in GetCallers(name))
                {
                    if (string.IsNullOrEmpty(caller)) continue;
                    if (!visited.Add(caller)) continue;

                    result.Nodes.Add(caller);
                    if (result.Nodes.Count >= maxNodes)
                    {
                        result.Truncated = true;
                        result.Depth = Math.Max(maxDepth, d + 1);
                        return result;
                    }
                    queue.Enqueue((caller, d + 1));
                }
            }

            result.Depth = maxDepth;
            return result;
        }

        // BFS over callees, capped at maxNodes (exclusive of the root). Cycle-safe.
        public TransitiveResult GetCalleesTransitive(string root, int maxNodes = 200)
            => GetCalleesTransitive(root, maxNodes, System.Threading.CancellationToken.None);

        public TransitiveResult GetCalleesTransitive(string root, int maxNodes, System.Threading.CancellationToken ct)
        {
            var result = new TransitiveResult();
            if (string.IsNullOrEmpty(root) || maxNodes <= 0) return result;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((root, 0));
            visited.Add(root);

            int maxDepth = 0;

            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) { result.Truncated = true; result.Depth = maxDepth; return result; }
                var (name, d) = queue.Dequeue();
                maxDepth = Math.Max(maxDepth, d);

                foreach (var callee in GetCallees(name))
                {
                    if (string.IsNullOrEmpty(callee)) continue;
                    if (!visited.Add(callee)) continue;

                    result.Nodes.Add(callee);
                    if (result.Nodes.Count >= maxNodes)
                    {
                        result.Truncated = true;
                        result.Depth = Math.Max(maxDepth, d + 1);
                        return result;
                    }
                    queue.Enqueue((callee, d + 1));
                }
            }

            result.Depth = maxDepth;
            return result;
        }
    }
}
