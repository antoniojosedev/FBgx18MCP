using System;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    public class PerfProfileFlagTests
    {
        [Fact]
        public void V1Enabled_DefaultsToTrue_WhenEnvVarNotSet()
        {
            Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);
            Assert.True(PerfProfile.V1Enabled);
        }

        [Fact]
        public void V1Enabled_ReturnsFalse_WhenEnvVarIsLegacy()
        {
            Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", "legacy");
            try
            {
                Assert.False(PerfProfile.V1Enabled);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);
            }
        }

        [Fact]
        public void V1Enabled_ReturnsFalse_WhenEnvVarIsLegacyCaseInsensitive()
        {
            Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", "LEGACY");
            try
            {
                Assert.False(PerfProfile.V1Enabled);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MCP_PERF_PROFILE", null);
            }
        }
    }
}
