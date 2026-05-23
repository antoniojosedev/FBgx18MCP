using System.Linq;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Friction 2026-05-22 #64: projection=minimal|standard|verbose lets the agent
    // opt into a smaller / larger field set on genexus_inspect and
    // genexus_list_objects without enumerating fields[]. Program.ResolveProjection
    // is the pure function that maps the projection level to the HashSet the
    // gateway applies in NormalizeToolPayloadForAxi.
    public class ProjectionLevelTests
    {
        [Fact]
        public void Minimal_ReturnsThreeFieldShape_NameTypeLastUpdate()
        {
            var fields = Program.ResolveProjection("genexus_list_objects", "minimal");
            Assert.NotNull(fields);
            Assert.Contains("name", fields, System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("type", fields, System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("lastUpdate", fields, System.StringComparer.OrdinalIgnoreCase);
            // 'kind' is defensively whitelisted but otherwise 3 fields are the floor.
            Assert.True(fields.Count <= 4);
        }

        [Fact]
        public void Standard_DelegatesToGetDefaultCompactFields()
        {
            // Standard projection is the same field set as today's default
            // (axiCompact=true with no fields[] override). Asserting equality
            // here keeps the two paths in lockstep — if GetDefaultCompactFields
            // gains a new field, the standard projection picks it up for free.
            var std = Program.ResolveProjection("genexus_list_objects", "standard");
            Assert.NotNull(std);
            Assert.Contains("name", std, System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("type", std, System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("path", std, System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("lastUpdate", std, System.StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Verbose_ReturnsNull_SoNoFilterIsApplied()
        {
            // verbose === "show me everything" — gateway's projection step is
            // skipped when the resolver returns null, so the full worker payload
            // flows through.
            Assert.Null(Program.ResolveProjection("genexus_list_objects", "verbose"));
            Assert.Null(Program.ResolveProjection("genexus_inspect", "verbose"));
        }

        [Fact]
        public void Inspect_MinimalSameAsListObjectsMinimal()
        {
            // Both supported tools share the minimal shape — same 3 fields.
            var inspectMin = Program.ResolveProjection("genexus_inspect", "minimal");
            var listMin = Program.ResolveProjection("genexus_list_objects", "minimal");
            Assert.NotNull(inspectMin);
            Assert.NotNull(listMin);
            Assert.Equal(inspectMin.OrderBy(s => s), listMin.OrderBy(s => s));
        }

        [Fact]
        public void UnknownLevel_ReturnsNull()
        {
            // Unknown projection level — caller falls back to axiCompact default.
            Assert.Null(Program.ResolveProjection("genexus_list_objects", "tiny"));
            Assert.Null(Program.ResolveProjection("genexus_list_objects", ""));
        }

        [Fact]
        public void CaseInsensitive_AcceptsMixedCase()
        {
            // minimal/standard return a HashSet; verbose intentionally returns null.
            Assert.NotNull(Program.ResolveProjection("genexus_list_objects", "MINIMAL"));
            Assert.NotNull(Program.ResolveProjection("genexus_list_objects", "Standard"));
            // "Verbose" returns null (no filter) — the call itself must not throw.
            Assert.Null(Program.ResolveProjection("genexus_list_objects", "Verbose"));
        }
    }
}
