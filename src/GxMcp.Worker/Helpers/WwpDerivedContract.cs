using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The call contract between a WorkWithPlus generated helper (for example the
    /// <c>LoadDVCombo&lt;Trn&gt;</c> procedure) and the code that calls it.
    ///
    /// <para>
    /// A helper declares its contract in its <c>parm(...)</c> rule plus the types of the
    /// variables it names; a caller states it by the argument list of each call. After a
    /// pattern edit the two live in different objects and are regenerated separately, so
    /// either side can be left on the old shape while the other is already on the new one.
    /// The argument count alone does not catch that: swapping two attributes, or putting a
    /// numeric key where a GUID was expected, keeps the count and breaks the meaning. This
    /// class compares count, order, variable names and declared types, position by
    /// position, and reports each divergence with the position it was found at.
    /// </para>
    ///
    /// <para>
    /// Pure text analysis: no SDK type is referenced, so the rules are unit-testable
    /// and apply identically to every GeneXus major.
    /// </para>
    /// </summary>
    internal static class WwpDerivedContract
    {
        internal sealed class Parameter
        {
            internal int Position;
            internal string Direction;
            internal string Name;
            internal string Type;
        }

        internal sealed class CallSite
        {
            internal string Caller;
            internal string Part;
            internal int Line;
            internal List<string> Arguments = new List<string>();
        }

        /// <summary>One text part of one object that may contain calls to the helper.</summary>
        internal sealed class CallerPart
        {
            internal string Object;
            internal string Part;
            internal string Text;
            internal string Variables;
        }

        private static readonly Regex ParmRule = new Regex(
            @"\bparm\s*\((?<list>[^;]*?)\)\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex VariableLine = new Regex(
            @"^\s*&(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<type>.+?)\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex ParameterItem = new Regex(
            @"^\s*(?:(?<dir>in|out|inout)\s*:\s*)?&?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SimpleVariable = new Regex(
            @"^&(?<name>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);

        /// <summary>The declared variables of a Variables part: name (no <c>&amp;</c>) to its type text.</summary>
        internal static Dictionary<string, string> ParseVariables(string variables)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(variables)) return result;
            foreach (string line in variables.Replace("\r\n", "\n").Split('\n'))
            {
                Match match = VariableLine.Match(line);
                if (match.Success) result[match.Groups["name"].Value] = NormalizeType(match.Groups["type"].Value);
            }
            return result;
        }

        /// <summary>
        /// The ordered <c>parm(...)</c> list of a Rules part, with each parameter's declared
        /// type taken from the same object's Variables. An empty list means no parm rule.
        /// </summary>
        internal static List<Parameter> ParseParameters(string rules, string variables)
        {
            var result = new List<Parameter>();
            Match match = ParmRule.Match(StripComments(rules));
            if (!match.Success) return result;
            Dictionary<string, string> types = ParseVariables(variables);
            int position = 0;
            foreach (string item in SplitTopLevel(match.Groups["list"].Value))
            {
                Match parsed = ParameterItem.Match(item);
                if (!parsed.Success) continue;
                string name = parsed.Groups["name"].Value;
                types.TryGetValue(name, out string type);
                result.Add(new Parameter
                {
                    Position = position++,
                    Direction = parsed.Groups["dir"].Success ? parsed.Groups["dir"].Value.ToLowerInvariant() : "inout",
                    Name = name,
                    Type = type
                });
            }
            return result;
        }

        /// <summary>
        /// Every call to <paramref name="helperName"/> in a text part: the plain
        /// <c>Helper(...)</c> form and the <c>Helper.Call(...)</c> form. Comments and string
        /// literals are blanked first, so a mention in a comment is not a call.
        /// </summary>
        internal static List<CallSite> FindCalls(string text, string objectName, string partName, string helperName)
        {
            var calls = new List<CallSite>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(helperName)) return calls;
            string clean = StripComments(text);
            var pattern = new Regex(
                @"(?<![A-Za-z0-9_.&])" + Regex.Escape(helperName) + @"(?:\s*\.\s*Call)?\s*\(",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match match in pattern.Matches(clean))
            {
                int open = match.Index + match.Length - 1;
                int close = FindClosingParenthesis(clean, open);
                if (close < 0) continue;
                var call = new CallSite
                {
                    Caller = objectName,
                    Part = partName,
                    Line = 1 + clean.Take(match.Index).Count(c => c == '\n')
                };
                string inside = clean.Substring(open + 1, close - open - 1);
                if (!string.IsNullOrWhiteSpace(inside))
                    foreach (string argument in SplitTopLevel(inside)) call.Arguments.Add(argument.Trim());
                calls.Add(call);
            }
            return calls;
        }

        /// <summary>
        /// Compares one helper against every call found in <paramref name="callers"/>.
        /// <c>consistent</c> is false when any call has a different argument count, the same
        /// names in a different order, or a position whose declared types disagree. A name
        /// difference that is not an order change is reported as a warning only: a hand-written
        /// call may legitimately pass a differently named variable of the same type.
        /// </summary>
        internal static JObject Evaluate(string helperName, string helperRules, string helperVariables,
            IEnumerable<CallerPart> callers)
        {
            List<Parameter> parameters = ParseParameters(helperRules, helperVariables);
            var mismatches = new JArray();
            var warnings = new JArray();
            var callReports = new JArray();
            int callCount = 0;

            foreach (CallerPart part in callers ?? Enumerable.Empty<CallerPart>())
            {
                Dictionary<string, string> callerTypes = ParseVariables(part.Variables);
                foreach (CallSite call in FindCalls(part.Text, part.Object, part.Part, helperName))
                {
                    callCount++;
                    var callMismatches = new JArray();
                    var arguments = new JArray();
                    for (int i = 0; i < call.Arguments.Count; i++)
                    {
                        string expression = call.Arguments[i];
                        Match variable = SimpleVariable.Match(expression);
                        string variableName = variable.Success ? variable.Groups["name"].Value : null;
                        string type = null;
                        if (variableName != null) callerTypes.TryGetValue(variableName, out type);
                        arguments.Add(new JObject
                        {
                            ["position"] = i,
                            ["expression"] = expression,
                            ["variable"] = variableName,
                            ["type"] = type
                        });
                    }

                    if (call.Arguments.Count != parameters.Count)
                        callMismatches.Add(Mismatch("argumentCount", null, call,
                            "The call passes " + call.Arguments.Count + " argument(s) and '" + helperName + "' declares "
                            + parameters.Count + " parameter(s)."));
                    else
                        CompareArguments(helperName, parameters, call, callerTypes, callMismatches, warnings);

                    foreach (JToken mismatch in callMismatches) mismatches.Add(mismatch.DeepClone());
                    callReports.Add(new JObject
                    {
                        ["caller"] = call.Caller,
                        ["part"] = call.Part,
                        ["line"] = call.Line,
                        ["arguments"] = arguments,
                        ["consistent"] = callMismatches.Count == 0
                    });
                }
            }

            return new JObject
            {
                ["helper"] = helperName,
                ["hasParmRule"] = parameters.Count > 0,
                ["parameters"] = new JArray(parameters.Select(p => new JObject
                {
                    ["position"] = p.Position,
                    ["direction"] = p.Direction,
                    ["name"] = p.Name,
                    ["type"] = p.Type
                })),
                ["calls"] = callReports,
                ["callCount"] = callCount,
                ["mismatches"] = mismatches,
                ["warnings"] = warnings,
                ["consistent"] = mismatches.Count == 0
            };
        }

        private static void CompareArguments(string helperName, List<Parameter> parameters, CallSite call,
            Dictionary<string, string> callerTypes, JArray mismatches, JArray warnings)
        {
            var argumentNames = new List<string>();
            foreach (string argument in call.Arguments)
            {
                Match variable = SimpleVariable.Match(argument);
                argumentNames.Add(variable.Success ? variable.Groups["name"].Value : null);
            }

            // The same names in another order: the count still matches, the meaning does not.
            bool allNamed = argumentNames.All(n => n != null);
            bool samePositions = true;
            for (int i = 0; i < parameters.Count; i++)
                if (!string.Equals(argumentNames[i], parameters[i].Name, StringComparison.OrdinalIgnoreCase))
                    samePositions = false;
            bool permutation = allNamed && !samePositions
                && argumentNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(parameters.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);
            if (permutation)
                mismatches.Add(Mismatch("order", null, call,
                    "The call passes '" + string.Join(", ", argumentNames.Select(n => "&" + n))
                    + "' and '" + helperName + "' declares '" + string.Join(", ", parameters.Select(p => "&" + p.Name))
                    + "': same variables, different order."));

            for (int i = 0; i < parameters.Count; i++)
            {
                Parameter parameter = parameters[i];
                string argumentName = argumentNames[i];
                string argumentType = null;
                if (argumentName != null) callerTypes.TryGetValue(argumentName, out argumentType);

                TypeRelation relation = Relate(parameter.Type, argumentType);
                if (relation == TypeRelation.Different)
                    mismatches.Add(Mismatch("type", i, call,
                        "Argument " + (i + 1) + " ('" + call.Arguments[i] + "' : " + argumentType + ") does not fit parameter '&"
                        + parameter.Name + "' : " + parameter.Type + "."));
                else if (relation == TypeRelation.Unverifiable)
                    warnings.Add(Warning("typeUnverifiable", i, call,
                        "Argument " + (i + 1) + " ('" + call.Arguments[i] + "') cannot be checked against parameter '&"
                        + parameter.Name + "' : " + (parameter.Type ?? "(undeclared)") + "."));

                if (!permutation && argumentName != null
                    && !string.Equals(argumentName, parameter.Name, StringComparison.OrdinalIgnoreCase))
                    warnings.Add(Warning("name", i, call,
                        "Argument " + (i + 1) + " is '&" + argumentName + "' and the parameter is '&" + parameter.Name + "'."));
            }
        }

        private enum TypeRelation { Same, Different, Unverifiable }

        // Two reference types ("Attribute:X", "Domain:X", "SDT:X") are the same only when they
        // name the same target; two basic types only when their normalized text is equal. A
        // reference compared with a basic type, or a type that is not declared, is left open
        // instead of guessed.
        private static TypeRelation Relate(string parameterType, string argumentType)
        {
            if (string.IsNullOrWhiteSpace(parameterType) || string.IsNullOrWhiteSpace(argumentType))
                return TypeRelation.Unverifiable;
            string left = Canonical(parameterType);
            string right = Canonical(argumentType);
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return TypeRelation.Same;
            bool leftReference = left.IndexOf(':') >= 0;
            bool rightReference = right.IndexOf(':') >= 0;
            return leftReference == rightReference ? TypeRelation.Different : TypeRelation.Unverifiable;
        }

        private static string Canonical(string type)
        {
            string text = NormalizeType(type);
            int comma = text.IndexOf(',');
            // "DVB_SDTComboData, WorkWithPlus_Web" names a module, not a different type.
            return comma >= 0 ? text.Substring(0, comma).Trim() : text;
        }

        private static string NormalizeType(string type) => Regex.Replace(type ?? string.Empty, @"\s+", " ").Trim();

        private static JObject Mismatch(string kind, int? position, CallSite call, string message) => new JObject
        {
            ["kind"] = kind,
            ["position"] = position,
            ["caller"] = call.Caller,
            ["part"] = call.Part,
            ["line"] = call.Line,
            ["message"] = message
        };

        private static JObject Warning(string kind, int position, CallSite call, string message) =>
            Mismatch(kind, position, call, message);

        /// <summary>
        /// The text with comments and string literals replaced by spaces of the same length (newlines kept),
        /// so offsets and line numbers of the remaining code are unchanged.
        /// </summary>
        internal static string StripComments(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var output = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (c == '/' && next == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? text.Length : end + 2;
                    Blank(text, i, stop, output);
                    i = stop;
                }
                else if (c == '/' && next == '/')
                {
                    int end = text.IndexOf('\n', i);
                    int stop = end < 0 ? text.Length : end;
                    Blank(text, i, stop, output);
                    i = stop;
                }
                else if (c == '"' || c == '\'')
                {
                    int stop = i + 1;
                    while (stop < text.Length && text[stop] != c && text[stop] != '\n') stop++;
                    if (stop < text.Length && text[stop] == c) stop++;
                    Blank(text, i, stop, output);
                    i = stop;
                }
                else
                {
                    output.Append(c);
                    i++;
                }
            }
            return output.ToString();
        }

        private static void Blank(string text, int from, int to, StringBuilder output)
        {
            for (int i = from; i < to; i++) output.Append(text[i] == '\n' || text[i] == '\r' ? text[i] : ' ');
        }

        private static int FindClosingParenthesis(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (start <= text.Length) yield return text.Substring(start);
        }
    }
}
