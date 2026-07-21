using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.8.0 (S1) — MCP completion/complete autocompletes object names from
    // the cached index. Real wire is exercised via the AutoTypeInjector
    // surface that completion/complete delegates to.
    [Collection("AutoTypeInjectorState")]
    public class CompletionNameTests
    {
        public CompletionNameTests() => AutoTypeInjector.ClearAll();

        [Fact]
        public void CompleteName_PrefixMatch_ReturnsCaseInsensitiveMatches()
        {
            AutoTypeInjector.PrimeIndex(new (string, string?)[]
            {
                ("Customer", "Transaction"),
                ("CustomerOrder", "Transaction"),
                ("Supplier", "Transaction"),
            });

            var matches = AutoTypeInjector.CompleteName("cust").ToList();
            Assert.Equal(2, matches.Count);
            Assert.Contains("Customer", matches);
            Assert.Contains("CustomerOrder", matches);
        }

        [Fact]
        public void CompleteName_EmptyPrefix_ReturnsAllUpToCap()
        {
            // Use a unique prefix so parallel tests in the same test class
            // (or test runner mucking with the shared static dict) can't
            // race-add entries that change the visible count for this scope.
            const string unique = "CompletionEmptyTest_";
            AutoTypeInjector.PrimeIndex(Enumerable.Range(0, 50)
                .Select(i => ($"{unique}{i:D2}", (string?)"Transaction")));
            var matches = AutoTypeInjector.CompleteName(unique, cap: 25).ToList();
            Assert.Equal(25, matches.Count);
        }

        [Fact]
        public void CompleteName_NoMatch_ReturnsEmpty()
        {
            AutoTypeInjector.PrimeIndex(new (string, string?)[]
            {
                ("Customer", "Transaction"),
            });
            Assert.Empty(AutoTypeInjector.CompleteName("zzz"));
        }

        [Fact]
        public void CompleteName_EmptyIndex_ReturnsEmpty()
        {
            Assert.Empty(AutoTypeInjector.CompleteName("anything"));
        }

        [Fact]
        public void CompleteName_RespectsCap()
        {
            AutoTypeInjector.PrimeIndex(new (string, string?)[]
            {
                ("CustA", "Transaction"),
                ("CustB", "Transaction"),
                ("CustC", "Transaction"),
            });
            var matches = AutoTypeInjector.CompleteName("Cust", cap: 2).ToList();
            Assert.Equal(2, matches.Count);
        }
    }
}
