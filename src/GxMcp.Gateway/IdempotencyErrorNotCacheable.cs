using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class ErrorNotCacheable : Exception
    {
        public JObject Result { get; }
        public ErrorNotCacheable(JObject result) : base("Error result — not cacheable") { Result = result; }

        internal bool MutationOutcomeUnknown
        {
            get
            {
                JObject? payload = Result["structuredContent"] as JObject;
                if (payload == null && Result["content"] is JArray content
                    && content.First is JObject first && first["text"]?.Type == JTokenType.String)
                {
                    try { payload = JObject.Parse(first["text"]!.Value<string>()!); }
                    catch (Newtonsoft.Json.JsonException) { return false; }
                }
                // Reconciliation may concern an earlier write that blocked this
                // request before dispatch. Retain only THIS mutation's fence.
                JToken? flag = payload?["mutationOutcomeUnknown"]
                    ?? (payload?["error"] as JObject)?["mutationOutcomeUnknown"];
                return flag?.Type == JTokenType.Boolean && flag.Value<bool>();
            }
        }
    }
}
