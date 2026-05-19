using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    internal static class ContractGoldenHarness
    {
        private static readonly HashSet<string> VolatilePropertyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "requestId",
            "correlationId",
            "traceId",
            "sessionId",
            "operationId",
            "timestamp",
            "createdAt",
            "updatedAt",
            "startedAt",
            "finishedAt",
            "expiresAt",
            "pid",
            "processId"
        };

        private static readonly IReadOnlyDictionary<string, string> ArraySortKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tools"] = "name",
            ["resources"] = "uri",
            ["resourceTemplates"] = "uriTemplate",
            ["removedTools"] = "name"
        };

        private static readonly Regex GuidPattern = new(
            @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex IsoTimestampPattern = new(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Lazy<string> FixtureRoot = new(ResolveFixtureRoot);

        public static void AssertMatchesGolden(string requestFixtureRelativePath)
        {
            var request = (JObject)LoadToken(requestFixtureRelativePath);
            var responseFixtureRelativePath = requestFixtureRelativePath.Replace(".request.json", ".response.json", StringComparison.OrdinalIgnoreCase);

            var actual = Normalize(ToToken(McpRouter.Handle((JObject)request.DeepClone())));
            var expected = Normalize(LoadToken(responseFixtureRelativePath));

            // Update-mode: set env GXMCP_UPDATE_GOLDEN=1 to overwrite the fixture with the
            // current actual output. Use sparingly (after intentional schema additions).
            if (Environment.GetEnvironmentVariable("GXMCP_UPDATE_GOLDEN") == "1")
            {
                var fixturePath = FindFixturePath(responseFixtureRelativePath);
                File.WriteAllText(fixturePath, actual.ToString(Formatting.Indented));
                return;
            }

            if (!JToken.DeepEquals(expected, actual))
            {
                Assert.Fail(BuildDiffMessage(requestFixtureRelativePath, expected, actual));
            }
        }

        private static JToken ToToken(object? value)
        {
            if (value is null)
            {
                return JValue.CreateNull();
            }

            return value as JToken ?? JToken.FromObject(value);
        }

        private static JToken LoadToken(string requestFixtureRelativePath)
        {
            var path = FindFixturePath(requestFixtureRelativePath);
            return JToken.Parse(File.ReadAllText(path));
        }

        private static string FindFixturePath(string relativePath)
        {
            var candidate = Path.Combine(FixtureRoot.Value, relativePath);
            if (File.Exists(candidate)) return candidate;
            throw new FileNotFoundException($"Could not locate contract fixture '{relativePath}' under '{FixtureRoot.Value}'.");
        }

        private static string ResolveFixtureRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "src", "GxMcp.Gateway.Tests", "Fixtures", "Contract", "Discovery");
                if (Directory.Exists(candidate)) return candidate;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate contract fixture root from '{AppContext.BaseDirectory}'.");
        }

        private static JToken Normalize(JToken token, string path = "")
        {
            return token.Type switch
            {
                JTokenType.Object => NormalizeObject((JObject)token, path),
                JTokenType.Array => NormalizeArray((JArray)token, path),
                JTokenType.String => NormalizeString((JValue)token, path),
                JTokenType.Integer => NormalizeNumber((JValue)token, path),
                JTokenType.Float => NormalizeNumber((JValue)token, path),
                JTokenType.Boolean => token.DeepClone(),
                JTokenType.Null => JValue.CreateNull(),
                _ => token.DeepClone()
            };
        }

        private static JToken NormalizeObject(JObject obj, string path)
        {
            var normalized = new JObject();
            foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                normalized[property.Name] = ShouldNormalize(property.Name, childPath)
                    ? new JValue(PlaceholderFor(property.Name, childPath))
                    : Normalize(property.Value, childPath);
            }

            return normalized;
        }

        private static JToken NormalizeArray(JArray array, string path)
        {
            var normalized = array.Select((item, index) => Normalize(item, $"{path}[{index}]")).ToList();
            var sortKey = GetSortKey(path);
            if (sortKey is not null && normalized.All(item => item is JObject obj && obj[sortKey] is not null))
            {
                normalized = normalized
                    .Cast<JObject>()
                    .OrderBy(item => item[sortKey]!.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Cast<JToken>()
                    .ToList();
            }

            return new JArray(normalized);
        }

        private static JToken NormalizeString(JValue value, string path)
        {
            var text = value.Value?.ToString() ?? string.Empty;
            if (GuidPattern.IsMatch(text))
            {
                return new JValue("<guid>");
            }

            if (IsoTimestampPattern.IsMatch(text))
            {
                return new JValue("<timestamp>");
            }

            if (path.EndsWith("serverInfo.version", StringComparison.OrdinalIgnoreCase))
            {
                return new JValue("<version>");
            }

            return new JValue(text);
        }

        private static JToken NormalizeNumber(JValue value, string path)
        {
            if (ShouldNormalize(GetLastSegment(path), path))
            {
                return new JValue(PlaceholderFor(GetLastSegment(path), path));
            }

            return value.DeepClone();
        }

        private static bool ShouldNormalize(string? pathSegment, string path)
        {
            if (string.IsNullOrEmpty(pathSegment))
            {
                return false;
            }

            return VolatilePropertyNames.Contains(pathSegment)
                || path.EndsWith("serverInfo.version", StringComparison.OrdinalIgnoreCase);
        }

        private static string PlaceholderFor(string? propertyName, string path)
        {
            if (path.EndsWith("serverInfo.version", StringComparison.OrdinalIgnoreCase))
            {
                return "<version>";
            }

            return propertyName?.ToLowerInvariant() switch
            {
                "id" => "<id>",
                "requestid" => "<request-id>",
                "correlationid" => "<correlation-id>",
                "traceid" => "<trace-id>",
                "sessionid" => "<session-id>",
                "operationid" => "<operation-id>",
                "timestamp" => "<timestamp>",
                "createdat" => "<created-at>",
                "updatedat" => "<updated-at>",
                "startedat" => "<started-at>",
                "finishedat" => "<finished-at>",
                "expiresat" => "<expires-at>",
                "pid" => "<pid>",
                "processid" => "<process-id>",
                _ => "<normalized>"
            };
        }

        private static string? GetSortKey(string path)
        {
            return ArraySortKeys.TryGetValue(GetLastSegment(path), out var sortKey) ? sortKey : null;
        }

        private static string GetLastSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var lastDot = path.LastIndexOf('.');
            var lastBracket = path.LastIndexOf('[');
            var start = Math.Max(lastDot, lastBracket);
            return start >= 0 ? path[(start + 1)..].TrimEnd(']') : path;
        }

        private static string BuildDiffMessage(string requestFixtureRelativePath, JToken expected, JToken actual)
        {
            return $"""
Contract golden mismatch: {requestFixtureRelativePath}
Expected:
{expected.ToString(Formatting.Indented)}
Actual:
{actual.ToString(Formatting.Indented)}
""";
        }
    }
}
