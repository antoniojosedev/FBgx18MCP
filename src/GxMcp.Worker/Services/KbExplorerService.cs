using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 item — "Locate in KB Explorer" parity.
    ///
    /// Given an object name, returns the folder/module breadcrumb (modulePath),
    /// the full breadcrumb-with-name (fullPath), and up to <see cref="SiblingCap"/>
    /// other objects in the same immediate parent. The parent walk mirrors
    /// <see cref="ListService.ResolveHierarchy"/>; the sibling list reads the
    /// in-memory <see cref="IndexCacheService"/> snapshot so we never walk the
    /// SDK twice on big KBs.
    /// </summary>
    public class KbExplorerService
    {
        public const int SiblingCap = 20;

        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCache;

        public KbExplorerService(ObjectService objectService, IndexCacheService indexCache)
        {
            _objectService = objectService;
            _indexCache = indexCache;
        }

        public string Locate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new JObject { ["error"] = "Missing 'name'." }.ToString(Newtonsoft.Json.Formatting.None);

            try
            {
                var obj = _objectService?.FindObject(name, null);
                if (obj == null)
                {
                    return new JObject
                    {
                        ["error"] = $"Object '{name}' not found.",
                        ["code"] = "NotFound",
                        ["name"] = name
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                string typeName = SafeType(obj);
                var (modulePath, immediateParent) = ResolveModulePath(obj);
                string fullPath = string.IsNullOrEmpty(modulePath)
                    ? obj.Name
                    : modulePath + "/" + obj.Name;

                var (siblings, truncated, totalSiblings) = ResolveSiblings(obj.Name, immediateParent);

                return new JObject
                {
                    ["name"] = obj.Name,
                    ["type"] = typeName,
                    ["modulePath"] = modulePath,
                    ["fullPath"] = fullPath,
                    ["immediateParent"] = immediateParent ?? string.Empty,
                    ["siblings"] = JArray.FromObject(siblings),
                    ["siblingCount"] = totalSiblings,
                    ["truncated"] = truncated
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["code"] = "LocateFailed"
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        private static string SafeType(dynamic obj)
        {
            try { return (string)obj.TypeDescriptor?.Name ?? obj.GetType().Name; }
            catch { try { return obj.GetType().Name; } catch { return null; } }
        }

        // Walks Parent → … → DesignModel. Mirrors ListService.ResolveHierarchy
        // but returns just the breadcrumb segments we need for KB Explorer.
        internal static (string modulePath, string immediateParent) ResolveModulePath(dynamic obj)
        {
            var segments = new List<string>();
            string immediate = null;
            try
            {
                dynamic current = obj.Parent;
                bool isFirst = true;
                while (current != null)
                {
                    try { if (current.Guid == obj.Guid) break; } catch { }

                    string parentTypeName = null;
                    try { parentTypeName = (string)current.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isFirst) immediate = "Root Module";
                        if (segments.Count == 0) segments.Insert(0, "Root Module");
                        break;
                    }

                    string currentName = null;
                    try { currentName = (string)current.Name; } catch { }
                    if (!string.IsNullOrWhiteSpace(currentName))
                    {
                        segments.Insert(0, currentName);
                        if (isFirst) immediate = currentName;
                    }

                    isFirst = false;
                    try { current = current.Parent; } catch { current = null; }
                }
            }
            catch { }

            string modulePath = string.Join("/", segments.Where(s => !string.IsNullOrWhiteSpace(s)));
            return (modulePath, immediate);
        }

        private (List<object> siblings, bool truncated, int total) ResolveSiblings(string objName, string immediateParent)
        {
            var list = new List<object>();
            int total = 0;
            try
            {
                var idx = _indexCache?.GetIndex();
                if (idx == null || string.IsNullOrEmpty(immediateParent))
                    return (list, false, 0);

                IEnumerable<SearchIndex.IndexEntry> source = null;
                if (idx.ChildrenByParent != null
                    && idx.ChildrenByParent.TryGetValue(immediateParent, out var children)
                    && children != null)
                {
                    source = children;
                }
                else
                {
                    source = idx.Objects.Values.Where(e =>
                        e != null &&
                        string.Equals(e.Parent, immediateParent, StringComparison.OrdinalIgnoreCase));
                }

                var ordered = source
                    .Where(e => e != null && !string.IsNullOrEmpty(e.Name))
                    .Where(e => !string.Equals(e.Name, objName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                total = ordered.Count;

                foreach (var entry in ordered.Take(SiblingCap))
                {
                    list.Add(new
                    {
                        name = entry.Name,
                        type = entry.Type ?? string.Empty
                    });
                }
            }
            catch { }

            return (list, total > SiblingCap, total);
        }
    }
}
