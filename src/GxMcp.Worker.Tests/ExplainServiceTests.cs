using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ExplainServiceTests
    {
        [Fact]
        public void Explain_MissingTarget_ReturnsErrorEnvelope()
        {
            var svc = new ExplainService(kbService: null, objectService: null);
            var raw = svc.Explain(null, null, null);
            var json = JObject.Parse(raw);
            Assert.Equal("Error", (string)json["status"]!);
        }

        [Fact]
        public void Explain_ObjectNotFound_ReturnsErrorEnvelope()
        {
            // ObjectService null → FindObject NRE → caught and returns ObjectNotFound path
            // when ObjectService is null we skip past FindObject by passing null; the guard
            // returns ObjectNotFound (because obj == null).
            var svc = new ExplainService(kbService: null, objectService: null);
            var raw = svc.Explain("DoesNotExist", null, null);
            var json = JObject.Parse(raw);
            Assert.Equal("Error", (string)json["status"]!);
            Assert.Equal("ObjectNotFound", (string)json["error"]!);
        }

        [Fact]
        public void BuildPurpose_UsesDescriptionFirstSentence_WhenPresent()
        {
            string p = ExplainService.BuildPurpose("Procedure", "DoStuff",
                "Calculates the order total. Side-effect free.", null);
            Assert.Contains("Calculates the order total", p);
        }

        [Fact]
        public void BuildPurpose_DerivesFromTypeAndName_WhenNoDescription()
        {
            var parms = new List<ObjectService.ParameterInfo>
            {
                new ObjectService.ParameterInfo { Name = "X", Accessor = "in", Type = "Numeric" },
                new ObjectService.ParameterInfo { Name = "Y", Accessor = "out", Type = "Numeric" }
            };
            string p = ExplainService.BuildPurpose("Procedure", "CustomerLookup", "", parms);
            Assert.Contains("procedure", p.ToLowerInvariant());
            Assert.Contains("customer lookup", p.ToLowerInvariant());
            Assert.Contains("input", p.ToLowerInvariant());
        }

        [Fact]
        public void BuildPurpose_Transaction_PrefersManagesVerb()
        {
            string p = ExplainService.BuildPurpose("Transaction", "Customer", null, null);
            Assert.Contains("Manages records of", p);
            Assert.Contains("customer", p.ToLowerInvariant());
        }
    }
}
