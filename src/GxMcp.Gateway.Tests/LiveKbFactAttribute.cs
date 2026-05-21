using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Mirror of GxMcp.Worker.Tests.LiveKbFactAttribute — same env-var contract
    // (GXMCP_TEST_KB to opt in; optional GXMCP_REQUIRE_WWP=1 for WWP-licensed
    // suites). Lives in this project so Gateway E2E tests can be skipped by
    // default on CI without referencing the worker test assembly.
    public sealed class LiveKbFactAttribute : FactAttribute
    {
        public LiveKbFactAttribute(bool requiresWWP = false)
        {
            string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            if (string.IsNullOrEmpty(kb))
            {
                Skip = "GXMCP_TEST_KB env var not set — set to a KB folder path to run live E2E tests.";
                return;
            }
            if (requiresWWP)
            {
                string wwp = Environment.GetEnvironmentVariable("GXMCP_REQUIRE_WWP");
                if (string.IsNullOrEmpty(wwp) || wwp == "0")
                {
                    Skip = "GXMCP_REQUIRE_WWP not set — set to 1 to run WorkWithPlus-licensed E2E tests.";
                }
            }
        }
    }
}
