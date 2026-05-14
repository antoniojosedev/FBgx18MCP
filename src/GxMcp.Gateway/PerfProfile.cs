using System;

namespace GxMcp.Gateway
{
    public static class PerfProfile
    {
        public static bool V1Enabled
        {
            get
            {
                string? v = Environment.GetEnvironmentVariable("MCP_PERF_PROFILE");
                if (string.IsNullOrWhiteSpace(v)) return true;
                return !string.Equals(v, "legacy", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
