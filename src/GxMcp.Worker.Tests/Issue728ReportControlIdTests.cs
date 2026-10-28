using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #728: report controls created through the MCP kept the SDK default Id (1) or
    /// the template's, and the generator keys every control's spec fact by Id, so twelve
    /// imported ReportLines all printed with the first one's coordinates.
    /// EnsureUniqueReportIds must give each component its own Id the way the IDE does.
    /// </summary>
    public class Issue728ReportControlIdTests
    {
        public sealed class FakeControl
        {
            public string Name { get; set; }
            public int Id { get; set; } = 1; // RPT_ID default
        }

        public sealed class FakeBand
        {
            public string Name { get; set; }
            public int Id { get; set; } = 1;
            public List<object> Items { get; } = new List<object>();
        }

        private static IEnumerable<int> AllIds(IEnumerable<FakeBand> bands)
            => bands.SelectMany(b => new[] { b.Id }.Concat(b.Items.Cast<FakeControl>().Select(c => c.Id)));

        [Fact]
        public void ImportedLinesWithDefaultId_GetDistinctIds_AndExistingKeepTheirs()
        {
            // IDE-drawn components already carry unique Ids.
            var header = new FakeBand { Name = "printBlock1", Id = 1 };
            var body = new FakeBand { Name = "printBlock2", Id = 2 };
            var rect = new FakeControl { Name = "Control1", Id = 3 };
            body.Items.Add(rect);

            var created = new List<object>();
            for (int i = 0; i < 12; i++)
            {
                var line = new FakeControl { Name = "Control" + (96 + i) }; // Id 1, as the SDK leaves it
                body.Items.Add(line);
                created.Add(line);
            }
            var bands = new[] { header, body };

            int reassigned = ReportLayoutHelper.EnsureUniqueReportIds(bands, created);

            Assert.Equal(12, reassigned);
            var ids = AllIds(bands).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(1, header.Id);
            Assert.Equal(2, body.Id);
            Assert.Equal(3, rect.Id);
            Assert.All(created.Cast<FakeControl>(), c => Assert.True(c.Id > 3));
        }

        [Fact]
        public void ClonedLabelsSharingTemplateId_AreRenumbered()
        {
            var band = new FakeBand { Name = "printBlock1", Id = 1 };
            var template = new FakeControl { Name = "LblSede1", Id = 7 };
            var clone = new FakeControl { Name = "LblSede2", Id = 7 };
            band.Items.Add(template);
            band.Items.Add(clone);

            ReportLayoutHelper.EnsureUniqueReportIds(new[] { band }, new List<object> { clone });

            Assert.Equal(7, template.Id);
            Assert.Equal(8, clone.Id);
        }

        [Fact]
        public void AlreadySavedDuplicates_AreRepairedKeepingTheFirst()
        {
            // A layout written by an earlier version: nothing new is created in this write.
            var band = new FakeBand { Name = "printBlock1", Id = 1 };
            var first = new FakeControl { Name = "Control96", Id = 5 };
            var second = new FakeControl { Name = "Control97", Id = 5 };
            var zero = new FakeControl { Name = "Control98", Id = 0 };
            band.Items.Add(first);
            band.Items.Add(second);
            band.Items.Add(zero);

            int reassigned = ReportLayoutHelper.EnsureUniqueReportIds(new[] { band });

            Assert.Equal(2, reassigned);
            Assert.Equal(5, first.Id);
            var ids = AllIds(new[] { band }).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.DoesNotContain(0, ids);
        }

        [Fact]
        public void UniqueLayout_IsLeftUntouched()
        {
            var band = new FakeBand { Name = "printBlock1", Id = 1 };
            band.Items.Add(new FakeControl { Name = "Control1", Id = 2 });
            band.Items.Add(new FakeControl { Name = "Control2", Id = 3 });

            Assert.Equal(0, ReportLayoutHelper.EnsureUniqueReportIds(new[] { band }));
            Assert.Equal(new[] { 1, 2, 3 }, AllIds(new[] { band }).ToArray());
        }
    }
}
