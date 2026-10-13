using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // Issue #497: the dedup key read hit["line"], but source hits publish
    // "lineNumber". The position component was therefore always empty, so two
    // different lines with byte-identical text in the same part of the same
    // object collapsed into one hit - while the response still reported
    // partial: false and truncated: false, making occurrence counts across an
    // edit silently wrong.
    public class SourceSearchDedupTests
    {
        private static JObject SourceHit(int lineNumber, string lineText) => new JObject
        {
            ["objectName"] = "PessoaEnderecos",
            ["type"] = "Transaction",
            ["guid"] = "11111111-1111-1111-1111-111111111111",
            ["part"] = "Rules",
            ["lineNumber"] = lineNumber,
            ["lineText"] = lineText
        };

        [Fact]
        public void IdenticalTextOnDifferentLines_IsNotDeduplicated()
        {
            const string repeated = "if PessoaEnderecosAtivo and PessoaEnderecosAtivo <> PessoaEnderecosAtivo.GetOldValue();";

            var first = SourceHit(46, repeated);
            var second = SourceHit(58, repeated);
            var third = SourceHit(72, repeated);

            // This is the exact shape the issue reported: the same condition
            // block repeated across consecutive rules.
            Assert.NotEqual(SourceSearchService.HitDedupKey(first), SourceSearchService.HitDedupKey(second));
            Assert.NotEqual(SourceSearchService.HitDedupKey(second), SourceSearchService.HitDedupKey(third));
            Assert.NotEqual(SourceSearchService.HitDedupKey(first), SourceSearchService.HitDedupKey(third));
        }

        [Fact]
        public void SameLineAndText_IsStillDeduplicated()
        {
            const string text = "CustomerBalance = 0;";

            Assert.Equal(
                SourceSearchService.HitDedupKey(SourceHit(51, text)),
                SourceSearchService.HitDedupKey(SourceHit(51, text)));
        }

        [Fact]
        public void DistinctParts_AreNotCollapsed()
        {
            var inRules = SourceHit(46, "Call(ProcAlpha)");
            var inEvents = (JObject)inRules.DeepClone();
            inEvents["part"] = "Events";

            Assert.NotEqual(
                SourceSearchService.HitDedupKey(inRules),
                SourceSearchService.HitDedupKey(inEvents));
        }

        [Fact]
        public void DistinctObjects_AreNotCollapsed()
        {
            var pessoaEnderecos = SourceHit(46, "Call(ProcAlpha)");
            var pessoa = (JObject)pessoaEnderecos.DeepClone();
            pessoa["objectName"] = "Pessoa";
            pessoa["guid"] = "33333333-3333-3333-3333-333333333333";

            Assert.NotEqual(
                SourceSearchService.HitDedupKey(pessoaEnderecos),
                SourceSearchService.HitDedupKey(pessoa));
        }

        [Fact]
        public void GuidWinsOverObjectName_ForIdentity()
        {
            // The same object can be surfaced under a different display name
            // (caption, homonym). Identity is the guid, so renaming must not
            // split one object into two dedup buckets.
            var hit = SourceHit(46, "Call(ProcAlpha)");
            var renamed = (JObject)hit.DeepClone();
            renamed["objectName"] = "PessoaEnderecos_Alt";

            Assert.Equal(
                SourceSearchService.HitDedupKey(hit),
                SourceSearchService.HitDedupKey(renamed));
        }

        [Fact]
        public void MetadataHits_AreKeyedByFieldAndStayDeduplicated()
        {
            // Metadata hits carry no line. Two attributes with the same value in
            // the same object are distinct hits; the same field twice is not.
            JObject MetadataHit(string field) => new JObject
            {
                ["objectName"] = "Pessoa",
                ["type"] = "Transaction",
                ["guid"] = "22222222-2222-2222-2222-222222222222",
                ["field"] = field,
                ["matchedValue"] = "ABC"
            };

            Assert.NotEqual(
                SourceSearchService.HitDedupKey(MetadataHit("AttrOne")),
                SourceSearchService.HitDedupKey(MetadataHit("AttrTwo")));
            Assert.Equal(
                SourceSearchService.HitDedupKey(MetadataHit("AttrOne")),
                SourceSearchService.HitDedupKey(MetadataHit("AttrOne")));
        }

        [Fact]
        public void NullHit_ProducesEmptyKey()
        {
            Assert.Equal(string.Empty, SourceSearchService.HitDedupKey(null));
        }
    }
}
