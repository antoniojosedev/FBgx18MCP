using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Decides whether a <c>genexus_wire</c> call is a <c>pack_task</c> on the native Knowledge
    /// Base the session has open, which the Worker answers from the SDK-backed index.
    ///
    /// The file-based gxwire engine cannot index a native KB (its object sources live in the KB
    /// database, see <see cref="NativeKnowledgeBaseEvidence"/>), so this is the one action that
    /// leaves the Gateway-owned path: the call falls through to the Worker instead of being
    /// refused with GXWIRE_REQUIRES_TEXT_SOURCE_TREE. Every other action, and every analysis of a
    /// text tree, keeps its existing behaviour.
    /// </summary>
    internal static class GxwireNativePackTask
    {
        internal static bool Applies(string? toolName, JObject? args, string? openKbPath)
        {
            if (!string.Equals(toolName, "genexus_wire", StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsPackTask(args)) return false;
            if (string.IsNullOrWhiteSpace(openKbPath)) return false;

            // The Worker answers for the KB it has open. A dir that names some other folder is a
            // text tree (or another KB) and stays on the existing path.
            string? dir = args?["dir"]?.ToString();
            if (!string.IsNullOrWhiteSpace(dir) && !SamePath(dir, openKbPath)) return false;

            return NativeKnowledgeBaseEvidence.Describe(openKbPath) != null;
        }

        /// <summary>
        /// The native-KB refusal reaches a pack_task call only when the KB is not open (or when dir names some
        /// other native KB). It keeps its code and text, and for pack_task it also says how to reach the SDK path
        /// instead of leaving a whole-KB export as the only advice.
        /// </summary>
        internal static JObject WithPackTaskHint(JObject refusal, JObject? args)
        {
            if (IsPackTask(args))
                refusal["packTask"] = "action=pack_task needs no export on a native Knowledge Base: open it (genexus_kb action=open path=<KB root>) and repeat the call with targets=[\"<ObjectName>\", ...] or the object names in task; the Worker resolves them through the SDK index, read-only.";
            return refusal;
        }

        internal static bool IsPackTask(JObject? args)
        {
            string action = args?["action"]?.ToString()?.Trim() ?? "pack_task";
            return string.Equals(action, "pack_task", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "pack-task", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            try
            {
                return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // A path that cannot be normalised is not the open KB; never claim it is.
                return false;
            }
        }

        private static string Normalize(string path)
            => Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');
    }
}
