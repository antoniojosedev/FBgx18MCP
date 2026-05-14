using System;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerPoolTests
    {
        private static Configuration CfgWithMax(int max) =>
            new Configuration { Server = new ServerConfig { MaxOpenKbs = max } };

        [Fact]
        public void ListOpen_excludes_entries_without_worker()
        {
            // ListOpen filters on Worker != null; with RegisterForTest the Worker is null,
            // so ListOpen returns empty (intentional — entries-in-flight aren't "open").
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            var open = pool.ListOpen();
            Assert.Empty(open);
        }

        [Fact]
        public void SelectVictim_picks_oldest_lastActivity()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"), lastActivity: DateTime.UtcNow.AddMinutes(-10));
            pool.RegisterForTest(new KbHandle("b", "C:/B"), lastActivity: DateTime.UtcNow.AddMinutes(-1));
            var victim = pool.SelectVictimForTest();
            Assert.NotNull(victim);
            Assert.Equal("a", victim!.Alias);
        }

        [Fact]
        public void IsAtCapacity_respects_MaxOpenKbs()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            Assert.False(pool.IsAtCapacity());
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            Assert.True(pool.IsAtCapacity());
        }

        [Fact]
        public void Close_returns_true_when_present_false_when_absent()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.True(pool.Close("a"));
            Assert.False(pool.Close("a"));
            Assert.False(pool.Close("ghost"));
        }

        [Fact]
        public void Close_is_case_insensitive()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("ProductionKb", "C:/P"));
            Assert.True(pool.Close("PRODUCTIONKB"));
        }
    }
}
