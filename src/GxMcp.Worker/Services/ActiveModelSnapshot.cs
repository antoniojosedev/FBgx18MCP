using System;
using System.Threading;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// The KB version this SDK session has active, and its Design model, as last observed on the SDK
    /// thread. gx-engine reads storage directly, so it must be told which version's Design model the
    /// SDK serves (see src/gx-engine/src/udm/version_scope.rs). The snapshot is read off the SDK thread,
    /// so an engine-routed call never waits behind a running SDK operation; it is cleared before any
    /// operation that may change the active version and captured again after it, so a reader sees the
    /// state before or after the change, or nothing, and never a mix.
    /// </summary>
    internal sealed class ActiveModelSnapshot
    {
        internal const string KnownCode = "KbActiveModel";
        internal const string UnknownCode = "KbActiveModelUnknown";

        private static ActiveModelSnapshot _current;

        internal ActiveModelSnapshot(string kbPath, int versionId, string versionName, bool isTrunk, bool isFrozen, int designModelId)
        {
            KbPath = kbPath;
            VersionId = versionId;
            VersionName = versionName;
            IsTrunk = isTrunk;
            IsFrozen = isFrozen;
            DesignModelId = designModelId;
            CapturedAtUtc = DateTime.UtcNow;
        }

        internal string KbPath { get; }
        internal int VersionId { get; }
        internal string VersionName { get; }
        internal bool IsTrunk { get; }
        internal bool IsFrozen { get; }
        internal int DesignModelId { get; }
        internal DateTime CapturedAtUtc { get; }

        internal static ActiveModelSnapshot Current => Volatile.Read(ref _current);

        internal static void Clear() => Volatile.Write(ref _current, null);

        internal static void Publish(ActiveModelSnapshot snapshot) => Volatile.Write(ref _current, snapshot);

        /// <summary>
        /// Observes <paramref name="kb"/> on the SDK thread. A successful observation replaces the
        /// previous snapshot in one write, so a concurrent reader never sees the scope withdrawn while
        /// it is being re-read. A null KB or any failure clears the snapshot: an unknown active version
        /// keeps the engine off a versioned Knowledge Base.
        /// </summary>
        internal static void Capture(KnowledgeBase kb)
        {
            if (kb == null)
            {
                Clear();
                return;
            }
            Refresh(() => Observe(kb));
        }

        /// <summary>
        /// Builds a new snapshot with <paramref name="observe"/> without touching the current one, then
        /// publishes it atomically. A null result or an exception clears the current snapshot.
        /// </summary>
        internal static void Refresh(Func<ActiveModelSnapshot> observe)
        {
            ActiveModelSnapshot observed = null;
            try
            {
                observed = observe();
            }
            catch (Exception ex)
            {
                Logger.Warn("[KB-VERSION] active model snapshot failed: " + ex.Message);
            }

            if (observed == null) Clear();
            else Publish(observed);
        }

        private static ActiveModelSnapshot Observe(KnowledgeBase kb)
        {
            KBVersion active = KBVersion.GetActive(kb);
            KBModel design = kb.DesignModel;
            if (active == null || design == null) return null;
            return new ActiveModelSnapshot(
                kb.Location, active.Id, active.Name, active.IsTrunk, active.IsFrozen, design.Id);
        }

        /// <summary>The worker answer for method kbversion, action ActiveModel.</summary>
        internal static string Describe(ActiveModelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return McpResponse.Err(
                    code: UnknownCode,
                    message: "The active KB version of this SDK session is not known (no KB is open, or it is being opened or changed).",
                    hint: "Retry after the KB open or version change completes.");
            }
            return McpResponse.Ok(
                code: KnownCode,
                result: new JObject
                {
                    ["kbPath"] = snapshot.KbPath,
                    ["kbVersionId"] = snapshot.VersionId,
                    ["kbVersionName"] = snapshot.VersionName,
                    ["isTrunk"] = snapshot.IsTrunk,
                    ["isFrozen"] = snapshot.IsFrozen,
                    ["designModelId"] = snapshot.DesignModelId,
                    ["capturedAtUtc"] = snapshot.CapturedAtUtc.ToString("o"),
                    ["source"] = "sdk:KBVersion.GetActive+KnowledgeBase.DesignModel"
                });
        }
    }
}
