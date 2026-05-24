using System;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Conditionally-skipped fact for tests that require a live GeneXus install with
    // an open KB and (depending on the pattern) a WorkWithPlus license. CI does not
    // set GXMCP_TEST_KB, so these are skipped by default; setting the env var to the
    // KB folder path opts in to running the integration smoke locally.
    //
    // Optional: GXMCP_REQUIRE_WWP=1 additionally guards WWP-licensed tests, so a
    // KB-only contributor can still run non-WWP integration tests without seeing
    // licensing failures.
    public sealed class LiveKbFactAttribute : FactAttribute
    {
        public LiveKbFactAttribute(bool requiresWWP = false, bool requiresParityFixture = false)
        {
            string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            if (string.IsNullOrEmpty(kb))
            {
                Skip = "GXMCP_TEST_KB env var not set — set to a KB folder path to run live integration tests.";
                return;
            }
            if (requiresWWP)
            {
                string wwp = Environment.GetEnvironmentVariable("GXMCP_REQUIRE_WWP");
                if (string.IsNullOrEmpty(wwp) || wwp == "0")
                {
                    Skip = "GXMCP_REQUIRE_WWP not set — set to 1 to run WorkWithPlus-licensed integration tests.";
                    return;
                }
            }
            // v2.6.9 — opt-in skip for tests that need paired pre-seeded objects
            // (one patterned via the IDE, one via the MCP) so the parity harness
            // can compare them. Without the env vars the test was always failing
            // because the KB doesn't carry the fixture by default.
            if (requiresParityFixture)
            {
                string mcpName = Environment.GetEnvironmentVariable("GXMCP_PARITY_MCP_NAME");
                string ideName = Environment.GetEnvironmentVariable("GXMCP_PARITY_IDE_NAME");
                if (string.IsNullOrEmpty(mcpName) || string.IsNullOrEmpty(ideName))
                {
                    Skip = "GXMCP_PARITY_MCP_NAME and GXMCP_PARITY_IDE_NAME must both be set, naming pre-seeded paired objects (one patterned via the IDE, one via the MCP).";
                }
            }
        }
    }
}
