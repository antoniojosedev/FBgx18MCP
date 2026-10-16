using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// How far gx-engine coverage goes for one capability. Only
    /// <see cref="Parity"/> may ever be served by the engine; the other two states
    /// document why the SDK still owns the path.
    /// </summary>
    internal enum EngineCapabilityStatus
    {
        /// <summary>Measured at full parity for the admitted space, with recorded evidence.</summary>
        Parity,

        /// <summary>Independently implemented and partially measured; not yet a full-parity claim.</summary>
        Partial,

        /// <summary>Not implemented by the engine at all.</summary>
        Unsupported,
    }

    /// <summary>
    /// One engine capability contract. <see cref="Consumers"/> names the product
    /// operations (tool or tool.action keys) that would be served by the engine
    /// when the gate routes this capability; a capability with no consumer is
    /// documented but changes no behavior.
    /// </summary>
    internal sealed class EngineCapability
    {
        internal EngineCapability(
            string id,
            string title,
            string engineSurface,
            EngineCapabilityStatus status,
            string evidence,
            string[] consumers,
            string notes)
        {
            Id = id;
            Title = title;
            EngineSurface = engineSurface;
            Status = status;
            Evidence = evidence;
            Consumers = consumers;
            Notes = notes;
        }

        internal string Id { get; }
        internal string Title { get; }
        internal string EngineSurface { get; }
        internal EngineCapabilityStatus Status { get; }
        internal string Evidence { get; }
        internal string[] Consumers { get; }
        internal string Notes { get; }

        internal bool MayRouteToEngine => Status == EngineCapabilityStatus.Parity;
    }

    /// <summary>
    /// The capability gate for the experimental engine backend
    /// (docs/engine_backend.md). Statuses here are claims with evidence, not
    /// intentions: a capability moves to <see cref="EngineCapabilityStatus.Parity"/>
    /// only after a measured native differential, and an entry that cannot cite
    /// evidence fails the registry tests.
    /// </summary>
    internal static class EngineCapabilityRegistry
    {
        internal static readonly IReadOnlyList<EngineCapability> All = new[]
        {
            new EngineCapability(
                id: "str_format",
                title: "Numeric Str(Numeric,width,scale) picture formatting",
                engineSurface: "typed runtime Str formatting",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (native double->decimal bound pipeline; 36,246 in-domain native rows, 0 mismatches); src/gx-engine/tests/fixtures/str_matrix.tsv; src/gx-engine/fixtures/str-bounds-native.tsv",
                consumers: Array.Empty<string>(),
                notes: "Measured for literal width 1..18 in the native double->decimal narrowing regime. No product operation consumes it yet; the first candidate if a Str-formatted value enters a product path."),

            new EngineCapability(
                id: "numeric_render",
                title: "Implicit Numeric rendering (&var.ToString() / Print(&numericVar))",
                engineSurface: "typed runtime NumericType::render",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (120 native KBTeste cases: implicit == StringUtil.Str, padding included); src/gx-engine/tests/fixtures/numeric_render_native.tsv (120 recorded rows); src/gx-engine/tests/numeric_render_native.rs",
                consumers: Array.Empty<string>(),
                notes: "Native &var.ToString() measured equal to Str(value, precision, scale) across precision 1..18 boundary families, scale corners and the width 15..18 narrowed-bound regime; Print of expressions, record members and date components still uses the untyped display path and is not part of this claim. No product operation consumes it yet."),

            new EngineCapability(
                id: "date_display",
                title: "Implicit Date/DateTime rendering (&var.ToString() / Print(&dateValue))",
                engineSurface: "typed runtime Date/DateTime to_text",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (18 recorded native KBTeste display rows across three probes: MM/dd/yy, 12-hour hh:mm AM/PM, empty sentinels, empty-date-with-time override, two-digit year modulo 100); src/gx-engine/tests/fixtures/date_display_native.tsv; src/gx-engine/tests/date_display_native.rs",
                consumers: Array.Empty<string>(),
                notes: "Native &Date.ToString()/&DateTime.ToString() measured in the KBTeste environment (two-digit window 2039): MM/dd/yy with zero padding and year modulo 100, 12-hour hh:mm AM/PM with seconds dropped, \"  /  /  \" and \"  /  /   12:00 AM\" for empty values, and an empty date with a valid time rendering through the 01/01/01 component override. The engine counterpart is Print of a Date/DateTime value (variable or expression). Bare msg(&date) is a native type mismatch, and text concatenation with dates stays rejected: the native concatenation path is culture-dependent C# DateTime.ToString(). No product operation consumes it yet."),

            new EngineCapability(
                id: "text_functions",
                title: "Text functions Substr/Upper/Lower/StrReplace and their method forms",
                engineSurface: "frontend method grammar + typed runtime Text methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (63 recorded native KBTeste rows: raw-value substring rules, per-character case mapping, replace semantics); src/gx-engine/tests/fixtures/text_functions_native.tsv; src/gx-engine/tests/text_functions.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured on Character/Varchar values in a standalone Procedure: Substr is 1-based over the raw stored value (no padding), an out-of-range start yields empty text and a negative length takes the remainder; case mapping keeps a character when its mapping expands; StrReplace is case-sensitive, non-overlapping and left to right, and an empty find is a no-op. Non-BMP inputs and database-column semantics are outside the measured set. No product operation consumes it yet."),

            new EngineCapability(
                id: "text_extended_functions",
                title: "Extended text functions (LTrim, RTrim, PadL, PadR, Space, Concat, StartsWith, EndsWith, Contains, StrSearch)",
                engineSurface: "frontend method grammar + typed runtime Text methods",
                status: EngineCapabilityStatus.Parity,
                evidence: "plans/129-implementation-progress.md (95 recorded native StringUtil oracle rows: exact spacing, trimming, padding, search and case comparisons); src/gx-engine/tests/fixtures/text_extended_native.tsv; src/gx-engine/tests/text_extended.rs",
                consumers: Array.Empty<string>(),
                notes: "Measured against GeneXus.Utils.StringUtil in genexus.classes.core: LTrim/RTrim trim ASCII spaces (' ') only preserving tabs/newlines; PadL trims both ends before padding, PadR trims right end before padding; Concat trims right of first operand only; StrSearch uses 1-based indexing and clamps negative/zero start to 1. All comparisons are ordinal case-sensitive. No product operation consumes it yet."),

            new EngineCapability(
                id: "numeric_core",
                title: "Signed 64-bit arithmetic, comparisons and checked overflow",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (portable project format); src/gx-engine/tests/decimal_oracle.rs, typed_values.rs",
                consumers: Array.Empty<string>(),
                notes: "Not a GeneXus Numeric/Decimal/null compatibility claim."),

            new EngineCapability(
                id: "text_core",
                title: "Bounded text operations (Len, Trim, comparisons) over CP1252 bytes",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/tests/len_builtin.rs, text_values.rs; high-byte CP1252 divergence recorded in plans/129-implementation-progress.md",
                consumers: Array.Empty<string>(),
                notes: "Full string-library collation semantics are not implemented."),

            new EngineCapability(
                id: "calendar_values",
                title: "Date/DateTime component and literal subset",
                engineSurface: "portable Procedure runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/tests/date_values.rs, date_pipeline.rs, datetime_values.rs",
                consumers: Array.Empty<string>(),
                notes: "No timezone, DST or fractional-second coverage claim."),

            new EngineCapability(
                id: "records_collections",
                title: "Bounded record/collection runtime with native-measured aliasing",
                engineSurface: "portable format-2 compiler and runtime",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (bounded records and collections); src/gx-engine/tests/record_runtime.rs, collection_indices.rs",
                consumers: Array.Empty<string>(),
                notes: "Invalid collection index is a controlled engine error where native crashes (deliberate safety divergence)."),

            new EngineCapability(
                id: "procedure_pipeline",
                title: "Admitted Procedure compile and run (control flow, calls, Parm modes)",
                engineSurface: "gx-engine check/build/run over the portable catalog",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/tests/typed_pipeline.rs, do_case.rs, for_loop.rs, parm_modes.rs; fixtures/catalog-demo.json",
                consumers: Array.Empty<string>(),
                notes: "Bounded reachability (1,024 Procedures); no Rules/Events beyond standalone Parm."),

            new EngineCapability(
                id: "xml_projection",
                title: "SDK ExportFile XML to portable catalog projection",
                engineSurface: "gx-engine xml-check/xml-emit/xml-build/xml-run",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (read-only SDK XML compilation projection); exchange suite 16/16; one fresh disposable-KB export ran unchanged",
                consumers: Array.Empty<string>(),
                notes: "Bounded depth and item-alias rules; a compilation projection, not a lossless importer."),

            new EngineCapability(
                id: "kb_catalog",
                title: "Portable KB catalog load, validate and edit",
                engineSurface: "gx-engine kb-check/kb-emit/kb-build/kb-run",
                status: EngineCapabilityStatus.Partial,
                evidence: "src/gx-engine/README.md (independent portable KB catalog); src/gx-engine/tests/kb_pipeline.rs, catalog.rs",
                consumers: Array.Empty<string>(),
                notes: "The catalog format is the engine's own, not a native KB reader; native access is native_kb_read."),

            new EngineCapability(
                id: "native_kb_read",
                title: "Native GeneXus Knowledge Base database read",
                engineSurface: "none",
                status: EngineCapabilityStatus.Unsupported,
                evidence: "plans/129-sdk-capability-matrix.md (native-KB read rows, 0% covered)",
                consumers: Array.Empty<string>(),
                notes: "No native KB decoder exists; every native read must stay on the SDK."),

            new EngineCapability(
                id: "sql_navigation",
                title: "Specifier, navigation and SQL planning",
                engineSurface: "none",
                status: EngineCapabilityStatus.Unsupported,
                evidence: "src/gx-engine/README.md (For each and SQL/navigation planning listed as unsupported)",
                consumers: Array.Empty<string>(),
                notes: "Plan 129 milestone M2 territory."),

            new EngineCapability(
                id: "general_gx_surface",
                title: "General GeneXus objects, Rules, Events and forms",
                engineSurface: "none",
                status: EngineCapabilityStatus.Unsupported,
                evidence: "src/gx-engine/README.md (unsupported features list)",
                consumers: Array.Empty<string>(),
                notes: "Anything outside the admitted Procedure/SDT subset fails closed by design."),
        };

        internal static EngineCapability? Find(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId)) return null;
            return All.FirstOrDefault(c => string.Equals(c.Id, capabilityId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Product operations currently served by the engine across all
        /// capabilities. Empty until the first parity capability with a consumer
        /// is registered; genexus_doctor reports this list.
        /// </summary>
        internal static IReadOnlyList<string> RoutedOperations() =>
            All.Where(c => c.MayRouteToEngine)
               .SelectMany(c => c.Consumers)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToArray();
    }

    internal enum EngineRouteTarget
    {
        /// <summary>The GeneXus SDK serves this call (default, and the only possible answer with Backend=sdk).</summary>
        Sdk,

        /// <summary>The engine serves this call (parity capability, Backend=gxengine).</summary>
        Engine,

        /// <summary>Neither backend may serve this call (Backend=gxengine, Fallback=error).</summary>
        Reject,
    }

    /// <summary>
    /// Outcome of one routing decision. <see cref="Code"/> and
    /// <see cref="Message"/> are only set for <see cref="EngineRouteTarget.Reject"/>.
    /// </summary>
    internal readonly struct EngineRouteDecision
    {
        private EngineRouteDecision(EngineRouteTarget target, string capabilityId, string? code, string? message)
        {
            Target = target;
            CapabilityId = capabilityId;
            Code = code;
            Message = message;
        }

        internal EngineRouteTarget Target { get; }
        internal string CapabilityId { get; }
        internal string? Code { get; }
        internal string? Message { get; }

        internal static EngineRouteDecision Sdk(string capabilityId) =>
            new EngineRouteDecision(EngineRouteTarget.Sdk, capabilityId, null, null);

        internal static EngineRouteDecision Engine(EngineCapability capability) =>
            new EngineRouteDecision(EngineRouteTarget.Engine, capability.Id, null, null);

        internal static EngineRouteDecision Reject(string capabilityId, string code, string message) =>
            new EngineRouteDecision(EngineRouteTarget.Reject, capabilityId, code, message);
    }

    /// <summary>
    /// The single gate a future SDK-to-engine swap must call. It answers which
    /// backend serves <paramref name="capabilityId"/> under the configured
    /// policy, and never returns a partially-engine path: the engine serves a
    /// capability only at measured parity, and Backend=gxengine with
    /// Fallback=error refuses anything else instead of quietly using the SDK.
    /// </summary>
    internal static class EngineRouter
    {
        internal const string NotParityCode = "EngineCapabilityNotParity";

        internal static EngineRouteDecision Decide(string capabilityId, EngineBackendSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!settings.EngineEnabled)
                return EngineRouteDecision.Sdk(capabilityId);

            var capability = EngineCapabilityRegistry.Find(capabilityId);
            if (capability != null && capability.MayRouteToEngine)
                return EngineRouteDecision.Engine(capability);

            string reason = capability == null
                ? $"Unknown engine capability '{capabilityId}'."
                : $"Engine capability '{capability.Id}' is {capability.Status.ToString().ToLowerInvariant()}, not parity.";

            if (settings.RejectUnsupported)
                return EngineRouteDecision.Reject(capabilityId, NotParityCode, reason + " Engine.Fallback=error refuses the SDK path.");

            return EngineRouteDecision.Sdk(capabilityId);
        }
    }
}
