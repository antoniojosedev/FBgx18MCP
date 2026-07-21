using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Helpers
{
    internal static class KbModelGuard
    {
        /// <summary>
        /// Resolves the open KB's design model. Returns true with <paramref name="model"/>
        /// set on success; false with <paramref name="errJson"/> set to the canonical
        /// NoKbOpen envelope when no KB/model is available.
        /// </summary>
        public static bool TryGetDesignModel(KbService kb, out KBModel model, out string errJson)
        {
            try { model = (kb?.GetKB() as KnowledgeBase)?.DesignModel; }
            catch { model = null; }
            if (model == null)
            {
                errJson = McpResponse.Err(
                    code: "NoKbOpen",
                    message: "No open KB / design model available.",
                    hint: "Open a KB first (genexus_kb action=open).");
                return false;
            }
            errJson = null;
            return true;
        }
    }
}
