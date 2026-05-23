using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class HtmlFormatGotchaTests
    {
        [Theory]
        [InlineData("<script>alert(1)</script>")]
        [InlineData("<SCRIPT src='x.js'></SCRIPT>")]
        [InlineData("<iframe src='evil'></iframe>")]
        [InlineData("<img src=x onerror=\"alert(1)\">")]
        [InlineData("<img src=\"foo\" onerror=alert(1)>")]
        public void EscapedHtmlPatterns_Detected(string content)
        {
            Assert.True(LayoutGotchaScanner.ContainsEscapedHtmlPatterns(content));
        }

        [Theory]
        [InlineData("<p>Hello world</p>")]
        [InlineData("<a href='/foo'>link</a>")]
        [InlineData("<img src='ok.png' alt='ok'>")]      // <img without onerror is fine
        [InlineData("&lt;script&gt;already escaped&lt;/script&gt;")]
        [InlineData("")]
        public void PlainHtml_NotFlagged(string content)
        {
            Assert.False(LayoutGotchaScanner.ContainsEscapedHtmlPatterns(content));
        }
    }
}
