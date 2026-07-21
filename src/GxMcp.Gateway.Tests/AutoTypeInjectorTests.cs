using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Unit tests for AutoTypeInjector.TryInject.
    /// </summary>
    [Collection("AutoTypeInjectorState")]
    public class AutoTypeInjectorTests
    {
        // Wipe all cached state before each test so tests are independent.
        public AutoTypeInjectorTests()
        {
            AutoTypeInjector.ClearAll();
        }

        // ── 1. Unique name → inject type ─────────────────────────────────────

        [Fact]
        public void UniqueNameMatch_InjectsType_ReturnsTrue()
        {
            AutoTypeInjector.PrimeIndex(new[]
            {
                ("WPMain", "WebPanel"),
                ("CustomerTransaction", "Transaction"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "WPMain" };
            bool result = AutoTypeInjector.TryInject("genexus_read", args, out string injected);

            Assert.True(result);
            Assert.Equal("WebPanel", injected);
            Assert.Equal("WebPanel", args["type"]?.ToString());
        }

        // ── 2. Ambiguous name (2+ objects with same name) → no inject ────────

        [Fact]
        public void AmbiguousName_NoInject_ReturnsFalse()
        {
            // Two entries with the same name but different types → ambiguous
            AutoTypeInjector.PrimeIndex(new[]
            {
                ("SharedName", "WebPanel"),
                ("SharedName", null!),       // null signals ambiguous in the map
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "SharedName" };
            bool result = AutoTypeInjector.TryInject("genexus_inspect", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 3. Unknown name → no inject ───────────────────────────────────────

        [Fact]
        public void UnknownName_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(new[]
            {
                ("KnownObject", "Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "NonExistent" };
            bool result = AutoTypeInjector.TryInject("genexus_edit", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 4. Tool doesn't accept 'type' → no inject ─────────────────────────

        [Fact]
        public void ToolDoesNotAcceptType_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(new[]
            {
                ("MyProc", "Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_run_object", false);

            var args = new JObject { ["name"] = "MyProc" };
            bool result = AutoTypeInjector.TryInject("genexus_run_object", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 5. Caller already supplied 'type' → no inject (don't override) ───

        [Fact]
        public void CallerSuppliedType_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(new[]
            {
                ("MyProc", "Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "MyProc", ["type"] = "Transaction" };
            bool result = AutoTypeInjector.TryInject("genexus_read", args, out _);

            Assert.False(result);
            // Original caller value must be preserved unchanged
            Assert.Equal("Transaction", args["type"]?.ToString());
        }

        // ── 6. Empty index → no inject ────────────────────────────────────────

        [Fact]
        public void EmptyIndex_NoInject_ReturnsFalse()
        {
            // No PrimeIndex call → empty map
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "AnyObject" };
            bool result = AutoTypeInjector.TryInject("genexus_inspect", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 7. Skip tool (exempt list) → no inject ────────────────────────────

        [Fact]
        public void SkipTool_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(new[]
            {
                ("SomeObject", "WebPanel"),
            });
            // genexus_kb is in the skip list even if it somehow gets a 'name' arg
            var args = new JObject { ["name"] = "SomeObject" };
            bool result = AutoTypeInjector.TryInject("genexus_kb", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 8. RefreshFromRecentlyChanged feeds unique names ──────────────────

        [Fact]
        public void RefreshFromRecentlyChanged_UniqueEntry_EnablesInject()
        {
            var recent = new JArray
            {
                new JObject { ["Name"] = "MyWebPanel", ["Type"] = "WebPanel" },
                new JObject { ["Name"] = "MyProc", ["Type"] = "Procedure" },
            };
            AutoTypeInjector.RefreshFromRecentlyChanged(recent);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "MyWebPanel" };
            bool result = AutoTypeInjector.TryInject("genexus_read", args, out string injected);

            Assert.True(result);
            Assert.Equal("WebPanel", injected);
        }

        // ── 9. RefreshFromRecentlyChanged: conflicting types → ambiguous ──────

        [Fact]
        public void RefreshFromRecentlyChanged_ConflictingTypes_Ambiguous()
        {
            var recent = new JArray
            {
                new JObject { ["Name"] = "Duplicate", ["Type"] = "WebPanel" },
                new JObject { ["Name"] = "Duplicate", ["Type"] = "Procedure" },
            };
            AutoTypeInjector.RefreshFromRecentlyChanged(recent);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "Duplicate" };
            bool result = AutoTypeInjector.TryInject("genexus_inspect", args, out _);

            Assert.False(result);
        }

        // ── 10. Null arguments → no inject (guard) ────────────────────────────

        [Fact]
        public void NullArguments_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(new[] { ("X", "Procedure") });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            bool result = AutoTypeInjector.TryInject("genexus_read", null, out _);

            Assert.False(result);
        }
    }
}
