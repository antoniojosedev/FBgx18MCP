using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Common.Diagnostics;

namespace GxMcp.Worker.Helpers
{
    public static class SdkDiagnosticsHelper
    {
        public static JArray GetDiagnostics(KBObject obj)
        {
            var issues = new JArray();
            try
            {
                // 1. Structural Validation (Standard SDK)
                var output = new OutputMessages();
                obj.Validate(output);

                foreach (var msg in output.OnlyMessages)
                {
                    issues.Add(CreateIssueFromSdkMessage(obj, msg));
                }

                // 2. Scan Parts for "Messages" property (Where parser/compiler errors live)
                foreach (var part in obj.Parts)
                {
                    CollectDetailedMessages(part, issues, obj, part.TypeDescriptor?.Name);
                }

                // 3. Scan the Object itself
                CollectDetailedMessages(obj, issues, obj, "Object");
            }
            catch (Exception ex)
            {
                Logger.Error("SdkDiagnosticsHelper Error: " + ex.Message);
            }
            return issues;
        }

        private static void CollectDetailedMessages(object target, JArray issues, KBObject contextObj, string partName)
        {
            try
            {
                var prop = target.GetType().GetProperty("Messages", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(target);
                    if (value is System.Collections.IEnumerable list)
                    {
                        foreach (object msg in list)
                        {
                            if (msg == null) continue;
                            issues.Add(CreateIssueFromSdkMessage(contextObj, msg, partName));
                        }
                    }
                }
            }
            catch { }
        }

        internal static JObject CreateIssueFromSdkMessage(KBObject obj, object msg, string partName = null)
        {
            string msgText = msg.ToString();
            string msgCode = "SDK";
            string severity = "Error";
            int line = 1;
            int column = 1;

            try {
                // Reflection over dynamic: SDK message types (OutputError, Message, ParserMessage, ...)
                // shift between GeneXus updates. dynamic + null-coalescing on missing members throws
                // RuntimeBinderException per call, which is slow and wipes out the actual code/text
                // (e.g. src0216) when it does. Reflection lets us probe each candidate name explicitly.
                Type t = msg.GetType();
                string textValue = ReadStringMember(msg, t, "Text") ?? ReadStringMember(msg, t, "Description") ?? ReadStringMember(msg, t, "Message");
                if (!string.IsNullOrEmpty(textValue)) msgText = textValue;

                string codeValue = ReadStringMember(msg, t, "ErrorCode") ?? ReadStringMember(msg, t, "Id") ?? ReadStringMember(msg, t, "Code");
                if (!string.IsNullOrEmpty(codeValue)) msgCode = codeValue;

                object position = ReadMember(msg, t, "Position");
                if (position != null && position.GetType().Name.Contains("TextPosition")) {
                    Type pt = position.GetType();
                    object pl = ReadMember(position, pt, "Line");
                    object pc = ReadMember(position, pt, "Char") ?? ReadMember(position, pt, "Column");
                    if (pl is IConvertible) { try { line = Convert.ToInt32(pl); } catch {} }
                    if (pc is IConvertible) { try { column = Convert.ToInt32(pc); } catch {} }
                }

                string lvl = (ReadMember(msg, t, "Level")?.ToString()) ?? (ReadMember(msg, t, "Type")?.ToString()) ?? "";
                if (lvl.IndexOf("Warn", StringComparison.OrdinalIgnoreCase) >= 0) severity = "Warning";
                else if (lvl.IndexOf("Info", StringComparison.OrdinalIgnoreCase) >= 0) severity = "Information";
            } catch { }

            // Heuristic for line numbers in plain text messages
            if (line == 1) {
                var match = System.Text.RegularExpressions.Regex.Match(msgText, @"line\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) int.TryParse(match.Groups[1].Value, out line);
            }

            var issue = new JObject();
            issue["code"] = msgCode;
            issue["title"] = "GeneXus Error/Message";
            issue["severity"] = severity;
            issue["description"] = msgText;
            issue["line"] = line;
            issue["column"] = column;
            issue["part"] = partName ?? GetObjectDefaultPart(obj);
            issue["suggestion"] = InferSuggestion(msgText);
            return issue;
        }

        // Per-(type,name) accessor cache. SDK message types are stable within a process,
        // and GetDiagnostics fans out to ~9 ReadMember calls per message in tight loops —
        // resolving the MemberInfo every time was burning cycles for no benefit.
        // null sentinel means "no such member"; we still cache that result.
        private static readonly ConcurrentDictionary<(Type, string), Func<object, object>> _accessorCache
            = new ConcurrentDictionary<(Type, string), Func<object, object>>();

        private static Func<object, object> ResolveAccessor((Type, string) key)
        {
            var (t, name) = key;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var prop = t.GetProperty(name, flags);
                if (prop != null && prop.CanRead) return prop.GetValue;
                var field = t.GetField(name, flags);
                if (field != null) return field.GetValue;
            }
            catch { }
            return null;
        }

        internal static object ReadMember(object instance, Type t, string name)
        {
            if (instance == null || t == null || string.IsNullOrEmpty(name)) return null;
            var accessor = _accessorCache.GetOrAdd((t, name), ResolveAccessor);
            if (accessor == null) return null;
            try { return accessor(instance); }
            catch { return null; }
        }

        internal static string ReadStringMember(object instance, Type t, string name)
        {
            object v = ReadMember(instance, t, name);
            if (v == null) return null;
            string s = v.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        internal static string InferSuggestion(string message)
        {
            string m = message.ToLower();
            // Display/UI property used on a variable that isn't bound to a WebForm control.
            // src0216: 'Visible' propriedade inválida — same applies to Class/Enabled/Caption.
            bool mentionsDisplayProp = m.Contains("'visible'") || m.Contains("'class'") || m.Contains("'enabled'") || m.Contains("'caption'")
                                     || m.Contains(".visible") || m.Contains(".class") || m.Contains(".enabled") || m.Contains(".caption");
            bool mentionsInvalidProperty = m.Contains("propriedade inválida") || m.Contains("invalid property")
                                         || m.Contains("propriedade invalida");
            if (mentionsDisplayProp && mentionsInvalidProperty)
                return "Display properties (.Visible, .Class, .Enabled, .Caption) only apply to variables bound to a WebForm control. Either drop the variable onto the form (gxAttribute) or remove the property assignment.";
            // Click/event-not-valid on a control: cue the agent that not all controls expose every event.
            if ((m.Contains("não é um evento válido") || m.Contains("not a valid event") || m.Contains("nao e um evento valido")))
                return "This control does not expose that event. Use genexus_inspect with include=[\"controls\"] to see the valid event repertoire for each control type.";
            if (m.Contains("not defined") || m.Contains("não definida")) return "Check if the variable or attribute is properly declared in the Variables part or exists in the Transaction structure.";
            if (m.Contains("expected") || m.Contains("esperado")) return "Check for missing syntax elements like ENDIF, ENDFOR, or semicolons.";
            if (m.Contains("type mismatch") || m.Contains("incompatíveis")) return "The data types of the expressions don't match. Check if you're comparing a String with a Numeric.";
            if (m.Contains("invalid command")) return "The command is not valid in this part of the object (e.g., UI commands inside a Procedure without output).";
            return "Review the syntax and ensure all referenced objects exist in the KB.";
        }

        public static string GetObjectDefaultPart(KBObject obj)
        {
            if (obj is Artech.Genexus.Common.Objects.Procedure) return "Source";
            if (obj is Artech.Genexus.Common.Objects.WebPanel) return "Events";
            if (obj is Artech.Genexus.Common.Objects.Transaction) return "Structure";
            if (obj is Artech.Genexus.Common.Objects.DataProvider) return "Source";
            return "Logic";
        }
    }
}
