using System;
using System.Linq;
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class LayoutGotchaScannerTests
    {
        // FR#1 (friction-report 2026-05-19): gxButton custom OnClickEvent in Form type="html"
        // compiles clean but the HTML generator wires data-gx-evt=5 (Enter) regardless. Scanner
        // must surface a warning so the agent doesn't waste a build cycle discovering this.
        [Fact]
        public void Scan_GxButtonCustomEventInHtmlForm_EmitsWarning()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxButton id=""BtnFoo"" caption=""Foo"" OnClickEvent=""'DoFoo'"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null, _ => false);
            Assert.Single(hits);
            Assert.Equal("GotchaGxButtonHtmlFormCustomEvent", hits[0].Code);
            Assert.Equal("Warning", hits[0].Severity);
            Assert.Equal("BtnFoo", hits[0].ControlId);
            Assert.Contains("DoFoo", hits[0].Message);
            Assert.Contains("Enter", hits[0].Message);
            Assert.NotNull(hits[0].Workaround);
        }

        [Fact]
        public void Scan_GxButtonEnterEvent_NoWarning()
        {
            // Enter (and Cancel / Refresh) are the only events the generator wires, so they are
            // legitimate values — don't warn.
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxButton id=""BtnConfirm"" caption=""Confirm"" eventGX=""'Enter'"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null, _ => false);
            Assert.Empty(hits);
        }

        [Fact]
        public void Scan_GxButtonCustomEventInLayoutForm_NoWarning()
        {
            // Form type="layout" supports <action onClickEvent> with custom events. We only flag
            // gxButton inside html forms.
            var xml = @"<GxMultiForm>
                <Form id=""2"" type=""layout"">
                    <gxButton id=""BtnFoo"" caption=""Foo"" OnClickEvent=""'DoFoo'"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null, _ => false);
            Assert.Empty(hits);
        }

        // FR#2 (revised 2026-05-19): gxAttribute ControlType="Radio Button" or "Combo Box" in
        // <Form type="html"> always renders disabled, regardless of variable name or ReadOnly /
        // Enabled attributes. Original hypothesis (variable-name shadow) was disproved by live
        // probe: renaming the bound variable did not change the rendering.
        [Fact]
        public void Scan_GxAttributeRadioInHtmlForm_EmitsWarning()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxAttribute AttID=""var:24"" ControlType=""Radio Button"" ReadOnly=""False"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, attId => attId == "var:24" ? "RespRegProf" : null);
            Assert.Single(hits);
            Assert.Equal("GotchaGxAttributeHtmlFormDiscreteReadOnly", hits[0].Code);
            Assert.Contains("RespRegProf", hits[0].Message);
            Assert.Contains("Radio Button", hits[0].Message);
            Assert.Contains("Form type=\"layout\"", hits[0].Workaround);
        }

        [Fact]
        public void Scan_GxAttributeComboInHtmlForm_EmitsWarning()
        {
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxAttribute AttID=""var:5"" ControlType=""Combo Box"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Single(hits);
            Assert.Equal("GotchaGxAttributeHtmlFormDiscreteReadOnly", hits[0].Code);
            Assert.Contains("Combo Box", hits[0].Message);
        }

        [Fact]
        public void Scan_GxAttributeRadioInLayoutForm_NoWarning()
        {
            // Form type="layout" supports editable Radio / Combo. Only html-form is broken.
            var xml = @"<GxMultiForm>
                <Form id=""2"" type=""layout"">
                    <gxAttribute AttID=""var:24"" ControlType=""Radio Button"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Empty(hits);
        }

        [Fact]
        public void Scan_GxAttributeTextInputInHtmlForm_NoWarning()
        {
            // Default ControlType (text input) is unaffected — renders editable in html form.
            var xml = @"<GxMultiForm>
                <Form id=""1"" type=""html"">
                    <gxAttribute AttID=""var:9"" ReadOnly=""False"" />
                </Form>
            </GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Empty(hits);
        }

        [Fact]
        public void Scan_EmptyOrInvalidXml_ReturnsEmpty()
        {
            Assert.Empty(LayoutGotchaScanner.Scan("", _ => null));
            Assert.Empty(LayoutGotchaScanner.Scan((string)null, _ => null));
            Assert.Empty(LayoutGotchaScanner.Scan("<not closed", _ => null));
        }

        // W5 phase 2 rules — structural / schema gotchas.

        [Fact]
        public void Scan_GxAttributeMissingDataField_EmitsWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxAttribute id=""Phantom"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaGxAttributeMissingDataField" && h.ControlId == "Phantom");
        }

        [Fact]
        public void Scan_UnknownControlType_EmitsWarning()
        {
            // "RadioButton" without the space is a common typo and the SDK silently falls back to Edit.
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxAttribute AttID=""var:8"" ControlType=""RadioButton"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaUnknownControlType");
        }

        [Fact]
        public void Scan_RecognizedControlType_NoWarning()
        {
            // "Combo Box" with the space is the canonical form — no UnknownControlType gotcha.
            // (May still trigger the html-form discrete-readonly gotcha — that's a different code.)
            var xml = @"<GxMultiForm><Form id=""1"" type=""layout"">
                <gxAttribute AttID=""var:8"" ControlType=""Combo Box"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.DoesNotContain(hits, h => h.Code == "GotchaUnknownControlType");
        }

        [Fact]
        public void Scan_WebComponentMissingObjectCall_EmitsWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxWebComponent id=""Header"" />
                <gxEmbeddedPage id=""Footer"" ObjectCall=""Footer.Create()"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Single(hits, h => h.Code == "GotchaWebComponentMissingObjectCall");
            Assert.Equal("Header", hits.First(h => h.Code == "GotchaWebComponentMissingObjectCall").ControlId);
        }

        [Fact]
        public void Scan_CellOutsideTable_EmitsWarning()
        {
            // <cell> directly under <body> without <table>...<row>...<cell> hierarchy
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <body><cell id=""Orphan"">x</cell></body>
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaCellOutsideTable");
        }

        [Fact]
        public void Scan_DuplicateControlName_EmitsWarning()
        {
            var xml = @"<GxMultiForm><Form id=""1"" type=""html"">
                <gxAttribute id=""Btn"" AttID=""var:8"" />
                <gxButton id=""Btn"" eventGX=""'Enter'"" />
            </Form></GxMultiForm>";
            var hits = LayoutGotchaScanner.Scan(xml, _ => null);
            Assert.Contains(hits, h => h.Code == "GotchaDuplicateControlName" && h.ControlId == "Btn");
        }
    }
}
