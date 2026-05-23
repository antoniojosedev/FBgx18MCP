using System;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class KbReadmeServiceTests
    {
        [Fact]
        public void Generate_InvalidAction_ReturnsError()
        {
            var svc = new KbReadmeService(kbService: null, indexCacheService: null);
            var json = JObject.Parse(svc.Generate("bogus", null));
            Assert.Equal("Error", (string)json["status"]!);
        }

        [Fact]
        public void Generate_WithoutKb_ReturnsMarkdownInline()
        {
            var svc = new KbReadmeService(kbService: null, indexCacheService: null);
            var json = JObject.Parse(svc.Generate("generate", null));
            Assert.Equal("Success", (string)json["status"]!);
            Assert.NotNull(json["markdown"]);
            Assert.Contains("Knowledge Base", (string)json["markdown"]!);
        }

        [Fact]
        public void Generate_OutputPathWritesFile()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gxmcp-readme-" + Guid.NewGuid().ToString("N") + ".md");
            try
            {
                var svc = new KbReadmeService(kbService: null, indexCacheService: null);
                var json = JObject.Parse(svc.Generate("generate", tmp));
                Assert.Equal("Success", (string)json["status"]!);
                Assert.Equal(tmp, (string)json["outputPath"]!);
                Assert.True(File.Exists(tmp));
                string md = File.ReadAllText(tmp);
                Assert.Contains("Knowledge Base", md);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        [Fact]
        public void BuildMarkdown_NoIndex_HasFallbackMessage()
        {
            string md = KbReadmeService.BuildMarkdown("MyKb", @"C:\kb", null, null);
            Assert.Contains("Index not available", md);
            Assert.Contains("MyKb", md);
        }

        [Fact]
        public void BuildMarkdown_WithIndex_RendersTransactions()
        {
            var idx = new SearchIndex();
            var t1 = new SearchIndex.IndexEntry
            {
                Name = "Customer", Type = "Transaction", Description = "Customer master.",
                Module = "Sales"
            };
            t1.CalledBy.Add("CustomerList");
            t1.CalledBy.Add("Invoice");
            var t2 = new SearchIndex.IndexEntry
            {
                Name = "Order", Type = "Transaction", Description = "Sales order header.",
                Module = "Sales"
            };
            t2.CalledBy.Add("Invoice");
            idx.Objects["Transaction:Customer"] = t1;
            idx.Objects["Transaction:Order"] = t2;

            string md = KbReadmeService.BuildMarkdown("MyKb", @"C:\kb", null, idx);
            Assert.Contains("`Customer`", md);
            Assert.Contains("`Order`", md);
            // Customer (2 refs) should appear before Order (1 ref).
            int ci = md.IndexOf("`Customer`", StringComparison.Ordinal);
            int oi = md.IndexOf("`Order`", StringComparison.Ordinal);
            Assert.True(ci > 0 && oi > 0 && ci < oi);
        }
    }
}
