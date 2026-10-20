using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The SDK-facing surface of one WorkWithPlus <c>attribute</c> element, as far as the
    /// combo planner needs it. The production implementation wraps the native
    /// <c>PatternInstanceElement</c>; tests substitute a fake.
    /// </summary>
    internal interface IWwpComboElement
    {
        string Path { get; }
        /// <summary>The element's attribute reference in <c>&lt;typeGuid&gt;-&lt;Name&gt;</c> form.</summary>
        string Reference { get; }
        /// <summary>Whether the element specification declares the property (the only ones the deserializer keeps).</summary>
        bool HasProperty(string sdkName);
        string VisibleIf(string sdkName);
        bool IsReadOnly(string sdkName);
        bool IsApplicable(string sdkName);
        /// <summary>The effective value in the SDK storage format, defaults included.</summary>
        string Effective(string sdkName);
        IReadOnlyDictionary<string, string> EffectiveValues();
        /// <summary>
        /// Converts <paramref name="text"/> with the SDK's own converter (the one
        /// <c>FromXml</c> uses) and renders it back with the serializer's converter
        /// (<c>ToXml</c>), without changing the element.
        /// </summary>
        bool TryConvert(string sdkName, string text, out object typed, out string storage, out string error);
        /// <summary>Applies one value through the native change command.</summary>
        bool Apply(string sdkName, object typed, string expectedStorage);
    }

    internal sealed class WwpComboPlanItem
    {
        internal WwpComboProperties.Definition Definition;
        internal string Key;
        internal string RequestedText;
        internal object Typed;
        internal string SdkStorage;
        internal string Before;
        internal bool Changed;
        internal bool Relevant;
        internal bool RelevanceEvaluated;
        internal string VisibleIf;
    }

    internal sealed class WwpComboPlan
    {
        internal List<WwpComboPlanItem> Items = new List<WwpComboPlanItem>();
        internal JObject Error;
        internal bool AnyChange => Items.Any(i => i.Changed);

        internal Dictionary<string, string> Expected() =>
            Items.ToDictionary(i => i.Definition.SdkName, i => i.SdkStorage, StringComparer.Ordinal);

        /// <summary>Per-property predictions: what the caller asked for, what the SDK would store, what is stored now.</summary>
        internal JObject ToReport(string elementPath, string reference)
        {
            var properties = new JObject();
            foreach (WwpComboPlanItem item in Items)
                properties[item.Definition.PublicName] = new JObject
                {
                    ["sdkName"] = item.Definition.SdkName,
                    ["requested"] = item.RequestedText,
                    ["before"] = item.Before,
                    ["after"] = item.SdkStorage,
                    ["changed"] = item.Changed,
                    ["normalizedBySdk"] = !string.Equals(item.RequestedText, item.SdkStorage, StringComparison.Ordinal),
                    ["applicableNow"] = item.RelevanceEvaluated ? (JToken)item.Relevant : JValue.CreateNull()
                };
            return new JObject
            {
                ["element"] = elementPath,
                ["attribute"] = reference,
                ["properties"] = properties,
                ["unknownToSdk"] = new JArray(),
                ["discardedOnSave"] = new JArray(),
                ["serializationPath"] = "PropertiesObject.ConvertPropertyValueFromString/ConvertPropertyValueToString<StringStorage> - the converters PatternInstanceElementAttributes.FromXml/ToXml use; the element is not changed."
            };
        }
    }

    internal static class WwpComboPlanner
    {
        private static readonly string[] ContextProperties = { "controlInfoDef", "controlType", "controlInputDS", "controlLoadDynamically" };

        /// <summary>
        /// Checks every requested property against the SDK property model of the element and
        /// predicts the stored values. Nothing is written. The first problem found is the plan's
        /// <see cref="WwpComboPlan.Error"/>.
        /// </summary>
        internal static WwpComboPlan Plan(IWwpComboElement element, IReadOnlyList<WwpComboProperties.Requested> requested,
            IReadOnlyCollection<string> candidateNames)
        {
            var plan = new WwpComboPlan();
            foreach (WwpComboProperties.Requested request in requested)
            {
                string sdk = request.Definition.SdkName;
                if (!element.HasProperty(sdk))
                {
                    plan.Error = Error("ComboPropertyUnknownToSdk",
                        "The installed WorkWithPlus does not declare '" + sdk + "' on this element, so the SDK would discard it on save. No change was applied.",
                        new JObject { ["property"] = request.Definition.PublicName, ["sdkName"] = sdk, ["discardedOnSave"] = true });
                    return plan;
                }
                if (element.IsReadOnly(sdk))
                {
                    plan.Error = Error("ComboPropertyReadOnly", "'" + sdk + "' is read-only on this element.",
                        new JObject { ["property"] = request.Definition.PublicName, ["sdkName"] = sdk });
                    return plan;
                }
                if (!element.IsApplicable(sdk))
                {
                    plan.Error = Error("ComboPropertyNotApplicable", "'" + sdk + "' is not applicable to this element in the SDK.",
                        new JObject { ["property"] = request.Definition.PublicName, ["sdkName"] = sdk });
                    return plan;
                }
                if (!element.TryConvert(sdk, request.Text, out object typed, out string storage, out string convertError))
                {
                    plan.Error = Error("ComboPropertyRejectedBySdk",
                        "The SDK converter rejected the value for '" + sdk + "': " + convertError,
                        new JObject { ["property"] = request.Definition.PublicName, ["sdkName"] = sdk, ["requested"] = request.Text });
                    return plan;
                }
                string before = element.Effective(sdk) ?? string.Empty;
                plan.Items.Add(new WwpComboPlanItem
                {
                    Definition = request.Definition,
                    Key = request.Key,
                    RequestedText = request.Text,
                    Typed = typed,
                    SdkStorage = storage,
                    Before = before,
                    Changed = !string.Equals(before, storage, StringComparison.Ordinal),
                    VisibleIf = element.VisibleIf(sdk)
                });
            }

            Dictionary<string, string> after = element.EffectiveValues().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (WwpComboPlanItem item in plan.Items) after[item.Definition.SdkName] = item.SdkStorage;
            foreach (WwpComboPlanItem item in plan.Items)
            {
                item.Relevant = WwpComboProperties.IsApplicable(item.VisibleIf, after, out bool evaluated);
                item.RelevanceEvaluated = evaluated;
                // Clearing a property back to its empty/false value is always allowed; a
                // property the control cannot use is refused only when it is being set.
                bool clearing = item.SdkStorage.Length == 0 || item.SdkStorage == "False";
                if (item.Changed && !clearing && !item.Relevant)
                {
                    var context = new JObject();
                    foreach (string name in ContextProperties)
                        context[name] = element.Effective(name);
                    plan.Error = Error("ComboPropertyNotApplicable",
                        "'" + item.Definition.SdkName + "' has no effect on this attribute as configured: it needs " + item.Definition.Requirement
                        + ". Configure the control first (controlInfoDef, controlType, controlInputDS); this action does not change them.",
                        new JObject { ["property"] = item.Definition.PublicName, ["sdkName"] = item.Definition.SdkName,
                            ["requires"] = item.Definition.Requirement, ["current"] = context, ["visibleIf"] = item.VisibleIf });
                    return plan;
                }
            }

            WwpComboPlanItem names = plan.Items.FirstOrDefault(i => i.Definition.Kind == WwpComboProperties.ValueKind.NameList);
            if (names != null && names.SdkStorage.Length > 0 && candidateNames != null)
            {
                var unresolved = names.SdkStorage.Split(';')
                    .Where(n => !candidateNames.Contains(n.Split('.')[0], StringComparer.OrdinalIgnoreCase)).ToList();
                if (unresolved.Count > 0)
                {
                    plan.Error = Error("ComboConditionNameNotFound",
                        "The condition names " + string.Join(", ", unresolved.Select(n => "'" + n + "'"))
                        + " are neither attributes nor variables of this form; the WorkWithPlus validator rejects such a value. No change was applied.",
                        new JObject { ["property"] = names.Definition.PublicName, ["unresolved"] = new JArray(unresolved),
                            ["candidates"] = new JArray(candidateNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) });
                    return plan;
                }
            }
            return plan;
        }

        /// <summary>Applies the changed properties in <see cref="WwpComboProperties.ApplyOrder"/>. Returns the property that was refused, or null.</summary>
        internal static string Apply(IWwpComboElement element, WwpComboPlan plan)
        {
            foreach (string sdk in WwpComboProperties.ApplyOrder)
            {
                WwpComboPlanItem item = plan.Items.FirstOrDefault(i => i.Definition.SdkName == sdk && i.Changed);
                if (item == null) continue;
                if (!element.Apply(sdk, item.Typed, item.SdkStorage)) return sdk;
            }
            return null;
        }

        private static JObject Error(string code, string message, JObject extra)
        {
            var error = new JObject { ["code"] = code, ["error"] = message };
            if (extra != null) foreach (JProperty property in extra.Properties()) error[property.Name] = property.Value;
            return error;
        }

        private static readonly Regex GuidPrefix = new Regex(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}-", RegexOptions.Compiled);

        internal static string NameFromReference(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return reference;
            Match match = GuidPrefix.Match(reference);
            return match.Success ? reference.Substring(match.Length) : reference;
        }

        internal static List<XElement> FindAttributeElements(XDocument document, string requested) =>
            document.Descendants()
                .Where(e => e.Name.LocalName.EndsWith("ttribute", StringComparison.OrdinalIgnoreCase)
                    && e.Attribute("attribute") != null
                    && Services.WwpActionService.AttributeReferenceMatches((string)e.Attribute("attribute"), requested))
                .ToList();

        /// <summary>
        /// The attribute and variable names of the form that holds <paramref name="target"/>: the
        /// top-level section of the instance (transaction, WPRoot, ...) it sits in, without the target itself.
        /// </summary>
        internal static List<string> CandidateNames(XElement target)
        {
            XElement scope = target;
            while (scope.Parent != null && scope.Parent.Parent != null) scope = scope.Parent;
            var names = new List<string>();
            foreach (XElement element in scope.DescendantsAndSelf())
            {
                if (ReferenceEquals(element, target)) continue;
                string reference = (string)element.Attribute("attribute");
                string variable = (string)element.Attribute("name");
                if (reference != null && element.Name.LocalName.EndsWith("ttribute", StringComparison.OrdinalIgnoreCase))
                    names.Add(NameFromReference(reference));
                else if (variable != null && element.Name.LocalName.EndsWith("ariable", StringComparison.OrdinalIgnoreCase))
                    names.Add(variable.TrimStart('&'));
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
