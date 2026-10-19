using System;
using System.Collections.Generic;

// Hermetic stand-ins for the typed Artech.Patterns.WorkWith surface. The adapter
// resolves every member by name through reflection, so declaring these types under
// the SDK namespace inside the test assembly lets WorkWithSdkAdapter.AssemblyResolverOverride
// route the native filter reader and writer at a model that needs no GeneXus
// installation and no Knowledge Base. Member shapes mirror the GeneXus 18 surface
// recorded in docs/sdk-probe/workwith-filter-authoring.md: `Default` is a settable
// string, `DomainName` and `VariableName` are derived read-only values.
namespace Artech.Patterns.WorkWith
{
    public sealed class WorkWithInstance
    {
        // Issue #589 tests replace Levels with arbitrary shapes (or a throwing enumerable)
        // through LoadHook; the typed filter tests use the List built by AddLevel.
        public object Levels { get; set; } = new List<LevelElement>();

        /// Scripts what the static `Load` returns or throws (issue #589); null loads a fresh instance.
        public static Func<object, object> LoadHook;

        public object SavedTo { get; private set; }

        public static object Load(object instance)
        {
            return LoadHook != null ? LoadHook(instance) : new WorkWithInstance();
        }

        public void SaveTo(object instance)
        {
            SavedTo = instance;
        }

        public LevelElement AddLevel(string id)
        {
            var level = new LevelElement { Id = id, Selection = new SelectionElement { Filter = new FilterElement() } };
            ((List<LevelElement>)Levels).Add(level);
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
        public List<FilterAttributeElement> Attributes { get; } = new List<FilterAttributeElement>();
        public List<ConditionElement> Conditions { get; } = new List<ConditionElement>();

        public FilterAttributeElement AddFilterAttribute(string name)
        {
            var attribute = new FilterAttributeElement { Name = name };
            Attributes.Add(attribute);
            return attribute;
        }

        public ConditionElement AddCondition(string value)
        {
            var condition = new ConditionElement { Value = value };
            Conditions.Add(condition);
            return condition;
        }
    }

    public sealed class FilterAttributeElement
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public object Domain { get; set; }
        public string DomainName => Domain?.ToString();
        public string Default { get; set; }
        public string VariableName => "&" + Name;
    }

    public sealed class ConditionElement
    {
        public string Value { get; set; }
    }
}
