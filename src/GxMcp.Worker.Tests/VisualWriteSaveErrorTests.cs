using System;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A visual (WebForm/Layout) write whose object save the SDK rejects must tell the caller what the
    /// SDK said. The rejection used to reach only the worker log; the caller got a verification
    /// mismatch whose diff read "One side empty" because the part could not be read back afterwards.
    /// </summary>
    public class VisualWriteSaveErrorTests
    {
        private const string UnknownAttribute = "Atributo desconhecido 'att:953'";

        private static Exception WrappedSdkRejection(string message = UnknownAttribute)
            => new TargetInvocationException(
                "Exception has been thrown by the target of an invocation.",
                new InvalidOperationException(message));

        [Fact]
        public void Evidence_ReportsTheInnerSdkErrorNotTheReflectionWrapper()
        {
            var evidence = WriteService.BuildVisualSaveErrorEvidence(WrappedSdkRejection(), "obj.Save(ForceSave)");

            Assert.Equal("obj.Save(ForceSave)", evidence["where"]?.ToString());
            Assert.Equal("System.InvalidOperationException", evidence["type"]?.ToString());
            Assert.Equal(UnknownAttribute, evidence["message"]?.ToString());
            var chain = evidence["chain"]?.ToString();
            Assert.Contains("TargetInvocationException", chain);
            Assert.Contains(UnknownAttribute, chain);
        }

        [Fact]
        public void Evidence_WithoutAnInnerException_ReportsTheExceptionItself()
        {
            var evidence = WriteService.BuildVisualSaveErrorEvidence(new InvalidOperationException("boom"), "obj.Save(ForceSave)");

            Assert.Equal("System.InvalidOperationException", evidence["type"]?.ToString());
            Assert.Equal("boom", evidence["message"]?.ToString());
        }

        [Fact]
        public void Evidence_OfNothing_IsNull()
        {
            Assert.Null(WriteService.BuildVisualSaveErrorEvidence(null, "obj.Save(ForceSave)"));
        }

        [Fact]
        public void Sanitize_RemovesLocalPathsAndKeepsTheCause()
        {
            var text = WriteService.SanitizeVisualSaveText(
                "Falha ao gravar C:\\Users\\someone\\KB\\data.bin e \\\\server\\share\\x.dat: Atributo desconhecido 'att:953'");

            Assert.DoesNotContain("someone", text);
            Assert.DoesNotContain("server", text);
            Assert.Contains("<path>", text);
            Assert.Contains("att:953", text);
        }

        [Fact]
        public void Sanitize_IsSingleLineAndBounded()
        {
            var text = WriteService.SanitizeVisualSaveText("linha um\r\n   linha dois\n" + new string('x', 2000));

            Assert.DoesNotContain("\n", text);
            Assert.DoesNotContain("\r", text);
            Assert.StartsWith("linha um linha dois", text);
            Assert.True(text.Length < 600, "the text must be bounded");
            Assert.EndsWith("[truncated]", text);
        }

        [Fact]
        public void Mismatch_WithoutAnSdkError_KeepsTheOriginalWording()
        {
            var text = WriteService.DescribeVisualVerificationMismatch("<GxMultiForm/>", "Attribute differs.", null);

            Assert.Equal(
                "The SDK save path completed, but the persisted WebForm XML does not match the requested content. Diff: Attribute differs.",
                text);
        }

        [Fact]
        public void Mismatch_WithAnEmptyPersistedSide_SaysThePartCouldNotBeReadBack()
        {
            var text = WriteService.DescribeVisualVerificationMismatch(string.Empty, "One side empty.", null);

            Assert.Contains("One side empty.", text);
            Assert.Contains("could not be read back", text);
            Assert.Contains("read the part again", text);
        }

        [Fact]
        public void Mismatch_WithAnSdkError_NamesTheSdkErrorInsteadOfClaimingTheSaveCompleted()
        {
            var evidence = WriteService.BuildVisualSaveErrorEvidence(WrappedSdkRejection(), "obj.Save(ForceSave)");

            var text = WriteService.DescribeVisualVerificationMismatch(string.Empty, "One side empty.", evidence);

            Assert.DoesNotContain("The SDK save path completed", text);
            Assert.Contains("The SDK reported a save error", text);
            Assert.Contains(UnknownAttribute, text);
            Assert.Contains("could not be read back", text);
        }

        [Fact]
        public void Hint_ForAnUnknownAttributeReference_NamesTheReferenceAndWhereToLook()
        {
            var evidence = WriteService.BuildVisualSaveErrorEvidence(WrappedSdkRejection(), "obj.Save(ForceSave)");

            var hint = WriteService.BuildVisualSaveRejectedHint(evidence);

            Assert.Contains("att:953", hint);
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", hint);
            Assert.Contains("TokenDataList", hint);
        }

        [Fact]
        public void Hint_DoesNotClaimTheIdIsAbsent_TheSdkAlsoRefusesAValidIdItLooksUpByName()
        {
            // Proven on a real KB: a raw rewrite of a layout form was refused with
            // "Atributo desconhecido 'att:226'" for an attribute that exists, because the stored att:<id>
            // was replayed as an attribute NAME. A hint saying the id "does not resolve" sent callers
            // chasing a wrong id.
            var evidence = WriteService.BuildVisualSaveErrorEvidence(WrappedSdkRejection("Atributo desconhecido 'att:226'"), "obj.Save(ForceSave)");

            var hint = WriteService.BuildVisualSaveRejectedHint(evidence);

            Assert.DoesNotContain("does not resolve in this KB", hint);
            Assert.Contains("att:226", hint);
            Assert.Contains("as an attribute name", hint);
        }

        [Fact]
        public void Hint_ForAnUnknownAttributeInEnglish_IsRecognisedToo()
        {
            var evidence = WriteService.BuildVisualSaveErrorEvidence(
                WrappedSdkRejection("Unknown attribute 'att:7'"), "obj.Save(ForceSave)");

            Assert.Contains("att:7", WriteService.BuildVisualSaveRejectedHint(evidence));
        }

        [Fact]
        public void Hint_ForAnyOtherSdkError_IsGenericAndDoesNotInventACause()
        {
            var evidence = WriteService.BuildVisualSaveErrorEvidence(
                WrappedSdkRejection("Something else went wrong"), "obj.Save(ForceSave)");

            var hint = WriteService.BuildVisualSaveRejectedHint(evidence);

            Assert.DoesNotContain("att:", hint);
            Assert.DoesNotContain("TokenDataList", hint);
            Assert.Contains("Read the part again", hint);
        }

        [Fact]
        public void Hint_WithoutAMessage_IsNull()
        {
            Assert.Null(WriteService.BuildVisualSaveRejectedHint(new JObject()));
            Assert.Null(WriteService.BuildVisualSaveRejectedHint(null));
        }

        [Fact]
        public void Attach_ObjectSaveRejectionOnly_TakesTheSdkSaveErrorSlot()
        {
            var error = new JObject { ["hint"] = "Existing hint." };
            var force = WriteService.BuildVisualSaveErrorEvidence(WrappedSdkRejection(), "obj.Save(ForceSave)");

            WriteService.AttachVisualSaveError(error, null, force);

            Assert.Equal(UnknownAttribute, error["sdkSaveError"]?["message"]?.ToString());
            Assert.True(error["sdkSaveRejected"]?.Value<bool>());
            Assert.Null(error["forceSaveError"]);
            var hint = error["hint"]?.ToString();
            Assert.StartsWith("Existing hint. ", hint);
            Assert.Contains("att:953", hint);
        }

        [Fact]
        public void Attach_BothFailures_KeepsThePartSaveErrorAndAddsTheObjectSaveError()
        {
            var error = new JObject();
            var part = new JObject { ["type"] = "System.Exception", ["message"] = "part failed", ["where"] = "webFormPart.Save()" };
            var force = WriteService.BuildVisualSaveErrorEvidence(WrappedSdkRejection(), "obj.Save(ForceSave)");

            WriteService.AttachVisualSaveError(error, part, force);

            Assert.Equal("part failed", error["sdkSaveError"]?["message"]?.ToString());
            Assert.Equal(UnknownAttribute, error["forceSaveError"]?["message"]?.ToString());
            Assert.Contains("att:953", error["hint"]?.ToString());
        }

        // The write path needs a live KB, so its wiring is pinned at source level, as the pattern
        // write's convention tests do: the first save's exception is recorded before the fallback
        // save runs, and both failure envelopes carry it.
        [Fact]
        public void VisualWrite_RecordsTheObjectSaveRejection_BeforeTheFallbackSave_ViaConvention()
        {
            string src = System.IO.File.ReadAllText(FindWorkerServiceFile("WriteService.VisualWrite.cs"));

            int record = src.IndexOf("forceSaveError = BuildVisualSaveErrorEvidence(fsEx", StringComparison.Ordinal);
            int fallback = src.IndexOf("obj.EnsureSave(true);", StringComparison.Ordinal);
            Assert.True(record >= 0, "the obj.Save(ForceSave) exception must be recorded");
            Assert.True(fallback > record, "it must be recorded before the fallback save runs");
            Assert.Contains("JObject forceSaveError = null;", src);
        }

        [Fact]
        public void VisualWrite_VerificationFailure_UsesTheSdkErrorAndTheReadBackWording_ViaConvention()
        {
            string src = System.IO.File.ReadAllText(FindWorkerServiceFile("WriteService.VisualWrite.cs"));

            Assert.Contains("DescribeVisualVerificationMismatch(persistedXml, visualDiff, forceSaveError ?? webFormPartSaveError)", src);
            Assert.DoesNotContain("The SDK save path completed, but the persisted WebForm XML does not match the requested content. Diff: \" + (visualDiff", src);
            // Both failure envelopes (strict verification and best-effort) attach it.
            Assert.Equal(2, CountOccurrences(src, "AttachVisualSaveError(errObj, webFormPartSaveError, forceSaveError);"));
            Assert.Contains("okResp[\"forceSaveFallback\"] = forceSaveError", src);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
            return count;
        }

        private static string FindWorkerServiceFile(string fileName)
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "src", "GxMcp.Worker", "Services", fileName);
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new System.IO.FileNotFoundException("Could not locate " + fileName + " starting from " + AppDomain.CurrentDomain.BaseDirectory);
        }

        [Fact]
        public void Attach_WithoutAnyFailure_LeavesTheEnvelopeUntouched()
        {
            var error = new JObject { ["code"] = "WriteFailed" };

            WriteService.AttachVisualSaveError(error, null, null);

            Assert.Equal("{\"code\":\"WriteFailed\"}", error.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
