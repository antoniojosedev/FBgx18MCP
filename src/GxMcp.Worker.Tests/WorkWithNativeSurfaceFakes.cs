using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// Hermetic stand-ins for the typed Artech.Patterns.WorkWith surface. The adapter
// resolves every member by name through reflection, so declaring these types under
// the SDK namespace inside the test assembly lets WorkWithSdkAdapter.AssemblyResolverOverride
// route the native filter reader and writer at a model that needs no GeneXus
// installation and no Knowledge Base. Member shapes mirror the GeneXus 18 surface
// recorded in docs/sdk-probe/workwith-filter-authoring.md: `Default` is a settable
// string, `DomainName` and `VariableName` are derived read-only values.
namespace Artech.Patterns.WorkWith
{
    // One shared stand-in for the filter tests (#588 typed writer, #589 read source,
    // #586 native removal): the adapter resolves these types by name in a single
    // assembly, so the test assembly can hold only one WorkWithInstance.
    public sealed class WorkWithInstance
    {
        /// Returned by `Load` when no LoadHook is set (#586 removal tests).
        public static WorkWithInstance Current;

        /// Scripts what the static `Load` returns or throws (#589, #586).
        public static Func<object, object> LoadHook;

        public static int LoadCalls;

        // The #589 tests replace Levels with arbitrary shapes (or a throwing enumerable);
        // the typed tests use the List built by AddLevel / LevelList.
        public object Levels { get; set; } = new List<LevelElement>();

        public List<LevelElement> LevelList => (List<LevelElement>)Levels;

        public object SavedTo { get; private set; }

        public int SaveToCalls;

        public static object Load(object instance)
        {
            LoadCalls++;
            return LoadHook != null ? LoadHook(instance) : Current;
        }

        public void SaveTo(object instance)
        {
            SaveToCalls++;
            SavedTo = instance;
        }

        public LevelElement AddLevel(string id)
        {
            var level = new LevelElement { Id = id, Selection = new SelectionElement { Filter = new FilterElement() } };
            LevelList.Add(level);
            return level;
        }
    }

    public sealed class LevelElement
    {
        public string Id { get; set; }
        public SelectionElement Selection { get; set; }
    }

    public sealed class SelectionElement
    {
        public FilterElement Filter { get; set; }
    }

    public sealed class FilterElement
    {
        public IEnumerable Attributes { get; set; } = new FilterAttributesElement();
        public IEnumerable Conditions { get; set; } = new ConditionsElement();

        public FilterAttributeElement AddFilterAttribute(string name)
        {
            var attribute = new FilterAttributeElement { Name = name };
            ((IList)Attributes).Add(attribute);
            return attribute;
        }

        public ConditionElement AddCondition(string value)
        {
            var condition = new ConditionElement { Value = value };
            ((IList)Conditions).Add(condition);
            return condition;
        }
    }

    public sealed class FilterAttributesElement : List<FilterAttributeElement> { }

    public sealed class ConditionsElement : List<ConditionElement> { }

    /// A collection that enumerates but exposes no Remove member: the "no native
    /// removal route" shape the adapter must refuse instead of writing XML.
    public sealed class SealedAttributes : IEnumerable
    {
        private readonly List<FilterAttributeElement> _items;
        public SealedAttributes(IEnumerable<FilterAttributeElement> items) { _items = items.ToList(); }
        public IEnumerator GetEnumerator() { return _items.GetEnumerator(); }
    }

    public sealed class FilterAttributeElement
    {
        private string _domainName;

        public string Name { get; set; }
        public string Description { get; set; }
        public object Domain { get; set; }

        /// Derived from Domain as on GeneXus 18; the #586 fixtures set it directly.
        public string DomainName
        {
            get { return _domainName ?? Domain?.ToString(); }
            set { _domainName = value; }
        }

        public string Default { get; set; }
        public string VariableName => "&" + Name;
    }

    public sealed class ConditionElement
    {
        public string Value { get; set; }
    }
}
