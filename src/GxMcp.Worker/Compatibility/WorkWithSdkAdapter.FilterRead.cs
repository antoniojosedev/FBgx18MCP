using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    internal static partial class WorkWithSdkAdapter
    {
        internal sealed class FilterLevelCandidate
        {
            public string Id;
            public bool HasSelection;
            public object Filter;
        }

        private static string LevelIdOrDefault(string id, int index)
        {
            if (!string.IsNullOrWhiteSpace(id)) return id;
            return index == 0 ? "Root" : "Level" + index;
        }

        /// Fail-closed Level selection shared by the native and XML readers and by the
        /// native writer: one Level resolves implicitly, two or more require `level`.
        internal static bool TryResolveFilterLevel(
            IList<FilterLevelCandidate> candidates,
            string levelId,
            out FilterLevelCandidate resolved,
            out string error,
            out string errorMessage)
        {
            resolved = null;
            error = null;
            errorMessage = null;
            string available = string.Join(", ", candidates.Select(c => c.Id));

            if (!string.IsNullOrWhiteSpace(levelId))
            {
                resolved = candidates.FirstOrDefault(c => string.Equals(c.Id, levelId, StringComparison.OrdinalIgnoreCase));
                if (resolved == null)
                {
                    error = "FilterLevelNotFound";
                    errorMessage = candidates.Count == 0
                        ? $"Level '{levelId}' was not found: the WorkWith instance has no Levels."
                        : $"Level '{levelId}' was not found in the WorkWith instance. Available levels: {available}.";
                    return false;
                }
            }
            else if (candidates.Count == 0)
            {
                error = "FilterLevelNotFound";
                errorMessage = "The WorkWith instance has no Levels.";
                return false;
            }
            else if (candidates.Count > 1)
            {
                error = "FilterLevelAmbiguous";
                errorMessage = $"The WorkWith instance has {candidates.Count} Levels; pass `level` to choose one. Candidates: {available}.";
                return false;
            }
            else
            {
                resolved = candidates[0];
            }

            if (!resolved.HasSelection)
            {
                error = "WorkWithSelectionMissing";
                errorMessage = $"Level '{resolved.Id}' has no selection node.";
                resolved = null;
                return false;
            }

            if (resolved.Filter == null)
            {
                error = "WorkWithFilterNotPresent";
                errorMessage = $"Standard WorkWith filter node is not present on the selection of Level '{resolved.Id}'. Create the filter in the GeneXus IDE.";
                resolved = null;
                return false;
            }

            return true;
        }

        public static bool ListFilters(KBObject instanceObj, PatternAnalysisService patterns, string levelId, out JObject filters, out WorkWithReadSource source, out string error, out string errorMessage)
        {
            filters = null;
            source = WorkWithReadSource.NativeInstance;
            error = null;
            errorMessage = null;

            if (instanceObj == null)
            {
                error = "NullInstance";
                errorMessage = "WorkWith PatternInstance object is null.";
                return false;
            }

            return ListFiltersCore(
                instanceObj,
                () => patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _),
                levelId,
                out filters,
                out source,
                out error,
                out errorMessage);
        }

        /// Native-first read with the XML projection as fallback. `readPatternXml` is the
        /// seam that lets every path run without a Knowledge Base; `source` names the
        /// reader that produced `filters`. A Level resolution error on the native route
        /// is returned as-is: the projection never masks it.
        internal static bool ListFiltersCore(KBObject instanceObj, Func<string> readPatternXml, string levelId, out JObject filters, out WorkWithReadSource source, out string error, out string errorMessage)
        {
            filters = null;
            error = null;
            errorMessage = null;

            if (TryLoadNativeInstance(instanceObj, "ListFilters", out var wwiType, out var wwi, out source))
            {
                try
                {
                    var levels = wwiType.GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
                    if (levels != null)
                    {
                        return ReadNativeFilters(wwi, levelId, out filters, out error, out errorMessage);
                    }

                    source = WorkWithReadSource.XmlProjectionNativeLevelsAbsent;
                }
                catch (Exception ex)
                {
                    LogNativeReadFallback("ListFilters", ex);
                    source = WorkWithReadSource.XmlProjectionNativeReadFailed;
                }
            }

            string xml = readPatternXml?.Invoke();
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "PatternInstanceXmlEmpty";
                errorMessage = "PatternInstance XML was empty; filters could not be read.";
                return false;
            }

            return TryListFiltersFromXml(xml, levelId, out filters, out error, out errorMessage);
        }

        /// Projects the filter of one Level from an already loaded typed WorkWithInstance
        /// (the `list_filters` shape). Shared by the native read path and the removal
        /// orchestration so tests can read a fake typed model through the same code.
        internal static bool ReadNativeFilters(object wwi, string levelId, out JObject filters, out string error, out string errorMessage)
        {
            filters = null;
            error = null;
            errorMessage = null;

            var levels = wwi?.GetType().GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
            if (levels == null)
            {
                error = "WorkWithLevelsUnavailable";
                errorMessage = "The typed WorkWith instance exposes no Levels collection.";
                return false;
            }

            if (!TryResolveFilterLevel(CollectNativeFilterLevels(levels), levelId, out var resolved, out error, out errorMessage))
                return false;

            filters = ReadNativeFilter(resolved);
            return true;
        }

        private static List<FilterLevelCandidate> CollectNativeFilterLevels(IEnumerable levels)
        {
            var candidates = new List<FilterLevelCandidate>();
            int index = 0;
            foreach (var lvl in levels)
            {
                string id = lvl.GetType().GetProperty("Id")?.GetValue(lvl, null)?.ToString()
                    ?? lvl.GetType().GetProperty("Name")?.GetValue(lvl, null)?.ToString();
                var selection = lvl.GetType().GetProperty("Selection")?.GetValue(lvl, null);
                candidates.Add(new FilterLevelCandidate
                {
                    Id = LevelIdOrDefault(id, index),
                    HasSelection = selection != null,
                    Filter = selection?.GetType().GetProperty("Filter")?.GetValue(selection, null)
                });
                index++;
            }
            return candidates;
        }

        /// Typed reader over the loaded WorkWith model. The filter attribute's `default`
        /// (schema attribute `default`, typed member `FilterAttributeElement.Default`) is
        /// always reported as a string, "" when unset, so both readers share one shape
        /// and verification never compares null with "" (issue #588).
        internal static JObject ReadNativeFilter(FilterLevelCandidate level)
        {
            object filter = level.Filter;
            var attrsArray = new JArray();
            var filterAttrs = filter.GetType().GetProperty("Attributes")?.GetValue(filter, null) as IEnumerable;
            if (filterAttrs != null)
            {
                foreach (var fa in filterAttrs)
                {
                    string faName = fa.GetType().GetProperty("Name")?.GetValue(fa, null)?.ToString() ?? "";
                    string faDesc = fa.GetType().GetProperty("Description")?.GetValue(fa, null)?.ToString();
                    // DomainName is the SDK's own plain-name projection of the typed Domain
                    // reference; the ToString fallback may carry the `{guid}-Name` form.
                    string faDom = fa.GetType().GetProperty("DomainName")?.GetValue(fa, null)?.ToString()
                        ?? StripObjectReferenceGuid(fa.GetType().GetProperty("Domain")?.GetValue(fa, null)?.ToString());
                    string faVar = fa.GetType().GetProperty("VariableName")?.GetValue(fa, null)?.ToString();
                    string faDefault = fa.GetType().GetProperty("Default")?.GetValue(fa, null)?.ToString();

                    var attrObj = new JObject
                    {
                        ["name"] = faName,
                        ["description"] = faDesc ?? faName,
                        ["domain"] = faDom ?? "",
                        ["default"] = faDefault ?? "",
                        ["variableName"] = faVar ?? ("&" + faName)
                    };
                    attrsArray.Add(attrObj);
                }
            }

            var condsArray = new JArray();
            var conds = filter.GetType().GetProperty("Conditions")?.GetValue(filter, null) as IEnumerable;
            if (conds != null)
            {
                foreach (var c in conds)
                {
                    string val = c.GetType().GetProperty("Value")?.GetValue(c, null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                        condsArray.Add(val);
                }
            }

            return new JObject
            {
                ["level"] = level.Id,
                ["attributes"] = attrsArray,
                ["conditions"] = condsArray
            };
        }

        /// Read-only XML projection of the standard WorkWith filter, used when the native
        /// surface is unavailable. The real nodes are `filterAttribute` under `attributes`
        /// and `condition` under `conditions` (see PatternChildOrderReconciler).
        internal static bool TryListFiltersFromXml(string xml, string levelId, out JObject filters, out string error, out string errorMessage)
        {
            filters = null;
            error = null;
            errorMessage = null;
            try
            {
                var doc = XDocument.Parse(xml);
                if (!TryResolveFilterLevel(CollectXmlFilterLevels(doc), levelId, out var resolved, out error, out errorMessage))
                    return false;

                var filter = (XElement)resolved.Filter;
                var attrsArray = new JArray();
                var attrsContainer = ChildNamed(filter, "attributes");
                if (attrsContainer != null)
                {
                    foreach (var a in attrsContainer.Elements().Where(e => HasLocalName(e, "filterAttribute")))
                    {
                        string aName = (string)a.Attribute("name") ?? "";
                        var attrObj = new JObject
                        {
                            ["name"] = aName,
                            ["description"] = (string)a.Attribute("description") ?? aName,
                            ["domain"] = StripObjectReferenceGuid((string)a.Attribute("domain")) ?? "",
                            ["default"] = (string)a.Attribute("default") ?? "",
                            ["variableName"] = (string)a.Attribute("variableName") ?? ("&" + aName)
                        };
                        attrsArray.Add(attrObj);
                    }
                }

                var condsArray = new JArray();
                var condsContainer = ChildNamed(filter, "conditions");
                if (condsContainer != null)
                {
                    foreach (var c in condsContainer.Elements().Where(e => HasLocalName(e, "condition")))
                    {
                        string val = (string)c.Attribute("value");
                        if (!string.IsNullOrWhiteSpace(val)) condsArray.Add(val);
                    }
                }

                filters = new JObject
                {
                    ["level"] = resolved.Id,
                    ["attributes"] = attrsArray,
                    ["conditions"] = condsArray
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "XmlParseError: " + ex.Message;
                errorMessage = "PatternInstance XML could not be parsed: " + ex.Message;
                return false;
            }
        }

        private static List<FilterLevelCandidate> CollectXmlFilterLevels(XDocument doc)
        {
            var candidates = new List<FilterLevelCandidate>();
            var levels = doc.Descendants().Where(e => HasLocalName(e, "level")).ToList();
            if (levels.Count == 0)
            {
                // Some XML variants store Selection directly under root
                var directSelection = doc.Descendants().FirstOrDefault(e => HasLocalName(e, "selection"));
                if (directSelection != null)
                    candidates.Add(new FilterLevelCandidate { Id = "Root", HasSelection = true, Filter = ChildNamed(directSelection, "filter") });
                return candidates;
            }

            int index = 0;
            foreach (var lvl in levels)
            {
                var selection = ChildNamed(lvl, "selection");
                candidates.Add(new FilterLevelCandidate
                {
                    Id = LevelIdOrDefault((string)lvl.Attribute("id") ?? (string)lvl.Attribute("name"), index),
                    HasSelection = selection != null,
                    Filter = selection != null ? ChildNamed(selection, "filter") : null
                });
                index++;
            }
            return candidates;
        }

        private static bool HasLocalName(XElement element, string localName)
        {
            return string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);
        }

        private static XElement ChildNamed(XElement parent, string localName)
        {
            return parent.Elements().FirstOrDefault(e => HasLocalName(e, localName));
        }

        /// Pattern XML stores object references as `{guid}-{Name}`; readers report the name.
        private static string StripObjectReferenceGuid(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return reference;
            var parts = reference.Split(new[] { '-' }, 6);
            return parts.Length == 6 && Guid.TryParse(string.Join("-", parts, 0, 5), out _) ? parts[5] : reference;
        }
    }
}
