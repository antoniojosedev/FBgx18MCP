using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class KbIdentityRegistryTests
    {
        [Fact]
        public void SamePhysicalPathKeepsOpaqueIdAcrossRegistryInstances()
        {
            string root = CreateTempDirectory();
            string kb = Path.Combine(root, "kb");
            Directory.CreateDirectory(kb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("Sales", kb);
                var second = new KbIdentityRegistry(root).GetOrCreate("sales", kb + Path.DirectorySeparatorChar);

                Assert.Equal(first.KbId, second.KbId);
                Assert.NotEqual(kb, first.KbId);
                Assert.Equal(1, second.ContextGeneration);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void AliasBoundToAnotherPathReturnsDeterministicConflict()
        {
            string root = CreateTempDirectory();
            string kbA = Path.Combine(root, "a");
            string kbB = Path.Combine(root, "b");
            Directory.CreateDirectory(kbA);
            Directory.CreateDirectory(kbB);
            try
            {
                _ = new KbIdentityRegistry(root).GetOrCreate("sales", kbA);
                var error = Assert.Throws<KbIdentityConflictException>(() =>
                    new KbIdentityRegistry(root).GetOrCreate("SALES", kbB));

                Assert.Equal("KB_ALIAS_CONFLICT", error.Code);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void DifferentAliasesForSamePathReuseOneIdentity()
        {
            string root = CreateTempDirectory();
            string kb = Path.Combine(root, "shared");
            Directory.CreateDirectory(kb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("one", kb);
                var second = new KbIdentityRegistry(root).GetOrCreate("two", kb);

                Assert.Equal(first.KbId, second.KbId);
            }
            finally { TryDelete(root); }
        }

        private static string CreateTempDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
