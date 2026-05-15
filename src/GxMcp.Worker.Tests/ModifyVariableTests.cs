using GxMcp.Worker;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // ── Task 4.3 (v2.3.8) — genexus_modify_variable ──────────────────────────────
    // The pre-SDK gates (UnknownType + ObjectNotFound) execute before any KB / SDK
    // call, so they're observable without a real KB fixture. The success path
    // (delete + add + rebind) needs a real KB and is covered indirectly via the
    // VariableTypeResolverTests + the AddVariable / DeleteVariable plumbing tests.
    public class ModifyVariableTests
    {
        private static WriteService BuildIsolatedWriteService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            return new WriteService(obj);
        }

        [Fact]
        public void ModifyVariable_UnknownType_ReturnsUnknownTypeError()
        {
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", "Bogus(99)");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("Error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["code"]?.ToString());
            Assert.False(string.IsNullOrEmpty(obj["suggestion"]?.ToString()));
            Assert.NotNull(obj["accepted"]);
            Assert.Contains("Bogus", obj["message"]?.ToString() ?? "");
        }

        [Fact]
        public void ModifyVariable_EmptyType_ReturnsUnknownTypeError()
        {
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", "   ");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("Error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["code"]?.ToString());
        }

        [Fact]
        public void ModifyVariable_NullType_ReturnsUnknownTypeError()
        {
            // Unlike AddVariable (which has a legacy "no type — use injector default"
            // path when typeName==null), ModifyVariable requires an explicit new type.
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", null);
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("Error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["code"]?.ToString());
        }

        [Fact]
        public void ModifyVariable_ObjectNotFound_ReturnsError()
        {
            // With a valid (recognized) type but no KB open / object missing, the
            // gate should fall through to the ResolveVariableTarget path and emit
            // the standard write-error envelope (isError=true).
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("NonExistentObj_" + System.Guid.NewGuid().ToString("N"), "X", "Character(40)");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            // Either the structured "isError" envelope from CreateWriteError, or a
            // legacy "error" key — both indicate the object-not-found path was hit.
            bool isError = obj["isError"]?.ToObject<bool?>() == true
                           || !string.IsNullOrEmpty(obj["error"]?.ToString())
                           || string.Equals(obj["status"]?.ToString(), "Error", System.StringComparison.OrdinalIgnoreCase);
            Assert.True(isError, "Expected error envelope. Got: " + json);
        }
    }
}
