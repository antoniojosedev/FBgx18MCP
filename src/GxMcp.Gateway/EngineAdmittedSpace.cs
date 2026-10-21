using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The arguments one engine-routed operation serves (docs/engine_backend.md,
    /// "Served operations and admitted space"). The list is the whole contract:
    /// a requested argument that is not listed is outside the space, so a new
    /// MCP argument is refused on the engine path until it is added here on
    /// purpose, instead of being silently ignored.
    /// </summary>
    internal sealed class EngineAdmittedSpace
    {
        /// <summary>
        /// Arguments every routed tool accepts: they shape the response, name the
        /// client or are consumed by the gateway pipeline (`idempotencyKey`), and
        /// the engine path never consults them to do the work.
        /// </summary>
        private static readonly string[] SharedArguments =
        {
            "axiCompact", "projection", "fields", "full", "kbAlias", "alias", "workingDir", "correlationId",
            "idempotencyKey",
        };

        private readonly string _operation;
        private readonly string[] _supportedArguments;
        private readonly HashSet<string> _admitted;

        internal EngineAdmittedSpace(string operation, params string[] supportedArguments)
        {
            _operation = operation;
            _supportedArguments = supportedArguments;
            _admitted = new HashSet<string>(supportedArguments.Concat(SharedArguments), StringComparer.Ordinal);
        }

        /// <summary>
        /// The reason the first requested argument outside the space cannot be
        /// served, generated from the supported list, or null when every
        /// requested argument is listed. An argument counts as requested when it
        /// carries a value: null, a blank string, an empty array or object and
        /// <c>false</c> ask for nothing.
        /// </summary>
        internal string? RefusalFor(JObject args)
        {
            foreach (JProperty property in args.Properties())
            {
                if (_admitted.Contains(property.Name) || !IsRequested(property.Value)) continue;
                return $"`{property.Name}` is outside the engine {_operation} space yet "
                    + $"(supported arguments: {string.Join(", ", _supportedArguments)})";
            }
            return null;
        }

        private static bool IsRequested(JToken value) =>
            EngineRoutedOperations.EngineArgPresent(value)
            && !(value.Type == JTokenType.Boolean && !value.Value<bool>());
    }
}
