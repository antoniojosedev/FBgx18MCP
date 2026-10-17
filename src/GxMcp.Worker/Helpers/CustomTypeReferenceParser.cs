using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    // issue #543: a persisted SDT reference (the ATTCUSTOMTYPE of an SDT-bound variable, the
    // StructureTypeReference of an SDT member) serializes as an EntityKey pair, <Type>guid</Type>
    // plus <Id>n</Id>. Three readers parsed that text with two different element orders and
    // whitespace rules, so at least one of them could never match the SDK output and the
    // post-save read-back reported a false VariableTypeNotPersisted. This is the single
    // tolerant parser they share: either element order, any whitespace or line breaks around
    // and between the elements, upper- or lower-case guid. Which order the SDK really emits
    // still has to be confirmed on a test KB; this parser does not depend on the answer.
    public static class CustomTypeReferenceParser
    {
        private static readonly Regex TypeElement = new Regex(
            @"<Type>\s*([0-9a-fA-F\-]{36})\s*</Type>", RegexOptions.CultureInvariant);
        private static readonly Regex IdElement = new Regex(
            @"<Id>\s*(\d+)\s*</Id>", RegexOptions.CultureInvariant);

        // True when text carries both elements with a well-formed guid and a numeric id.
        // A bare type name, a missing element or a non-numeric id is false, never a throw.
        public static bool TryParse(string text, out Guid typeGuid, out int id)
        {
            typeGuid = Guid.Empty;
            id = 0;
            if (string.IsNullOrEmpty(text)) return false;

            var typeMatch = TypeElement.Match(text);
            var idMatch = IdElement.Match(text);
            if (!typeMatch.Success || !idMatch.Success) return false;
            if (!Guid.TryParse(typeMatch.Groups[1].Value, out typeGuid)) return false;
            if (!int.TryParse(idMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                typeGuid = Guid.Empty;
                return false;
            }
            return true;
        }
    }
}
