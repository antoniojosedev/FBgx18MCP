using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // KB-wide source analytics: per-object metric extraction (genexus_analyze mode=code_metrics).
    public class CodeMetricsExtractorTests
    {
        [Fact]
        public void Extract_CountsForEachWhereNewCommit()
        {
            var src = @"
for each Customer
    where CustomerId > 0
    print Customer
endfor
new
    InvoiceId = 1
endnew
commit
";
            var m = CodeMetricsExtractor.Extract(src);
            Assert.Equal(1, m.ForEach);
            Assert.Equal(1, m.Where);
            Assert.Equal(1, m.New);
            Assert.Equal(1, m.Commit);
            Assert.Equal(0, m.NestedForEach);
        }

        [Fact]
        public void Extract_DetectsNestedForEach()
        {
            var src = @"
for each Order
    for each OrderLine
        where OrderId = Order.OrderId
        print OrderLine
    endfor
endfor
";
            var m = CodeMetricsExtractor.Extract(src);
            Assert.Equal(2, m.ForEach);
            Assert.Equal(1, m.NestedForEach); // the inner one
            Assert.Equal(1, m.Where);
        }

        [Fact]
        public void Extract_IgnoresCommentedCode()
        {
            var src = @"
// for each Ghost
/* for each BlockGhost
   where x = 1 */
for each Real
endfor
";
            var m = CodeMetricsExtractor.Extract(src);
            Assert.Equal(1, m.ForEach);
            Assert.Equal(0, m.Where);
        }

        [Fact]
        public void Extract_EmptyOrNull_IsZero()
        {
            var m = CodeMetricsExtractor.Extract(null);
            Assert.Equal(0, m.ForEach);
            Assert.Equal(0, m.Lines);
            Assert.Equal(0, CodeMetricsExtractor.Extract("").ForEach);
        }
    }
}
