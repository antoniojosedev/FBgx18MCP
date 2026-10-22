using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The call contract between a generated helper and its callers. The cases are the shapes a
    // helper left on the old contract produces: the count still matches, the meaning does not.
    public class WwpDerivedContractTests
    {
        private const string HelperRules = "parm(in:&Cond_Owner, in:&Cond_Tenant, in:&Cond_Link);";

        private const string HelperVariables =
            "&Cond_Owner : Attribute:OwnerId\r\n"
            + "&Cond_Tenant : Attribute:TenantId\r\n"
            + "&Cond_Link : Attribute:LinkGuid\r\n"
            + "&Data : DVB_SDTComboData, WorkWithPlus_Web";

        private static WwpDerivedContract.CallerPart Caller(string events, string variables) => new WwpDerivedContract.CallerPart
        {
            Object = "Parent", Part = "Events", Text = events, Variables = variables
        };

        private const string CallerVariables =
            "&Cond_Owner : Attribute:OwnerId\r\n"
            + "&Cond_Tenant : Attribute:TenantId\r\n"
            + "&Cond_Link : Attribute:LinkGuid\r\n"
            + "&Cond_Key : Attribute:ParentKey";

        private static JObject Evaluate(string events, string callerVariables = CallerVariables, string rules = HelperRules,
            string variables = HelperVariables) =>
            WwpDerivedContract.Evaluate("LoadDVComboX", rules, variables, new[] { Caller(events, callerVariables) });

        [Fact]
        public void ParsesTheParmRuleWithDirectionsAndTheDeclaredTypes()
        {
            List<WwpDerivedContract.Parameter> parameters = WwpDerivedContract.ParseParameters(
                "/* parm(in:&Old); */\r\nparm(in:&Cond_Owner, out:&Cond_Link, &Plain);", HelperVariables + "\r\n&Plain : VARCHAR(10)");

            Assert.Equal(new[] { "Cond_Owner", "Cond_Link", "Plain" }, parameters.Select(p => p.Name).ToArray());
            Assert.Equal(new[] { "in", "out", "inout" }, parameters.Select(p => p.Direction).ToArray());
            Assert.Equal("Attribute:OwnerId", parameters[0].Type);
            Assert.Equal("VARCHAR(10)", parameters[2].Type);
        }

        [Fact]
        public void AMatchingCall_IsConsistent_InBothCallForms()
        {
            JObject plain = Evaluate("&x = LoadDVComboX(&Cond_Owner, &Cond_Tenant, &Cond_Link)");
            JObject viaCall = Evaluate("LoadDVComboX.Call(&Cond_Owner, &Cond_Tenant, &Cond_Link)");

            Assert.True((bool)plain["consistent"]);
            Assert.True((bool)viaCall["consistent"]);
            Assert.Equal(1, (int)plain["callCount"]);
            Assert.Empty(plain["mismatches"]);
        }

        [Fact]
        public void AMentionInACommentOrAStringIsNotACall()
        {
            JObject result = Evaluate("// LoadDVComboX(&a)\r\n/* LoadDVComboX(&a, &b) */\r\n&s = 'LoadDVComboX(&c)'");

            Assert.Equal(0, (int)result["callCount"]);
            Assert.True((bool)result["consistent"]);
        }

        [Fact]
        public void ADifferentArgumentCount_IsReportedWithItsLine()
        {
            JObject result = Evaluate("Event Start\r\n  LoadDVComboX(&Cond_Owner, &Cond_Tenant)\r\nEndEvent");

            Assert.False((bool)result["consistent"]);
            JObject mismatch = (JObject)result["mismatches"][0];
            Assert.Equal("argumentCount", (string)mismatch["kind"]);
            Assert.Equal(2, (int)mismatch["line"]);
        }

        [Fact]
        public void SameVariablesInAnotherOrder_IsAnOrderMismatch_EvenThoughTheCountMatches()
        {
            // The helper expects (owner, tenant, link) and the caller sends (tenant, owner, link).
            JObject result = Evaluate("LoadDVComboX(&Cond_Tenant, &Cond_Owner, &Cond_Link)");

            Assert.False((bool)result["consistent"]);
            Assert.Contains(result["mismatches"].Select(m => (string)m["kind"]), kind => kind == "order");
        }

        [Fact]
        public void AVariableOfAnotherType_AtAPosition_IsATypeMismatch()
        {
            // A numeric parent key where the helper expects a GUID link.
            JObject result = Evaluate("LoadDVComboX(&Cond_Owner, &Cond_Tenant, &Cond_Key)");

            Assert.False((bool)result["consistent"]);
            JObject mismatch = (JObject)result["mismatches"].Single(m => (string)m["kind"] == "type");
            Assert.Equal(2, (int)mismatch["position"]);
            Assert.Contains("Cond_Link", (string)mismatch["message"]);
        }

        [Fact]
        public void ADifferentNameOfTheSameType_IsOnlyAWarning()
        {
            string variables = CallerVariables + "\r\n&Other : Attribute:LinkGuid";

            JObject result = Evaluate("LoadDVComboX(&Cond_Owner, &Cond_Tenant, &Other)", variables);

            Assert.True((bool)result["consistent"]);
            Assert.Equal("name", (string)result["warnings"].Single()["kind"]);
        }

        [Fact]
        public void AnArgumentThatIsNotAVariable_CannotBeTypeChecked_ButIsStillCounted()
        {
            JObject result = Evaluate("LoadDVComboX(&Cond_Owner, &Cond_Tenant, 0)");

            Assert.True((bool)result["consistent"]);
            Assert.Equal("typeUnverifiable", (string)result["warnings"].Single(w => (int)w["position"] == 2)["kind"]);
        }

        [Fact]
        public void BasicTypesAreComparedByTheirNormalizedText()
        {
            string rules = "parm(in:&A, in:&B);";
            string helper = "&A : NUMERIC(4)\r\n&B : VARCHAR(20)";

            JObject same = Evaluate("LoadDVComboX(&A, &B)", "&A : NUMERIC(4)\r\n&B : VARCHAR(20)", rules, helper);
            JObject different = Evaluate("LoadDVComboX(&A, &B)", "&A : NUMERIC(4)\r\n&B : NUMERIC(20)", rules, helper);

            Assert.True((bool)same["consistent"]);
            Assert.False((bool)different["consistent"]);
        }

        [Fact]
        public void ACallWithNoMatchingParmRule_IsAnArgumentCountMismatch()
        {
            JObject result = Evaluate("LoadDVComboX(&Cond_Owner)", rules: "// no parm", variables: HelperVariables);

            Assert.False((bool)result["hasParmRule"]);
            Assert.False((bool)result["consistent"]);
        }

        [Fact]
        public void NestedParenthesesAndCommasInAnArgument_AreOneArgument()
        {
            JObject result = Evaluate("LoadDVComboX(&Cond_Owner, Format(&a, &b), &Cond_Link)");

            Assert.Equal(3, ((JArray)result["calls"][0]["arguments"]).Count);
            Assert.True((bool)result["consistent"]);
        }

        [Fact]
        public void StrippingComments_KeepsOffsetsAndLines()
        {
            string text = "a /* x\r\ny */ b // z\r\nc 'q' d";

            string clean = WwpDerivedContract.StripComments(text);

            Assert.Equal(text.Length, clean.Length);
            Assert.Equal(text.Count(c => c == '\n'), clean.Count(c => c == '\n'));
            Assert.DoesNotContain("x", clean);
            Assert.Contains("c", clean);
        }
    }
}
