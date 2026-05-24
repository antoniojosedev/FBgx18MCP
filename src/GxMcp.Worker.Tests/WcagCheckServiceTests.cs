using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WcagCheckServiceTests
    {
        [Fact]
        public void AnalyzeWebForm_EmptyCaptionWithTooltip_Flagged()
        {
            string xml = @"<Form><gxButton Name=""BtnSave"" Caption="""" Tooltip=""Save record""/></Form>";
            var violations = WcagCheckService.AnalyzeWebForm(xml);

            Assert.Contains(violations,
                v => (string)v["rule"]! == "empty-caption-with-tooltip" &&
                     (string)v["control"]! == "BtnSave");
        }

        [Fact]
        public void AnalyzeWebForm_LongCaption_Flagged()
        {
            string longText = new string('A', 95);
            string xml = $@"<Form><gxLabel Name=""Lbl1"" Caption=""{longText}""/></Form>";
            var violations = WcagCheckService.AnalyzeWebForm(xml);

            Assert.Contains(violations,
                v => (string)v["rule"]! == "caption-too-long" &&
                     (string)v["control"]! == "Lbl1");
        }

        [Fact]
        public void AnalyzeWebForm_HtmlInPlainCaption_Flagged()
        {
            string xml = @"<Form><gxLabel Name=""Lbl2"" Caption=""&lt;b&gt;Bold&lt;/b&gt; text""/></Form>";
            // XML attr value will be decoded by XDocument → "<b>Bold</b> text"
            var violations = WcagCheckService.AnalyzeWebForm(xml);

            Assert.Contains(violations,
                v => (string)v["rule"]! == "html-in-plain-caption" &&
                     (string)v["control"]! == "Lbl2");
        }

        [Fact]
        public void AnalyzeWebForm_CleanInput_NoViolations()
        {
            string xml = @"<Form><gxButton Name=""Ok"" Caption=""OK"" Tooltip=""OK""/></Form>";
            var violations = WcagCheckService.AnalyzeWebForm(xml);
            Assert.Empty(violations);
        }
    }
}
