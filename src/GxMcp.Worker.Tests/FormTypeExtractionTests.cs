using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class FormTypeExtractionTests
    {
        [Fact]
        public void ExtractsTypeFromWellFormedFormElement()
        {
            string xml = "<Form id=\"1\" type=\"html\"><table /></Form>";
            Assert.Equal("html", WriteService.TryExtractFormType(xml));
        }

        [Fact]
        public void ExtractsTypeFromLayoutForm()
        {
            string xml = "<Form type=\"layout\" id=\"1\"><detail><layout><table /></layout></detail></Form>";
            Assert.Equal("layout", WriteService.TryExtractFormType(xml));
        }

        [Fact]
        public void ReturnsNullForFragmentWithoutFormElement()
        {
            string xml = "<table><row /></table>";
            Assert.Null(WriteService.TryExtractFormType(xml));
        }

        [Fact]
        public void ReturnsNullForNullOrEmpty()
        {
            Assert.Null(WriteService.TryExtractFormType(null));
            Assert.Null(WriteService.TryExtractFormType(""));
            Assert.Null(WriteService.TryExtractFormType("   "));
        }

        [Fact]
        public void RegexFallbackHandlesMalformedXmlWithFormType()
        {
            string xml = "<Form type='html' unclosed-attr=\"<broken><table />";
            Assert.Equal("html", WriteService.TryExtractFormType(xml));
        }

        [Fact]
        public void DistinguishesHtmlVsLayoutForTransitionDetection()
        {
            string before = "<Form type=\"html\"><table /></Form>";
            string after = "<Form type=\"layout\"><detail><layout><table /></layout></detail></Form>";
            string a = WriteService.TryExtractFormType(before);
            string b = WriteService.TryExtractFormType(after);
            Assert.NotEqual(a, b);
            Assert.Equal("html", a);
            Assert.Equal("layout", b);
        }
    }
}
