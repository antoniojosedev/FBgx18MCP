using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Covers the friction-session2 issue C hints (Visible/Class/Enabled on unbound vars,
    // event-not-valid on controls) and the fragility fix #7 that replaced `dynamic`-based
    // SDK message extraction with reflection that survives SDK type drift.
    //
    // CreateIssueFromSdkMessage end-to-end tests would need the Artech.Architecture.Common
    // assembly loaded at runtime; that path is exercised live via genexus_analyze linter.
    // Here we cover the two pure-CLR helpers it composes.
    public class SdkDiagnosticsHelperTests
    {
        // ---- InferSuggestion ----

        [Theory]
        [InlineData("'Visible' propriedade inválida")]
        [InlineData("'Class' propriedade invalida")]
        [InlineData("'Enabled' invalid property")]
        [InlineData("&Foo.Visible is an invalid property")]
        public void InferSuggestion_DisplayPropOnUnboundVar_ReturnsBindingHint(string message)
        {
            string s = SdkDiagnosticsHelper.InferSuggestion(message);
            Assert.Contains("WebForm control", s, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".Visible", s, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("'Click' não é um evento válido")]
        [InlineData("'OnClick' is not a valid event")]
        [InlineData("'Foo' nao e um evento valido")]
        public void InferSuggestion_EventNotValid_PointsToControlsRepertoire(string message)
        {
            string s = SdkDiagnosticsHelper.InferSuggestion(message);
            Assert.Contains("genexus_inspect", s, StringComparison.Ordinal);
            Assert.Contains("controls", s, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Variable &Foo is not defined")]
        [InlineData("Atributo 'Bar' não definida")]
        public void InferSuggestion_NotDefined_KeepsExistingHint(string message)
        {
            string s = SdkDiagnosticsHelper.InferSuggestion(message);
            Assert.Contains("Variables part", s, StringComparison.Ordinal);
        }

        [Fact]
        public void InferSuggestion_UnknownMessage_ReturnsGenericFallback()
        {
            string s = SdkDiagnosticsHelper.InferSuggestion("totally unrelated noise");
            Assert.Contains("Review the syntax", s, StringComparison.Ordinal);
        }

        [Fact]
        public void InferSuggestion_DisplayPropAlonePastInvalidProperty_DoesNotFire()
        {
            // The hint requires BOTH a display-prop mention AND an invalid-property phrase,
            // so plain "'Visible'" without "invalid property" should fall through.
            string s = SdkDiagnosticsHelper.InferSuggestion("'Visible' something else");
            Assert.DoesNotContain("WebForm control", s, StringComparison.OrdinalIgnoreCase);
        }

        // ---- ReadMember / ReadStringMember (reflection over varying SDK message shapes) ----

        private class MsgWithText { public string Text => "boom"; public string Code => "src0216"; }
        private class MsgWithDescription { public string Description = "kaboom"; public string Id = "src9999"; }
        private class MsgWithMessage { public string Message { get; set; } = "ka-pow"; public string ErrorCode { get; set; } = "src1111"; }
        private class MsgEmpty { }

        [Fact]
        public void ReadMember_PrefersProperty_OverField_AndReturnsValue()
        {
            var msg = new MsgWithText();
            object v = SdkDiagnosticsHelper.ReadMember(msg, msg.GetType(), "Text");
            Assert.Equal("boom", v);
        }

        [Fact]
        public void ReadMember_FallsBackToField_WhenNoProperty()
        {
            var msg = new MsgWithDescription();
            object v = SdkDiagnosticsHelper.ReadMember(msg, msg.GetType(), "Description");
            Assert.Equal("kaboom", v);
        }

        [Fact]
        public void ReadMember_ReturnsNull_OnMissingMember()
        {
            var msg = new MsgEmpty();
            Assert.Null(SdkDiagnosticsHelper.ReadMember(msg, msg.GetType(), "DoesNotExist"));
        }

        [Fact]
        public void ReadMember_ReturnsNull_OnNullInstance()
        {
            Assert.Null(SdkDiagnosticsHelper.ReadMember(null, typeof(MsgEmpty), "Text"));
        }

        [Fact]
        public void ReadMember_ReturnsNull_OnNullType()
        {
            Assert.Null(SdkDiagnosticsHelper.ReadMember(new MsgEmpty(), null, "Text"));
        }

        [Fact]
        public void ReadStringMember_TreatsEmptyAsNull()
        {
            var msg = new MsgWithMessage { Message = "" };
            Assert.Null(SdkDiagnosticsHelper.ReadStringMember(msg, msg.GetType(), "Message"));
        }

        [Fact]
        public void ReadStringMember_NormalValue_RoundTrips()
        {
            var msg = new MsgWithMessage();
            Assert.Equal("ka-pow", SdkDiagnosticsHelper.ReadStringMember(msg, msg.GetType(), "Message"));
        }

        [Fact]
        public void ReadMember_DoesNotThrow_OnPropertyGetterException()
        {
            var msg = new ThrowingMsg();
            // Should swallow internal exceptions and return null rather than letting them bubble
            // (the diagnostics path runs from background threads where unhandled throws break things).
            Assert.Null(SdkDiagnosticsHelper.ReadMember(msg, msg.GetType(), "Text"));
        }

        private class ThrowingMsg { public string Text => throw new InvalidOperationException("nope"); }
    }
}
