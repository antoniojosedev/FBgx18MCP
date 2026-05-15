using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class VariableTypeResolverTests
    {
        [Theory]
        [InlineData("Character", "Character", null, null)]
        [InlineData("VarChar(120)", "Character", 120, null)]
        [InlineData("String(50)", "Character", 50, null)]
        [InlineData("Int", "Numeric", null, null)]
        [InlineData("Numeric(10,2)", "Numeric", 10, 2)]
        [InlineData("Bool", "Boolean", null, null)]
        [InlineData("DateTime", "DateTime", null, null)]
        public void Resolve_KnownSynonyms_ReturnsCanonical(string input, string expType, int? expLen, int? expDec)
        {
            var r = VariableTypeResolver.Resolve(input);
            Assert.True(r.Recognized);
            Assert.Equal(expType, r.CanonicalType);
            Assert.Equal(expLen, r.Length);
            Assert.Equal(expDec, r.Decimals);
        }

        [Fact]
        public void Resolve_Unknown_ReturnsNotRecognizedWithSuggestion()
        {
            var r = VariableTypeResolver.Resolve("Bogus(99)");
            Assert.False(r.Recognized);
            Assert.NotNull(r.Suggestion);
            Assert.NotEmpty(r.AcceptedList);
        }

        [Fact]
        public void Resolve_DomainReference_PassesThrough()
        {
            var r = VariableTypeResolver.Resolve("&PesCod");
            Assert.True(r.Recognized);
            Assert.Equal("DomainReference", r.CanonicalType);
            Assert.Equal("PesCod", r.DomainName);
        }
    }
}
