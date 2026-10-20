using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The combo properties of a WorkWithPlus <c>attribute</c> element that
    /// <c>genexus_wwp action=set_combo_properties</c> configures, and the pure rules
    /// that turn a caller's request into the text the SDK stores.
    /// </summary>
    /// <remarks>
    /// The names are the ones in the WorkWithPlus instance specification
    /// (<c>WorkWithPlusInstance.xml</c>, element type <c>Attribute</c>), not the
    /// captions the property grid shows. The pattern deserializer
    /// (<c>PatternInstanceElementAttributes.FromXml</c>) walks only the specification's
    /// attributes, so an XML attribute with any other name is discarded on save.
    /// </remarks>
    internal static class WwpComboProperties
    {
        internal enum ValueKind
        {
            Bool,
            ExpressionList,
            NameList
        }

        internal sealed class Definition
        {
            internal string PublicName;
            internal string SdkName;
            internal ValueKind Kind;
            internal string[] Aliases;
            internal string Requirement;
        }

        internal const string ParametersName = "Parameters";
        internal const string LoadDataDynamicallyName = "LoadDataDynamically";
        internal const string ConditionsName = "ConditionsDynamicAttsOrVars";

        internal static readonly Definition[] All =
        {
            new Definition
            {
                PublicName = ParametersName,
                SdkName = "controlInputDPParameters",
                Kind = ValueKind.ExpressionList,
                Aliases = new[] { "Parameters", "DataProviderParameters", "controlInputDPParameters" },
                Requirement = "controlInfoDef=Custom, controlType Dynamic Combo Box|Dynamic List Box|DVelop Combo|Extended Suggest, controlInputDS='Data Provider'"
            },
            new Definition
            {
                PublicName = LoadDataDynamicallyName,
                SdkName = "controlLoadDynamically",
                Kind = ValueKind.Bool,
                Aliases = new[] { "LoadDataDynamically", "Load Data Dynamically", "LoadDynamically", "controlLoadDynamically" },
                Requirement = "controlInfoDef=Custom, controlType DVelop Combo|Extended Suggest, controlInputDS!='Fixed Values'"
            },
            new Definition
            {
                PublicName = ConditionsName,
                SdkName = "conditionDynAtts",
                Kind = ValueKind.NameList,
                Aliases = new[] { "ConditionsDynamicAttsOrVars", "Conditions dynamic atts. or vars.", "ConditionDynAtts", "conditionDynAtts" },
                Requirement = "controlInfoDef=Custom, controlType DVelop Combo|Extended Suggest, controlLoadDynamically=True"
            }
        };

        /// <summary>The order the SDK properties must be applied in: the loading flag first, because it makes the conditions applicable.</summary>
        internal static readonly string[] ApplyOrder = { "controlInputDPParameters", "controlLoadDynamically", "conditionDynAtts" };

        internal static string Normalize(string key) =>
            Regex.Replace(key ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();

        internal static Definition Find(string key)
        {
            string normalized = Normalize(key);
            if (normalized.Length == 0) return null;
            return All.FirstOrDefault(d => d.Aliases.Any(a => Normalize(a) == normalized));
        }

        internal static Definition FindBySdkName(string sdkName) =>
            All.FirstOrDefault(d => string.Equals(d.SdkName, sdkName, StringComparison.Ordinal));

        /// <summary>
        /// The supported property a caller most likely meant by an unknown key: the key
        /// contains (or is contained in) a supported name once punctuation and case are
        /// ignored, or it is one edit or two away.
        /// </summary>
        internal static Definition Suggest(string key)
        {
            string normalized = Normalize(key);
            if (normalized.Length < 4) return null;
            Definition best = null;
            int bestDistance = int.MaxValue;
            foreach (Definition definition in All)
            {
                foreach (string alias in definition.Aliases)
                {
                    string candidate = Normalize(alias);
                    if (candidate.Length >= 6 && (normalized.Contains(candidate) || candidate.Contains(normalized)))
                        return definition;
                    int distance = EditDistance(normalized, candidate);
                    if (distance < bestDistance) { bestDistance = distance; best = definition; }
                }
            }
            return bestDistance <= 3 ? best : null;
        }

        private static int EditDistance(string a, string b)
        {
            var row = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) row[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }

        internal sealed class Requested
        {
            internal Definition Definition;
            internal string Key;
            /// <summary>The text in the SDK storage format the caller asked for, before the SDK's own converter runs.</summary>
            internal string Text;
            internal JToken Raw;
        }

        /// <summary>
        /// Parses the <c>properties</c> object. Returns the typed error (code, error,
        /// details) for the first invalid entry, or null with <paramref name="requested"/>
        /// filled.
        /// </summary>
        internal static JObject Parse(JToken properties, out List<Requested> requested)
        {
            requested = new List<Requested>();
            if (!(properties is JObject map) || !map.HasValues)
                return Error("MissingComboProperties", "properties must be an object with at least one combo property.",
                    new JObject { ["supported"] = SupportedNames() });

            var unknown = new JArray();
            foreach (JProperty entry in map.Properties())
            {
                Definition definition = Find(entry.Name);
                if (definition == null)
                {
                    Definition suggestion = Suggest(entry.Name);
                    unknown.Add(new JObject
                    {
                        ["property"] = entry.Name,
                        ["didYouMean"] = suggestion?.PublicName,
                        ["sdkName"] = suggestion?.SdkName
                    });
                    continue;
                }
                if (requested.Any(r => r.Definition == definition))
                    return Error("DuplicateComboProperty", "The property '" + definition.PublicName + "' was given more than once.",
                        new JObject { ["property"] = entry.Name });
                JObject valueError = ParseValue(definition, entry.Value, out string text);
                if (valueError != null) { valueError["property"] = entry.Name; return valueError; }
                requested.Add(new Requested { Definition = definition, Key = entry.Name, Text = text, Raw = entry.Value });
            }

            if (unknown.Count > 0)
                return Error("UnknownComboProperty",
                    "The WorkWithPlus SDK does not know " + string.Join(", ", unknown.Select(u => "'" + u["property"] + "'"))
                    + " on an attribute: a property outside the pattern specification is discarded when the instance is saved. No change was applied.",
                    new JObject { ["unknown"] = unknown, ["supported"] = SupportedNames() });
            return null;
        }

        internal static JArray SupportedNames() => new JArray(All.Select(d => new JObject
        {
            ["property"] = d.PublicName,
            ["sdkName"] = d.SdkName,
            ["accepts"] = d.Kind == ValueKind.Bool ? "boolean"
                : d.Kind == ValueKind.ExpressionList ? "array of expressions or one comma-separated string"
                : "array of attribute/variable names or one ';'-separated string",
            ["requires"] = d.Requirement
        }));

        private static JObject ParseValue(Definition definition, JToken value, out string text)
        {
            text = null;
            switch (definition.Kind)
            {
                case ValueKind.Bool:
                    if (value.Type == JTokenType.Boolean) { text = value.ToObject<bool>() ? "True" : "False"; return null; }
                    string flag = value.Type == JTokenType.String ? value.ToString().Trim() : null;
                    if (string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) { text = "True"; return null; }
                    if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)) { text = "False"; return null; }
                    return Error("InvalidComboPropertyValue", definition.PublicName + " must be true or false.", null);
                case ValueKind.ExpressionList:
                    return ParseExpressionList(definition, value, out text);
                default:
                    return ParseNameList(definition, value, out text);
            }
        }

        private static JObject ParseExpressionList(Definition definition, JToken value, out string text)
        {
            text = null;
            List<string> items;
            if (value is JArray array)
            {
                items = array.Select(item => item.Type == JTokenType.String ? item.ToString().Trim() : null).ToList();
                if (items.Any(string.IsNullOrEmpty))
                    return Error("InvalidComboPropertyValue", definition.PublicName + " items must be non-empty strings.", null);
            }
            else if (value.Type == JTokenType.String)
            {
                string whole = value.ToString().Trim();
                items = whole.Length == 0 ? new List<string>() : SplitTopLevel(whole, out _);
                if (items.Any(string.IsNullOrWhiteSpace))
                    return Error("InvalidComboPropertyValue", definition.PublicName + " has an empty item between commas.", null);
            }
            else return Error("InvalidComboPropertyValue", definition.PublicName + " must be an array of expressions or a comma-separated string.", null);

            foreach (string item in items)
            {
                if (!IsBalanced(item))
                    return Error("InvalidComboPropertyValue",
                        definition.PublicName + " item '" + item + "' has unbalanced parentheses, brackets or quotes.", null);
                if (value is JArray && SplitTopLevel(item, out _).Count > 1)
                    return Error("InvalidComboPropertyValue",
                        definition.PublicName + " item '" + item + "' contains a top-level comma; pass one expression per array item.", null);
            }
            text = string.Join(", ", items.Select(i => i.Trim()));
            return null;
        }

        private static readonly Regex XmlAttributeName = new Regex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

        private static readonly Regex NamePattern =new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        private static JObject ParseNameList(Definition definition, JToken value, out string text)
        {
            text = null;
            IEnumerable<string> raw;
            if (value is JArray array)
            {
                if (array.Any(item => item.Type != JTokenType.String))
                    return Error("InvalidComboPropertyValue", definition.PublicName + " items must be strings.", null);
                raw = array.Select(item => item.ToString());
            }
            else if (value.Type == JTokenType.String)
            {
                string whole = value.ToString().Trim();
                raw = whole.Length == 0 ? new string[0] : whole.Split(';');
            }
            else return Error("InvalidComboPropertyValue", definition.PublicName + " must be an array of names or a ';'-separated string.", null);

            var names = new List<string>();
            foreach (string item in raw)
            {
                string name = item.Trim();
                if (name.StartsWith("&", StringComparison.Ordinal)) name = name.Substring(1);
                if (!NamePattern.IsMatch(name))
                    return Error("InvalidComboPropertyValue",
                        definition.PublicName + " item '" + item.Trim() + "' is not an attribute or variable name (names are separated by ';', not by commas or spaces).", null);
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return Error("InvalidComboPropertyValue", definition.PublicName + " names '" + name + "' twice.", null);
                names.Add(name);
            }
            text = string.Join(";", names);
            return null;
        }

        /// <summary>Splits on commas outside quotes, parentheses and brackets. Null parts mean the text is unbalanced.</summary>
        internal static List<string> SplitTopLevel(string text, out bool balanced)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; current.Append(c); continue; }
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                if (c == ',' && depth == 0) { parts.Add(current.ToString().Trim()); current.Clear(); continue; }
                current.Append(c);
            }
            parts.Add(current.ToString().Trim());
            balanced = depth == 0 && quote == '\0';
            return parts;
        }

        internal static bool IsBalanced(string text)
        {
            SplitTopLevel(text, out bool balanced);
            return balanced;
        }

        /// <summary>
        /// Evaluates a specification <c>VisibleIf</c> (an XPath over the element, such as
        /// <c>self::*[@controlInfoDef='Custom' and ...]</c>) against effective property values.
        /// An empty or unevaluable expression is reported as applicable with
        /// <paramref name="evaluated"/> false.
        /// </summary>
        internal static bool IsApplicable(string visibleIf, IReadOnlyDictionary<string, string> effectiveValues, out bool evaluated)
        {
            evaluated = false;
            if (string.IsNullOrWhiteSpace(visibleIf)) return true;
            try
            {
                var element = new XElement("attribute");
                foreach (var pair in effectiveValues)
                    if (XmlAttributeName.IsMatch(pair.Key))
                        element.SetAttributeValue(pair.Key, pair.Value ?? string.Empty);
                object result = element.XPathEvaluate(visibleIf);
                evaluated = true;
                if (result is bool flag) return flag;
                return result is System.Collections.IEnumerable nodes && nodes.Cast<object>().Any();
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static JObject Error(string code, string message, JObject extra)
        {
            var error = new JObject { ["code"] = code, ["error"] = message };
            if (extra != null) foreach (JProperty property in extra.Properties()) error[property.Name] = property.Value;
            return error;
        }
    }
}
