using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Thrown by the Attribute create path when the requested type metadata cannot be
    /// applied in memory. It is raised before Save(), so the object is never persisted
    /// with the SDK default; <see cref="Code"/> becomes the response's error code.
    /// </summary>
    public sealed class AttributeCreateMetadataException : Exception
    {
        public string Code { get; }
        public JObject Details { get; }

        public AttributeCreateMetadataException(string code, string message, JObject details)
            : base(message)
        {
            Code = code;
            Details = details;
        }
    }

    /// <summary>
    /// The type metadata <c>genexus_create type=Attribute</c> applies to a new Attribute,
    /// resolved from the request alone so the dry-run preview, the write path and the
    /// tests share one shape (issue #541).
    ///
    /// Only an explicit <c>dataType</c>/<c>length</c>/<c>decimals</c>/<c>basedOn</c>
    /// counts: the request's <c>type</c> is the object type ("Attribute") and is never
    /// read as a data type. When nothing is requested the SDK default (Numeric(4))
    /// stands and <see cref="Requested"/> is false. A request that cannot be honoured
    /// as written carries <see cref="Error"/> instead of being silently narrowed.
    /// </summary>
    public sealed class AttributeCreatePlan
    {
        public const string NotAppliedCode = "CreateMetadataNotApplied";
        public const string DomainNotFoundCode = "DomainNotFound";
        public const string SupportedDataTypes =
            "Character, VarChar, Numeric, Date, DateTime, Time, Boolean, LongVarChar, Blob, Image, GUID";

        /// <summary>True when dataType, length, decimals or basedOn was supplied.</summary>
        public bool Requested { get; private set; }
        /// <summary>The dataType text as requested (for messages).</summary>
        public string DataType { get; private set; }
        /// <summary>Canonical primitive type ("Numeric", "VarChar", "Blob", ...); null for a Domain plan.</summary>
        public string CanonicalType { get; private set; }
        /// <summary>Domain bound through DomainBasedOn; null for a primitive plan.</summary>
        public string DomainName { get; private set; }
        public int? Length { get; private set; }
        public int? Decimals { get; private set; }
        /// <summary>Non-null when the request cannot be honoured as written.</summary>
        public string Error { get; private set; }

        public bool IsDomain => DomainName != null;

        public static AttributeCreatePlan FromOptions(JObject options)
        {
            var plan = new AttributeCreatePlan();
            string dataType = options?["dataType"]?.ToString();
            string basedOn = options?["basedOn"]?.ToString();
            bool hasDataType = !string.IsNullOrWhiteSpace(dataType);
            bool hasBasedOn = !string.IsNullOrWhiteSpace(basedOn);
            bool hasLength = options?["length"] != null && options["length"].Type != JTokenType.Null;
            bool hasDecimals = options?["decimals"] != null && options["decimals"].Type != JTokenType.Null;

            plan.Requested = hasDataType || hasBasedOn || hasLength || hasDecimals;
            if (!plan.Requested) return plan;
            plan.DataType = hasDataType ? dataType.Trim() : null;

            int? length = null, decimals = null;
            if (hasLength && !TryReadInt(options["length"], out length))
            {
                plan.Error = "length must be an integer.";
                return plan;
            }
            if (hasDecimals && !TryReadInt(options["decimals"], out decimals))
            {
                plan.Error = "decimals must be an integer.";
                return plan;
            }

            if (hasBasedOn)
            {
                if (hasDataType)
                {
                    plan.Error = "basedOn and dataType are mutually exclusive for an Attribute; pass one of them.";
                    return plan;
                }
                if (length.HasValue || decimals.HasValue)
                {
                    plan.Error = "length/decimals cannot be combined with basedOn; a Domain-based Attribute inherits its size.";
                    return plan;
                }
                plan.DomainName = basedOn.Trim();
                return plan;
            }

            if (!hasDataType)
            {
                plan.Error = "length/decimals require dataType; without dataType the SDK default type would be created.";
                return plan;
            }

            var spec = AttributeTypeApplier.Parse(dataType);
            if (!spec.Recognized)
            {
                plan.Error = "dataType '" + plan.DataType + "' is not a supported Attribute type. Supported: " + SupportedDataTypes + ", or basedOn=<Domain>.";
                return plan;
            }
            if (string.Equals(spec.CanonicalType, "DomainReference", StringComparison.OrdinalIgnoreCase))
            {
                if (length.HasValue || decimals.HasValue)
                {
                    plan.Error = "length/decimals cannot be combined with a Domain reference; a Domain-based Attribute inherits its size.";
                    return plan;
                }
                plan.DomainName = spec.DomainName;
                return plan;
            }
            if (!AttributeTypeApplier.CanonicalToEdb.ContainsKey(spec.CanonicalType))
            {
                plan.Error = "dataType '" + plan.DataType + "' has no eDBType mapping. Supported: " + SupportedDataTypes + ".";
                return plan;
            }

            plan.CanonicalType = spec.CanonicalType;
            plan.Length = length ?? spec.Length;
            plan.Decimals = decimals ?? spec.Decimals;
            return plan;
        }

        /// <summary>
        /// Requested shape for the dry-run preview and the response's
        /// <c>_meta.attribute.requested</c>: <c>dataType</c>/<c>length</c>/<c>decimals</c>
        /// for a primitive, <c>basedOn</c> for a Domain. Empty when nothing was requested.
        /// </summary>
        public JObject ToJson()
        {
            var json = new JObject();
            if (!Requested) return json;
            if (IsDomain)
            {
                json["basedOn"] = DomainName;
                return json;
            }
            json["dataType"] = CanonicalType ?? DataType;
            if (Length.HasValue) json["length"] = Length.Value;
            if (Decimals.HasValue) json["decimals"] = Decimals.Value;
            return json;
        }

        /// <summary>
        /// Reads Type/Length/Decimals/DomainBasedOn back from an Attribute instance (real
        /// SDK object or test fake) as <c>{dataType, length, decimals, basedOn}</c>.
        /// <c>dataType</c> is the eDBType member name ("NUMERIC", "VARCHAR", "BINARY").
        /// </summary>
        public static JObject ReadApplied(object attr)
        {
            var applied = new JObject();
            if (attr == null) return applied;
            Type t = attr.GetType();

            object type = ReadProperty(attr, t, "Type");
            if (type != null) applied["dataType"] = type.ToString();

            int? length = ReadInt(ReadProperty(attr, t, "Length"));
            if (length.HasValue) applied["length"] = length.Value;

            int? decimals = ReadInt(ReadProperty(attr, t, "Decimals"));
            if (decimals.HasValue) applied["decimals"] = decimals.Value;

            string basedOn = DomainPropertyApplier.GetDomainBasedOnName(attr);
            if (!string.IsNullOrEmpty(basedOn)) applied["basedOn"] = basedOn;
            return applied;
        }

        /// <summary>
        /// Fields of <paramref name="applied"/> (from <see cref="ReadApplied"/>) that do not
        /// match this plan. Only requested fields are compared; empty means everything landed.
        /// </summary>
        public IList<string> Mismatches(JObject applied)
        {
            var mismatches = new List<string>();
            if (!Requested || Error != null) return mismatches;

            if (IsDomain)
            {
                string got = applied?["basedOn"]?.ToString();
                if (!string.Equals(got, DomainName, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add("basedOn: requested '" + DomainName + "', applied '" + (string.IsNullOrEmpty(got) ? "(none)" : got) + "'");
                return mismatches;
            }

            string wantedEdb;
            if (!AttributeTypeApplier.CanonicalToEdb.TryGetValue(CanonicalType, out wantedEdb)) wantedEdb = CanonicalType;
            string gotType = applied?["dataType"]?.ToString();
            if (!string.Equals(gotType, wantedEdb, StringComparison.OrdinalIgnoreCase))
                mismatches.Add("dataType: requested " + CanonicalType + " (" + wantedEdb + "), applied " + (string.IsNullOrEmpty(gotType) ? "(none)" : gotType));

            if (Length.HasValue)
            {
                int? got = ReadInt(applied?["length"]);
                if (got != Length)
                    mismatches.Add("length: requested " + Length.Value + ", applied " + (got.HasValue ? got.Value.ToString() : "(none)"));
            }
            if (Decimals.HasValue)
            {
                int? got = ReadInt(applied?["decimals"]);
                if (got != Decimals)
                    mismatches.Add("decimals: requested " + Decimals.Value + ", applied " + (got.HasValue ? got.Value.ToString() : "(none)"));
            }
            return mismatches;
        }

        private static bool TryReadInt(JToken token, out int? value)
        {
            value = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Integer) { value = token.Value<int>(); return true; }
            if (token.Type == JTokenType.String && int.TryParse(token.ToString().Trim(), out int parsed)) { value = parsed; return true; }
            return false;
        }

        private static int? ReadInt(object value)
        {
            if (value == null) return null;
            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null) return null;
                try { return token.Value<int>(); } catch { return null; }
            }
            try { return Convert.ToInt32(value); } catch { return null; }
        }

        private static object ReadProperty(object target, Type type, string name)
        {
            try
            {
                var p = AttributeTypeApplier.GetPropertyUnambiguous(type, name);
                if (p == null || !p.CanRead) return null;
                return p.GetValue(target, null);
            }
            catch { return null; }
        }
    }
}
