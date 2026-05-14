using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class KbResolverTests
    {
        private static Configuration MakeConfig(params (string alias, string path)[] kbs)
        {
            var env = new EnvironmentConfig();
            foreach (var (alias, path) in kbs)
            {
                env.KBs.Add(new KbEntry { Alias = alias, Path = path });
            }
            if (kbs.Length > 0) env.DefaultKb = kbs[0].alias;
            return new Configuration { Environment = env };
        }

        [Fact]
        public void Resolves_alias_from_config()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"), ("order", "C:/KB/Order"));
            var resolver = new KbResolver(cfg);
            var handle = resolver.Resolve("order", Array.Empty<KbHandle>());
            Assert.Equal("order", handle.Alias);
            Assert.Equal("C:/KB/Order", handle.Path);
        }

        [Fact]
        public void Alias_match_is_case_insensitive()
        {
            var cfg = MakeConfig(("Customer", "C:/KB/Customer"));
            var resolver = new KbResolver(cfg);
            var handle = resolver.Resolve("CUSTOMER", Array.Empty<KbHandle>());
            Assert.Equal("Customer", handle.Alias);
        }

        [Fact]
        public void Falls_back_to_default_when_arg_null_and_no_open_kbs()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"));
            var resolver = new KbResolver(cfg);
            var handle = resolver.Resolve(null, Array.Empty<KbHandle>());
            Assert.Equal("customer", handle.Alias);
        }

        [Fact]
        public void Uses_sole_open_kb_when_arg_null_and_one_open()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"), ("order", "C:/KB/Order"));
            var resolver = new KbResolver(cfg);
            var open = new List<KbHandle> { new KbHandle("order", "C:/KB/Order") };
            var handle = resolver.Resolve(null, open);
            Assert.Equal("order", handle.Alias);
        }

        [Fact]
        public void Throws_ambiguous_when_arg_null_and_multiple_open()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"), ("order", "C:/KB/Order"));
            var resolver = new KbResolver(cfg);
            var open = new List<KbHandle>
            {
                new KbHandle("customer", "C:/KB/Customer"),
                new KbHandle("order", "C:/KB/Order"),
            };
            var ex = Assert.Throws<KbResolutionException>(() => resolver.Resolve(null, open));
            Assert.Equal("KB_AMBIGUOUS", ex.Code);
        }

        [Fact]
        public void Throws_not_found_for_unknown_alias()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"));
            var resolver = new KbResolver(cfg);
            var ex = Assert.Throws<KbResolutionException>(() => resolver.Resolve("nope", Array.Empty<KbHandle>()));
            Assert.Equal("KB_NOT_FOUND", ex.Code);
        }

        [Fact]
        public void Returns_adhoc_handle_for_existing_absolute_path()
        {
            var tmp = Directory.CreateTempSubdirectory();
            try
            {
                var cfg = MakeConfig(("customer", "C:/KB/Customer"));
                var resolver = new KbResolver(cfg);
                var handle = resolver.Resolve(tmp.FullName, Array.Empty<KbHandle>());
                Assert.Equal(tmp.FullName, handle.Path);
                Assert.Equal(tmp.Name.ToLowerInvariant(), handle.Alias);
            }
            finally
            {
                tmp.Delete(true);
            }
        }

        [Fact]
        public void Throws_ambiguous_when_no_arg_no_default_no_open()
        {
            var cfg = new Configuration { Environment = new EnvironmentConfig() };
            var resolver = new KbResolver(cfg);
            var ex = Assert.Throws<KbResolutionException>(() => resolver.Resolve(null, Array.Empty<KbHandle>()));
            Assert.Equal("KB_AMBIGUOUS", ex.Code);
        }
    }
}
