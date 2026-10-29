using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    // The U16 + WorkWithPlus 16.2 bundle keeps its name and surface; the audit itself lives in EventsBundleAudit, which
    // serves every audited bundle, and the manifests are listed in EventsBundleAudits.
    internal static class EventsU16BundleAudit
    {
        internal const string BundleId = "18.0.16.189595+wwp16.2.0.8386";

        internal static Dictionary<string, string> CriticalHashes => EventsBundleAudits.U16.CriticalHashes;

        internal static bool MaySaveTarget(JObject verification, string name)
            => EventsBundleAudit.MaySaveTarget(verification, name);

        internal static void RequireExactHash(string expected, string actual, string name)
            => EventsBundleAudit.RequireExactHash(expected, actual, name);
    }
}
