using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class PatternService
    {
        private readonly IndexCacheService _indexCache;
        private readonly ObjectService _objectService;

        public PatternService(IndexCacheService indexCache, ObjectService objectService)
        {
            _indexCache = indexCache;
            _objectService = objectService;
        }

        public string GetSample(string type)
        {
            try
            {
                var index = _indexCache.GetIndex();
                if (index == null) return McpResponse.Error("Search Index not found", type, null, "Run the KB indexing flow before requesting pattern samples.");

                var candidates = index.Objects.Values
                    .Where(o => IsTypeMatch(o.Type, type))
                    .Where(o => o.Complexity > 5 && o.CalledBy.Count > 2)
                    .OrderBy(o => o.Complexity)
                    .Take(5)
                    .ToList();

                if (candidates.Count == 0)
                {
                    candidates = index.Objects.Values.Where(o => IsTypeMatch(o.Type, type)).Take(1).ToList();
                }

                if (candidates.Count == 0) return McpResponse.Error("Pattern sample not found", type, null, "No indexed objects matched the requested type.");

                var best = candidates.First();
                
                string sourceJson = _objectService.ReadObjectSource(best.Name, "Source");
                var json = JObject.Parse(sourceJson);
                string source = json["source"] != null ? json["source"].ToString() : "// No source available";
                
                var result = new JObject();
                result["sampleName"] = best.Name;
                result["type"] = best.Type;
                result["complexity"] = best.Complexity;
                result["source"] = source ?? "// No source available";

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private bool IsTypeMatch(string actual, string expected)
        {
            if (string.IsNullOrEmpty(actual)) return false;
            return actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
