using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Services;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // WorkWithPlus settings.template contract: an explicit template is honoured or
    // refused, never swapped for another one. The standard templates (Selection,
    // Transaction, View, Prompt, Association, ...) sit in a folder with the same name,
    // so an untyped name lookup resolved the folder, judged the template missing and
    // silently attached an unrelated template that ValidateAndSave then rejected.
    [Collection("InProcessSdkReflection")]
    public class PatternApplyExplicitTemplateTests
    {
        private static readonly Guid WWP = PatternApplyService.WorkWithPlusPatternId;

        // Registration order of a real WorkWithPlus 16.2 KB puts an unrelated card
        // template ahead of Empty and of the standard pattern templates.
        private static readonly string[] Registered =
        {
            "ProgressBarPercentageLight", "PopoverEmpty", "Selection", "Transaction", "View", "Prompt", "Empty"
        };

        private sealed class CountingEngine : IPatternEngineAdapter
        {
            public int ApplyCalls;
            public int ReapplyCalls;
            public int InstanceProbes;
            public object GetPatternDefinition(Guid patternId) => new object();
            public object GetPatternInstance(KBObject parent, Guid patternId) { InstanceProbes++; return null; }
            public PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings)
            {
                ApplyCalls++;
                return new PatternApplyResult { GeneratedObjects = new List<string> { "Generated" } };
            }
            public PatternApplyResult ReapplyPattern(object patternInstance, JObject settings)
            {
                ReapplyCalls++;
                return new PatternApplyResult();
            }
        }

        private static PatternApplyService ServiceWith(IReadOnlyList<string> templates, IPatternEngineAdapter engine = null)
        {
            return new PatternApplyService(null, engine ?? new CountingEngine(), name => null)
            {
                WwpTemplateLister = () => templates
            };
        }

        [Theory]
        [InlineData("Selection", "Selection")]
        [InlineData("selection", "Selection")]
        [InlineData("  Transaction ", "Transaction")]
        [InlineData("VIEW", "View")]
        [InlineData("Empty", "Empty")]
        public void ExplicitRegisteredTemplate_IsUsedWithItsRegisteredSpelling(string requested, string expected)
        {
            Assert.Equal(expected, PatternApplyService.SelectWwpWebTemplate(Registered, requested, out string source));
            Assert.Equal("caller", source);
        }

        [Theory]
        [InlineData("Selection")]
        [InlineData("TransactionTabs")]
        [InlineData("Folder")]
        public void ExplicitUnregisteredTemplate_IsNeverReplacedByAnotherTemplate(string requested)
        {
            var onlyOthers = new[] { "ProgressBarPercentageLight", "PopoverEmpty", "Empty" };
            Assert.Null(PatternApplyService.SelectWwpWebTemplate(onlyOthers, requested, out string source));
            Assert.Equal("notRegistered", source);
        }

        [Fact]
        public void WithoutExplicitTemplate_DocumentedEmptyDefaultWinsOverRegistrationOrder()
        {
            Assert.Equal("Empty", PatternApplyService.SelectWwpWebTemplate(Registered, null, out string source));
            Assert.Equal("defaultEmpty", source);
            Assert.Equal("Empty", PatternApplyService.SelectWwpWebTemplate(Registered, "   ", out source));
            Assert.Equal("defaultEmpty", source);
        }

        [Fact]
        public void WithoutEmpty_FirstNonPopoverTemplateIsUsedInRegistrationOrder()
        {
            Assert.Equal("ProgressBarPercentageLight",
                PatternApplyService.SelectWwpWebTemplate(new[] { "PopoverEmpty", "ProgressBarPercentageLight", "AddGridItems" }, null, out string source));
            Assert.Equal("firstRegistered", source);
            Assert.Equal("PopoverEmpty",
                PatternApplyService.SelectWwpWebTemplate(new[] { "PopoverEmpty", "PopoverDetail" }, null, out source));
            Assert.Equal("firstRegistered", source);
        }

        [Fact]
        public void WithNoRegisteredTemplate_TheDocumentedDefaultIsReturnedAsUnlisted()
        {
            Assert.Equal("Empty", PatternApplyService.SelectWwpWebTemplate(new string[0], null, out string source));
            Assert.Equal("unlisted", source);
            Assert.Equal("Empty", PatternApplyService.SelectWwpWebTemplate(null, null, out source));
            Assert.Equal("unlisted", source);
        }

        [Fact]
        public void ServiceResolution_HonoursOrRefusesTheRequestedTemplate()
        {
            var svc = ServiceWith(Registered);
            Assert.Equal("Selection", svc.ResolveAvailableWwpTemplate("selection", out string source));
            Assert.Equal("caller", source);
            Assert.Null(svc.ResolveAvailableWwpTemplate("Missing", out source));
            Assert.Equal("notRegistered", source);
            Assert.Equal("Empty", svc.ResolveAvailableWwpTemplate(null, out source));
            Assert.Equal("defaultEmpty", source);
        }

        [Theory]
        [InlineData("Transaction", "View")]
        [InlineData("Procedure", "Empty")]
        [InlineData(null, "Selection")]
        [InlineData("", "Selection")]
        [InlineData("WebPanel", "")]
        [InlineData("WebPanel", "   ")]
        [InlineData("WebComponent", null)]
        public void ExplicitTemplate_UnsupportedCombinations_HaveAReason(string parentType, string requested)
        {
            Assert.NotNull(PatternApplyService.ExplicitTemplateUnsupportedReason(parentType, requested));
        }

        [Theory]
        [InlineData("WebPanel", "Selection")]
        [InlineData("webpanel", "Empty")]
        [InlineData("WebComponent", "View")]
        [InlineData("SDPanel", "Prompt")]
        public void ExplicitTemplate_DirectAttachParents_AreSupported(string parentType, string requested)
        {
            Assert.Null(PatternApplyService.ExplicitTemplateUnsupportedReason(parentType, requested));
        }

        [Theory]
        [InlineData("View")]
        [InlineData("")]
        [InlineData("Selection")]
        public void ExplicitTemplate_WithoutDirectAttachParent_IsRefusedBeforeTheEngine(string template)
        {
            var engine = new CountingEngine();
            var response = JObject.Parse(ServiceWith(Registered, engine).ApplyPatternToObject(null, WWP, "WorkWithPlus",
                new JObject { ["template"] = template }, false, "SomeTransaction"));
            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("PatternTemplateUnsupported", response["error"]?["code"]?.ToString());
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);
            Assert.Equal(0, engine.InstanceProbes);
        }

        [Fact]
        public void ExplicitTemplate_NonStringValue_IsRefusedBeforeTheEngine()
        {
            var engine = new CountingEngine();
            var response = JObject.Parse(ServiceWith(Registered, engine).ApplyPatternToObject(null, WWP, "WorkWithPlus",
                new JObject { ["template"] = 42 }, false, "SomeWebPanel"));
            Assert.Equal("PatternTemplateUnsupported", response["error"]?["code"]?.ToString());
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(0, engine.InstanceProbes);
        }

        [Fact]
        public void NullTemplateSetting_KeepsTheExistingApplyRoute()
        {
            var engine = new CountingEngine();
            ServiceWith(Registered, engine).ApplyPatternToObject(null, WWP, "WorkWithPlus",
                new JObject { ["template"] = JValue.CreateNull() }, false, "SomeTransaction");
            Assert.Equal(1, engine.ApplyCalls);
        }

        [Theory]
        [InlineData("<instance><WPRoot Template='View'/></instance>", "View", true)]
        [InlineData("<instance><WPRoot Template='view'/></instance>", "View", true)]
        [InlineData("<?xml version='1.0' encoding='utf-8'?><instance><WPRoot Template='Selection'/></instance>", "Selection", true)]
        [InlineData("<instance><WPRoot Template='ProgressBarPercentageLight'/></instance>", "Selection", false)]
        [InlineData("<instance><WPRoot Template='Empty'/></instance>", "View", false)]
        [InlineData("<instance><WPRoot/></instance>", "View", false)]
        [InlineData("<instance><WPRoot Template='View'/><WPRoot Template='Empty'/></instance>", "View", false)]
        [InlineData("<instance/>", "View", false)]
        [InlineData("<broken", "View", false)]
        [InlineData(null, "View", false)]
        [InlineData("<instance><WPRoot Template='View'/></instance>", "", false)]
        public void PersistedTemplate_IsConfirmedOnlyFromASingleRootWithThatTemplate(string xml, string requested, bool expected)
        {
            Assert.Equal(expected, PatternApplyService.ExplicitTemplateMatches(xml, requested));
        }

        [Theory]
        [InlineData("<instance><WPRoot Template='Selection'/></instance>", "Selection")]
        [InlineData("<instance><WPRoot/></instance>", null)]
        [InlineData("<instance><WPRoot Template='A'/><WPRoot Template='B'/></instance>", null)]
        [InlineData("<broken", null)]
        [InlineData("", null)]
        public void PersistedTemplate_IsReadFromTheSingleRoot(string xml, string expected)
        {
            Assert.Equal(expected, PatternApplyService.ReadPersistedWwpTemplate(xml));
        }

        // ── The package's own validation reason reaches the caller ─────────────────
        // A KB whose WorkWithPlus Design System was never imported rejects every first
        // web attach; the reason only reached the worker log and the response advised
        // trying another template.
        private const string DesignSystemError =
            "Before applying WorkWithPlus for Web Pattern for the first time, a Design System must be imported (WorkWithPlus -> for Web -> Design System Wizard)";

        private static PatternApplyService.WwpAttachResult ValidateFailure(params string[] packageErrors) =>
            new PatternApplyService.WwpAttachResult
            {
                FailureStage = "validateAndSave",
                ErrorCode = "PatternAttachValidateFailed",
                ErrorMessage = "ValidateAndSave returned false for host 'WorkWithPlusMyPanel' (template='Empty').",
                PackageErrors = packageErrors.Length == 0 ? null : new List<string>(packageErrors)
            };

        [Fact]
        public void ValidateFailure_DesignSystemNotImported_PointsAtTheWizardNotAtTemplates()
        {
            string recommendation = PatternApplyService.BuildAttachRecommendation(
                ValidateFailure("Error validating WorkWithPlusMyPanel:", DesignSystemError), "WebPanel", "MyPanel");
            Assert.Contains("Design System Wizard", recommendation);
            Assert.Contains("a different template will not help", recommendation);
            Assert.Contains(DesignSystemError, recommendation);
            Assert.DoesNotContain("Try a different 'WorkWithPlus for Web Template'", recommendation);
        }

        [Fact]
        public void ValidateFailure_OtherPackageError_IsReportedInsteadOfTemplateAdvice()
        {
            string recommendation = PatternApplyService.BuildAttachRecommendation(
                ValidateFailure("Property 'Grid' is invalid."), "WebPanel", "MyPanel");
            Assert.Contains("reported: Property 'Grid' is invalid.", recommendation);
            Assert.DoesNotContain("Design System Wizard", recommendation);
            Assert.DoesNotContain("Try a different 'WorkWithPlus for Web Template'", recommendation);
        }

        [Fact]
        public void ValidateFailure_WithoutPackageErrors_KeepsTheTemplateAdvice()
        {
            Assert.Contains("Try a different 'WorkWithPlus for Web Template'",
                PatternApplyService.BuildAttachRecommendation(ValidateFailure(), "WebPanel", "MyPanel"));
        }

        [Fact]
        public void ValidateFailure_NoHostLeft_DoesNotClaimAnUnremovedOrphan()
        {
            var attach = ValidateFailure();
            attach.OrphanHostAbsent = true;
            string recommendation = PatternApplyService.BuildAttachRecommendation(attach, "WebPanel", "MyPanel");
            Assert.Contains("No 'WorkWithPlusMyPanel' host remains in the KB", recommendation);
            Assert.DoesNotContain("could not be removed automatically", recommendation);
        }

        [Theory]
        [InlineData(DesignSystemError, true)]
        [InlineData("a design system must be imported", true)]
        [InlineData("Property 'Grid' is invalid.", false)]
        [InlineData(null, false)]
        public void DesignSystemRequirement_IsRecognisedFromThePackageMessage(string error, bool expected)
        {
            Assert.Equal(expected, PatternApplyService.PackageErrorsRequireDesignSystem(new[] { error }));
        }

        [Fact]
        public void NoOpReason_CarriesThePackageErrorsOnlyWhenTheyExist()
        {
            string withErrors = PatternApplyService.BuildAttachNoOpReason(ValidateFailure(DesignSystemError), "WebPanel");
            Assert.StartsWith("Engine ApplyPattern void overload no-op'd on a WebPanel target, and the WorkWithPlus direct-attach route failed at stage 'validateAndSave': ", withErrors);
            Assert.EndsWith("The WorkWithPlus package reported: " + DesignSystemError, withErrors);
            string withoutErrors = PatternApplyService.BuildAttachNoOpReason(ValidateFailure(), "WebPanel");
            Assert.DoesNotContain("package reported", withoutErrors);
        }

        [Fact]
        public void AttachResultJson_EmitsPackageErrorsOnlyWhenPresent()
        {
            Assert.Equal(DesignSystemError, ValidateFailure(DesignSystemError).ToJson()["packageErrors"]?[0]?.ToString());
            Assert.Null(ValidateFailure().ToJson()["packageErrors"]);
        }

        [Fact]
        public void TaskLogger_KeepsErrorEventsButNotWarnings()
        {
            var engine = new PatternApplyService.NullBuildEngine();
            engine.LogErrorEvent(new Microsoft.Build.Framework.BuildErrorEventArgs("", "", "", 0, 0, 0, 0, "  " + DesignSystemError + "  ", "", "WWP"));
            engine.LogErrorEvent(new Microsoft.Build.Framework.BuildErrorEventArgs("", "", "", 0, 0, 0, 0, "   ", "", "WWP"));
            engine.LogWarningEvent(new Microsoft.Build.Framework.BuildWarningEventArgs("", "", "", 0, 0, 0, 0, "Object not found", "", "WWP"));
            Assert.Equal(new[] { DesignSystemError }, engine.Errors);
        }

        private static string ServiceSource() =>
            RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "PatternApplyService.cs");

        [Fact]
        public void LastResortPackageErrors_ReachTheAttachResultAndTheCleanupRecordsAnAbsentHost()
        {
            string source = ServiceSource();
            Assert.Contains("if (packageErrors != null && packageErrors.Count > 0) result.PackageErrors = packageErrors;", source);
            string cleanup = Between(source, "private void RemoveOrphanHost(KBObject parent, WwpAttachResult result)", "internal static string BuildAttachRecommendation(");
            Assert.Contains("result.OrphanHostAbsent = true;", cleanup);
            Assert.Contains("patternResult[\"noOpReason\"] = BuildAttachNoOpReason(attach, parentTypeName);", source);
        }

        private static string Between(string source, string start, string end)
        {
            int from = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, "missing: " + start);
            int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to > from, "missing: " + end);
            return source.Substring(from, to - from);
        }

        [Fact]
        public void TemplateResolution_NeverUsesAnUntypedNameLookup()
        {
            string body = Between(ServiceSource(),
                "internal string ResolveAvailableWwpTemplate(string preferred, out string source)",
                "internal static string ExplicitTemplateUnsupportedReason(");
            Assert.DoesNotContain("FindObject(", body);
            Assert.Contains("SelectWwpWebTemplate(registered, preferred, out source)", body);
        }

        [Fact]
        public void ApplyRoute_ValidatesTheTemplateBeforeTouchingTheKb()
        {
            string body = Between(ServiceSource(),
                "internal string ApplyPatternToObject(KBObject obj, Guid patternId,",
                "PatternApplyResult result;");
            int validate = body.IndexOf("TryResolveExplicitWwpTemplate(obj, settings, objectNameForResponse, out string explicitTemplate)", StringComparison.Ordinal);
            int probe = body.IndexOf("_engine.GetPatternInstance(obj, patternId)", StringComparison.Ordinal);
            int change = body.IndexOf("PatternTemplateChangeUnsupported", StringComparison.Ordinal);
            Assert.True(validate >= 0 && probe > validate, "template must be validated before the instance probe");
            Assert.True(change > probe, "the template-change refusal must precede the engine apply");
        }

        [Fact]
        public void DirectAttach_ReceivesTheValidatedTemplateAndConfirmsItFromAFreshRead()
        {
            string source = ServiceSource();
            Assert.Contains("TryPackageInterfaceAttach(obj, explicitTemplate)", source);
            Assert.DoesNotContain("TryPackageInterfaceAttach(obj, sett_template)", source);
            string confirmation = Between(source, "if (explicitTemplate != null)\n                        {", "PatternTemplateNotPersisted");
            Assert.Contains("ReadPatternPartXmlFresh(obj, \"PatternInstance\", WorkWithPlusPatternId", confirmation);
            Assert.Contains("ExplicitTemplateMatches(persistedXml, explicitTemplate)", confirmation);
        }
    }
}
