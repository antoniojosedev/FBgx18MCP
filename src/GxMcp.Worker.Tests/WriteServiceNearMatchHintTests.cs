using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class WriteServiceNearMatchHintTests
    {
        [Fact]
        public void NearMatch_OneCharDifferent_ReturnsContentDivergence()
        {
            var source = "    Where IdAgenda = &IdAgenda\n    Where ParecerFinal = 3\n";
            var context = "    Where IdAgenda = &IdAgenda\n    Where ParecerFinal <> 3\n";
            var hint = DiffBuilder.ByteLevelDivergence(source, context);
            Assert.True(hint["similarity"].Value<double>() >= 0.80);
            Assert.Equal("Content", hint["topWindow"]["divergenceKind"].ToString());
            Assert.Equal(2, hint["topWindow"]["firstDivergenceAt"]["line"].Value<int>());
        }

        [Fact]
        public void NearMatch_OnlyEolDiffers_ReturnsEolKind()
        {
            var source = "foo\r\nbar\r\n";
            var context = "foo\nbar\n";
            var hint = DiffBuilder.ByteLevelDivergence(source, context);
            Assert.Equal("EOL", hint["topWindow"]["divergenceKind"].ToString());
        }
    }
}
