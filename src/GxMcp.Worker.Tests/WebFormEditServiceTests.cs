using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
    /// Pure-XML mutation tests + service orchestration via a fake backend.
    /// </summary>
    public class WebFormEditServiceTests
    {
        private const string MinimalWebForm =
            "<Form><Body><Table><gxTextBlock ControlName=\"Hello\" Caption=\"Hi\" /></Table></Body></Form>";

        private const string FormWithButton =
            "<Form><Body><Table><gxButton ControlName=\"BtnConfirm\" Caption=\"OK\" /></Table></Body></Form>";

        private sealed class FakeBackend : WebFormEditService.IWebFormBackend
        {
            public string Xml { get; set; } = MinimalWebForm;
            public string LastWritten { get; private set; }
            public bool LastDryRun { get; private set; }
            public string ReadWebFormXml(string target) => Xml;
            public string WriteWebFormXml(string target, string xml, bool dryRun)
            {
                LastWritten = xml;
                LastDryRun = dryRun;
                return "{\"status\":\"Success\"}";
            }
        }

        // --- Pure XML mutation ---

        [Fact]
        public void ApplyAction_AddTextBlock_AppendsElementAndAssignsId()
        {
            var added = new List<string>();
            var args = new JObject { ["caption"] = "Welcome" };
            string after = WebFormEditService.ApplyAction(MinimalWebForm, "add_textblock", args, null, added);
            var doc = XDocument.Parse(after);
            var blocks = doc.Descendants("gxTextBlock").ToList();
            Assert.Equal(2, blocks.Count);
            Assert.Equal("Welcome", (string)blocks.Last().Attribute("Caption"));
            Assert.Single(added);
        }

        [Fact]
        public void ApplyAction_AddButton_SetsOnClickEventDescriptor()
        {
            var added = new List<string>();
            var args = new JObject
            {
                ["caption"] = "Confirm",
                ["event"] = "Enter",
                ["controlId"] = "BtnConfirm2"
            };
            string after = WebFormEditService.ApplyAction(MinimalWebForm, "add_button", args, null, added);
            var btn = XDocument.Parse(after).Descendants("gxButton").Single();
            Assert.Equal("BtnConfirm2", (string)btn.Attribute("ControlName"));
            // Descriptor name is preserved; WebFormTypedPropertyWriter routes to canonical 'Event'.
            Assert.Equal("'Enter'", (string)btn.Attribute("OnClickEvent"));
            Assert.Contains("BtnConfirm2", added);
        }

        [Fact]
        public void ApplyAction_SetVisibility_SetsVisibleAttribute()
        {
            var args = new JObject { ["controlId"] = "BtnConfirm", ["visible"] = false };
            string after = WebFormEditService.ApplyAction(FormWithButton, "set_visibility", args);
            var btn = XDocument.Parse(after).Descendants("gxButton").Single();
            Assert.Equal("False", (string)btn.Attribute("Visible"));
        }

        [Fact]
        public void ApplyAction_RemoveControl_DropsElement()
        {
            var args = new JObject { ["controlId"] = "BtnConfirm" };
            string after = WebFormEditService.ApplyAction(FormWithButton, "remove_control", args);
            Assert.Empty(XDocument.Parse(after).Descendants("gxButton"));
        }

        [Fact]
        public void ApplyAction_WrapInFieldset_MovesControlsIntoFieldset()
        {
            string xml = "<Form><Body><Table>"
                + "<gxTextBlock ControlName=\"A\" />"
                + "<gxTextBlock ControlName=\"B\" />"
                + "<gxTextBlock ControlName=\"C\" />"
                + "</Table></Body></Form>";
            var args = new JObject
            {
                ["controlIds"] = new JArray("A", "B"),
                ["legend"] = "First two"
            };
            string after = WebFormEditService.ApplyAction(xml, "wrap_in_fieldset", args);
            var doc = XDocument.Parse(after);
            var fs = doc.Descendants("gxFieldSet").Single();
            Assert.Equal("First two", (string)fs.Attribute("Caption"));
            var inside = fs.Descendants("gxTextBlock").Select(e => (string)e.Attribute("ControlName")).ToList();
            Assert.Equal(new[] { "A", "B" }, inside);
            // C should remain a sibling at the Table level
            var siblings = doc.Descendants("Table").Single().Elements("gxTextBlock").Select(e => (string)e.Attribute("ControlName")).ToList();
            Assert.Single(siblings);
            Assert.Equal("C", siblings[0]);
        }

        [Fact]
        public void ApplyAction_SetVisibility_UnknownControl_Throws()
        {
            var args = new JObject { ["controlId"] = "Missing", ["visible"] = true };
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                WebFormEditService.ApplyAction(MinimalWebForm, "set_visibility", args));
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public void ApplyAction_UnknownAction_Throws()
        {
            Assert.Throws<System.ArgumentException>(() =>
                WebFormEditService.ApplyAction(MinimalWebForm, "do_magic", new JObject()));
        }

        // --- Service orchestration ---

        [Fact]
        public void Execute_AddTextBlock_RoutesThroughBackend()
        {
            var backend = new FakeBackend();
            var svc = new WebFormEditService(backend);
            var resp = JObject.Parse(svc.Execute("add_textblock", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Hello there"
            }));
            Assert.Equal("Success", resp["status"]?.ToString());
            Assert.NotNull(backend.LastWritten);
            Assert.Contains("Hello there", backend.LastWritten);
            Assert.False(backend.LastDryRun);
        }

        [Fact]
        public void Execute_DryRun_PassesFlagThrough()
        {
            var backend = new FakeBackend();
            var svc = new WebFormEditService(backend);
            svc.Execute("add_textblock", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "X",
                ["dryRun"] = true
            });
            Assert.True(backend.LastDryRun);
        }

        [Fact]
        public void Execute_MissingName_ReturnsStructuredError()
        {
            var backend = new FakeBackend();
            var svc = new WebFormEditService(backend);
            var resp = JObject.Parse(svc.Execute("add_button", new JObject { ["caption"] = "X" }));
            Assert.Equal("Error", resp["status"]?.ToString());
            Assert.Equal("MissingName", resp["code"]?.ToString());
        }

        [Fact]
        public void Execute_ReadReturnsJsonEnvelope_ExtractsContent()
        {
            var backend = new FakeBackend
            {
                Xml = "{\"content\":\"" + MinimalWebForm.Replace("\"", "\\\"") + "\"}"
            };
            var svc = new WebFormEditService(backend);
            var resp = JObject.Parse(svc.Execute("add_textblock", new JObject
            {
                ["name"] = "Y",
                ["caption"] = "Z"
            }));
            Assert.Equal("Success", resp["status"]?.ToString());
            Assert.Contains("Z", backend.LastWritten);
        }
    }
}
