using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class EngineTestSupportTests : EngineRoutingTestBase
    {
        [Fact]
        public void ScratchCleanup_ReportsLockedFilesInsteadOfHidingLeaks()
        {
            string directory = NewTempDir();
            try
            {
                using var lockedFile = File.Open(Path.Combine(directory, "locked.txt"),
                    FileMode.Create, FileAccess.Write, FileShare.None);
                Assert.ThrowsAny<IOException>(() => TryDeleteDirectory(directory));
                Assert.True(Directory.Exists(directory));
            }
            finally
            {
                TryDeleteDirectory(directory);
            }
        }

        [Fact]
        public void ScratchCleanup_RemovesContentsAndAcceptsAnAbsentDirectory()
        {
            string directory = NewTempDir();
            File.WriteAllText(Path.Combine(directory, "result.txt"), "result");
            try
            {
                TryDeleteDirectory(directory);
                Assert.False(Directory.Exists(directory));
                TryDeleteDirectory(directory);
            }
            finally
            {
                TryDeleteDirectory(directory);
            }
        }
    }
}
