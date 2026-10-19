using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

// The fake typed WorkWith model (issue #586) lives in WorkWithNativeSurfaceFakes.cs.

namespace GxMcp.Worker.Tests
{
    using Artech.Patterns.WorkWith;

    public class WorkWithRemoveFilterAttributeTests
    {
        private const string CustomerCondition = "CustomerName like &CustomerName when not &CustomerName.IsEmpty();";
        private const string OrderDateCondition = "OrderDate >= &OrderDate when not &OrderDate.IsEmpty();";
        private const string StatusCondition = "CustomerStatus = 'Active';";

        // ── fixtures ─────────────────────────────────────────────────────────────

        private static JObject Filters(params object[] attributes)
        {
            return Filters(new[] { CustomerCondition, OrderDateCondition, StatusCondition }, attributes);
        }

        private static JObject Filters(string[] conditions, params object[] attributes)
        {
            var attrs = new JArray();
            foreach (var a in attributes)
            {
                if (a is JObject o) attrs.Add(o);
                else attrs.Add(Attr(a.ToString()));
            }
            return new JObject
            {
                ["level"] = "Root",
                ["attributes"] = attrs,
                ["conditions"] = new JArray(conditions)
            };
        }

        private static JObject Attr(string name, string description = null, string domain = "", string @default = null)
        {
            var o = new JObject
            {
                ["name"] = name,
                ["description"] = description ?? name,
                ["domain"] = domain,
                ["variableName"] = "&" + name
            };
            if (!string.IsNullOrEmpty(@default)) o["default"] = @default;
            return o;
        }

        private static WorkWithInstance NewModel(params string[] conditions)
        {
            var filter = new FilterElement();
            var attrs = (FilterAttributesElement)filter.Attributes;
            attrs.Add(new FilterAttributeElement { Name = "CustomerName", Description = "Customer Name", DomainName = "" });
            attrs.Add(new FilterAttributeElement { Name = "OrderDate", Description = "Order Date", DomainName = "DateDomain" });
            attrs.Add(new FilterAttributeElement { Name = "CustomerStatus", Description = "Status", DomainName = "StatusDomain" });
            var conds = (ConditionsElement)filter.Conditions;
            foreach (var c in conditions.Length > 0 ? conditions : new[] { CustomerCondition, OrderDateCondition, StatusCondition })
                conds.Add(new ConditionElement { Value = c });

            var model = new WorkWithInstance();
            model.LevelList.Add(new LevelElement { Id = "Root", Selection = new SelectionElement { Filter = filter } });
            WorkWithInstance.Current = model;
            WorkWithInstance.LoadHook = null;
            WorkWithInstance.LoadCalls = 0;
            return model;
        }

        private static Assembly FakeSdk => typeof(WorkWithInstance).Assembly;

        private static WorkWithSdkAdapter.FilterReader ReaderFor(WorkWithInstance model)
        {
            return (string level, out JObject filters, out string error, out string message) =>
                WorkWithSdkAdapter.ReadNativeFilters(model, level, out filters, out error, out message);
        }

        private static IEnumerable<string> ModelAttributeNames(WorkWithInstance model)
        {
            return model.LevelList[0].Selection.Filter.Attributes.Cast<FilterAttributeElement>().Select(a => a.Name);
        }

        private static IEnumerable<string> ModelConditions(WorkWithInstance model)
        {
            return model.LevelList[0].Selection.Filter.Conditions.Cast<ConditionElement>().Select(c => c.Value);
        }

        // ── reference matcher (pure) ──────────────────────────────────────────────

        [Theory]
        [InlineData("&Nome", "Nome")]
        [InlineData("&nome.IsEmpty()", "Nome")]
        [InlineData("CustomerName like &CustomerName", "CustomerName")]
        [InlineData("CustomerName like &CustomerName", "&CustomerName")]
        [InlineData("not &Nome.IsEmpty() and &Outro = 1", "NOME")]
        [InlineData("x = 1 or (&Nome)", "Nome")]
        public void Matcher_FindsWholeVariableToken_CaseInsensitive(string condition, string attribute)
        {
            var analysis = FilterConditionReferenceMatcher.Analyze(condition, attribute);

            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.Referenced, analysis.Verdict);
            Assert.True(analysis.BlocksRemoval);
            Assert.Contains("&", analysis.Reason);
        }

        [Theory]
        [InlineData("&Nome2 = 1", "Nome")]
        [InlineData("&NomeCompleto like 'a'", "Nome")]
        [InlineData("&Nom = 1", "Nome")]
        [InlineData("&_Nome = 1", "Nome")]
        public void Matcher_DoesNotMatchLongerOrShorterIdentifiers(string condition, string attribute)
        {
            var analysis = FilterConditionReferenceMatcher.Analyze(condition, attribute);

            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.NotReferenced, analysis.Verdict);
            Assert.False(analysis.BlocksRemoval);
            Assert.Empty(analysis.IncidentalMentions);
        }

        [Fact]
        public void Matcher_BareNameIsAnIncidentalMention_NotAReference()
        {
            var analysis = FilterConditionReferenceMatcher.Analyze("CustomerName like 'A%' and CustomerName <> ''", "CustomerName");

            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.NotReferenced, analysis.Verdict);
            Assert.Equal(2, analysis.IncidentalMentions.Count);
            Assert.All(analysis.IncidentalMentions, m => Assert.Contains("bare identifier", m));
        }

        [Theory]
        [InlineData("CustomerStatus = '&CustomerName'", "string literal")]
        [InlineData("CustomerStatus = \"&CustomerName\"", "string literal")]
        [InlineData("CustomerStatus = 'x' /* uses &CustomerName */", "comment")]
        [InlineData("CustomerStatus = 'x' // &CustomerName", "comment")]
        public void Matcher_LiteralAndCommentTextAreIncidental(string condition, string expectedMention)
        {
            var analysis = FilterConditionReferenceMatcher.Analyze(condition, "CustomerName");

            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.NotReferenced, analysis.Verdict);
            Assert.Single(analysis.IncidentalMentions);
            Assert.Contains(expectedMention, analysis.IncidentalMentions[0]);
        }

        [Fact]
        public void Matcher_QualifiedMemberAccessIsNotAReference_ButVariableWithMemberIs()
        {
            var member = FilterConditionReferenceMatcher.Analyze("&Customer.Name = 'x'", "Name");
            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.NotReferenced, member.Verdict);
            Assert.Single(member.IncidentalMentions);
            Assert.Contains("member access", member.IncidentalMentions[0]);

            var variable = FilterConditionReferenceMatcher.Analyze("&Name.Trim() = 'x'", "Name");
            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.Referenced, variable.Verdict);

            var qualifiedBare = FilterConditionReferenceMatcher.Analyze("Customer.Name = &Other", "Name");
            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.NotReferenced, qualifiedBare.Verdict);
        }

        [Theory]
        [InlineData("CustomerStatus = 'unterminated")]
        [InlineData("CustomerStatus = 1 /* open comment")]
        [InlineData("CustomerStatus = & 1")]
        [InlineData("&amp;CustomerName = 1")]
        public void Matcher_FailsClosedOnUnanalyzableText(string condition)
        {
            var analysis = FilterConditionReferenceMatcher.Analyze(condition, "CustomerName");

            Assert.Equal(FilterConditionReferenceMatcher.ReferenceVerdict.Unanalyzable, analysis.Verdict);
            Assert.True(analysis.BlocksRemoval);
            Assert.False(string.IsNullOrWhiteSpace(analysis.Reason));
        }

        [Theory]
        [InlineData("&CustomerName", "CustomerName")]
        [InlineData("  &CustomerName ", "CustomerName")]
        [InlineData("CustomerName", "CustomerName")]
        [InlineData("&&CustomerName", "&CustomerName")]
        [InlineData(null, "")]
        public void NormalizeAttributeName_StripsOneLeadingAmpersand(string given, string expected)
        {
            Assert.Equal(expected, FilterConditionReferenceMatcher.NormalizeAttributeName(given));
        }

        // ── planner (pure) ───────────────────────────────────────────────────────

        [Fact]
        public void Plan_HappyPath_ListsReferencingConditionsAndPreservedState()
        {
            var filters = Filters("CustomerName", Attr("OrderDate", "Order Date", "DateDomain", "&Today"), "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("WorkWithWebCustomer", filters, "CustomerName", new List<string> { CustomerCondition }, out var plan, out var code, out var message);

            Assert.True(ok, message);
            Assert.Null(code);
            Assert.Equal("Root", plan["level"]?.ToString());
            Assert.Equal("native-sdk", plan["route"]?.ToString());
            Assert.Equal("CustomerName", plan["filterAttribute"]?["name"]?.ToString());

            var remove = plan["removeConditions"] as JArray;
            Assert.Single(remove);
            Assert.Equal(1, remove[0]["occurrences"]?.ToObject<int>());
            Assert.True(remove[0]["referencesAttribute"]?.ToObject<bool>());

            var referencing = plan["referencingConditions"] as JArray;
            Assert.Single(referencing);
            Assert.Equal("referenced", referencing[0]["verdict"]?.ToString());
            Assert.True(referencing[0]["listedForRemoval"]?.ToObject<bool>());

            Assert.Equal(new[] { "OrderDate", "CustomerStatus" }, (plan["preservedAttributes"] as JArray).Select(a => a["name"]?.ToString()).ToArray());
            Assert.Equal(new[] { OrderDateCondition, StatusCondition }, (plan["preservedConditions"] as JArray).Select(c => c.ToString()).ToArray());
            Assert.Empty(plan["neighborDefaultReferences"] as JArray);

            var validations = plan["validations"] as JObject;
            Assert.True(validations["attributeFound"]?.ToObject<bool>());
            Assert.True(validations["noUnlistedConditionReferencesAttribute"]?.ToObject<bool>());
            Assert.True(validations["noNeighborDefaultReferencesAttribute"]?.ToObject<bool>());

            Assert.NotEmpty(plan["references"]?["checked"] as JArray);
            Assert.Contains((plan["references"]?["notVerified"] as JArray).Select(x => x.ToString()), s => s.Contains("WebForm"));
            Assert.Contains("FilterConditionDuplicate", plan["policy"]?["duplicateRemoveConditions"]?.ToString());
            Assert.Contains("every exact", plan["policy"]?["repeatedOccurrencesOnFilter"]?.ToString());
            Assert.Equal("not_guaranteed", plan["derivedObjectRegeneration"]?.ToString());
            Assert.Contains("fail-closed", plan["validationLimits"]?["conditionReferences"]?.ToString());
        }

        [Fact]
        public void Plan_AcceptsAttributeWithLeadingAmpersand_AndNormalizesBeforeComparing()
        {
            var filters = Filters("CustomerName", "OrderDate", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "&customername", new List<string> { CustomerCondition }, out var plan, out _, out var message);

            Assert.True(ok, message);
            Assert.Equal("CustomerName", plan["filterAttribute"]?["name"]?.ToString());
        }

        [Fact]
        public void Plan_UnlistedReferencingCondition_RefusesWithTheList()
        {
            var filters = Filters("CustomerName", "OrderDate", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", new List<string>(), out var plan, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("FilterAttributeReferencedByConditions", code);
            Assert.Contains(CustomerCondition, message);
            Assert.DoesNotContain(OrderDateCondition, message);
            Assert.NotNull(plan);
            Assert.False(plan["validations"]?["noUnlistedConditionReferencesAttribute"]?.ToObject<bool>());
            var referencing = plan["referencingConditions"] as JArray;
            Assert.Single(referencing);
            Assert.False(referencing[0]["listedForRemoval"]?.ToObject<bool>());
            Assert.Empty(plan["removeConditions"] as JArray);
        }

        [Fact]
        public void Plan_WithoutRemoveConditions_RemovesNoCondition_WhenNoneReferences()
        {
            var filters = Filters(new[] { OrderDateCondition, StatusCondition }, "CustomerName", "OrderDate", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", null, out var plan, out _, out var message);

            Assert.True(ok, message);
            Assert.Empty(plan["removeConditions"] as JArray);
            Assert.Equal(new[] { OrderDateCondition, StatusCondition }, (plan["preservedConditions"] as JArray).Select(c => c.ToString()).ToArray());
        }

        [Fact]
        public void Plan_DuplicateRemoveConditions_IsRefusedWithFilterConditionDuplicate()
        {
            var filters = Filters("CustomerName", "OrderDate", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", new List<string> { CustomerCondition, CustomerCondition }, out _, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("FilterConditionDuplicate", code);
            Assert.Contains("removeConditions[1]", message);
        }

        [Fact]
        public void Plan_EmptyRemoveCondition_IsRefused()
        {
            var filters = Filters("CustomerName", "OrderDate", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", new List<string> { CustomerCondition, " " }, out _, out var code, out _);

            Assert.False(ok);
            Assert.Equal("FilterConditionEmpty", code);
        }

        [Fact]
        public void Plan_ListedConditionAbsentFromFilter_IsRefusedExactOrdinal()
        {
            var filters = Filters("CustomerName", "OrderDate", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", new List<string> { CustomerCondition.ToLowerInvariant() }, out _, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("FilterConditionNotFound", code);
            Assert.Contains("removeConditions[0]", message);
        }

        [Fact]
        public void Plan_RepeatedOccurrencesOnFilter_AreAllCounted()
        {
            var filters = Filters(new[] { CustomerCondition, OrderDateCondition, CustomerCondition }, "CustomerName", "OrderDate");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", new List<string> { CustomerCondition }, out var plan, out _, out var message);

            Assert.True(ok, message);
            Assert.Equal(2, (plan["removeConditions"] as JArray)[0]["occurrences"]?.ToObject<int>());
            Assert.Equal(new[] { OrderDateCondition }, (plan["preservedConditions"] as JArray).Select(c => c.ToString()).ToArray());
        }

        [Fact]
        public void Plan_FilterAttributeNotFound_ListsAvailableNames()
        {
            var filters = Filters("CustomerName", "OrderDate");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "Missing", null, out var plan, out var code, out var message);

            Assert.False(ok);
            Assert.Null(plan);
            Assert.Equal("FilterAttributeNotFound", code);
            Assert.Contains("CustomerName, OrderDate", message);
        }

        [Theory]
        [InlineData("Customer Name")]
        [InlineData("&&CustomerName")]
        [InlineData("1Customer")]
        [InlineData("")]
        public void Plan_InvalidAttributeName_IsRefused(string name)
        {
            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", Filters("CustomerName"), name, null, out _, out var code, out _);

            Assert.False(ok);
            Assert.Equal("InvalidFilterAttributeName", code);
        }

        [Fact]
        public void Plan_NeighborDefaultReferencingTheVariable_IsRefused()
        {
            var filters = Filters(new[] { StatusCondition }, "CustomerName", Attr("OrderDate", "Order Date", "", "&CustomerName"), "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", null, out var plan, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("FilterAttributeReferencedByAttributeDefault", code);
            Assert.Contains("OrderDate", message);
            Assert.Single(plan["neighborDefaultReferences"] as JArray);
            Assert.False(plan["validations"]?["noNeighborDefaultReferencesAttribute"]?.ToObject<bool>());
        }

        [Fact]
        public void Plan_UnanalyzableUnlistedCondition_FailsClosed()
        {
            var filters = Filters(new[] { "CustomerStatus = 'open", StatusCondition }, "CustomerName", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", null, out var plan, out var code, out _);

            Assert.False(ok);
            Assert.Equal("FilterAttributeReferencedByConditions", code);
            var referencing = plan["referencingConditions"] as JArray;
            Assert.Single(referencing);
            Assert.Equal("unanalyzable", referencing[0]["verdict"]?.ToString());
        }

        [Fact]
        public void Plan_IncidentalMentions_AreReportedWithoutBlocking()
        {
            var filters = Filters(new[] { "CustomerName like 'A%'", StatusCondition }, "CustomerName", "CustomerStatus");

            bool ok = WorkWithSdkAdapter.PlanFilterAttributeRemoval("I", filters, "CustomerName", null, out var plan, out _, out var message);

            Assert.True(ok, message);
            var incidental = plan["incidentalMentions"] as JArray;
            Assert.Single(incidental);
            Assert.Equal("CustomerName like 'A%'", incidental[0]["value"]?.ToString());
            Assert.Empty(plan["referencingConditions"] as JArray);
        }

        // ── orchestration over the fake typed model ──────────────────────────────

        [Fact]
        public void Core_DryRun_ReturnsPlanAndNeverTouchesTheModel()
        {
            var model = NewModel();
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "WorkWithWebCustomer", FakeSdk, ReaderFor(model), () => persisted++,
                null, "CustomerName", new List<string> { CustomerCondition }, dryRun: true, out var plan, out var code, out var message);

            Assert.True(ok, message);
            Assert.Null(code);
            Assert.Equal("WorkWithWebCustomer", plan["instance"]?.ToString());
            Assert.Null(plan["saved"]);
            Assert.Equal(0, persisted);
            Assert.Equal(0, model.SaveToCalls);
            Assert.Equal(new[] { "CustomerName", "OrderDate", "CustomerStatus" }, ModelAttributeNames(model).ToArray());
            Assert.Equal(3, ModelConditions(model).Count());
        }

        [Fact]
        public void Core_Write_RemovesAttributeAndListedConditions_VerifiedFieldByField()
        {
            var model = NewModel(CustomerCondition, OrderDateCondition, CustomerCondition, StatusCondition);
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "WorkWithWebCustomer", FakeSdk, ReaderFor(model), () => persisted++,
                "Root", "&CustomerName", new List<string> { CustomerCondition }, dryRun: false, out var receipt, out var code, out var message);

            Assert.True(ok, message);
            Assert.Null(code);

            // the typed model lost exactly the attribute and every occurrence of the listed condition
            Assert.Equal(new[] { "OrderDate", "CustomerStatus" }, ModelAttributeNames(model).ToArray());
            Assert.Equal(new[] { OrderDateCondition, StatusCondition }, ModelConditions(model).ToArray());
            Assert.Equal(1, model.SaveToCalls);
            Assert.Equal(1, persisted);

            // the receipt trio and the field-by-field verification
            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.True(receipt["persisted"]?.ToObject<bool>());
            Assert.True(receipt["verified"]?.ToObject<bool>());
            Assert.Equal("native-sdk", receipt["route"]?.ToString());
            Assert.Equal("Root", receipt["level"]?.ToString());
            Assert.Equal("CustomerName", receipt["removedFilterAttribute"]?["name"]?.ToString());
            Assert.Equal("Customer Name", receipt["removedFilterAttribute"]?["description"]?.ToString());
            Assert.Equal("&CustomerName", receipt["removedFilterAttribute"]?["variableName"]?.ToString());
            var removedConditions = receipt["removedConditions"] as JArray;
            Assert.Single(removedConditions);
            Assert.Equal(2, removedConditions[0]["occurrencesRemoved"]?.ToObject<int>());
            Assert.Equal(new[] { "OrderDate", "CustomerStatus" }, (receipt["remainingAttributes"] as JArray).Select(a => a["name"]?.ToString()).ToArray());
            Assert.Equal(new[] { OrderDateCondition, StatusCondition }, (receipt["remainingConditions"] as JArray).Select(c => c.ToString()).ToArray());
            Assert.Equal("&CustomerName", receipt["requested"]?["attribute"]?.ToString());
            Assert.Equal("CustomerName", receipt["requested"]?["normalizedAttribute"]?.ToString());

            var verification = receipt["verification"] as JObject;
            Assert.Equal("filter_reread", verification["scope"]?.ToString());
            Assert.True(verification["allFieldsMatched"]?.ToObject<bool>());
            Assert.Equal("in_memory_instance_reread", verification["observedSource"]?.ToString());
            Assert.Equal(new[] { "attributes[].name", "attributes[].description", "attributes[].domain", "conditions[]" }, (verification["fieldsCompared"] as JArray).Select(f => f.ToString()).ToArray());
            Assert.NotEmpty(verification["referencesNotVerified"] as JArray);
            Assert.Null(verification["reason"]);
            Assert.Equal("not_guaranteed", receipt["derivedObjectRegeneration"]?.ToString());
            Assert.Null(receipt["generatedObjectsRegenerated"]);
        }

        [Fact]
        public void Core_Write_WithoutRemoveConditions_RemovesNoCondition()
        {
            var model = NewModel(OrderDateCondition, StatusCondition);

            bool ok = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "I", FakeSdk, ReaderFor(model), () => { },
                null, "CustomerName", null, dryRun: false, out var receipt, out _, out var message);

            Assert.True(ok, message);
            Assert.Equal(new[] { "OrderDate", "CustomerStatus" }, ModelAttributeNames(model).ToArray());
            Assert.Equal(new[] { OrderDateCondition, StatusCondition }, ModelConditions(model).ToArray());
            Assert.Empty(receipt["removedConditions"] as JArray);
            Assert.True(receipt["verified"]?.ToObject<bool>());
        }

        [Fact]
        public void Core_UnlistedReferencingCondition_RefusesBeforeAnyWrite()
        {
            var model = NewModel();
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "I", FakeSdk, ReaderFor(model), () => persisted++,
                null, "CustomerName", null, dryRun: false, out var plan, out var code, out _);

            Assert.False(ok);
            Assert.Equal("FilterAttributeReferencedByConditions", code);
            Assert.NotNull(plan);
            Assert.Equal(0, persisted);
            Assert.Equal(0, model.SaveToCalls);
            Assert.Equal(3, ModelAttributeNames(model).Count());
        }

        [Fact]
        public void Core_WithoutNativeAssembly_RefusesWorkWithSdkUnavailable_NeverXml()
        {
            var model = NewModel();
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "I", null, ReaderFor(model), () => persisted++,
                null, "CustomerName", new List<string> { CustomerCondition }, dryRun: false, out var plan, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("WorkWithSdkUnavailable", code);
            Assert.Contains("no XML fallback", message);
            Assert.NotNull(plan);
            Assert.Equal(0, persisted);
            Assert.Equal(0, model.SaveToCalls);
            Assert.Equal(3, ModelAttributeNames(model).Count());
            Assert.Equal(3, ModelConditions(model).Count());
        }

        [Fact]
        public void Core_RereadFailureAfterSave_ReportsVerifiedFalseWithReason()
        {
            var model = NewModel();
            int reads = 0;
            WorkWithSdkAdapter.FilterReader reader = (string level, out JObject filters, out string error, out string message) =>
            {
                if (++reads > 1)
                {
                    filters = null;
                    error = "PatternInstanceXmlEmpty";
                    message = "simulated reread failure";
                    return false;
                }
                return WorkWithSdkAdapter.ReadNativeFilters(model, level, out filters, out error, out message);
            };
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "I", FakeSdk, reader, () => persisted++,
                null, "CustomerName", new List<string> { CustomerCondition }, dryRun: false, out var receipt, out var code, out _);

            Assert.True(ok);
            Assert.Null(code);
            Assert.Equal(1, persisted);
            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.True(receipt["persisted"]?.ToObject<bool>());
            Assert.False(receipt["verified"]?.ToObject<bool>());
            var verification = receipt["verification"] as JObject;
            Assert.Equal(JTokenType.Null, verification["allFieldsMatched"]?.Type);
            Assert.Equal("none", verification["observedSource"]?.ToString());
            Assert.Contains("PatternInstanceXmlEmpty", verification["reason"]?.ToString());
            Assert.Contains("simulated reread failure", verification["reason"]?.ToString());
            Assert.Equal(JTokenType.Null, receipt["remainingAttributes"]?.Type);
        }

        [Fact]
        public void Core_RereadDivergence_IsVerificationMismatchWithUnverifiedReceipt()
        {
            var model = NewModel();
            int reads = 0;
            WorkWithSdkAdapter.FilterReader reader = (string level, out JObject filters, out string error, out string message) =>
            {
                bool ok = WorkWithSdkAdapter.ReadNativeFilters(model, level, out filters, out error, out message);
                if (ok && ++reads > 1)
                {
                    // a neighbor's description drifted on the reread: same count, different field
                    ((JObject)((JArray)filters["attributes"])[0])["description"] = "Drifted";
                }
                return ok;
            };

            bool ok2 = WorkWithSdkAdapter.RemoveFilterAttributeCore(null, "I", FakeSdk, reader, () => { },
                null, "CustomerName", new List<string> { CustomerCondition }, dryRun: false, out var receipt, out var code, out var message2);

            Assert.False(ok2);
            Assert.Equal("VerificationMismatch", code);
            Assert.Contains("description mismatch", message2);
            Assert.NotNull(receipt);
            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.False(receipt["verified"]?.ToObject<bool>());
            Assert.False(receipt["verification"]?["allFieldsMatched"]?.ToObject<bool>());
            Assert.Contains("Drifted", receipt["verification"]?["reason"]?.ToString());
        }

        // ── native write (reflection over the typed collections) ─────────────────

        [Fact]
        public void Native_CollectionWithoutRemove_RefusesWorkWithSdkUnavailable_NothingSaved()
        {
            var model = NewModel(OrderDateCondition);
            var filter = model.LevelList[0].Selection.Filter;
            filter.Attributes = new SealedAttributes(filter.Attributes.Cast<FilterAttributeElement>());
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.TryRemoveFilterAttributeNative(null, FakeSdk, () => persisted++, "Root", "CustomerName", new List<string>(), out var removal, out var code, out var message);

            Assert.False(ok);
            Assert.Null(removal);
            Assert.Equal("WorkWithSdkUnavailable", code);
            Assert.Contains("FilterAttributesElement.Remove", message);
            Assert.Contains("no XML fallback", message);
            Assert.Equal(0, persisted);
            Assert.Equal(0, model.SaveToCalls);
            Assert.Equal(3, filter.Attributes.Cast<FilterAttributeElement>().Count());
        }

        [Fact]
        public void Native_ListedConditionVanishedSinceRead_RefusesNothingSaved()
        {
            var model = NewModel(OrderDateCondition);
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.TryRemoveFilterAttributeNative(null, FakeSdk, () => persisted++, "Root", "CustomerName", new List<string> { CustomerCondition }, out _, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("FilterConditionNotFound", code);
            Assert.Contains("nothing was saved", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, persisted);
            Assert.Equal(0, model.SaveToCalls);
            Assert.Equal(3, ModelAttributeNames(model).Count());
        }

        [Fact]
        public void Native_UnlistedReferenceAppearedSinceRead_RefusesNothingSaved()
        {
            var model = NewModel(CustomerCondition, OrderDateCondition);
            int persisted = 0;

            bool ok = WorkWithSdkAdapter.TryRemoveFilterAttributeNative(null, FakeSdk, () => persisted++, "Root", "CustomerName", new List<string>(), out _, out var code, out _);

            Assert.False(ok);
            Assert.Equal("FilterAttributeReferencedByConditions", code);
            Assert.Equal(0, persisted);
            Assert.Equal(0, model.SaveToCalls);
            Assert.Equal(2, ModelConditions(model).Count());
        }

        [Fact]
        public void Native_AttributeVanishedSinceRead_RefusesFilterAttributeNotFound()
        {
            var model = NewModel(OrderDateCondition);

            bool ok = WorkWithSdkAdapter.TryRemoveFilterAttributeNative(null, FakeSdk, () => { }, "Root", "Vanished", new List<string>(), out _, out var code, out _);

            Assert.False(ok);
            Assert.Equal("FilterAttributeNotFound", code);
            Assert.Equal(0, model.SaveToCalls);
        }

        [Fact]
        public void Native_AmbiguousLevelWithoutLevelArgument_RefusesBeforeWriting()
        {
            var model = NewModel(OrderDateCondition);
            model.LevelList.Add(new LevelElement { Id = "Orders", Selection = new SelectionElement { Filter = new FilterElement() } });

            bool ok = WorkWithSdkAdapter.TryRemoveFilterAttributeNative(null, FakeSdk, () => { }, null, "CustomerName", new List<string>(), out _, out var code, out _);

            Assert.False(ok);
            Assert.Equal("FilterLevelAmbiguous", code);
            Assert.Equal(0, model.SaveToCalls);
        }

        [Fact]
        public void Native_LoadThrows_ReportsAuthoringFailedAndNothingSaved()
        {
            NewModel(OrderDateCondition);
            WorkWithInstance.LoadHook = _ => throw new InvalidOperationException("boom");
            int persisted = 0;
            try
            {
                bool ok = WorkWithSdkAdapter.TryRemoveFilterAttributeNative(null, FakeSdk, () => persisted++, "Root", "CustomerName", new List<string>(), out _, out var code, out var message);

                Assert.False(ok);
                Assert.Equal("WorkWithFilterAuthoringFailed", code);
                Assert.Contains("boom", message);
                Assert.Contains("Nothing was saved", message);
                Assert.Equal(0, persisted);
            }
            finally
            {
                WorkWithInstance.LoadHook = null;
            }
        }

        // ── verification (pure) ──────────────────────────────────────────────────

        private static JArray Neighbors()
        {
            return new JArray(Attr("OrderDate", "Order Date", "DateDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));
        }

        [Fact]
        public void Verify_AcceptsExactPostState_AndCaseInsensitiveNamesAndDomains()
        {
            var observed = Filters(new[] { OrderDateCondition, StatusCondition }, Attr("orderdate", "Order Date", "datedomain"), Attr("CustomerStatus", "Status", "StatusDomain"));

            bool ok = WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "&CustomerName", new[] { CustomerCondition }, observed, out var detail);

            Assert.True(ok, detail);
            Assert.Null(detail);
        }

        [Fact]
        public void Verify_RejectsAttributeStillPresent()
        {
            var observed = Filters(new[] { OrderDateCondition, StatusCondition }, "CustomerName", Attr("OrderDate", "Order Date", "DateDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, observed, out var detail));
            Assert.Contains("still present", detail);
        }

        [Fact]
        public void Verify_RejectsListedConditionStillPresent()
        {
            var observed = Filters(new[] { OrderDateCondition, CustomerCondition, StatusCondition }, Attr("OrderDate", "Order Date", "DateDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, observed, out var detail));
            Assert.Contains("Condition", detail);
            Assert.Contains("still present", detail);
        }

        [Fact]
        public void Verify_ComparesNeighborsFieldByField_NotByCount()
        {
            var sameCountDescriptionDrift = Filters(new[] { OrderDateCondition, StatusCondition }, Attr("OrderDate", "Drifted", "DateDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, sameCountDescriptionDrift, out var detail));
            Assert.Contains("description mismatch", detail);

            var sameCountDomainDrift = Filters(new[] { OrderDateCondition, StatusCondition }, Attr("OrderDate", "Order Date", "OtherDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, sameCountDomainDrift, out detail));
            Assert.Contains("domain mismatch", detail);

            var sameCountRenamed = Filters(new[] { OrderDateCondition, StatusCondition }, Attr("OrderDate", "Order Date", "DateDomain"), Attr("Replaced", "Status", "StatusDomain"));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, sameCountRenamed, out detail));
            Assert.Contains("position 1", detail);

            var lostNeighbor = Filters(new[] { OrderDateCondition, StatusCondition }, Attr("OrderDate", "Order Date", "DateDomain"));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, lostNeighbor, out detail));
            Assert.Contains("count mismatch", detail);
        }

        [Fact]
        public void Verify_ComparesPreservedConditionTextsInOrder()
        {
            var sameCountDifferentText = Filters(new[] { OrderDateCondition, "CustomerStatus = 'Inactive';" }, Attr("OrderDate", "Order Date", "DateDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, sameCountDifferentText, out var detail));
            Assert.Contains("Preserved conditions mismatch", detail);

            var reordered = Filters(new[] { StatusCondition, OrderDateCondition }, Attr("OrderDate", "Order Date", "DateDomain"), Attr("CustomerStatus", "Status", "StatusDomain"));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeRemoval(Neighbors(), new[] { OrderDateCondition, StatusCondition }, "CustomerName", new[] { CustomerCondition }, reordered, out detail));
            Assert.Contains("Preserved conditions mismatch", detail);
        }

        // ── receipt (pure) ───────────────────────────────────────────────────────

        [Fact]
        public void Receipt_UnverifiedKeepsTheTrio_NoNewState()
        {
            var removal = new JObject
            {
                ["removedFilterAttribute"] = Attr("CustomerName", "Customer Name"),
                ["removedConditions"] = new JArray(new JObject { ["value"] = CustomerCondition, ["occurrencesRemoved"] = 1 })
            };

            var receipt = WorkWithSdkAdapter.BuildRemoveFilterReceipt("I", "Root", removal, null, "&CustomerName", "CustomerName", new[] { CustomerCondition }, verified: false, allFieldsMatched: null, unverifiedReason: "reread failed");

            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.True(receipt["persisted"]?.ToObject<bool>());
            Assert.False(receipt["verified"]?.ToObject<bool>());
            Assert.Equal("reread failed", receipt["verification"]?["reason"]?.ToString());
            Assert.Equal("none", receipt["verification"]?["observedSource"]?.ToString());
            Assert.Equal(JTokenType.Null, receipt["verification"]?["allFieldsMatched"]?.Type);
            Assert.Equal("native-sdk", receipt["route"]?.ToString());
            Assert.Equal("not_guaranteed", receipt["derivedObjectRegeneration"]?.ToString());
            Assert.Null(receipt["generatedObjectsRegenerated"]);
            Assert.Null(receipt["applied"]);
            Assert.Equal(new[] { "saved", "persisted", "verified" }, receipt.Properties().Select(p => p.Name).Where(n => n == "saved" || n == "persisted" || n == "verified").ToArray());
        }

        // ── list_filters projection ──────────────────────────────────────────────

        [Fact]
        public void TryListFiltersFromXml_ReportsDefaultAsEmptyWhenUnset()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<instance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level id=""Root"">
    <selection>
      <filter>
        <attributes>
          <filterAttribute name=""CustomerName"" description=""Customer Name"" />
          <filterAttribute name=""OrderDate"" description=""Order Date"" default=""&amp;Today"" />
        </attributes>
        <conditions />
      </filter>
    </selection>
  </level>
</instance>";

            bool ok = WorkWithSdkAdapter.TryListFiltersFromXml(xml, null, out var filters, out _, out var message);

            Assert.True(ok, message);
            var attrs = filters["attributes"] as JArray;
            // #588 contract: both readers report `default` as a string, "" when unset.
            Assert.Equal("", attrs[0]["default"]?.ToString());
            Assert.Equal("&Today", attrs[1]["default"]?.ToString());
        }

        [Fact]
        public void ReadNativeFilters_ProjectsDefaultAndVariableName()
        {
            var model = NewModel(OrderDateCondition);
            model.LevelList[0].Selection.Filter.Attributes.Cast<FilterAttributeElement>().First(a => a.Name == "OrderDate").Default = "&Today";

            bool ok = WorkWithSdkAdapter.ReadNativeFilters(model, "Root", out var filters, out _, out var message);

            Assert.True(ok, message);
            var attrs = filters["attributes"] as JArray;
            Assert.Equal("", attrs[0]["default"]?.ToString());
            Assert.Equal("&Today", attrs[1]["default"]?.ToString());
            Assert.Equal("&OrderDate", attrs[1]["variableName"]?.ToString());
            Assert.Equal("DateDomain", attrs[1]["domain"]?.ToString());
            Assert.Equal(new[] { OrderDateCondition }, (filters["conditions"] as JArray).Select(c => c.ToString()).ToArray());
        }

        // ── contract seams ───────────────────────────────────────────────────────

        [Fact]
        public void RemoveFilterAttribute_HasNoXmlWriteFallbackSeam()
        {
            var remove = typeof(WorkWithSdkAdapter).GetMethod("RemoveFilterAttribute", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(remove);
            Assert.DoesNotContain(remove.GetParameters(), p => p.ParameterType == typeof(WriteService));

            var xmlWriter = typeof(WorkWithSdkAdapter).GetMethod("TryRemoveFilterAttributeFromXml", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.Null(xmlWriter);

            // The removal orchestration and the native writer never read or write the
            // PatternInstance XML: a missing native surface is a refusal, not a fallback.
            string adapter = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Compatibility", "WorkWithSdkAdapter.cs");
            int start = adapter.IndexOf("static bool RemoveFilterAttribute(", StringComparison.Ordinal);
            int end = adapter.IndexOf("static bool VerifyFilterAttributeRemoval(", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "the removal members must exist in WorkWithSdkAdapter");
            string removal = adapter.Substring(start, end - start);
            Assert.DoesNotContain("ReadPatternPartXml", removal, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteObject", removal, StringComparison.Ordinal);
            Assert.DoesNotContain("XDocument", removal, StringComparison.Ordinal);
            Assert.DoesNotContain("xml-fallback", removal, StringComparison.Ordinal);
        }

        [Fact]
        public void Service_DispatchesRemoveFilterAttributeUnderThePerTargetLock()
        {
            string service = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WorkWithService.cs");
            Assert.Contains("case \"remove_filter_attribute\":", service, StringComparison.Ordinal);

            int start = service.IndexOf("ExecuteRemoveFilterAttribute(string target", StringComparison.Ordinal);
            int end = service.IndexOf("RemoveFilterAttributeError(string target", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "ExecuteRemoveFilterAttribute must exist");
            string body = service.Substring(start, end - start);
            Assert.Contains("lock (WriteService.AcquirePerTargetLock(instanceObj.Name))", body, StringComparison.Ordinal);
            Assert.Contains("WorkWithFilterAttributeRemovedUnverified", body, StringComparison.Ordinal);
            Assert.DoesNotContain("expectedVersion", body, StringComparison.Ordinal);
        }
    }
}
