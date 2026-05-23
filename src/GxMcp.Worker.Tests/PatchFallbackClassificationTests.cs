using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction 2026-05-22 #8 — write-fallback false negatives.
    //
    // Before: a "Patch write fallback failed after persistence mismatch" error
    // fired even when the primary write had landed (the SDK's fallback retry
    // reported a generic failure but the on-disk source actually matched the
    // patched content). The agent saw an error envelope and re-applied the
    // patch, which then either no-op'd or fought a concurrent sibling.
    //
    // PatchService.ClassifyFallbackFailure differentiates three cases driven
    // by what the on-disk source looks like *after* the fallback claim of
    // failure:
    //   - matches the patched content → Success + post_write_hash_drift (mode:
    //     persisted_with_concurrent_change). Patch effectively landed.
    //   - matches neither original nor patched → Success + post_write_hash_drift
    //     (mode: persisted_with_concurrent_change) with RequiresReread=true.
    //     A sibling write raced ahead.
    //   - matches the original → Error + write_not_persisted. Retry-safe; the
    //     SDK truly never persisted our payload.
    public class PatchFallbackClassificationTests
    {
        [Fact]
        public void PersistedMatchesFinal_ReturnsSuccess_WithPatchLanded()
        {
            var c = PatchService.ClassifyFallbackFailure(
                persistedMatchesOriginal: false,
                persistedMatchesFinal: true,
                fallbackError: "SDK fallback error");

            Assert.Equal("Success", c.Status);
            Assert.Equal("post_write_hash_drift", c.Code);
            Assert.Equal("persisted_with_concurrent_change", c.Mode);
            Assert.True(c.PatchLanded);
            Assert.False(c.RequiresReread);
        }

        [Fact]
        public void PersistedMatchesNeither_ReturnsSuccess_WithRequiresReread()
        {
            // Sibling write landed something else between our save + verify.
            // Don't error out (we'd clobber on retry); flag for re-read.
            var c = PatchService.ClassifyFallbackFailure(
                persistedMatchesOriginal: false,
                persistedMatchesFinal: false,
                fallbackError: "SDK fallback error");

            Assert.Equal("Success", c.Status);
            Assert.Equal("post_write_hash_drift", c.Code);
            Assert.Equal("persisted_with_concurrent_change", c.Mode);
            Assert.False(c.PatchLanded);
            Assert.True(c.RequiresReread);
        }

        [Fact]
        public void PersistedMatchesOriginal_ReturnsError_WriteNotPersisted()
        {
            // Retry-safe: nothing landed; the on-disk source is identical to
            // what we started with. The agent can retry the same patch.
            var c = PatchService.ClassifyFallbackFailure(
                persistedMatchesOriginal: true,
                persistedMatchesFinal: false,
                fallbackError: "SDK fallback error");

            Assert.Equal("Error", c.Status);
            Assert.Equal("write_not_persisted", c.Code);
            Assert.Equal("write_not_persisted", c.Mode);
            Assert.False(c.PatchLanded);
            Assert.False(c.RequiresReread);
            Assert.Contains("Retry is safe", c.Message);
        }

        [Fact]
        public void BothTrue_PrefersFinalMatch_PatchLandedWins()
        {
            // Defensive case: caller miscomputed (input is empty so original
            // and final both match). Treat as patch-landed so we don't return
            // an error when the source is clean.
            var c = PatchService.ClassifyFallbackFailure(
                persistedMatchesOriginal: true,
                persistedMatchesFinal: true,
                fallbackError: "SDK fallback error");

            Assert.Equal("Success", c.Status);
            Assert.True(c.PatchLanded);
        }
    }
}
