using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// <c>kb=&lt;absolute path&gt;</c> must resolve to the KB the Gateway already knows by that
    /// path, under that KB's alias. A recovery fence is scoped by alias, so an ad-hoc alias
    /// minted from the folder name split one physical KB into two scopes and the fence could
    /// not be found by a call that addressed the KB by its path.
    /// </summary>
    public class KbResolverPhysicalPathTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-kb-path-" + Guid.NewGuid().ToString("N"));
        private readonly string _kbDirectory;

        public KbResolverPhysicalPathTests()
        {
            _kbDirectory = Path.Combine(_root, "FolderName");
            Directory.CreateDirectory(_kbDirectory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private static KbResolver Resolver(params (string alias, string path)[] declared)
        {
            var env = new EnvironmentConfig { ResolutionPolicy = "strict" };
            foreach (var (alias, path) in declared) env.KBs.Add(new KbEntry { Alias = alias, Path = path });
            return new KbResolver(new Configuration { Environment = env });
        }

        [Fact]
        public void PathOfAnOpenKbResolvesToTheOpenHandleNotAFolderNameAlias()
        {
            var open = new List<KbHandle> { new KbHandle("handle-alias", _kbDirectory) };

            var handle = Resolver().Resolve(_kbDirectory, open);

            Assert.Equal("handle-alias", handle.Alias);
            Assert.Same(open[0], handle);
        }

        [Theory]
        [InlineData("trailing")]
        [InlineData("upper")]
        [InlineData("dots")]
        public void SamePhysicalPathInAnotherSpellingIsTheSameKb(string spelling)
        {
            var open = new List<KbHandle> { new KbHandle("handle-alias", _kbDirectory) };
            string argument = spelling switch
            {
                "trailing" => _kbDirectory + Path.DirectorySeparatorChar,
                "upper" => _kbDirectory.ToUpperInvariant(),
                _ => Path.Combine(_kbDirectory, "..", "FolderName")
            };

            Assert.Equal("handle-alias", Resolver().Resolve(argument, open).Alias);
        }

        [Fact]
        public void PathOfAKnownButClosedKbResolvesToTheKnownHandle()
        {
            var known = new List<KbHandle> { new KbHandle("known-alias", _kbDirectory) };

            var handle = Resolver().Resolve(_kbDirectory, Array.Empty<KbHandle>(), known);

            Assert.Equal("known-alias", handle.Alias);
        }

        [Fact]
        public void PathOfADeclaredKbResolvesToTheDeclaredAlias()
        {
            var handle = Resolver(("declared-alias", _kbDirectory)).Resolve(_kbDirectory, Array.Empty<KbHandle>());

            Assert.Equal("declared-alias", handle.Alias);
        }

        [Fact]
        public void PathOfAKbNobodyKnowsKeepsTheAdHocFolderAlias()
        {
            var open = new List<KbHandle> { new KbHandle("elsewhere", Path.Combine(_root, "Elsewhere")) };

            var handle = Resolver().Resolve(_kbDirectory, open);

            Assert.Equal("foldername", handle.Alias);
            Assert.Equal(_kbDirectory, handle.Path);
        }

        [Fact]
        public void AliasStillWinsOverAPathThatHappensToMatchAnotherKb()
        {
            var open = new List<KbHandle> { new KbHandle("handle-alias", _kbDirectory) };

            Assert.Equal("handle-alias", Resolver().Resolve("HANDLE-ALIAS", open).Alias);
        }

        [Fact]
        public void FenceRecordedUnderTheHandleAliasIsFoundByAPathSelectedCall()
        {
            // The scenario the alias fix exists for, end to end through the registry: the
            // write ran without kb (single-open => the handle's alias) and the recovery
            // call addressed the same KB by its absolute path.
            var open = new List<KbHandle> { new KbHandle("handle-alias", _kbDirectory) };
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(open[0].NormalizedAlias, "SyntheticPanel", "Events", "op-1");

            string scopeOfPathCall = Resolver().Resolve(_kbDirectory, open).NormalizedAlias;

            Assert.True(registry.TryGet(scopeOfPathCall, "SyntheticPanel", "Events", out _));
        }
    }
}
