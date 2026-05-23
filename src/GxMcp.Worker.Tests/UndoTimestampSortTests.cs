using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Snapshot files live as <c>&lt;guid&gt;-&lt;part&gt;-&lt;yyyyMMddTHHmmssfffZ&gt;.bak</c>.
    /// Ordinal sort of the full filename is dominated by the leading guid, so
    /// the "N most recent" undo selection used to silently pick GUID-buckets
    /// instead of the actually-newest timestamps. The fix sorts by the
    /// timestamp segment via ExtractSnapshotTimestamp.
    /// </summary>
    public class UndoTimestampSortTests
    {
        [Fact]
        public void ExtractSnapshotTimestamp_ParsesTimestampSegment()
        {
            // ExtractSnapshotTimestamp is private static — reach in via reflection
            // (it's a parser helper; pure function over the filename string).
            var mi = typeof(GxMcp.Worker.Services.UndoService)
                .GetMethod("ExtractSnapshotTimestamp", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(mi);

            string a = (string)mi!.Invoke(null, new object[] { @"C:\kb\.gx\snapshots\AAAAGUID-Source-20260101T120000000Z.bak" })!;
            string b = (string)mi.Invoke(null, new object[] { @"C:\kb\.gx\snapshots\BBBBGUID-Source-20260101T120500000Z.bak.gz" })!;

            Assert.Equal("20260101T120000000Z", a);
            Assert.Equal("20260101T120500000Z", b);
            Assert.True(string.CompareOrdinal(b, a) > 0, "b is later, must sort greater");
        }

        [Fact]
        public void ExtractSnapshotTimestamp_FallsBackToFilename_OnUnexpectedShape()
        {
            var mi = typeof(GxMcp.Worker.Services.UndoService)
                .GetMethod("ExtractSnapshotTimestamp", BindingFlags.Static | BindingFlags.NonPublic);
            string fallback = (string)mi!.Invoke(null, new object[] { "weirdname.bak" })!;
            Assert.Equal("weirdname", fallback);
        }

        // Production bug this catches: ParseSnapshotFileName splits on '-'
        // from the right; a sanitized GUID that contains underscores
        // (the sanitizer's encoding of '-') used to be reassembled
        // incorrectly, so the rawGuid wouldn't round-trip and the
        // restore phase couldn't find the object.
        [Fact]
        public void ParseSnapshotFileName_RoundtripsSanitizedGuid()
        {
            var mi = typeof(GxMcp.Worker.Services.UndoService)
                .GetMethod("ParseSnapshotFileName", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(mi);

            // sanitized guid has no '-' (sanitizer turns them into '_')
            string name = @"C:\snap\AAAA_BBBB_CCCC_DDDD-Source-20260101T120000000Z.bak";
            object meta = mi!.Invoke(null, new object[] { name })!;
            Assert.NotNull(meta);

            string rawGuid = (string)meta.GetType().GetField("RawGuid")!.GetValue(meta)!;
            string part    = (string)meta.GetType().GetField("Part")!.GetValue(meta)!;
            string ts      = (string)meta.GetType().GetField("Timestamp")!.GetValue(meta)!;

            Assert.Equal("AAAA_BBBB_CCCC_DDDD", rawGuid);
            Assert.Equal("Source", part);
            Assert.Equal("20260101T120000000Z", ts);
        }

        // Production bug this catches: a malformed snapshot filename
        // (under-segmented) used to throw out of ParseSnapshotFileName
        // instead of being skipped as "Could not parse" in the failed[]
        // array. ParseSnapshotFileName must return null on too-short input.
        [Fact]
        public void ParseSnapshotFileName_TooFewSegments_ReturnsNull()
        {
            var mi = typeof(GxMcp.Worker.Services.UndoService)
                .GetMethod("ParseSnapshotFileName", BindingFlags.Static | BindingFlags.NonPublic);
            object result = mi!.Invoke(null, new object[] { @"C:\snap\too-short.bak" })!;
            Assert.Null(result);
        }

        // Production bug this catches: .bak.gz suffix handling — the right-
        // anchored suffix strip must remove BOTH ".gz" AND ".bak"
        // (or the 7-char compound), not leave a dangling ".bak" segment
        // that gets parsed as the part name.
        [Fact]
        public void ParseSnapshotFileName_GzipSuffix_StrippedCorrectly()
        {
            var mi = typeof(GxMcp.Worker.Services.UndoService)
                .GetMethod("ParseSnapshotFileName", BindingFlags.Static | BindingFlags.NonPublic);
            object meta = mi!.Invoke(null, new object[] { @"C:\snap\GGUUIIDD-Layout-20260202T030405000Z.bak.gz" })!;
            Assert.NotNull(meta);
            string part = (string)meta.GetType().GetField("Part")!.GetValue(meta)!;
            string ts   = (string)meta.GetType().GetField("Timestamp")!.GetValue(meta)!;
            Assert.Equal("Layout", part);
            Assert.Equal("20260202T030405000Z", ts);
        }

        [Fact]
        public void SortPicksLatestTimestamp_NotLargestGuid()
        {
            var mi = typeof(GxMcp.Worker.Services.UndoService)
                .GetMethod("ExtractSnapshotTimestamp", BindingFlags.Static | BindingFlags.NonPublic);
            string[] files =
            {
                @"snap\ZZZZGUID-Source-20260101T100000000Z.bak",   // largest guid but oldest
                @"snap\AAAAGUID-Source-20260101T120000000Z.bak",   // smallest guid but newest
                @"snap\MMMMGUID-Source-20260101T110000000Z.bak",
            };
            var ordered = files
                .OrderByDescending(p => (string)mi!.Invoke(null, new object[] { p })!, StringComparer.Ordinal)
                .ToList();
            Assert.EndsWith("100000000Z.bak", ordered[2]); // oldest is last
            Assert.EndsWith("120000000Z.bak", ordered[0]); // newest is first — would be wrong under path sort
        }
    }
}
