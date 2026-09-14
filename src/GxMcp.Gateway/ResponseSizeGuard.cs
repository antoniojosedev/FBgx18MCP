using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace GxMcp.Gateway
{
    public sealed class ResponseSizeGuard
    {
        public const int DefaultMaxBytes = 220_000; // ~55k tokens

        private readonly int _maxBytes;
        private readonly Action<string> _log;

        public ResponseSizeGuard(int maxBytes = DefaultMaxBytes) : this(maxBytes, Program.Log) { }

        internal ResponseSizeGuard(int maxBytes, Action<string> log)
        {
            _maxBytes = maxBytes;
            _log = log;
        }

        public (JObject result, bool truncated) Apply(JObject payload, string toolName, JObject? args)
        {
            long size = ByteSize(payload);
            if (size <= _maxBytes) return (payload, false);

            int originalSize = size > int.MaxValue ? int.MaxValue : (int)size;

            var sentinel = new JObject
            {
                ["_meta"] = new JObject
                {
                    ["truncated"] = new JObject
                    {
                        ["reason"] = "response_exceeded_cap",
                        ["original_size"] = originalSize,
                        ["cap_bytes"] = _maxBytes,
                        ["follow_up"] = BuildFollowUp(toolName, args)
                    }
                }
            };

            _log($"[Gateway] OVERSIZE tool={toolName} size={size}");
            return (sentinel, true);
        }

        private static JObject BuildFollowUp(string tool, JObject? args)
        {
            var followArgs = args != null ? (JObject)args.DeepClone() : new JObject();
            // issue #25 follow-up (A3): the retry hint must use args the tool actually
            // understands. inspect/analyze don't paginate (page/page_size were a no-op
            // that looped the agent back to the same oversize call) — steer them to the
            // levers that shrink the payload instead.
            string t = tool ?? string.Empty;
            if (t.Equals("genexus_inspect", StringComparison.OrdinalIgnoreCase))
            {
                followArgs["include"] = new JArray("signature", "variables", "metadata");
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Re-request a subset with include=[...], or read a single part with genexus_read (paginated)." };
            }
            if (t.Equals("genexus_analyze", StringComparison.OrdinalIgnoreCase)
                || t.Equals("genexus_navigation", StringComparison.OrdinalIgnoreCase)
                || t.Equals("genexus_structure", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject { ["tool"] = tool, ["args"] = followArgs,
                    ["hint"] = "Response too large. Narrow the request (a specific target/mode/part) or read incrementally with genexus_read." };
            }
            followArgs["page"] = 1;
            followArgs["page_size"] = 25;
            return new JObject { ["tool"] = tool, ["args"] = followArgs };
        }

        // PERFORMANCE (G-B2): existing path streams the JSON through a CountingStream so we
        // never allocate the full string just to measure its size — that's still the right
        // call for unknown payloads. Two micro-tweaks below:
        //  - buffer bumped from 1KB to 32KB so a 220KB payload triggers ~7 internal flushes
        //    instead of ~220;
        //  - overload that takes an already-serialised string uses Encoding.UTF8.GetByteCount
        //    directly, which is the fast path for callers that have the JSON in hand already.
        internal static long ByteSize(string serializedJson)
        {
            if (string.IsNullOrEmpty(serializedJson)) return 0;
            return Encoding.UTF8.GetByteCount(serializedJson);
        }

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        [ThreadStatic]
        private static CountingContext? t_countingContext;

        private sealed class CountingContext
        {
            public readonly CountingStream Stream = new CountingStream();
            public readonly StreamWriter Writer;

            public CountingContext()
            {
                Writer = new StreamWriter(Stream, Utf8NoBom, bufferSize: 32 * 1024, leaveOpen: true) { AutoFlush = false };
            }

            public void Reset()
            {
                Stream.Count = 0;
            }
        }

        internal static long ByteSize(JToken token)
        {
            if (token == null) return 0;
            var ctx = t_countingContext ??= new CountingContext();
            ctx.Reset();
            using (var jw = new JsonTextWriter(ctx.Writer) { Formatting = Formatting.None, CloseOutput = false })
            {
                token.WriteTo(jw);
                jw.Flush();
                ctx.Writer.Flush();
            }
            return ctx.Stream.Length;
        }

        private sealed class CountingStream : Stream
        {
            public long Count;
            public override bool CanWrite => true;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) => Count += count;
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
