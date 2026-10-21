using System;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    internal static partial class WorkWithSdkAdapter
    {
        /// Issue #590: what the post-save verification is and is not. Every write
        /// receipt rereads the typed WorkWithInstance through `Load(instanceObj)` on
        /// the same object that was just saved (the XML projection, when used, reads
        /// the same in-memory PatternInstance part), so `verified` proves that the
        /// typed model round-trips through SaveTo/Save in memory. Nothing in the
        /// receipt re-opens the persisted definition from the model store: the reread
        /// is not independent of the saved instance and is not evidence that the
        /// change survives a Worker restart or is visible in the GeneXus IDE. The
        /// durability proof is the reload smoke named in `durabilitySmoke`.
        internal const string PersistenceEvidenceRereadOf = "received_in_memory_instance";

        internal const string PersistenceEvidenceLimit =
            "saved and persisted report that the SDK Save completed; verified reports that the post-save reread of the received "
            + "in-memory instance matched the request. That reread is not an independent read of the persisted definition from the "
            + "model store and is not proof that the change survives a Worker restart or is visible in the GeneXus IDE.";

        internal const string PersistenceDurabilitySmoke =
            "scripts/workwith_persistence_smoke.ps1: write, restart the Worker with genexus_worker_reload, then reread with "
            + "list_filters/list_actions on the new Worker of a disposable WorkWith KB.";

        /// The declared limit of every WorkWith write receipt (`add_action`,
        /// `remove_action`, `add_filter_attribute`). It never changes `verified`;
        /// it says what `verified` was measured against.
        internal static JObject BuildPersistenceEvidence()
        {
            return new JObject
            {
                ["rereadOf"] = PersistenceEvidenceRereadOf,
                ["independentPersistedReread"] = false,
                ["provesDurability"] = false,
                ["limit"] = PersistenceEvidenceLimit,
                ["durabilitySmoke"] = PersistenceDurabilitySmoke
            };
        }
    }
}
