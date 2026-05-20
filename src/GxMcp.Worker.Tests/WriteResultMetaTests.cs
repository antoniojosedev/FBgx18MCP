using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WriteResultMetaTests
    {
        [Fact]
        public void TagSdkPath_OnJObject_AttachesMetaWhenAbsent()
        {
            var env = new JObject { ["status"] = "Success" };
            WriteResultMeta.TagSdkPath(env, WriteResultMeta.TypedSdk);
            Assert.Equal("typed-sdk", env["_meta"]?["sdkPath"]?.ToString());
        }

        [Fact]
        public void TagSdkPath_IsIdempotent_DoesNotOverwriteExistingValue()
        {
            // Deeper writers tag their actual path first. When WrapWithPersistedState (which
            // defaults to typed-sdk) wraps the response, it must NOT overwrite the more
            // specific value the inner writer already wrote (e.g. raw-xml from LayoutService).
            var env = new JObject
            {
                ["status"] = "Success",
                ["_meta"] = new JObject { ["sdkPath"] = "raw-xml" }
            };
            WriteResultMeta.TagSdkPath(env, WriteResultMeta.TypedSdk);
            Assert.Equal("raw-xml", env["_meta"]["sdkPath"].ToString());
        }

        [Fact]
        public void TagSdkPath_PreservesOtherMetaKeys()
        {
            var env = new JObject
            {
                ["status"] = "Success",
                ["_meta"] = new JObject { ["seeded"] = new JArray("Item1") }
            };
            WriteResultMeta.TagSdkPath(env, WriteResultMeta.TypedWriter);
            Assert.Equal("typed-writer", env["_meta"]["sdkPath"].ToString());
            Assert.NotNull(env["_meta"]["seeded"]);
        }

        [Fact]
        public void TagSdkPath_OnString_ReturnsTaggedJson()
        {
            string raw = "{\"status\":\"Success\"}";
            string tagged = WriteResultMeta.TagSdkPath(raw, WriteResultMeta.Ops);
            var parsed = JObject.Parse(tagged);
            Assert.Equal("ops", parsed["_meta"]["sdkPath"].ToString());
        }

        [Fact]
        public void TagSdkPath_OnMalformedString_ReturnsOriginal()
        {
            string raw = "not json at all";
            Assert.Equal(raw, WriteResultMeta.TagSdkPath(raw, WriteResultMeta.RawXml));
        }

        [Fact]
        public void TagSdkPath_OnNullOrEmpty_NoThrow()
        {
            WriteResultMeta.TagSdkPath((JObject)null, "typed-sdk");
            WriteResultMeta.TagSdkPath(new JObject(), "");
            Assert.Equal((string)null, WriteResultMeta.TagSdkPath((string)null, "typed-sdk"));
        }
    }
}
