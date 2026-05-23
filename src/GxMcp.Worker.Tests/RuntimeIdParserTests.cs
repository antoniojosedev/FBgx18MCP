using GxMcp.Worker.Helpers;
using System.IO;
using System.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class RuntimeIdParserTests
    {
        [Fact]
        public void ParseSource_ExtractsInternalnameAssignments()
        {
            string cs = @"
                protected GXButton BtnConfirmar;
                protected GXGroup GrpNumRegProf;
                protected GXAttribute vNumRegProf;
                public void InitializeDynEvents() {
                    this.BtnConfirmar._Internalname = ""BTT58"";
                    this.GrpNumRegProf._Internalname = ""GRPNUMREGPROF"";
                    this.vNumRegProf._Internalname = ""vNUMREGPROF"";
                }
            ";
            var entries = RuntimeIdParser.ParseSource(cs);
            Assert.Equal(3, entries.Count);

            var btn = entries.First(e => e.DesignId == "BtnConfirmar");
            Assert.Equal("BTT58", btn.HtmlId);
            Assert.Equal("gxButton", btn.Kind, ignoreCase: true);

            var grp = entries.First(e => e.DesignId == "GrpNumRegProf");
            Assert.Equal("GRPNUMREGPROF", grp.HtmlId);
        }

        [Fact]
        public void ParseSource_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Empty(RuntimeIdParser.ParseSource(null));
            Assert.Empty(RuntimeIdParser.ParseSource(""));
        }

        [Fact]
        public void ParseSource_NoInternalnameLines_ReturnsEmpty()
        {
            string cs = @"public class Foo { void Bar() { var x = 1; } }";
            Assert.Empty(RuntimeIdParser.ParseSource(cs));
        }

        [Fact]
        public void ParseSource_HiddenFieldType_FlagsHiddenTrue()
        {
            string cs = @"
                protected GXHidden vSomeHidden;
                public void Init() { this.vSomeHidden._Internalname = ""vSOMEHIDDEN""; }
            ";
            var entries = RuntimeIdParser.ParseSource(cs);
            var hit = Assert.Single(entries);
            Assert.True(hit.Hidden ?? false);
        }

        // Production bug this catches: GeneXus regenerates the same _Internalname
        // assignment in multiple init blocks (e.g. InitializeDynEvents +
        // standalone_init); the parser must dedup so callers don't see the
        // same DesignId twice with possibly conflicting HtmlIds.
        [Fact]
        public void ParseSource_DuplicateInternalname_FirstWins()
        {
            string cs = @"
                protected GXButton BtnSave;
                public void A() { this.BtnSave._Internalname = ""BTT1""; }
                public void B() { this.BtnSave._Internalname = ""BTT2""; }
            ";
            var entries = RuntimeIdParser.ParseSource(cs);
            var hit = Assert.Single(entries);
            Assert.Equal("BTT1", hit.HtmlId);
        }

        // Production bug this catches: when there's no `protected GX<X>` decl
        // for a found `_Internalname` (e.g. dynamically generated control),
        // Kind/Hidden must remain null instead of NPE-ing or guessing.
        [Fact]
        public void ParseSource_InternalnameWithoutDecl_LeavesKindAndHiddenNull()
        {
            string cs = @"this.OrphanCtrl._Internalname = ""ORPH"";";
            var entries = RuntimeIdParser.ParseSource(cs);
            var hit = Assert.Single(entries);
            Assert.Equal("OrphanCtrl", hit.DesignId);
            Assert.Equal("ORPH", hit.HtmlId);
            Assert.Null(hit.Kind);
            Assert.Null(hit.Hidden);
        }

        // Production bug this catches: when GeneXus adds a new GXFoo control
        // type the parser hasn't seen, we must still emit a non-null Kind
        // (lower-cased canonical form) rather than dropping the entry or
        // returning an empty string the caller has to special-case.
        [Fact]
        public void ParseSource_UnknownGxType_FallsBackToLowercasedTypeName()
        {
            string cs = @"
                protected GXSomeFutureType vNew;
                this.vNew._Internalname = ""VNEW"";";
            var entries = RuntimeIdParser.ParseSource(cs);
            var hit = Assert.Single(entries);
            Assert.Equal("gxsomefuturetype", hit.Kind);
            Assert.False(hit.Hidden ?? true);
        }

        // Production bug this catches: assignments without the `this.`
        // prefix (the SDK sometimes emits both forms in the same file) used
        // to be silently skipped by an over-strict regex.
        [Fact]
        public void ParseSource_BareInternalname_WithoutThisPrefix_IsParsed()
        {
            string cs = @"
                protected GXButton BtnX;
                BtnX._Internalname = ""BTX"";
            ";
            var entries = RuntimeIdParser.ParseSource(cs);
            var hit = Assert.Single(entries);
            Assert.Equal("BtnX", hit.DesignId);
            Assert.Equal("BTX", hit.HtmlId);
        }

        // Production bug this catches: ParseFromKbDirectory must never
        // throw on a null/missing/file-instead-of-dir KB path — callers
        // depend on a graceful empty-list return so the response carries
        // an empty mapping instead of a 500.
        [Fact]
        public void ParseFromKbDirectory_NullEmptyOrNonexistent_ReturnsEmptyList()
        {
            Assert.Empty(RuntimeIdParser.ParseFromKbDirectory(null, "Foo"));
            Assert.Empty(RuntimeIdParser.ParseFromKbDirectory("", "Foo"));
            Assert.Empty(RuntimeIdParser.ParseFromKbDirectory(@"C:\nonexistent_path_for_test_2026", "Foo"));
            Assert.Empty(RuntimeIdParser.ParseFromKbDirectory(@"C:\Windows", null));
            Assert.Empty(RuntimeIdParser.ParseFromKbDirectory(@"C:\Windows", ""));
        }

        // Production bug this catches: a KB directory that contains no
        // GXSPC*/GEN* generator folders (fresh KB, never built) used to
        // throw EnumerationException on some Windows versions. Must
        // return empty silently.
        [Fact]
        public void ParseFromKbDirectory_KbWithoutGeneratorOutput_ReturnsEmpty()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-runtime-id-parser-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Assert.Empty(RuntimeIdParser.ParseFromKbDirectory(tmp, "Foo"));
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }
    }
}
