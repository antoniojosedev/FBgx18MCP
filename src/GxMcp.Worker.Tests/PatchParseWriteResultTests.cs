using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #24 (mode=patch) — PatchService keys success off the legacy _internalStatus
    // shape, but WriteService emits the canonical {status:"ok", code:...} envelope.
    // ParseWriteResult must bridge canonical -> _internalStatus so a clean write isn't
    // treated as a failure (which forced the fallback/rollback path and produced the
    // mangled {"message":"{"} envelope on inline native-code [! !] content).
    public class PatchParseWriteResultTests
    {
        // ParseWriteResult is private static — reach it via reflection (same assembly,
        // InternalsVisibleTo doesn't expose privates).
        private static JObject Parse(string json)
        {
            var m = typeof(PatchService).GetMethod("ParseWriteResult",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (JObject)m!.Invoke(null, new object[] { json })!;
        }

        [Theory]
        [InlineData("WriteApplied")]
        [InlineData("WriteNoChange")]
        public void Canonical_OkEnvelope_LiftsToSuccess(string code)
        {
            var jo = Parse(new JObject { ["status"] = "ok", ["code"] = code, ["target"] = "P" }.ToString());
            Assert.Equal("Success", (string)jo["_internalStatus"]);
        }

        [Fact]
        public void Canonical_PartialEnvelope_LiftsToSuccess()
        {
            var jo = Parse(new JObject { ["status"] = "partial", ["code"] = "WriteApplied" }.ToString());
            Assert.Equal("Success", (string)jo["_internalStatus"]);
        }

        [Fact]
        public void Canonical_ErrorEnvelope_LiftsToErrorAndMessage()
        {
            var jo = Parse(new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "Boom", ["message"] = "it failed" }
            }.ToString());
            Assert.Equal("Error", (string)jo["_internalStatus"]);
            Assert.Equal("it failed", (string)jo["message"]);
        }

        [Fact]
        public void Legacy_SuccessEnvelope_StillRecognized()
        {
            // A not-yet-migrated caller may still emit status:"Success".
            var jo = Parse(new JObject { ["status"] = "Success" }.ToString());
            Assert.Equal("Success", (string)jo["_internalStatus"]);
        }

        [Fact]
        public void Unparseable_FallsBackToError()
        {
            var jo = Parse("{");
            Assert.Equal("Error", (string)jo["_internalStatus"]);
            Assert.Equal("{", (string)jo["message"]);
        }
    }
}
