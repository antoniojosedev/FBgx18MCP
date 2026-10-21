using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_wire action=pack_task on a native Knowledge Base: the orientation bundle built
    /// from the SDK-backed search index instead of from a text tree on disk.
    ///
    /// A native KB keeps its object sources in the KB database, so the file-based gxwire engine
    /// cannot index it. This service answers the same question - "which objects does this task
    /// touch and what surrounds them" - for exactly the objects asked about: explicit
    /// <c>targets</c>, or whole identifiers in the <c>task</c> text that match an indexed object.
    /// It is read-only by construction (it holds no write dependency), runs no Specify, Generate,
    /// Build, Rebuild, reorganization or execution, and never exports the KB.
    ///
    /// The answer says what it resolved, what it did not (with the reason), which sources it
    /// consulted, and where its evidence is partial. A name that is missing from the answer is
    /// always listed under <c>unresolved</c>, <c>ambiguous</c>, <c>skipped</c> or
    /// <c>omittedTargets</c>; nothing is dropped silently.
    /// </summary>
    public sealed class WirePackTaskService
    {
        internal const string Schema = "gxwire.pack/v1";
        internal const string Engine = "native-sdk";
        internal const int DefaultBudgetTokens = 1200;

        private const int MinBudgetTokens = 200;
        private const int MaxBudgetTokens = 20000;
        private const int MaxInputTargets = 50;
        private const int MaxAnchors = 12;
        private const int MaxAnchorsFull = 25;
        private const int NamesPerSide = 8;
        private const int NamesPerSideFull = 50;
        private const int ReferenceDepth = 1;
        private const int ReferenceNodeCap = 100;
        private const int MaxSuggestionTokens = 5;
        private const int MaxSuggestions = 5;
        private const int MaxFailuresInMessage = 6;
        private const int MinSuggestionPrefix = 4;
        private const int MaxReportedUnresolved = 20;
        private const int MaxReportedCandidates = 10;
        private const int MaxDescriptionChars = 120;
        private const int MaxTablesPerAnchor = 10;
        private const double CharsPerToken = 3.5;
        private const int DefaultTimeBudgetMs = 40000;

        // Names that mean "structure of the model", not "code the task touches". A bare word in
        // the task text that matches only these is reported as skipped instead of becoming an
        // anchor; an explicit target is never filtered.
        private static readonly HashSet<string> StructuralTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Attribute", "Folder", "Module", "Table"
        };

        private static readonly Regex IdentifierToken = new Regex(
            @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);

        private readonly IndexCacheService _indexCache;
        private readonly GraphToolsService _graph;
        private readonly Func<SearchIndex.IndexEntry, IReadOnlyList<string>> _listParts;

        /// <summary>
        /// Wall-clock allowance for building anchors. Each anchor may enrich an index entry and touch the
        /// SDK, so a long list must stop before the Gateway's own tool timeout does: the anchors not built
        /// are declared by name under omittedTargets instead of the whole call timing out.
        /// </summary>
        internal int TimeBudgetMs { get; set; } = DefaultTimeBudgetMs;

        public WirePackTaskService(IndexCacheService indexCache, GraphToolsService graph, ObjectService objects)
            : this(indexCache, graph, SdkPartsLister(objects))
        {
        }

        internal WirePackTaskService(
            IndexCacheService indexCache,
            GraphToolsService graph,
            Func<SearchIndex.IndexEntry, IReadOnlyList<string>> listParts)
        {
            _indexCache = indexCache;
            _graph = graph;
            _listParts = listParts;
        }

        /// <summary>Names of the parts an object exposes, never their content.</summary>
        private static Func<SearchIndex.IndexEntry, IReadOnlyList<string>> SdkPartsLister(ObjectService objects)
        {
            if (objects == null) return null;
            return entry =>
            {
                var obj = objects.FindObject(entry);
                return obj == null ? null : GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj);
            };
        }

        public string Run(JObject args)
        {
            args = args ?? new JObject();
            string action = args["action"]?.ToString();
            if (!string.IsNullOrWhiteSpace(action)
                && !string.Equals(action, "pack_task", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "pack-task", StringComparison.OrdinalIgnoreCase))
            {
                return McpResponse.Err(
                    "UnsupportedNativeAction",
                    "Action '" + action + "' is not available on a native Knowledge Base.",
                    "Only pack_task reads a native KB through the SDK; the other genexus_wire actions need a text-tree export. "
                    + "Use genexus_object_context, genexus_impact or genexus_query for the same questions on a native KB.");
            }

            string task = FirstText(args["task"], args["query"], args["target"]);
            var requests = ParseTargets(args["targets"], out bool inputCapped);

            if (string.IsNullOrWhiteSpace(task) && requests.Count == 0)
            {
                return McpResponse.Err(
                    "TaskRequired",
                    "Action 'pack_task' requires 'task' or 'targets'.",
                    "Pass targets:[\"<ObjectName>\", ...] for the objects to resolve, or a task that names them.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        "genexus_wire",
                        new JObject { ["action"] = "pack_task", ["targets"] = new JArray("<ObjectName>") },
                        "Resolve explicit objects through the SDK index.")));
            }

            SearchIndex index = _indexCache?.GetIndex();
            if (index == null)
            {
                return McpResponse.Err(
                    "IndexNotReady",
                    "The search index is not available yet.",
                    "Retry after genexus_lifecycle action=index completes.");
            }

            bool full = args["full"]?.Type == JTokenType.Boolean && args["full"].Value<bool>();
            int budget = ClampInt(args["token_budget"], DefaultBudgetTokens, MinBudgetTokens, MaxBudgetTokens);
            int anchorCap = full ? MaxAnchorsFull : MaxAnchors;
            int sideCap = full ? NamesPerSideFull : NamesPerSide;

            var resolution = new Resolution();
            foreach (TargetRequest request in requests) ResolveExplicit(index, request, resolution);
            bool extractedFromText = false;
            if (!string.IsNullOrWhiteSpace(task))
                extractedFromText = ExtractFromTask(index, task, resolution);

            if (resolution.Resolved.Count == 0 && requests.Count > 0)
            {
                // The gateway trims an error to code/message/hint unless verbose_errors is set, so what
                // failed has to be in the message itself, not only in the structured sections below.
                return McpResponse.Err(
                    "TargetsNotResolved",
                    "None of the " + requests.Count + " requested target(s) resolved to an indexed object: "
                        + DescribeFailures(resolution) + ".",
                    resolution.Ambiguous.Count > 0
                        ? "Disambiguate with Type:Name, guid or path; the candidates are listed under 'ambiguous'."
                        : "Check the names with genexus_query; near matches are listed under 'unresolved'.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        "genexus_query", new JObject { ["query"] = requests[0].Describe() }, "Find the object by name.")),
                    extra: new JObject
                    {
                        ["engine"] = Engine,
                        ["unresolved"] = resolution.Unresolved,
                        ["ambiguous"] = resolution.Ambiguous
                    });
            }

            var limitations = new JArray();
            var consultedSources = new SortedSet<string>(StringComparer.Ordinal) { "search-index:identity" };
            var omittedTargets = new JArray();
            var finalAnchors = new List<JObject>();
            var finalFacts = new List<AnchorFacts>();

            List<ResolvedTarget> kept = resolution.Resolved.Take(anchorCap).ToList();
            List<ResolvedTarget> capped = resolution.Resolved.Skip(anchorCap).ToList();

            // Budget: the sections that explain what was not found are never trimmed (an answer
            // that hides its own gaps is worse than a larger one); anchors are added until the
            // estimate reaches the budget and the rest are declared, by name, as omitted. Anchors
            // past the budget are not built at all, so a small budget also spends less SDK time.
            JObject skeleton = AssembleResult(task, budget, resolution, limitations, extractedFromText, inputCapped,
                new List<JObject>(), new JArray(), consultedSources, truncated: false, omittedTargets: new JArray());
            int used = skeleton.ToString(Formatting.None).Length;
            string stopReason = null;
            var clock = Stopwatch.StartNew();
            foreach (ResolvedTarget target in kept)
            {
                if (stopReason == null && finalAnchors.Count > 0 && clock.ElapsedMilliseconds >= TimeBudgetMs)
                    stopReason = "TimeBudget";
                if (stopReason != null)
                {
                    omittedTargets.Add(OmittedTarget(target.Entry, stopReason));
                    continue;
                }

                AnchorFacts facts = MakeAnchor(index, target, finalAnchors.Count + 1, sideCap);
                int length = facts.Json.ToString(Formatting.None).Length + facts.Limitations.ToString(Formatting.None).Length;
                if (!full && finalAnchors.Count > 0 && (used + length) / CharsPerToken > budget)
                {
                    stopReason = "TokenBudget";
                    omittedTargets.Add(OmittedTarget(target.Entry, stopReason));
                    continue;
                }

                finalAnchors.Add(facts.Json);
                finalFacts.Add(facts);
                used += length;
                foreach (JToken limitation in facts.Limitations) limitations.Add(limitation);
                foreach (string source in facts.Sources) consultedSources.Add(source);
            }
            foreach (ResolvedTarget target in capped)
                omittedTargets.Add(OmittedTarget(target.Entry, "TargetsCapped"));

            bool truncated = omittedTargets.Count > 0;
            if (truncated)
            {
                limitations.Add(Limitation("TargetsOmitted",
                    omittedTargets.Count + " resolved object(s) were left out of 'anchors' (see 'omittedTargets'). "
                    + "Pass fewer targets, raise token_budget or set full=true (TimeBudget: pass fewer targets per call)."));
            }

            JObject result = AssembleResult(task, budget, resolution, limitations, extractedFromText, inputCapped,
                finalAnchors, MakeLinks(finalFacts), consultedSources, truncated, omittedTargets);
            result["est_tokens"] = (int)Math.Ceiling(result.ToString(Formatting.None).Length / CharsPerToken);

            return McpResponse.Ok(
                code: "WirePackTaskNative",
                target: finalAnchors.Count > 0 ? (string)finalAnchors[0]["name"] : null,
                result: result);
        }

        // ---- result assembly ---------------------------------------------------------------

        private JObject AssembleResult(
            string task,
            int budget,
            Resolution resolution,
            JArray limitations,
            bool extractedFromText,
            bool inputCapped,
            List<JObject> anchors,
            JArray links,
            ISet<string> consultedSources,
            bool truncated,
            JArray omittedTargets)
        {
            var allLimitations = new JArray(limitations);
            if (extractedFromText)
            {
                allLimitations.Add(Limitation("NamesExtractedFromTask",
                    "Object names were taken from the task text: only whole identifiers that match an indexed object name "
                    + "are resolved, and plain words are not looked up. Pass targets[] to control exactly which objects are packed."));
            }
            if (inputCapped)
            {
                allLimitations.Add(Limitation("TargetsInputCapped",
                    "Only the first " + MaxInputTargets + " entries of targets[] were processed."));
            }
            allLimitations.Add(Limitation("PartBodiesNotRead",
                "No part body (Source, Rules, Events, WebForm, ...) was read to build this pack; 'parts_listed' only names what each "
                + "object exposes. Read a body with genexus_read name=<object> part=<part>."));

            bool empty = anchors.Count == 0 && resolution.Resolved.Count == 0;
            if (empty)
            {
                allLimitations.Add(Limitation("NoObjectMatched",
                    "No indexed object was matched. The native pack does not rank free text semantically; pass targets[] with the object names, "
                    + "or use genexus_query to find them first."));
            }

            var consulted = new JObject
            {
                ["sources"] = new JArray(consultedSources),
                // Not "parts consulted" in the sense of reading code: the part names are listed, the bodies are not read.
                ["partBodiesRead"] = new JArray(),
                ["objectsResolved"] = resolution.Resolved.Count
            };

            var result = new JObject
            {
                ["schema"] = Schema,
                ["engine"] = Engine,
                ["action"] = "pack_task",
                ["readOnly"] = true,
                ["task"] = task ?? string.Empty,
                ["budget"] = budget,
                ["anchors"] = new JArray(anchors),
                ["links"] = links,
                ["unresolved"] = Cap(resolution.Unresolved, MaxReportedUnresolved),
                ["ambiguous"] = resolution.Ambiguous,
                ["skipped"] = resolution.Skipped,
                ["omittedTargets"] = omittedTargets,
                ["consulted"] = consulted,
                ["limitations"] = allLimitations,
                ["empty"] = empty,
                ["complete"] = !empty && !truncated && resolution.Unresolved.Count == 0 && resolution.Ambiguous.Count == 0
                    && !allLimitations.Any(l => (string)l["code"] == "ReferencesPartial"),
                ["truncated"] = truncated,
                ["omitted"] = omittedTargets.Count
            };

            if (resolution.Unresolved.Count > MaxReportedUnresolved)
                result["unresolvedOmitted"] = resolution.Unresolved.Count - MaxReportedUnresolved;

            string first = anchors.Count > 0 ? (string)anchors[0]["name"] : null;
            result["next"] = first == null
                ? "genexus_query to find the objects, then genexus_wire action=pack_task targets=[\"<ObjectName>\"]"
                : "genexus_read name=" + first + " for its parts; genexus_object_context / genexus_impact for deeper graphs "
                  + "(the gxwire actions for/callers/impact/path/slice/safe_delete still need a text-tree export)";
            return result;
        }

        private JArray MakeLinks(List<AnchorFacts> facts)
        {
            var names = new HashSet<string>(facts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var links = new JArray();
            foreach (AnchorFacts from in facts)
            {
                foreach (string callee in from.AllCallees)
                {
                    if (!string.Equals(callee, from.Name, StringComparison.OrdinalIgnoreCase) && names.Contains(callee))
                        links.Add(new JObject { ["from"] = from.Name, ["to"] = callee, ["kind"] = "calls" });
                }
            }
            return links;
        }

        private AnchorFacts MakeAnchor(
            SearchIndex index,
            ResolvedTarget target,
            int rank,
            int sideCap)
        {
            SearchIndex.IndexEntry entry = target.Entry;
            var limitations = new JArray();
            var sources = new SortedSet<string>(StringComparer.Ordinal);

            // The edges and the parm rule come from enrichment; promote this object so its own are read.
            if (!entry.IsEnriched)
            {
                // A failed promotion leaves the entry unenriched; the reference read below reports its own field trust.
                try { _indexCache.GetEnrichmentQueue()?.PromoteAsync(entry).GetAwaiter().GetResult(); }
                catch (Exception ex) { Logger.Debug("[WirePack] enrichment of '" + entry.Name + "' failed: " + ex.Message); }
            }

            JObject references = _graph.References(index, entry, ReferenceDepth, ReferenceNodeCap, includeTruncationReasons: true);
            JObject callers = (JObject)references["callers"];
            JObject callees = (JObject)references["callees"];
            sources.Add("search-index:edges");
            if ((callers["fieldTrust"]?.ToString() ?? string.Empty).Contains("sdk-fallback")
                || (callees["fieldTrust"]?.ToString() ?? string.Empty).Contains("sdk-fallback"))
                sources.Add("sdk:references");

            var allCallers = Names(callers);
            var allCallees = Names(callees);

            var anchor = new JObject
            {
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["rank"] = rank,
                ["via"] = target.Via
            };
            if (!string.IsNullOrWhiteSpace(entry.Path)) anchor["path"] = entry.Path;
            if (!string.IsNullOrWhiteSpace(entry.Module)) anchor["module"] = entry.Module;
            if (!string.IsNullOrWhiteSpace(entry.Guid)) anchor["guid"] = entry.Guid;
            if (!string.IsNullOrWhiteSpace(entry.Description)) anchor["description"] = Shorten(entry.Description, MaxDescriptionChars);
            if (!string.IsNullOrWhiteSpace(entry.ParmRule)) anchor["parm_rule"] = entry.ParmRule;
            if (entry.Tables != null && entry.Tables.Count > 0)
                anchor["tables"] = new JArray(entry.Tables.Take(MaxTablesPerAnchor));

            AddSide(anchor, "callers", allCallers, callers, sideCap);
            AddSide(anchor, "callees", allCallees, callees, sideCap);
            anchor["trust"] = new JObject
            {
                ["callers"] = callers["fieldTrust"],
                ["callees"] = callees["fieldTrust"]
            };

            AddPartialLimitation(limitations, entry, "callers", callers);
            AddPartialLimitation(limitations, entry, "callees", callees);
            AddPartsListing(anchor, entry, limitations, sources);

            return new AnchorFacts(entry, anchor, allCallees, limitations, sources);
        }

        private static void AddSide(JObject anchor, string side, List<string> all, JObject source, int cap)
        {
            bool read = source["read"]?.Value<bool>() ?? true;
            anchor[side] = new JArray(all.Take(cap));
            anchor[side + "_total"] = all.Count;
            // An empty list that was not read is not a zero, so the anchor says so.
            if (!read) anchor[side + "_read"] = false;
            // Only the direct relations are asked for, so stopping at depth 1 is not a truncation; a node cap is.
            bool nodeCapReached = (source["truncatedBy"] as JArray)?.Any(t => string.Equals((string)t, "maxNodes", StringComparison.Ordinal)) ?? false;
            if (all.Count > cap || nodeCapReached) anchor[side + "_truncated"] = true;
        }

        private void AddPartialLimitation(JArray limitations, SearchIndex.IndexEntry entry, string side, JObject source)
        {
            string trust = source["fieldTrust"]?.ToString() ?? string.Empty;
            if (string.Equals(trust, "complete", StringComparison.Ordinal)) return;
            bool read = source["read"]?.Value<bool>() ?? true;
            string message;
            if (!read)
                message = "The " + side + " of '" + entry.Name + "' were not read (partial index coverage): an empty list is not a confirmed zero.";
            else if (trust.IndexOf("sdk-fallback", StringComparison.Ordinal) >= 0)
                message = "The " + side + " of '" + entry.Name + "' were completed from the SDK's own references because the index had no edges for them; "
                    + "that read stops at " + GraphToolsService.SdkReferenceCap + " references.";
            else
                message = "The " + side + " of '" + entry.Name + "' come from a partially enriched index: the list may be incomplete.";
            limitations.Add(new JObject
            {
                ["code"] = "ReferencesPartial",
                ["object"] = entry.Name,
                ["side"] = side,
                ["trust"] = trust,
                ["message"] = message
            });
        }

        private void AddPartsListing(JObject anchor, SearchIndex.IndexEntry entry, JArray limitations, ISet<string> sources)
        {
            if (_listParts == null)
            {
                limitations.Add(PartsUnavailable(entry, "no SDK object reader is attached"));
                return;
            }

            try
            {
                IReadOnlyList<string> parts = _listParts(entry);
                if (parts == null)
                {
                    limitations.Add(PartsUnavailable(entry, "the SDK did not return the object"));
                    return;
                }
                sources.Add("sdk:availableParts");
                anchor["parts_listed"] = new JArray(parts);
            }
            catch (Exception ex)
            {
                // Listing the parts is a courtesy on top of the index facts; a failure here is a gap in
                // the answer, declared by exception type only, never an error for the whole pack.
                limitations.Add(PartsUnavailable(entry, ex.GetType().Name));
            }
        }

        private static JObject PartsUnavailable(SearchIndex.IndexEntry entry, string reason)
            => new JObject
            {
                ["code"] = "PartsListingUnavailable",
                ["object"] = entry.Name,
                ["message"] = "The parts of '" + entry.Name + "' could not be listed (" + reason + "); the rest of its anchor is unaffected."
            };

        private static JObject OmittedTarget(SearchIndex.IndexEntry entry, string reason)
            => new JObject
            {
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["guid"] = entry.Guid,
                ["reason"] = reason
            };

        // ---- target resolution -------------------------------------------------------------

        private sealed class Resolution
        {
            internal readonly List<ResolvedTarget> Resolved = new List<ResolvedTarget>();
            internal readonly JArray Unresolved = new JArray();
            internal readonly JArray Ambiguous = new JArray();
            internal readonly JArray Skipped = new JArray();
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal void Add(SearchIndex.IndexEntry entry, string via, string requested)
            {
                string key = !string.IsNullOrWhiteSpace(entry.Guid) ? entry.Guid : entry.Type + ":" + entry.Name + ":" + entry.Path;
                if (_seen.Add(key)) Resolved.Add(new ResolvedTarget(entry, via, requested));
            }
        }

        private sealed class ResolvedTarget
        {
            internal readonly SearchIndex.IndexEntry Entry;
            internal readonly string Via;
            internal readonly string Requested;

            internal ResolvedTarget(SearchIndex.IndexEntry entry, string via, string requested)
            {
                Entry = entry;
                Via = via;
                Requested = requested;
            }
        }

        private sealed class AnchorFacts
        {
            internal readonly SearchIndex.IndexEntry Entry;
            internal readonly JObject Json;
            internal readonly List<string> AllCallees;
            internal readonly JArray Limitations;
            internal readonly ISet<string> Sources;
            internal string Name { get { return Entry.Name; } }

            internal AnchorFacts(SearchIndex.IndexEntry entry, JObject json, List<string> allCallees, JArray limitations, ISet<string> sources)
            {
                Entry = entry;
                Json = json;
                AllCallees = allCallees;
                Limitations = limitations;
                Sources = sources;
            }
        }

        private sealed class TargetRequest
        {
            internal string Name;
            internal string Type;
            internal string Guid;
            internal string EntityKey;
            internal string Path;

            internal string Describe()
                => !string.IsNullOrWhiteSpace(Name) ? Name
                    : !string.IsNullOrWhiteSpace(Guid) ? Guid
                    : !string.IsNullOrWhiteSpace(Path) ? Path
                    : EntityKey;
        }

        private static List<TargetRequest> ParseTargets(JToken token, out bool capped)
        {
            capped = false;
            var requests = new List<TargetRequest>();
            if (token == null || token.Type == JTokenType.Null) return requests;

            IEnumerable<JToken> items = token is JArray array
                ? (IEnumerable<JToken>)array
                : token.Type == JTokenType.String
                    ? token.ToString().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => (JToken)s.Trim())
                    : new[] { token };

            foreach (JToken item in items)
            {
                TargetRequest request = null;
                if (item is JObject obj)
                {
                    request = new TargetRequest
                    {
                        Name = obj["name"]?.ToString() ?? obj["target"]?.ToString(),
                        Type = obj["type"]?.ToString(),
                        Guid = obj["guid"]?.ToString(),
                        EntityKey = obj["entityKey"]?.ToString(),
                        Path = obj["path"]?.ToString()
                    };
                }
                else if (item.Type == JTokenType.String)
                {
                    request = new TargetRequest { Name = item.ToString().Trim() };
                }
                if (request == null || string.IsNullOrWhiteSpace(request.Describe())) continue;
                if (requests.Count >= MaxInputTargets) { capped = true; break; }
                requests.Add(request);
            }
            return requests;
        }

        private void ResolveExplicit(SearchIndex index, TargetRequest request, Resolution resolution)
        {
            string name = request.Name?.Trim();
            string type = request.Type;
            string path = request.Path;

            // "Type:Name" disambiguates in one string; only a prefix that is a real type counts, so a
            // name that merely contains a colon is not silently cut.
            if (string.IsNullOrWhiteSpace(type) && name != null)
            {
                int colon = name.IndexOf(':');
                if (colon > 0 && colon < name.Length - 1 && index.FindByType(name.Substring(0, colon)).Count > 0)
                {
                    type = name.Substring(0, colon);
                    name = name.Substring(colon + 1).Trim();
                }
            }

            // A module-qualified name is a path, never a bare name.
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(request.Guid)
                && string.IsNullOrWhiteSpace(request.EntityKey) && name != null
                && (name.IndexOf('.') > 0 || name.IndexOf('/') > 0 || name.IndexOf('\\') > 0))
            {
                path = name.Replace('.', '/');
                name = null;
            }

            List<SearchIndex.IndexEntry> candidates;
            SearchIndex.IndexEntry entry = IndexEntryResolver.Resolve(
                index, name, type, request.Guid, request.EntityKey, path, out candidates);
            if (entry != null)
            {
                resolution.Add(entry, "targets", request.Describe());
                return;
            }

            if (candidates.Count > 1)
            {
                resolution.Ambiguous.Add(AmbiguousItem(request.Describe(), "targets", candidates));
                return;
            }

            resolution.Unresolved.Add(UnresolvedItem(index, request.Describe(), "targets", withSuggestions: true));
        }

        /// <summary>
        /// Whole identifiers in the task text that match an indexed object name become anchors. Returns
        /// true when the text produced at least one anchor, ambiguity, skip or unresolved name.
        /// </summary>
        private bool ExtractFromTask(SearchIndex index, string task, Resolution resolution)
        {
            bool produced = false;
            int suggestionBudget = MaxSuggestionTokens;
            var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in IdentifierToken.Matches(task))
            {
                string token = match.Value;
                if (token.Length < 3 || !seenTokens.Add(token)) continue;

                List<SearchIndex.IndexEntry> candidates;
                if (token.IndexOf('.') > 0)
                {
                    SearchIndex.IndexEntry byPath = IndexEntryResolver.Resolve(
                        index, null, null, null, null, token.Replace('.', '/'), out candidates);
                    candidates = byPath != null ? new List<SearchIndex.IndexEntry> { byPath } : new List<SearchIndex.IndexEntry>();
                }
                else
                {
                    candidates = index.FindByName(token);
                }

                if (candidates.Count == 0)
                {
                    // A plain word is not an object name and is not reported; an identifier that has the
                    // shape of one (CamelCase, digits, underscore) and matched nothing is.
                    if (!LooksLikeObjectName(token)) continue;
                    bool suggest = suggestionBudget-- > 0;
                    resolution.Unresolved.Add(UnresolvedItem(index, token, "task-text", suggest));
                    produced = true;
                    continue;
                }

                var kept = candidates.Where(c => !StructuralTypes.Contains(c.Type ?? string.Empty)).ToList();
                if (kept.Count == 0)
                {
                    resolution.Skipped.Add(new JObject
                    {
                        ["token"] = token,
                        ["types"] = new JArray(candidates.Select(c => c.Type).Distinct(StringComparer.OrdinalIgnoreCase)),
                        ["reason"] = "StructuralTypeFromFreeText",
                        ["hint"] = "A bare word that only matches an attribute, table, folder or module is not packed from free text; "
                            + "pass it in targets[] (for example \"Attribute:" + token + "\") to include it."
                    });
                    produced = true;
                    continue;
                }

                if (kept.Count > 1)
                {
                    resolution.Ambiguous.Add(AmbiguousItem(token, "task-text", kept));
                    produced = true;
                    continue;
                }

                resolution.Add(kept[0], "task-text", token);
                produced = true;
            }
            return produced;
        }

        private static bool LooksLikeObjectName(string token)
        {
            if (token.Length < 4) return false;
            if (token.IndexOf('_') >= 0 || token.IndexOf('.') > 0) return true;
            bool interiorUpper = false;
            for (int i = 1; i < token.Length; i++)
            {
                if (char.IsDigit(token[i])) return true;
                if (char.IsUpper(token[i])) interiorUpper = true;
            }
            return interiorUpper;
        }

        private static string DescribeFailures(Resolution resolution)
        {
            var parts = new List<string>();
            foreach (JObject item in resolution.Unresolved.OfType<JObject>().Take(MaxFailuresInMessage))
            {
                string near = string.Join(", ", (item["suggestions"] as JArray ?? new JArray()).Select(n => (string)n["name"]));
                parts.Add("'" + (string)item["requested"] + "' not found" + (near.Length > 0 ? " (near: " + near + ")" : string.Empty));
            }
            foreach (JObject item in resolution.Ambiguous.OfType<JObject>().Take(MaxFailuresInMessage))
            {
                string types = string.Join("/", ((JArray)item["candidates"]).Select(c => (string)c["type"]).Distinct(StringComparer.OrdinalIgnoreCase));
                parts.Add("'" + (string)item["requested"] + "' is ambiguous (" + types + ")");
            }
            int total = resolution.Unresolved.Count + resolution.Ambiguous.Count;
            string text = string.Join("; ", parts);
            return total > parts.Count ? text + "; and " + (total - parts.Count) + " more" : text;
        }

        private static JObject AmbiguousItem(string requested, string via, IEnumerable<SearchIndex.IndexEntry> candidates)
        {
            // The index holds candidates in hash order; a stable order keeps the answer reproducible.
            var list = candidates
                .OrderBy(c => c.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var first = list.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Type));
            return new JObject
            {
                ["requested"] = requested,
                ["via"] = via,
                ["reason"] = "AmbiguousName",
                ["candidates"] = new JArray(list.Take(MaxReportedCandidates).Select(c => new JObject
                {
                    ["name"] = c.Name, ["type"] = c.Type, ["guid"] = c.Guid, ["path"] = c.Path
                })),
                ["hint"] = "Pass targets:[\"" + (first?.Type ?? "Type") + ":" + requested + "\"], or a guid or path from the candidates."
            };
        }

        private static JObject UnresolvedItem(SearchIndex index, string requested, string via, bool withSuggestions)
        {
            var item = new JObject
            {
                ["requested"] = requested,
                ["via"] = via,
                ["reason"] = "NotFound"
            };
            if (withSuggestions)
            {
                JArray near = NearMatches(index, requested);
                if (near.Count > 0) item["suggestions"] = near;
            }
            return item;
        }

        private static JArray NearMatches(SearchIndex index, string requested)
        {
            string probe = requested ?? string.Empty;
            int cut = Math.Max(probe.LastIndexOf('.'), Math.Max(probe.LastIndexOf('/'), probe.LastIndexOf(':')));
            if (cut >= 0 && cut < probe.Length - 1) probe = probe.Substring(cut + 1);
            if (probe.Length < 3 || index?.Objects == null) return new JArray();

            var named = index.Objects.Values.Where(e => e != null && !string.IsNullOrEmpty(e.Name)).ToList();
            List<SearchIndex.IndexEntry> near = named
                .Where(e => e.Name.IndexOf(probe, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // A typo at the end of the name matches nothing as a whole; the longest prefix that still
            // matches finds the object it was aiming at.
            for (int length = probe.Length - 1; near.Count == 0 && length >= MinSuggestionPrefix; length--)
            {
                string prefix = probe.Substring(0, length);
                near = named.Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return new JArray(near
                .OrderBy(e => e.Name.Length)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)
                .Select(e => new JObject { ["name"] = e.Name, ["type"] = e.Type }));
        }

        // ---- helpers -----------------------------------------------------------------------

        private static List<string> Names(JObject side)
            => ((JArray)side["names"]).Select(n => (string)n).Where(n => !string.IsNullOrEmpty(n)).ToList();

        private static JObject Limitation(string code, string message)
            => new JObject { ["code"] = code, ["message"] = message };

        private static JArray Cap(JArray array, int max)
            => array.Count <= max ? array : new JArray(array.Take(max));

        private static string Shorten(string text, int max)
        {
            string flat = Regex.Replace(text.Trim(), @"\s+", " ");
            return flat.Length <= max ? flat : flat.Substring(0, max - 1) + "…";
        }

        private static string FirstText(params JToken[] tokens)
        {
            foreach (JToken token in tokens)
            {
                if (token != null && token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.ToString()))
                    return token.ToString();
            }
            return null;
        }

        private static int ClampInt(JToken token, int fallback, int min, int max)
        {
            int value = token != null && token.Type == JTokenType.Integer ? token.Value<int>() : fallback;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
