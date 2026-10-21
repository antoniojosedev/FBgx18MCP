using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Language.Parser;
using Artech.Common.Diagnostics;
using Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Helpers
{
    /// <summary>One problem the SDK's language engine reported for a parameter list.</summary>
    internal sealed class ParameterListDiagnostic
    {
        public int? Index { get; set; }
        public string Expression { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// The SDK's own parameter-list language (<c>ParserType.ParameterList</c>), used the way the
    /// IDE's property grid uses it: validate the text against the object's model, tokenize it
    /// into the token list that is stored, and render a stored token list back to text. Nothing
    /// here writes: a token list is the value to store, not a stored change.
    /// </summary>
    internal static class ParameterListEngine
    {
        private static ParserInfo InfoFor(KBObject obj) => new ParserInfo(obj, ParserType.ParameterList);

        /// <summary>False, with a reason, when this installation has no language engine.</summary>
        internal static bool IsAvailable(out string reason)
        {
            reason = null;
            try
            {
                if (!GenexusBLServices.IsLanguageAvailable) { reason = "The GeneXus language service is not available in this Worker."; return false; }
                if (GenexusBLServices.Language?.CreateEngine() == null) { reason = "The GeneXus language service returned no parser engine."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                reason = "The GeneXus language service failed to start: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>The text of a stored token list (what the property grid shows), or null if it cannot be rendered.</summary>
        internal static string ToText(KBObject obj, string storedTokens, out string failure)
        {
            failure = null;
            if (string.IsNullOrWhiteSpace(storedTokens)) return string.Empty;
            try
            {
                var engine = GenexusBLServices.Language.CreateEngine();
                string text = engine.ConvertSourceToString(InfoFor(obj), storedTokens);
                return (text ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                failure = "The stored parameter list could not be rendered as text: " + (ex.InnerException ?? ex).Message;
                return null;
            }
        }

        /// <summary>
        /// Validates <paramref name="text"/> as a parameter list of <paramref name="obj"/>'s model.
        /// An unknown attribute, an undeclared variable or a malformed expression comes back as
        /// diagnostics; an empty list is valid.
        /// </summary>
        internal static List<ParameterListDiagnostic> Validate(KBObject obj, string text, int? index = null, string expression = null)
        {
            var diagnostics = new List<ParameterListDiagnostic>();
            if (string.IsNullOrWhiteSpace(text)) return diagnostics;

            var output = new OutputMessages();
            bool valid;
            try
            {
                valid = InvokeValidate(text, InfoFor(obj), output);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ParameterListDiagnostic
                {
                    Index = index,
                    Expression = expression ?? text,
                    Code = "ValidationUnavailable",
                    Message = "The SDK validator could not run: " + (ex.InnerException ?? ex).Message
                });
                return diagnostics;
            }

            foreach (var error in output.OnlyErrors)
            {
                diagnostics.Add(new ParameterListDiagnostic
                {
                    Index = index,
                    Expression = expression ?? text,
                    Code = string.IsNullOrWhiteSpace(error.ErrorCode) ? null : error.ErrorCode,
                    Message = (error.Text ?? string.Empty).Trim()
                });
            }
            if (!valid && diagnostics.Count == 0)
            {
                diagnostics.Add(new ParameterListDiagnostic
                {
                    Index = index,
                    Expression = expression ?? text,
                    Message = "The SDK rejected this expression without a message."
                });
            }
            return diagnostics;
        }

        /// <summary>The token list to store for a valid <paramref name="text"/>.</summary>
        internal static string ToStorage(KBObject obj, string text, out string failure)
        {
            failure = null;
            try
            {
                var engine = GenexusBLServices.Language.CreateEngine();
                string storage = engine.ConvertStringToSource(InfoFor(obj), text ?? string.Empty);
                if (string.IsNullOrEmpty(storage) && !string.IsNullOrWhiteSpace(text))
                {
                    failure = "The SDK produced no token list for a non-empty parameter list.";
                    return null;
                }
                return storage ?? string.Empty;
            }
            catch (Exception ex)
            {
                failure = "The SDK could not tokenize the parameter list: " + (ex.InnerException ?? ex).Message;
                return null;
            }
        }

        // SourceHelper.Validate is internal to Artech.Genexus.Common; the property grid reaches it
        // from inside the SDK. The (string, ParserInfo, OutputMessages) overload is the one the
        // parameter-list converter itself calls.
        private static bool InvokeValidate(string text, ParserInfo info, OutputMessages output)
        {
            var helper = typeof(Artech.Genexus.Common.Parts.SourcePart).Assembly.GetType("Artech.Genexus.Common.Parts.SourceHelper");
            var method = helper?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Validate"
                    && m.ReturnType == typeof(bool)
                    && m.GetParameters().Length == 3
                    && m.GetParameters()[0].ParameterType == typeof(string)
                    && m.GetParameters()[1].ParameterType == typeof(ParserInfo)
                    && m.GetParameters()[2].ParameterType == typeof(OutputMessages));
            if (method == null) throw new MissingMethodException("SourceHelper.Validate(string, ParserInfo, OutputMessages) was not found in this GeneXus installation.");
            try { return (bool)method.Invoke(null, new object[] { text, info, output }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
    }
}
