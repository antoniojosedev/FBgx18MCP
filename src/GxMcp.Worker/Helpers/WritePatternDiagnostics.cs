// WritePatternDiagnostics.cs
//
// Partial-class home for the GX_MCP_PATTERN_DEBUG diagnostics that live in
// WriteService.  The methods are declared in WriteService.cs; this file marks
// the boundary so they can be extracted into a standalone static helper class
// in a future pass once the private-helper dependencies are audited.
//
// Methods currently housed in WriteService.cs under this logical grouping:
//
//   LogPatternInMemoryStateIfEnabled   – dumps in-memory pattern state when
//     GX_MCP_PATTERN_DEBUG=1 is set.
//   LogPatternDiagnosticsIfEnabled     – walks the resolved part tree and
//     emits structured trace lines.
//   TryRunPatternUpdateProcess         – attempts to call the pattern's own
//     update-process hook via reflection.
//   TryLogPatternDefinitionHooks       – enumerates IPatternDefinition hooks
//     and logs their signatures.
//   TryPatternDirectSaveExperiment     – experiments with a direct SDK save
//     path; only active under GX_MCP_PATTERN_DEBUG=1.
//
// None of the above run in production (all check the env flag on entry and
// return immediately when it is unset), so this is purely a dev-time concern.
//
// TODO: once the helper dependencies (DescribeValue, FormatMethodSignature,
// GetTypeHierarchy, GetInterestingPropertySignatures, TryGetMethodSignature,
// LogResolvedObjectDiagnostics, WritePatternDebugTrace, etc.) are extracted to
// a shared static class, move the five methods above into a proper
// WritePatternDiagnostics internal static class.

namespace GxMcp.Worker.Services
{
    public partial class WriteService
    {
        // Diagnostics methods are declared in WriteService.cs.
        // This partial declaration exists to mark the file boundary and
        // allow future extraction without a big-bang refactor.
    }
}
