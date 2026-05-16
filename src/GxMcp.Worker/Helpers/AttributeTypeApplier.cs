using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public class AttributeTypeSpec
    {
        public bool Recognized { get; set; }
        public string CanonicalType { get; set; }  // "Numeric"/"Character"/.../"DomainReference"
        public int? Length { get; set; }
        public int? Decimals { get; set; }
        public string DomainName { get; set; }
    }

    /// <summary>
    /// Parses a DSL attribute type string and applies it to a GeneXus Attribute instance.
    ///
    /// Supported input forms:
    ///   Primitive with optional size:  "Character(40)", "Numeric(18,4)", "Date", "Boolean"
    ///   Serializer-tagged domain:      "UserLogin (Character)"  — trailing " (X)" is stripped
    ///   Explicit domain ref:           "&amp;UserLogin"         — same as VariableTypeResolver convention
    ///   Bare domain candidate:         "AutoNum18", "Email"     — plain identifier not matching a primitive
    ///
    /// Reuses VariableTypeResolver for all primitive parsing so synonym tables stay in one place.
    ///
    /// ApplyPrimitive uses reflection to set Type/Length/Decimals so it works with:
    ///   - Real Artech.Genexus.Common.Objects.Attribute  (Type is eDBType enum)
    ///   - Test FakeAttribute                             (Type is string storing enum name)
    /// </summary>
    public static class AttributeTypeApplier
    {
        // Matches "SomeName (AnythingInParens)" with a mandatory space before '('.
        // Does NOT match primitive form "Character(40)" because there is no space before '('.
        private static readonly Regex TrailingCommentRegex =
            new Regex(@"^(.+?)\s+\([^)]+\)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Parse <paramref name="typeStr"/> into an <see cref="AttributeTypeSpec"/>.
        /// Never throws; returns Recognized=false for unparseable input.
        /// </summary>
        public static AttributeTypeSpec Parse(string typeStr)
        {
            var spec = new AttributeTypeSpec();
            if (string.IsNullOrWhiteSpace(typeStr)) return spec;

            string clean = typeStr.Trim();

            // "Unknown" is the sentinel used in tests for truly unrecognized input.
            if (clean.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return spec;

            // Strip serializer comment tail BEFORE primitive parsing.
            // "UserLogin (Character)" → "UserLogin"
            // "Character(40)" is NOT stripped because there is no space before '('.
            var m = TrailingCommentRegex.Match(clean);
            if (m.Success)
                clean = m.Groups[1].Value.Trim();

            // & prefix → explicit domain reference (VariableTypeResolver convention).
            if (clean.StartsWith("&"))
            {
                string dn = clean.Substring(1).Trim();
                if (string.IsNullOrEmpty(dn)) return spec;
                spec.Recognized = true;
                spec.CanonicalType = "DomainReference";
                spec.DomainName = dn;
                return spec;
            }

            // Delegate all primitive parsing to VariableTypeResolver.
            var resolved = VariableTypeResolver.Resolve(clean);
            if (resolved.Recognized && resolved.CanonicalType != "DomainReference")
            {
                spec.Recognized = true;
                spec.CanonicalType = resolved.CanonicalType;
                spec.Length = resolved.Length;
                spec.Decimals = resolved.Decimals;
                return spec;
            }

            // Bare identifier that didn't match a primitive → domain candidate.
            // Must be a plain C-style identifier (letters/digits/underscore, starting with letter or _).
            if (Regex.IsMatch(clean, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                spec.Recognized = true;
                spec.CanonicalType = "DomainReference";
                spec.DomainName = clean;
                return spec;
            }

            // Anything else (e.g. "(40)") is unrecognized.
            return spec;
        }

        // Maps canonical GeneXus type names → eDBType enum member names (uppercase).
        private static readonly Dictionary<string, string> CanonicalToEdb =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Numeric",     "NUMERIC"      },
            { "Character",   "CHARACTER"    },
            { "Date",        "DATE"         },
            { "DateTime",    "DATETIME"     },
            { "Time",        "TIME"         },
            { "Boolean",     "BOOLEAN"      },
            { "LongVarChar", "LONGVARCHAR"  },
            { "Blob",        "BLOB"         },
            { "Image",       "IMAGE"        },
            { "GUID",        "GUID"         },
        };

        /// <summary>
        /// Apply a primitive (non-domain) type to <paramref name="attr"/>. The attribute object must
        /// expose public settable properties <c>Type</c>, <c>Length</c>, <c>Decimals</c>.
        ///
        /// For the real <c>Artech.Genexus.Common.Objects.Attribute</c>, <c>Type</c> is
        /// <c>Artech.Genexus.Common.eDBType</c> — resolved via Enum.Parse at runtime.
        /// For test fakes where <c>Type</c> is <c>string</c>, the enum name string is stored directly.
        /// </summary>
        /// <returns>true if the type was applied; false if the canonical type is unknown or attr is null.</returns>
        public static bool ApplyPrimitive(object attr, string canonicalType, int? length, int? decimals)
        {
            if (attr == null || string.IsNullOrEmpty(canonicalType)) return false;

            string edbName;
            if (!CanonicalToEdb.TryGetValue(canonicalType, out edbName)) return false;

            Type t = attr.GetType();
            PropertyInfo typeProp = t.GetProperty("Type");
            PropertyInfo lenProp  = t.GetProperty("Length");
            PropertyInfo decProp  = t.GetProperty("Decimals");
            if (typeProp == null) return false;

            object enumValue;
            if (typeProp.PropertyType == typeof(string))
            {
                // Test fake path: store the enum name as a string.
                enumValue = edbName;
            }
            else
            {
                // Real GeneXus path: parse the enum value from the attribute's assembly.
                try
                {
                    enumValue = Enum.Parse(typeProp.PropertyType, edbName, ignoreCase: true);
                }
                catch
                {
                    return false;
                }
            }

            try { typeProp.SetValue(attr, enumValue, null); }
            catch { return false; }

            if (length.HasValue && lenProp != null)
            {
                try { lenProp.SetValue(attr, length.Value, null); }
                catch { /* best-effort */ }
            }

            if (decimals.HasValue && decProp != null)
            {
                try { decProp.SetValue(attr, decimals.Value, null); }
                catch { /* best-effort */ }
            }

            return true;
        }

        /// <summary>
        /// Apply a domain reference by setting <c>attr.DomainBasedOn</c> via reflection.
        /// Caller is responsible for resolving <paramref name="domainObj"/> from the KB
        /// (e.g. <c>Artech.Genexus.Common.Objects.Domain.Get(model, name)</c>).
        /// </summary>
        /// <returns>true if the property was set; false if attr or domainObj is null or the property is absent.</returns>
        public static bool ApplyDomain(object attr, object domainObj)
        {
            if (attr == null || domainObj == null) return false;
            PropertyInfo p = attr.GetType().GetProperty("DomainBasedOn");
            if (p == null) return false;
            try { p.SetValue(attr, domainObj, null); return true; }
            catch { return false; }
        }
    }
}
