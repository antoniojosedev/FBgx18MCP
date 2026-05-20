using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Tags write-tool responses with `_meta.sdkPath` so we can measure how often each write
    /// path is used. The values are intentionally coarse — they describe which write strategy
    /// the handler chose, not which low-level SDK call. A weekly aggregate of these tags is
    /// the KPI that tells us where parity with the IDE still relies on raw-XML fallbacks.
    ///
    /// Conventions for sdkPath values:
    ///   "typed-sdk"            — IDE-native setter / PropertiesObject.SetPropertyValue.
    ///   "typed-writer"         — our typed helpers (WebFormTypedPropertyWriter,
    ///                            AttributeTypeApplier, DomainPropertyApplier, VariableInjector).
    ///   "raw-xml"              — XElement.SetAttributeValue / source string replace.
    ///   "hybrid"               — bulk path that mixed typed and raw writes in the same batch.
    ///   "sdk-pattern-engine"   — IPatternEngine.ApplyPattern (with or without projection).
    ///   "ops"                  — semantic-ops / json-patch routed through structured editors.
    /// </summary>
    public static class WriteResultMeta
    {
        public const string TypedSdk         = "typed-sdk";
        public const string TypedWriter      = "typed-writer";
        public const string RawXml           = "raw-xml";
        public const string Hybrid           = "hybrid";
        public const string SdkPatternEngine = "sdk-pattern-engine";
        public const string Ops              = "ops";

        /// <summary>
        /// Sets _meta.sdkPath on the given envelope (idempotent — does not overwrite a value
        /// previously set by a downstream call). Returns the envelope for chaining.
        /// </summary>
        public static JObject TagSdkPath(JObject envelope, string sdkPath)
        {
            if (envelope == null || string.IsNullOrEmpty(sdkPath)) return envelope;
            var meta = envelope["_meta"] as JObject;
            if (meta == null)
            {
                meta = new JObject();
                envelope["_meta"] = meta;
            }
            if (meta["sdkPath"] == null)
                meta["sdkPath"] = sdkPath;
            return envelope;
        }

        /// <summary>
        /// Parses, tags, and re-serializes a JSON response string. Safe to call on malformed
        /// input (returns the original string unchanged). Use when the caller already has a
        /// finished response string from a downstream method.
        /// </summary>
        public static string TagSdkPath(string responseJson, string sdkPath)
        {
            if (string.IsNullOrEmpty(responseJson) || string.IsNullOrEmpty(sdkPath)) return responseJson;
            JObject parsed;
            try { parsed = JObject.Parse(responseJson); }
            catch { return responseJson; }
            TagSdkPath(parsed, sdkPath);
            return parsed.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
