using System;
using System.IO;
using System.Text;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ResponseSizeGuardTests
    {
        // ── helpers ──────────────────────────────────────────────────────────

        private static JObject SmallPayload() =>
            JObject.Parse("""{"result":"ok","data":"hello"}""");

        private static JObject OversizedPayload(int targetBytes = 250_000)
        {
            // Build a payload whose serialized UTF-8 length exceeds targetBytes
            var padding = new string('x', targetBytes);
            return JObject.Parse($$$"""{"result":"ok","data":"{{{padding}}}"}""");
        }

        private static JObject SomeArgs() =>
            JObject.Parse("""{"object_name":"Invoice","type":"Transaction"}""");

        // ── small payload passes through unchanged ────────────────────────────

        [Fact]
        public void SmallPayload_PassesThrough_Unchanged()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var payload = SmallPayload();
            var originalJson = payload.ToString(Newtonsoft.Json.Formatting.None);

            var (result, truncated) = guard.Apply(payload, "genexus_read", SomeArgs());

            Assert.False(truncated);
            Assert.Equal(originalJson, result.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void SmallPayload_DoesNotMutateInput()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var payload = SmallPayload();
            var before = payload.ToString(Newtonsoft.Json.Formatting.None);

            guard.Apply(payload, "genexus_read", SomeArgs());

            Assert.Equal(before, payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        // ── oversized payload returns sentinel ───────────────────────────────

        [Fact]
        public void OversizedPayload_ReturnsTruncatedTrue()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (_, truncated) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            Assert.True(truncated);
        }

        [Fact]
        public void OversizedPayload_SentinelHas_MetaTruncated()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            Assert.IsType<JObject>(result["_meta"]);
            Assert.IsType<JObject>(result["_meta"]!["truncated"]);
            Assert.IsType<JObject>(result["_meta"]!["truncated"]!["follow_up"]);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasReason()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var reason = result["_meta"]!["truncated"]!["reason"]?.ToString();
            Assert.Equal("response_exceeded_cap", reason);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasOriginalSize()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var payload = OversizedPayload(250_000);
            long expectedSize = ResponseSizeGuard.ByteSize(payload);

            var (result, _) = guard.Apply(payload, "genexus_read", SomeArgs());

            var originalSize = result["_meta"]!["truncated"]!["original_size"]?.Value<long>();
            Assert.Equal(expectedSize, originalSize);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasCapBytes()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var capBytes = result["_meta"]!["truncated"]!["cap_bytes"]?.Value<int>();
            Assert.Equal(220_000, capBytes);
        }

        [Fact]
        public void OversizedPayload_Sentinel_HasFollowUpToolName()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var followUpTool = result["_meta"]!["truncated"]!["follow_up"]!["tool"]?.ToString();
            Assert.Equal("genexus_read", followUpTool);
        }

        [Fact]
        public void OversizedPayload_FollowUpArgs_ContainPage1AndPageSize25()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", SomeArgs());

            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal(1, followArgs["page"]?.Value<int>());
            Assert.Equal(25, followArgs["page_size"]?.Value<int>());
        }

        [Fact]
        public void OversizedPayload_FollowUpArgs_PreservesOriginalArgs()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var args = SomeArgs();

            var (result, _) = guard.Apply(OversizedPayload(), "genexus_read", args);

            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal("Invoice", followArgs["object_name"]?.ToString());
            Assert.Equal("Transaction", followArgs["type"]?.ToString());
        }

        [Fact]
        public void OversizedPayload_FollowUpArgs_DoesNotMutateOriginalArgs()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            var args = SomeArgs();
            var argsBefore = args.ToString(Newtonsoft.Json.Formatting.None);

            guard.Apply(OversizedPayload(), "genexus_read", args);

            Assert.Equal(argsBefore, args.ToString(Newtonsoft.Json.Formatting.None));
        }

        // ── null args handled gracefully ─────────────────────────────────────

        [Fact]
        public void OversizedPayload_NullArgs_FollowUpArgsHasPageFields()
        {
            var guard = new ResponseSizeGuard(maxBytes: 220_000);

            var (result, truncated) = guard.Apply(OversizedPayload(), "genexus_list_objects", null);

            Assert.True(truncated);
            var followArgs = (JObject)result["_meta"]!["truncated"]!["follow_up"]!["args"]!;
            Assert.Equal(1, followArgs["page"]?.Value<int>());
            Assert.Equal(25, followArgs["page_size"]?.Value<int>());
        }

        // ── custom cap respected ─────────────────────────────────────────────

        [Fact]
        public void CustomCap_SmallCapTriggersOnSmallPayload()
        {
            // Cap set to 10 bytes — any real JSON will exceed it
            var guard = new ResponseSizeGuard(maxBytes: 10);

            var (result, truncated) = guard.Apply(SmallPayload(), "genexus_read", SomeArgs());

            Assert.True(truncated);
            var capBytes = result["_meta"]!["truncated"]!["cap_bytes"]?.Value<int>();
            Assert.Equal(10, capBytes);
        }

        // ── default constant ─────────────────────────────────────────────────

        [Fact]
        public void DefaultMaxBytes_Is220000()
        {
            Assert.Equal(220_000, ResponseSizeGuard.DefaultMaxBytes);
        }

        // ── oversize telemetry ───────────────────────────────────────────────

        /// <summary>
        /// When Apply truncates a payload, it must emit an OVERSIZE log line via
        /// Program.Log.  Program.Log appends to gateway_debug.log in
        /// AppDomain.CurrentDomain.BaseDirectory; we snapshot the file length
        /// before the call and check the newly appended bytes for the expected
        /// marker.
        /// </summary>
        [Fact]
        public void OversizedPayload_EmitsOversizeLogLine()
        {
            string logPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "gateway_debug.log");

            // Snapshot current end-of-file position (0 if file does not exist yet)
            long positionBefore = File.Exists(logPath) ? new FileInfo(logPath).Length : 0L;

            var guard = new ResponseSizeGuard(maxBytes: 220_000);
            guard.Apply(OversizedPayload(), "genexus_inspect", SomeArgs());

            // Give the lock-based append a moment to flush (it's synchronous, so
            // this is just a defensive yield).
            System.Threading.Thread.Sleep(50);

            // Read only the bytes appended after our snapshot
            string appended = string.Empty;
            if (File.Exists(logPath))
            {
                long positionAfter = new FileInfo(logPath).Length;
                if (positionAfter > positionBefore)
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(positionBefore, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    appended = reader.ReadToEnd();
                }
            }

            Assert.Contains("OVERSIZE tool=genexus_inspect", appended);
        }
    }
}
