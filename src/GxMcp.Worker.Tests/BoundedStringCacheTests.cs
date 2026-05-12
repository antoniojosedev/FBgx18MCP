using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BoundedStringCacheTests
    {
        [Fact]
        public void TryAdd_EvictsLeastRecentlyUsedItem()
        {
            var cache = new BoundedStringCache(2);

            cache.TryAdd("one", "1");
            cache.TryAdd("two", "2");
            Assert.True(cache.TryGetValue("one", out _));

            cache.TryAdd("three", "3");

            Assert.True(cache.TryGetValue("one", out _));
            Assert.False(cache.TryGetValue("two", out _));
            Assert.True(cache.TryGetValue("three", out _));
        }
    }
}
