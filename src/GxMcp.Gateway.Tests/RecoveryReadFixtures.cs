using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Synthetic parts and the certificates a Worker would build for them
    /// (<c>gx-recovery-read/1</c>). Nothing here touches a Knowledge Base or a Worker; it
    /// only shapes the data the Gateway's recovery logic decides on.
    /// </summary>
    internal static class RecoveryReadFixtures
    {
        internal const string Kb = "synthetic-kb";
        internal const string ObjectName = "SyntheticPanel";
        internal const string PartName = "Events";
        internal const string ObjectGuid = "0f8fad5b-d9cb-469f-a165-70867728950e";
        internal const string ObjectEntityKey = "d2b4a0c0-0000-0000-0000-000000000000:7";

        /// <summary>A part of about <paramref name="chars"/> characters over <paramref name="lines"/> lines.</summary>
        internal static string SyntheticPart(int chars, int lines)
        {
            int baseWidth = Math.Max(1, chars / lines - 1);
            int extra = Math.Max(0, chars - lines * (baseWidth + 1));
            var builder = new StringBuilder(chars + 16);
            for (int index = 0; index < lines; index++)
            {
                string text = "    &Counter" + (index % 97) + " += " + index + "; // synthetic";
                int width = baseWidth + (index < extra ? 1 : 0);
                builder.Append(text.Length >= width ? text.Substring(0, width) : text.PadRight(width, '.'));
                builder.Append('\n');
            }
            return builder.ToString();
        }

        internal static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)))
                .Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>The certificate the Worker builds for a whole, quiet read of <paramref name="text"/>.</summary>
        internal static JObject Certificate(
            string text,
            int lines,
            string name = ObjectName,
            string part = PartName,
            string type = "WebPanel",
            string guid = ObjectGuid,
            string entityKey = ObjectEntityKey,
            string path = ObjectName,
            string stamp = "w100",
            string revision = "638000000000000000:root")
        {
            string sha = Sha256(text);
            return new JObject
            {
                ["schema"] = "gx-recovery-read/1",
                ["complete"] = true,
                ["coverage"] = "sdk-complete",
                ["authoritative"] = true,
                ["stable"] = true,
                ["origin"] = "fresh-sdk-read",
                ["identity"] = new JObject
                {
                    ["name"] = name, ["type"] = type, ["guid"] = guid, ["entityKey"] = entityKey,
                    ["path"] = path, ["part"] = part, ["requestedPart"] = part
                },
                ["totalLines"] = lines,
                ["totalChars"] = text.Length,
                ["utf8Bytes"] = Encoding.UTF8.GetByteCount(text),
                ["sha256"] = sha,
                ["versionToken"] = revision + ":" + sha,
                ["targetWriteStamp"] = stamp
            };
        }

        /// <summary>The certificate with one field replaced (null removes it).</summary>
        internal static JObject With(this JObject certificate, string path, object? value)
        {
            var clone = (JObject)certificate.DeepClone();
            JToken? token = clone.SelectToken(path);
            if (token == null) throw new InvalidOperationException("no such certificate field: " + path);
            if (value == null) token.Parent!.Remove();
            else ((JProperty)token.Parent!).Value = JToken.FromObject(value);
            return clone;
        }

        /// <summary>A Worker envelope as <c>SendWorkerCommandAsync</c> hands it to the Gateway.</summary>
        internal static JObject WorkerEnvelope(JObject certificate) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = new JObject
            {
                ["status"] = "ok",
                ["code"] = "RecoveryReadCertificate",
                ["target"] = certificate["identity"]?["name"],
                ["result"] = new JObject { ["recoveryCertificate"] = certificate.DeepClone() }
            }
        };
    }
}
