using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models
{
    public class McpResponse
    {
        public static string Success(string action, string target, JObject data = null)
        {
            var result = new JObject
            {
                ["status"] = "Success",
                ["action"] = action,
                ["target"] = target
            };

            if (data != null)
            {
                foreach (var prop in data.Properties())
                {
                    result[prop.Name] = prop.Value;
                }
            }

            return result.ToString();
        }

        public static string Error(string message, string target = null)
        {
            // v2.3.8 (Task 7.1) — translate SDK PT-BR messages to canonical EN;
            // keep the original under _meta.sourceMessage so support tooling can
            // still grep for the literal SDK string.
            // v2.6.9 — canonical key is ["message"] (REST / JSON-Schema convention).
            // The legacy ["error"] alias was dropped; it duplicated bytes on every
            // error envelope. Gateway's TrimErrorEnvelope still falls back to
            // ["error"] for back-compat with any path that hasn't migrated.
            string en = GxMcp.Worker.Helpers.ErrorMessages.Translate(message);
            var err = new JObject
            {
                ["status"] = "Error",
                ["message"] = en
            };
            if (!string.IsNullOrEmpty(target)) err["target"] = target;
            if (!string.Equals(en, message, StringComparison.Ordinal))
            {
                err["_meta"] = new JObject { ["sourceMessage"] = message };
            }
            return err.ToString();
        }

        public static string Error(
            string message,
            string target,
            string part,
            string details,
            string objectName = null,
            string objectType = null,
            JArray availableParts = null)
        {
            string enMsg = GxMcp.Worker.Helpers.ErrorMessages.Translate(message);
            string enDetails = GxMcp.Worker.Helpers.ErrorMessages.Translate(details);
            var err = new JObject
            {
                ["status"] = "Error",
                ["message"] = enMsg
            };

            if (!string.IsNullOrEmpty(target)) err["target"] = target;
            if (!string.IsNullOrEmpty(part)) err["part"] = part;
            if (!string.IsNullOrEmpty(enDetails)) err["details"] = enDetails;
            if (!string.IsNullOrEmpty(objectName)) err["objectName"] = objectName;
            if (!string.IsNullOrEmpty(objectType)) err["objectType"] = objectType;
            if (availableParts != null && availableParts.Count > 0) err["availableParts"] = availableParts;

            bool msgChanged = !string.Equals(enMsg, message, StringComparison.Ordinal);
            bool detailsChanged = !string.Equals(enDetails, details, StringComparison.Ordinal);
            if (msgChanged || detailsChanged)
            {
                var meta = new JObject();
                if (msgChanged) meta["sourceMessage"] = message;
                if (detailsChanged) meta["sourceDetails"] = details;
                err["_meta"] = meta;
            }

            return err.ToString();
        }
    }
}
