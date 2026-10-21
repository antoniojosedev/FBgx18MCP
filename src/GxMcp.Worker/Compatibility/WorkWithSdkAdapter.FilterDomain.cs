using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    internal static partial class WorkWithSdkAdapter
    {
        /// A KB object as the filter authoring path sees it: enough identity to build the
        /// typed reference, the advisory and the receipt without holding the SDK type in
        /// hermetic tests.
        internal sealed class FilterKbObjectRef
        {
            public string Name;
            public Guid Guid;
            public string Type;
            public object Instance;

            public static FilterKbObjectRef FromKbObject(KBObject obj)
            {
                if (obj == null) return null;
                return new FilterKbObjectRef
                {
                    Name = obj.Name,
                    Guid = obj.Guid,
                    Type = obj.TypeDescriptor?.Name,
                    Instance = obj
                };
            }
        }

        /// Lookup seam: (name or GUID, object type name) -> the KB object, or null.
        internal delegate FilterKbObjectRef FilterObjectLookup(string nameOrGuid, string typeName);

        internal sealed class FilterDomainResolution
        {
            public string Requested;
            public string Name;
            public Guid Guid;
            /// "name" or "guid": which form of the request resolved.
            public string ResolvedBy;
            public object Instance;
        }

        internal const string FilterDomainTypedPersistence =
            "Applied through the typed FilterAttributeElement.Domain reference: the SDK stores a KBObjectReference (`domainReference`) "
            + "that serializes as `{guid}-Name` in the PatternInstance XML; list_filters reports the plain Domain name.";

        internal const string FilterDomainStringPersistence =
            "Applied through the FilterAttributeElement.DomainName string setter: the SDK stores the plain Domain name.";

        /// Resolves `domain` against the KB Domains by name or GUID; the `{guid}-Name`
        /// form the pattern XML stores is accepted too. A Domain that is given but does
        /// not resolve is an input error: the typed `Domain` reference cannot be built
        /// from a name the KB does not know (issue #587).
        internal static bool TryResolveFilterDomain(
            string domain,
            FilterObjectLookup lookup,
            out FilterDomainResolution resolution,
            out string errorCode,
            out string errorMessage)
        {
            resolution = null;
            errorCode = null;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(domain)) return true;

            string requested = domain.Trim();
            var attempts = new List<KeyValuePair<string, string>>();
            if (TrySplitObjectReference(requested, out string guidPart, out string namePart))
            {
                attempts.Add(new KeyValuePair<string, string>(guidPart, "guid"));
                attempts.Add(new KeyValuePair<string, string>(namePart, "name"));
            }
            else
            {
                attempts.Add(new KeyValuePair<string, string>(requested, Guid.TryParse(requested, out _) ? "guid" : "name"));
            }

            foreach (var attempt in attempts)
            {
                var found = lookup?.Invoke(attempt.Key, "Domain");
                if (found == null) continue;
                if (!string.IsNullOrEmpty(found.Type) && !string.Equals(found.Type, "Domain", StringComparison.OrdinalIgnoreCase)) continue;

                resolution = new FilterDomainResolution
                {
                    Requested = requested,
                    Name = string.IsNullOrEmpty(found.Name) ? attempt.Key : found.Name,
                    Guid = found.Guid,
                    ResolvedBy = attempt.Value,
                    Instance = found.Instance
                };
                return true;
            }

            string tried = string.Join(" and ", attempts.Select(a => (a.Value == "guid" ? "GUID '" : "name '") + a.Key + "'"));
            errorCode = "FilterDomainNotFound";
            errorMessage = $"Domain '{requested}' was not found in the Knowledge Base (looked up by {tried}). Pass the name or GUID of an existing Domain, or omit `domain`.";
            return false;
        }

        /// `{guid}-{Name}` is how pattern XML stores object references.
        private static bool TrySplitObjectReference(string reference, out string guidPart, out string namePart)
        {
            guidPart = null;
            namePart = null;
            if (string.IsNullOrEmpty(reference)) return false;
            var parts = reference.Split(new[] { '-' }, 6);
            if (parts.Length != 6) return false;
            string candidate = string.Join("-", parts, 0, 5);
            if (!Guid.TryParse(candidate, out _) || string.IsNullOrWhiteSpace(parts[5])) return false;
            guidPart = candidate;
            namePart = parts[5];
            return true;
        }

        /// The `&` prefix stays accepted on the filter attribute name but is not a
        /// discriminator: whether the filter targets a KB Attribute or a variable is
        /// decided by looking the plain name up as an Attribute (issue #587).
        internal static FilterKbObjectRef ResolveFilterAttributeTarget(string attributeName, FilterObjectLookup lookup)
        {
            if (lookup == null || string.IsNullOrWhiteSpace(attributeName)) return null;
            string plain = attributeName.Trim().TrimStart('&');
            if (plain.Length == 0) return null;
            var match = lookup(plain, "Attribute");
            if (match != null && !string.IsNullOrEmpty(match.Type) && !string.Equals(match.Type, "Attribute", StringComparison.OrdinalIgnoreCase))
                return null;
            return match;
        }

        /// Same structured shape as add_action's `signatureAdvisory`: the inspected facts,
        /// one boolean verdict, and `warning` only when the verdict is negative. The schema
        /// applies `domain` to variables only; a filter attribute that names a KB Attribute
        /// takes its type from that Attribute, so the Domain is still written but reported
        /// as advisory rather than refused (issue #587).
        internal static JObject BuildFilterDomainAdvisory(string attributeName, FilterKbObjectRef attributeTarget, FilterDomainResolution domain)
        {
            if (domain == null) return null;
            bool appliesToVariable = attributeTarget == null;
            var advisory = new JObject
            {
                ["domain"] = domain.Name,
                ["domainGuid"] = domain.Guid.ToString(),
                ["filterAttribute"] = attributeName ?? "",
                ["filterAttributeResolvedAs"] = appliesToVariable ? "Variable" : "Attribute",
                ["appliesToVariable"] = appliesToVariable
            };
            if (!appliesToVariable)
            {
                advisory["warning"] = $"Domain '{domain.Name}' was resolved, but filter attribute '{attributeName}' names the KB Attribute '{attributeTarget.Name}'. "
                    + "The WorkWith schema applies `domain` only to variables (a filter on an Attribute takes its type from the Attribute), "
                    + "so the Domain is stored on the filter attribute but the pattern may not use it.";
            }
            return advisory;
        }

        private static JObject DescribeDomainResolution(FilterDomainResolution resolution, string appliedThrough)
        {
            var described = new JObject
            {
                ["requested"] = resolution.Requested,
                ["name"] = resolution.Name,
                ["guid"] = resolution.Guid.ToString(),
                ["resolvedBy"] = resolution.ResolvedBy
            };
            if (appliedThrough == null)
            {
                described["applyPolicy"] = "typed FilterAttributeElement.Domain reference when the SDK exposes it (GeneXus 18 does); the DomainName string setter only on SDK builds that expose one";
            }
            else
            {
                described["appliedThrough"] = appliedThrough;
                described["persistedFormat"] = appliedThrough == "Domain" ? FilterDomainTypedPersistence : FilterDomainStringPersistence;
            }
            return described;
        }
    }
}
