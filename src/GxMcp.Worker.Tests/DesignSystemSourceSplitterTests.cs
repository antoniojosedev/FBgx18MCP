using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #26 (Humberto DSO case): a Design System's combined `tokens {…} styles {…}`
    // source must split into its two blocks so each lands in its own SDK part. These
    // cover the pure splitter; the SDK write/read wiring is exercised end-to-end at
    // runtime (no in-process KBObject mock harness exists in this test project).
    public class DesignSystemSourceSplitterTests
    {
        private const string Combined =
            "tokens IndigoSlateDSTokens {\n" +
            "    #colors {\n" +
            "        primary: #3525cd;\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "styles IndigoSlateDSStyles {\n" +
            "    .button-primary {\n" +
            "        color: $colors.primary;\n" +
            "    }\n" +
            "}";

        [Fact]
        public void TrySplit_Combined_SeparatesTokensAndStyles()
        {
            bool ok = DesignSystemSourceSplitter.TrySplit(Combined, out var tokens, out var styles);

            Assert.True(ok);
            Assert.NotNull(tokens);
            Assert.NotNull(styles);
            // tokens block holds the tokens, not the styles
            Assert.StartsWith("tokens ", tokens);
            Assert.Contains("#colors", tokens);
            Assert.DoesNotContain("styles ", tokens);
            Assert.DoesNotContain(".button-primary", tokens);
            // styles block holds the styles, not the tokens
            Assert.StartsWith("styles ", styles);
            Assert.Contains(".button-primary", styles);
            Assert.DoesNotContain("tokens ", styles);
            Assert.DoesNotContain("#colors", styles);
        }

        [Fact]
        public void TrySplit_OnlyTokens_ReturnsTokensStylesNull()
        {
            bool ok = DesignSystemSourceSplitter.TrySplit(
                "tokens T {\n    #colors { primary: #000; }\n}", out var tokens, out var styles);

            Assert.True(ok);
            Assert.NotNull(tokens);
            Assert.StartsWith("tokens ", tokens);
            Assert.Null(styles);
        }

        [Fact]
        public void TrySplit_OnlyStyles_ReturnsStylesTokensNull()
        {
            bool ok = DesignSystemSourceSplitter.TrySplit(
                "styles S {\n    .a { color: red; }\n}", out var tokens, out var styles);

            Assert.True(ok);
            Assert.Null(tokens);
            Assert.NotNull(styles);
            Assert.StartsWith("styles ", styles);
        }

        [Fact]
        public void TrySplit_NeitherBlock_ReturnsFalse()
        {
            bool ok = DesignSystemSourceSplitter.TrySplit(
                "/* just a comment */\nsome random text", out var tokens, out var styles);

            Assert.False(ok);
            Assert.Null(tokens);
            Assert.Null(styles);
        }

        [Fact]
        public void TrySplit_DoesNotMatchIdentifierPrefixes()
        {
            // "tokensList" / "stylesheet" are identifiers, not top-level block keywords.
            bool ok = DesignSystemSourceSplitter.TrySplit(
                "tokensList = 1\nstylesheet = 2", out var tokens, out var styles);

            Assert.False(ok);
            Assert.Null(tokens);
            Assert.Null(styles);
        }
    }
}
