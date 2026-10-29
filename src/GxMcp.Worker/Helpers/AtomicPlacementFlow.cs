using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The placement a caller asked for on <c>genexus_create action=object_atomic</c>:
    /// <c>folder</c>, <c>module</c> or <c>parentPath</c>.
    /// </summary>
    internal sealed class PlacementRequest
    {
        public string Raw { get; private set; }
        /// <summary>Folder, Module or null (a path or a name names its own kind).</summary>
        public string Kind { get; private set; }
        /// <summary>The argument the text came from: folder, module or parentPath.</summary>
        public string Source { get; private set; }

        /// <summary>
        /// Reads the placement arguments. Returns null when none was given. When two of them name
        /// different places the request is ambiguous and <paramref name="conflict"/> says so: an object
        /// must never be created somewhere the caller did not mean.
        /// </summary>
        public static PlacementRequest FromCreateArgs(JObject args, out string conflict)
        {
            conflict = null;
            var given = new List<PlacementRequest>();
            AddIfGiven(given, args?["folder"], PlacementContainer.FolderKind, "folder");
            AddIfGiven(given, args?["module"], PlacementContainer.ModuleKind, "module");
            AddIfGiven(given, args?["parentPath"], null, "parentPath");
            if (given.Count == 0) return null;

            var places = given.Select(g => string.Join("/", PlacementResolver.SplitSegments(g.Raw, out _)).ToLowerInvariant()).Distinct().ToList();
            if (places.Count > 1)
            {
                conflict = "Conflicting placement arguments: "
                    + string.Join(", ", given.Select(g => g.Source + "='" + g.Raw + "'"))
                    + ". They name different places.";
                return null;
            }
            var kinds = given.Where(g => g.Kind != null).Select(g => g.Kind).Distinct().ToList();
            if (kinds.Count > 1)
            {
                conflict = "Conflicting placement arguments: folder and module both name '" + given[0].Raw + "' but with different kinds.";
                return null;
            }
            return new PlacementRequest { Raw = given[0].Raw, Kind = kinds.FirstOrDefault(), Source = given[0].Source };
        }

        private static void AddIfGiven(List<PlacementRequest> into, JToken token, string kind, string source)
        {
            string text = token?.Type == JTokenType.String || token?.Type == JTokenType.Integer ? token.ToString() : null;
            if (string.IsNullOrWhiteSpace(text)) return;
            into.Add(new PlacementRequest { Raw = text.Trim(), Kind = kind, Source = source });
        }
    }

    /// <summary>
    /// Proves, with a fresh read, that an object just created by <c>object_atomic</c> sits in the
    /// Folder/Module that was asked for, repairs it through the move pipeline when the SDK ignored the
    /// parent given at creation, and deletes the object again (confirming it is gone) when neither
    /// worked. The SDK access sits behind <see cref="Ports"/>, so every branch is unit-testable.
    /// </summary>
    internal static class AtomicPlacementFlow
    {
        internal const string AtCreation = "atCreation";
        internal const string ByMove = "move";

        internal const string SdkRefusedReason = "PlacementSdkRefused";
        internal const string NotPersistedReason = "PlacementNotPersisted";
        internal const string VerificationFailedReason = "PlacementVerificationFailed";

        /// <summary>What a fresh read of the created object reports.</summary>
        internal sealed class Probe
        {
            public bool Readable { get; set; }
            public Guid ObjectGuid { get; set; }
            /// <summary>The container actually holding the object; null for the Root Module.</summary>
            public Guid? ParentGuid { get; set; }
            public string ParentPath { get; set; }
            public string Error { get; set; }
        }

        internal sealed class MoveAttempt
        {
            public bool Ok { get; set; }
            public string Strategy { get; set; }
            public string Code { get; set; }
            public string Error { get; set; }
        }

        internal sealed class DeleteAttempt
        {
            public bool Deleted { get; set; }
            /// <summary>A fresh lookup by identity found nothing after the delete.</summary>
            public bool AbsentVerified { get; set; }
            public string Error { get; set; }
        }

        internal sealed class Ports
        {
            public Func<Probe> FreshProbe { get; set; }
            public Func<MoveAttempt> MoveIntoContainer { get; set; }
            public Func<DeleteAttempt> DeleteCreated { get; set; }
        }

        internal sealed class Outcome
        {
            public bool Ok { get; set; }
            public string Strategy { get; set; }
            public string ReasonCode { get; set; }
            public string Message { get; set; }
            public Probe Persisted { get; set; }
            public JObject Compensation { get; set; }
            public JObject Attempt { get; set; }
        }

        public static Outcome Verify(PlacementResolution destination, Ports ports)
        {
            if (destination == null || !destination.Ok) throw new ArgumentException("A resolved destination is required.", nameof(destination));
            if (ports?.FreshProbe == null || ports.MoveIntoContainer == null || ports.DeleteCreated == null)
                throw new ArgumentException("Every port is required.", nameof(ports));

            Guid? expected = destination.IsRoot ? (Guid?)null : destination.Container.Guid;
            string expectedPath = destination.ResolvedPath;
            var attempt = new JObject { ["expectedParent"] = ParentJson(expected, expectedPath) };

            Probe first = SafeProbe(ports);
            attempt["afterCreate"] = ProbeJson(first);
            if (first.Readable && Matches(first, expected))
                return new Outcome { Ok = true, Strategy = AtCreation, Persisted = first, Attempt = attempt };

            if (!first.Readable)
                return Fail(ports, VerificationFailedReason,
                    "The created object could not be re-read to confirm where it was saved: " + (first.Error ?? "fresh read unavailable") + ".",
                    first, attempt);

            // The SDK saved the object but left it somewhere else (the parent given at creation was not
            // honoured): place it with the same pipeline genexus_properties action=move uses.
            MoveAttempt move;
            try { move = ports.MoveIntoContainer() ?? new MoveAttempt { Ok = false, Error = "the move returned nothing" }; }
            catch (Exception ex) { move = new MoveAttempt { Ok = false, Error = ex.Message }; }
            attempt["move"] = new JObject
            {
                ["ok"] = move.Ok,
                ["strategy"] = move.Strategy,
                ["code"] = move.Code,
                ["error"] = move.Error
            };
            if (!move.Ok)
                return Fail(ports, SdkRefusedReason,
                    "The SDK left the new object in " + (first.ParentPath ?? PlacementCatalog.RootName) + " and refused to move it into " + expectedPath
                    + (string.IsNullOrWhiteSpace(move.Error) ? "." : ": " + move.Error),
                    first, attempt);

            Probe second = SafeProbe(ports);
            attempt["afterMove"] = ProbeJson(second);
            if (!second.Readable)
                return Fail(ports, VerificationFailedReason,
                    "The object was moved but could not be re-read to confirm it: " + (second.Error ?? "fresh read unavailable") + ".",
                    second, attempt);
            if (!Matches(second, expected))
                return Fail(ports, NotPersistedReason,
                    "The SDK reported the move into " + expectedPath + " but a fresh read still finds the object in " + (second.ParentPath ?? PlacementCatalog.RootName) + ".",
                    second, attempt);

            return new Outcome { Ok = true, Strategy = ByMove, Persisted = second, Attempt = attempt };
        }

        private static bool Matches(Probe probe, Guid? expected)
            => expected.HasValue ? probe.ParentGuid == expected : !probe.ParentGuid.HasValue;

        private static Probe SafeProbe(Ports ports)
        {
            try { return ports.FreshProbe() ?? new Probe { Readable = false, Error = "the fresh read returned nothing" }; }
            catch (Exception ex) { return new Probe { Readable = false, Error = ex.Message }; }
        }

        private static Outcome Fail(Ports ports, string reason, string message, Probe last, JObject attempt)
        {
            DeleteAttempt delete;
            try { delete = ports.DeleteCreated() ?? new DeleteAttempt { Error = "the delete returned nothing" }; }
            catch (Exception ex) { delete = new DeleteAttempt { Error = ex.Message }; }

            bool rolledBack = delete.Deleted && delete.AbsentVerified;
            return new Outcome
            {
                Ok = false,
                ReasonCode = reason,
                Message = message,
                Persisted = last,
                Attempt = attempt,
                Compensation = new JObject
                {
                    ["action"] = "delete_created_object",
                    ["deleted"] = delete.Deleted,
                    ["absentVerified"] = delete.AbsentVerified,
                    ["rolledBack"] = rolledBack,
                    ["status"] = rolledBack ? "Deleted" : (delete.Deleted ? "DeletedUnverified" : "Failed"),
                    ["error"] = delete.Error,
                    ["note"] = rolledBack
                        ? "The object was deleted again and a fresh lookup by identity confirmed it is gone: nothing was left behind."
                        : "The object may still exist in " + (last?.ParentPath ?? PlacementCatalog.RootName) + "; verify it with genexus_query and remove it with genexus_delete_object."
                }
            };
        }

        private static JObject ParentJson(Guid? guid, string path)
            => new JObject { ["guid"] = guid?.ToString(), ["path"] = path };

        private static JObject ProbeJson(Probe probe)
            => probe.Readable
                ? new JObject { ["readable"] = true, ["parent"] = ParentJson(probe.ParentGuid, probe.ParentPath) }
                : new JObject { ["readable"] = false, ["error"] = probe.Error };
    }
}
