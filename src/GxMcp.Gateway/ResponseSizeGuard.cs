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

        public ResponseSizeGuard(int maxBytes = DefaultMaxBytes) => _maxBytes = maxBytes;

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

            Program.Log($"[Gateway] OVERSIZE tool={toolName} size={size}");
            return (sentinel, true);
        }

        private static JObject BuildFollowUp(string tool, JObject? args)
        {
            var followArgs = args != null ? (JObject)args.DeepClone() : new JObject();
            followArgs["page"] = 1;
            followArgs["page_size"] = 25;
            return new JObject { ["tool"] = tool, ["args"] = followArgs };
        }

        internal static long ByteSize(JToken token)
        {
            var counter = new CountingStream();
            using (var writer = new StreamWriter(counter, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = false })
            using (var jw = new JsonTextWriter(writer) { Formatting = Formatting.None })
            {
                token.WriteTo(jw);
                jw.Flush();
                writer.Flush();
            }
            return counter.Length;
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
