using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Packages.Patterns.Specification;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// <see cref="IPatternSpecView"/> over the pattern's instance specification
    /// (<c>PatternDefinition.InstanceSpecification</c>), as the SDK loaded it.
    /// </summary>
    internal sealed class SdkPatternSpecView : IPatternSpecView
    {
        private readonly SpecificationRoot _root;
        private readonly Dictionary<string, HashSet<string>> _attributes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        internal SdkPatternSpecView(SpecificationRoot root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string RootType() => _root.RootType;

        public string ChildType(string parentType, string childName)
        {
            try { return _root.FindSpecificationType(parentType)?.TryFindChildElement(childName)?.Type; }
            catch (Exception) { return null; }
        }

        // Declared by the type or, to stay on the lenient side, by any type derived from it.
        public IReadOnlyCollection<string> AttributeNames(string typeName)
        {
            if (_attributes.TryGetValue(typeName, out HashSet<string> cached)) return cached;
            HashSet<string> names = null;
            try
            {
                SpecificationType type = _root.FindSpecificationType(typeName);
                if (type != null)
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    Collect(type, names, new HashSet<string>(StringComparer.Ordinal));
                }
            }
            catch (Exception) { names = null; }
            _attributes[typeName] = names;
            return names;
        }

        private static void Collect(SpecificationType type, HashSet<string> names, HashSet<string> visited)
        {
            if (!visited.Add(type.Name)) return;
            foreach (SpecificationAttribute attribute in type.Attributes) names.Add(attribute.Name);
            IList<SpecificationType> derived = type.DerivedTypes;
            if (derived == null) return;
            foreach (SpecificationType child in derived) Collect(child, names, visited);
        }
    }
}
