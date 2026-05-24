using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Wave-3 doc-flagged "Long-term / strategic XL" + "Skip / wait for user
    // feedback" items. The schema is shipped so agents can discover the
    // intended shape; the body returns a typed `{ status:"Future", code:
    // "ItemDeferred", hint, docRef }` envelope. When demand for one of these
    // tools materialises, swap the dispatch target from this stub to a real
    // service — no schema migration required.
    public static class FutureItemStub
    {
        public const string DocBase = "docs/mcp-improvements-2026-05-22.md#item-";

        public static string Deferred(int itemNumber, string hint)
        {
            var envelope = new JObject
            {
                ["status"] = "Future",
                ["code"] = "ItemDeferred",
                ["itemNumber"] = itemNumber,
                ["hint"] = hint ?? string.Empty,
                ["docRef"] = DocBase + itemNumber
            };
            return envelope.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
