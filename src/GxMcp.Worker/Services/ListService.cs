using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ListService
    {
        private readonly KbService _kbService;
        private readonly IndexCacheService _indexCacheService;

        public ListService(KbService kbService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _indexCacheService = indexCacheService;
        }

        public string ListObjects(string filter, int limit, int offset, string parentFilter = null, string typeFilter = null, string parentPathFilter = null, bool verbose = false)
        {
            var sw = Stopwatch.StartNew();
            string source = "none";
            string Finalize(string response)
            {
                sw.Stop();
                Logger.Debug($"[ListService] source={source} limit={limit} offset={offset} parentPath='{parentPathFilter ?? ""}' parent='{parentFilter ?? ""}' typeFilter='{typeFilter ?? ""}' filter='{filter ?? ""}' verbose={verbose} elapsedMs={sw.ElapsedMilliseconds}");
                return response;
            }

            try
            {
                var array = new JArray();

                // Parse filter: can be a comma-separated list of types or a partial name
                var filterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string nameFilter = null;

                if (!string.IsNullOrEmpty(filter))
                {
                    if (filter.Contains(","))
                    {
                        foreach (var t in filter.Split(',')) filterTypes.Add(t.Trim());
                    }
                    else if (IsLikelyType(filter))
                    {
                        filterTypes.Add(filter.Trim());
                    }
                    else
                    {
                        nameFilter = filter.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(typeFilter))
                {
                    foreach (var t in typeFilter.Split(','))
                    {
                        var trimmed = t.Trim();
                        if (!string.IsNullOrEmpty(trimmed)) filterTypes.Add(trimmed);
                    }
                }

                var index = _indexCacheService.GetIndex();
                if (index != null && index.Objects.Count > 0)
                {
                    IEnumerable<SearchIndex.IndexEntry> entries;
                    source = "index-all";

                    if (!string.IsNullOrWhiteSpace(parentPathFilter) &&
                        index.ChildrenByParent != null &&
                        index.ChildrenByParent.TryGetValue(parentPathFilter, out var childrenByPath))
                    {
                        entries = childrenByPath;
                        source = "index-parentPath";
                    }
                    else if (!string.IsNullOrWhiteSpace(parentPathFilter))
                    {
                        entries = Enumerable.Empty<SearchIndex.IndexEntry>();
                        source = "index-parentPath-miss";
                    }
                    else if (!string.IsNullOrWhiteSpace(parentFilter) &&
                             index.ChildrenByParent != null &&
                             index.ChildrenByParent.TryGetValue(parentFilter, out var childrenByParent))
                    {
                        entries = childrenByParent;
                        source = "index-parent";
                    }
                    else if (!string.IsNullOrWhiteSpace(parentFilter))
                    {
                        entries = Enumerable.Empty<SearchIndex.IndexEntry>();
                        source = "index-parent-miss";
                    }
                    else
                    {
                        entries = index.Objects.Values;
                    }

                    if (filterTypes.Count > 0)
                    {
                        entries = entries.Where(e => filterTypes.Contains(e.Type ?? string.Empty));
                    }

                    if (!string.IsNullOrEmpty(nameFilter))
                    {
                        entries = entries.Where(e =>
                            (e.Name ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (e.Description ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    var orderedIndexEntries = entries
                        .OrderBy(e => GetTypeSortBucket(e.Type))
                        .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(e => e.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    int totalIndex = orderedIndexEntries.Count;
                    int startIndex = Math.Max(0, offset);
                    int pageSize = limit <= 0 ? int.MaxValue : limit;
                    foreach (var entry in orderedIndexEntries
                        .Skip(startIndex)
                        .Take(pageSize))
                    {
                        array.Add(BuildItem(
                            entry.Name,
                            entry.Type ?? "Unknown",
                            entry.Description,
                            entry.Parent ?? string.Empty,
                            entry.Module ?? string.Empty,
                            entry.Path ?? string.Empty,
                            entry.ParentPath ?? string.Empty,
                            verbose
                        ));
                    }

                    return Finalize(BuildPagedResponseInternal(array, totalIndex, startIndex, pageSize).ToString());
                }

                source = "runtime-sdk";
                var kb = _kbService.GetKB();
                if (kb == null) return Finalize("{\"error\":\"KB not open\"}");
                if (kb.DesignModel == null) return Finalize("{\"error\":\"KB DesignModel is null\"}");
                var objects = kb.DesignModel.Objects;
                if (objects == null) return Finalize("{\"error\":\"KB DesignModel.Objects is null\"}");

                var allObjects = ((System.Collections.IEnumerable)objects.GetAll())
                    .Cast<global::Artech.Architecture.Common.Objects.KBObject>();

                var filteredObjects = allObjects
                    .Select(obj => new RuntimeListEntry
                    {
                        Object = obj,
                        Hierarchy = ResolveHierarchy(obj),
                        TypeName = obj.TypeDescriptor?.Name ?? "Unknown",
                    });

                if (filterTypes.Count > 0)
                {
                    filteredObjects = filteredObjects.Where(x => filterTypes.Contains(x.TypeName));
                }

                if (!string.IsNullOrEmpty(nameFilter))
                {
                    filteredObjects = filteredObjects.Where(x =>
                        (x.Object.Name ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (x.Object.Description ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (parentPathFilter != null)
                {
                    filteredObjects = filteredObjects.Where(x => string.Equals(x.Hierarchy.ParentPath, parentPathFilter, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(parentFilter))
                {
                    filteredObjects = filteredObjects.Where(x => string.Equals(x.Hierarchy.ParentName, parentFilter, StringComparison.OrdinalIgnoreCase));
                }

                var orderedRuntime = filteredObjects
                    .OrderBy(x => GetTypeSortBucket(x.TypeName))
                    .ThenBy(x => x.Object.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int totalRuntime = orderedRuntime.Count;
                int startRuntime = Math.Max(0, offset);
                int pageSizeRuntime = limit <= 0 ? int.MaxValue : limit;
                foreach (var item in orderedRuntime
                    .Skip(startRuntime)
                    .Take(pageSizeRuntime))
                {
                    array.Add(BuildItem(
                        item.Object.Name,
                        item.TypeName,
                        item.Object.Description,
                        item.Hierarchy.ParentName,
                        item.Hierarchy.ModuleName,
                        item.Hierarchy.Path,
                        item.Hierarchy.ParentPath,
                        verbose
                    ));
                }

                return Finalize(BuildPagedResponseInternal(array, totalRuntime, startRuntime, pageSizeRuntime).ToString());
            }
            catch (Exception ex)
            {
                source = source + "-error";
                return Finalize("{\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}");
            }
        }

        public JObject BuildPagedResponseInternal(JArray results, int total, int offset, int pageSize)
        {
            var response = new JObject();
            response["count"] = results.Count;
            response["total"] = total;
            response["offset"] = offset;
            int consumed = offset + results.Count;
            bool hasMore = consumed < total;
            response["hasMore"] = hasMore;
            if (hasMore)
            {
                response["nextOffset"] = consumed;
            }
            response["results"] = results;

            var meta = new JObject();

            // Handle empty results: determine and attach empty_reason
            if (results.Count == 0)
            {
                string emptyReason = DetermineEmptyReason(total);
                meta["empty_reason"] = emptyReason;
            }
            else
            {
                // Non-empty results: compute and attach aggregates
                var aggregates = ComputeAggregates(results);
                if (aggregates != null)
                {
                    meta["aggregates"] = aggregates;
                }

                // Add suggested_next if we have results
                var suggestion = BuildSuggestedNext(results);
                if (suggestion != null)
                {
                    meta["suggested_next"] = suggestion;
                }
            }

            // Only attach _meta if it has content
            if (meta.Count > 0)
            {
                response["_meta"] = meta;
            }

            return response;
        }

        private string DetermineEmptyReason(int total)
        {
            // If total is 0, no items match at all
            if (total == 0)
            {
                // Check if KB is loaded by trying to get it
                // In test contexts where _kbService is null, default to "no_matches"
                if (_kbService != null)
                {
                    var kb = _kbService.GetKB();
                    if (kb == null || kb.DesignModel == null || kb.DesignModel.Objects == null)
                    {
                        return "kb_not_loaded";
                    }
                }

                // KB is loaded but no objects match (either no filter applied or filter matched nothing)
                // We can't directly tell if a filter was applied from this context,
                // so we default to "no_matches"
                return "no_matches";
            }

            // total > 0 but results.Count == 0 means a filter was applied and filtered everything out
            return "filtered_out";
        }

        private JObject ComputeAggregates(JArray items)
        {
            if (items == null || items.Count == 0)
                return null;

            var aggregates = new JObject();

            // total: count of items in the current page result
            aggregates["total"] = items.Count;

            // by_type: group items by type and count each type
            var typeGrouping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Cast<JObject>())
            {
                var type = item["type"]?.ToString() ?? "Unknown";
                if (typeGrouping.ContainsKey(type))
                {
                    typeGrouping[type]++;
                }
                else
                {
                    typeGrouping[type] = 1;
                }
            }

            var byTypeObj = new JObject();
            foreach (var kvp in typeGrouping.OrderBy(x => x.Key))
            {
                byTypeObj[kvp.Key] = kvp.Value;
            }
            aggregates["by_type"] = byTypeObj;

            // Note: modified_last_7d is skipped because IndexEntry does not have timestamp data
            // and KBObject does not expose modification time through the public API

            return aggregates;
        }

        public static JObject BuildSuggestedNext(JArray items)
        {
            if (items == null || items.Count == 0)
                return null;

            var top = items[0] as JObject;
            if (top == null)
                return null;

            return new JObject
            {
                ["tool"] = "genexus_read",
                ["args"] = new JObject
                {
                    ["name"] = top["name"]?.ToString(),
                    ["type"] = top["type"]?.ToString()
                }
            };
        }

        public static JObject BuildItemForTest(string name, string type, string description, string parent, string module, string path, string parentPath, bool verbose = false)
        {
            return BuildItemInternal(name, type, description, parent, module, path, parentPath, verbose);
        }

        // Test helper: allows tests to call BuildPagedResponse with mocked data
        // Note: This uses null for _kbService, so DetermineEmptyReason will always return "no_matches" for empty results
        public static JObject BuildPagedResponseForTest(JArray items, int total, int offset, int pageSize)
        {
            var svc = new ListService(null, null);
            return svc.BuildPagedResponseInternal(items, total, offset, pageSize);
        }

        private JObject BuildItem(string name, string type, string description, string parent, string module, string path, string parentPath, bool verbose = false)
        {
            return BuildItemInternal(name, type, description, parent, module, path, parentPath, verbose);
        }

        private static JObject BuildItemInternal(string name, string type, string description, string parent, string module, string path, string parentPath, bool verbose = false)
        {
            var item = new JObject();
            item["name"] = name;
            item["type"] = type;

            // Check if we're in legacy mode (MCP_PERF_PROFILE=legacy means V1Enabled=false)
            string perfProfile = Environment.GetEnvironmentVariable("MCP_PERF_PROFILE");
            bool isLegacyMode = !string.IsNullOrWhiteSpace(perfProfile) &&
                               string.Equals(perfProfile, "legacy", StringComparison.OrdinalIgnoreCase);

            // In legacy mode, always return full shape for backward compatibility
            if (isLegacyMode || verbose)
            {
                item["description"] = description;
                item["parent"] = parent;
                item["module"] = module;
                item["path"] = path;
                item["parentPath"] = parentPath;
            }
            else
            {
                // Minimal shape (4 fields): name, type, path, parent
                item["path"] = path;
                item["parent"] = parent;
            }

            return item;
        }

        private HierarchyInfo ResolveHierarchy(dynamic obj)
        {
            string parentName = string.Empty;
            string moduleName = null;
            var parentSegments = new List<string>();

            try
            {
                dynamic currentParent = obj.Parent;
                bool isImmediateParent = true;

                while (currentParent != null)
                {
                    try
                    {
                        if (currentParent.Guid == obj.Guid)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    string parentTypeName = null;
                    try { parentTypeName = currentParent.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isImmediateParent)
                        {
                            parentName = "Root Module";
                        }
                        break;
                    }

                    if (currentParent is global::Artech.Architecture.Common.Objects.Module ||
                        currentParent is global::Artech.Architecture.Common.Objects.Folder)
                    {
                        string currentName = null;
                        try { currentName = currentParent.Name; } catch { }

                        if (!string.IsNullOrWhiteSpace(currentName))
                        {
                            parentSegments.Insert(0, currentName);
                            if (isImmediateParent)
                            {
                                parentName = currentName;
                            }

                            if (moduleName == null &&
                                currentParent is global::Artech.Architecture.Common.Objects.Module)
                            {
                                moduleName = currentName;
                            }
                        }
                    }

                    currentParent = currentParent.Parent;
                    isImmediateParent = false;
                }
            }
            catch { }

            try
            {
                if (moduleName == null && obj.Module != null && obj.Module.Guid != obj.Guid)
                {
                    moduleName = obj.Module.Name;
                }
            }
            catch
            {
            }

            string parentPath = string.Join("/", parentSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
            string resolvedPath = string.IsNullOrWhiteSpace(obj.Name)
                ? parentPath
                : string.IsNullOrEmpty(parentPath) ? (string)obj.Name : parentPath + "/" + (string)obj.Name;

            return new HierarchyInfo
            {
                ParentName = parentName,
                ParentPath = parentPath,
                Path = resolvedPath,
                ModuleName = moduleName ?? string.Empty,
            };
        }

        private bool IsLikelyType(string s)
        {
            var types = new[] { "Folder", "Module", "Procedure", "Transaction", "WebPanel", "Attribute", "Table", "DataView", "Domain", "WorkPanel", "ExternalObject", "Menu", "SDPanel", "DataProvider", "SDT", "StructuredDataType", "Image" };
            return types.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase));
        }

        private int GetTypeSortBucket(string type)
        {
            if (string.Equals(type, "Folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "Module", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return 1;
        }

        private sealed class RuntimeListEntry
        {
            public global::Artech.Architecture.Common.Objects.KBObject Object { get; set; }
            public HierarchyInfo Hierarchy { get; set; }
            public string TypeName { get; set; }
        }

        private sealed class HierarchyInfo
        {
            public string ParentName { get; set; }
            public string ParentPath { get; set; }
            public string Path { get; set; }
            public string ModuleName { get; set; }
        }
    }
}
