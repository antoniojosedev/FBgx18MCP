using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WebFormPropertyDeltaTests
    {
        [Fact]
        public void DetectSupportedPropertyDeltas_CapturesCaptionChangeOnExistingControl()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo\" Class=\"TextBlock\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo horas\" Class=\"TextBlock\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.True(result.IsSupported);
            var delta = Assert.Single(result.Deltas);
            Assert.Equal("TextBlockSaldoHoras", delta.ControlName);
            Assert.Equal("Caption", delta.PropertyName);
            Assert.Equal("Saldo horas", delta.Value);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_IgnoresFormattingWhitespace()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo\" Class=\"TextBlock\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm>\r\n  <Form>\r\n    <body>\r\n      <gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo horas\" Class=\"TextBlock\" />\r\n    </body>\r\n  </Form>\r\n</GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.True(result.IsSupported);
            Assert.Single(result.Deltas);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_RejectsStructuralChanges()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /><gxTextBlock ControlName=\"B\" Caption=\"B\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
        }
    }
}
