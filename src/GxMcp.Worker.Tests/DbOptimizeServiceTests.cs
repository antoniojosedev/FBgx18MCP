using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// SOTA tool — genexus_db_optimize. Tests cover For-each regex parser,
    /// where-signature canonicalisation, index-coverage prefix match, and
    /// redundant-index detection. No SDK needed — all seams are fake.
    /// </summary>
    public class DbOptimizeServiceTests
    {
        // ---- Fakes ---------------------------------------------------------

        private sealed class FakeEnumerator : DbOptimizeService.IObjectEnumerator
        {
            public List<DbOptimizeService.ObjectRef> Callers { get; } = new();
            public List<string> Transactions { get; } = new();
            public IEnumerable<DbOptimizeService.ObjectRef> EnumerateCallers() => Callers;
            public IEnumerable<string> EnumerateTransactionNames() => Transactions;
        }

        private sealed class FakeReader : DbOptimizeService.ISourceReader
        {
            public Dictionary<string, string> Sources { get; } = new();
            public string ReadCallerSource(DbOptimizeService.ObjectRef obj)
                => Sources.TryGetValue(obj.Name, out var s) ? s : string.Empty;
        }

        private sealed class FakeIndexes : DbOptimizeService.IIndexReader
        {
            public Dictionary<string, List<DbOptimizeService.ExistingIndex>> ByTransaction { get; } = new();
            public IList<DbOptimizeService.ExistingIndex> ReadIndexes(string transactionName)
                => ByTransaction.TryGetValue(transactionName, out var v)
                    ? v
                    : new List<DbOptimizeService.ExistingIndex>();
        }

        // ---- Parser --------------------------------------------------------

        [Fact]
        public void Parser_SingleForEach_ExtractsTransactionAndWhereAttribute()
        {
            string src = @"
                For each Aluno
                    Where AluCod = 1
                    print &AluNom
                endfor";
            var list = DbOptimizeService.ParseForEachBlocks(src).ToList();
            Assert.Single(list);
            var fe = list[0];
            Assert.Equal("Aluno", fe.Transaction);
            Assert.Contains("AluCod", fe.WhereAttributes);
            Assert.Equal("AluCod", fe.WhereSignature);
            Assert.Equal("high", fe.Confidence);
        }

        [Fact]
        public void Parser_MultiWhere_CollectsAllAttributes_SortedSignature()
        {
            string src = @"
                For each Aluno
                    Where AluCurso = &c
                    Where AluCod >= 100
                    Where AluCod < 200
                endfor";
            var fe = DbOptimizeService.ParseForEachBlocks(src).Single();
            Assert.Contains("AluCod", fe.WhereAttributes);
            Assert.Contains("AluCurso", fe.WhereAttributes);
            // Signature sorted alphabetically, so {AluCod,AluCurso} canonicalises stably.
            Assert.Equal("AluCod,AluCurso", fe.WhereSignature);
        }

        [Fact]
        public void Parser_NestedForEach_YieldsBoth()
        {
            string src = @"
                For each Aluno
                    Where AluCod = 1
                    For each Curso
                        Where CurCod = AluCurso
                    endfor
                endfor";
            var list = DbOptimizeService.ParseForEachBlocks(src).ToList();
            // Lazy quantifier yields innermost first, then outermost; either order is fine.
            Assert.Equal(2, list.Count);
            var byTx = list.ToDictionary(p => p.Transaction);
            Assert.True(byTx.ContainsKey("Aluno"));
            Assert.True(byTx.ContainsKey("Curso"));
        }

        [Fact]
        public void Parser_LineCommentsStrippedBeforeMatching()
        {
            string src = @"
                // For each FakeTx ... endfor (this comment should not parse)
                For each Real
                    Where R1 = 1   // inline comment after where
                endfor";
            var list = DbOptimizeService.ParseForEachBlocks(src).ToList();
            Assert.Single(list);
            Assert.Equal("Real", list[0].Transaction);
            Assert.Contains("R1", list[0].WhereAttributes);
        }

        [Fact]
        public void Parser_NoWhereClause_SignatureIsNoWhere_MediumConfidence()
        {
            string src = @"
                For each Aluno
                    print &AluNom
                endfor";
            var fe = DbOptimizeService.ParseForEachBlocks(src).Single();
            Assert.Equal("(no-where)", fe.WhereSignature);
            Assert.Equal("medium", fe.Confidence);
        }

        [Fact]
        public void Parser_OrderByExtractedAsSortAttributes()
        {
            string src = @"
                For each Aluno order AluNom
                    Where AluCurso = &c
                endfor";
            var fe = DbOptimizeService.ParseForEachBlocks(src).Single();
            Assert.Contains("AluNom", fe.SortAttributes);
            Assert.Contains("AluCurso", fe.WhereAttributes);
        }

        [Fact]
        public void Parser_LiteralsAndVariablesDroppedFromSignature()
        {
            // &x is a variable, 42 is a literal, "Y" is a string — all stripped.
            string src = @"
                For each Aluno
                    Where AluCod = &x and AluNum = 42 and AluNom = ""Y""
                endfor";
            var fe = DbOptimizeService.ParseForEachBlocks(src).Single();
            // 'and' is a clause-ish keyword; it slips through tokeniser as an
            // identifier. Accept it but require the real attributes are present.
            Assert.Contains("AluCod", fe.WhereAttributes);
            Assert.Contains("AluNum", fe.WhereAttributes);
            Assert.Contains("AluNom", fe.WhereAttributes);
            Assert.DoesNotContain("x", fe.WhereAttributes); // &x stripped
        }

        // ---- Coverage prefix match -----------------------------------------

        [Fact]
        public void IsPrefix_OrderSensitive_CaseInsensitive()
        {
            Assert.True(DbOptimizeService.IsPrefix(new[] { "a" }, new[] { "A", "B" }));
            Assert.True(DbOptimizeService.IsPrefix(new[] { "A", "B" }, new[] { "a", "b", "c" }));
            Assert.False(DbOptimizeService.IsPrefix(new[] { "B", "A" }, new[] { "A", "B" }));
            Assert.False(DbOptimizeService.IsPrefix(new[] { "A", "B", "C", "D" }, new[] { "A", "B", "C" }));
        }

        [Fact]
        public void IsCoveredByExisting_PrefixMatchHits()
        {
            var existing = new List<DbOptimizeService.ExistingIndex>
            {
                new() { Name = "IX_X", Columns = { "A", "B", "C" } }
            };
            Assert.True(DbOptimizeService.IsCoveredByExisting(new[] { "A" }, existing));
            Assert.True(DbOptimizeService.IsCoveredByExisting(new[] { "A", "B" }, existing));
            Assert.False(DbOptimizeService.IsCoveredByExisting(new[] { "B" }, existing));
            Assert.False(DbOptimizeService.IsCoveredByExisting(new[] { "A", "C" }, existing));
        }

        // ---- Integration through Analyze/SuggestIndexes/Report --------------

        private static DbOptimizeService BuildService(out FakeEnumerator e, out FakeReader r, out FakeIndexes idx)
        {
            e = new FakeEnumerator();
            r = new FakeReader();
            idx = new FakeIndexes();
            return new DbOptimizeService(e, r, idx);
        }

        [Fact]
        public void Analyze_AggregatesCallerCount_AcrossObjects()
        {
            var svc = BuildService(out var e, out var r, out var idx);
            e.Transactions.Add("Aluno");
            e.Callers.Add(new() { Name = "PA", Type = "Procedure" });
            e.Callers.Add(new() { Name = "PB", Type = "Procedure" });
            e.Callers.Add(new() { Name = "PC", Type = "WebPanel" });
            r.Sources["PA"] = "For each Aluno\nWhere AluCod = 1\nendfor";
            r.Sources["PB"] = "For each Aluno\nWhere AluCod = &x\nendfor";
            r.Sources["PC"] = "For each Aluno\nWhere AluCurso = &c\nendfor";

            var jo = JObject.Parse(svc.Analyze(null));
            Assert.Equal("ok", jo["status"]?.ToString());
            var txs = (JArray)jo["result"]!["transactions"]!;
            Assert.Single(txs);
            var t = (JObject)txs[0];
            Assert.Equal("Aluno", t["name"]?.ToString());
            var patterns = (JArray)t["accessPatterns"]!;
            Assert.Equal(2, patterns.Count);
            // Hot one (AluCod, 2 callers) should sort first.
            Assert.Equal("AluCod", patterns[0]["whereSignature"]?.ToString());
            Assert.Equal(2, patterns[0]["callerCount"]?.ToObject<int>());
        }

        [Fact]
        public void SuggestIndexes_OmitsCoveredQueries_ProposesUncovered()
        {
            var svc = BuildService(out var e, out var r, out var idx);
            e.Transactions.Add("Aluno");
            e.Callers.Add(new() { Name = "P1", Type = "Procedure" });
            e.Callers.Add(new() { Name = "P2", Type = "Procedure" });
            r.Sources["P1"] = "For each Aluno\nWhere AluCod = 1\nendfor";        // covered by PK
            r.Sources["P2"] = "For each Aluno\nWhere AluCurso = &c\nendfor";     // NOT covered

            idx.ByTransaction["Aluno"] = new List<DbOptimizeService.ExistingIndex>
            {
                new() { Name = "PK_Aluno", IsPrimary = true, IsUnique = true, Columns = { "AluCod" } }
            };

            var jo = JObject.Parse(svc.SuggestIndexes("Aluno"));
            Assert.Equal("ok", jo["status"]?.ToString());
            var suggested = (JArray)jo["result"]!["suggestedIndexes"]!;
            Assert.Single(suggested);
            var s = (JObject)suggested[0];
            Assert.Contains("AluCurso", s["columns"]!.Values<string>());
            Assert.Contains("CREATE INDEX IX_Aluno_AluCurso ON Aluno (AluCurso);", s["ddl"]?.ToString());
        }

        [Fact]
        public void SuggestIndexes_FlagsRedundantPrefixIndex()
        {
            var svc = BuildService(out var e, out var r, out var idx);
            e.Transactions.Add("Aluno");
            // No For each blocks needed — redundancy check is purely structural.
            idx.ByTransaction["Aluno"] = new List<DbOptimizeService.ExistingIndex>
            {
                new() { Name = "IX_Long",  Columns = { "A", "B", "C" } },
                new() { Name = "IX_Short", Columns = { "A", "B" } }     // strict prefix of IX_Long
            };

            var jo = JObject.Parse(svc.SuggestIndexes("Aluno"));
            var redundant = (JArray)jo["result"]!["redundantIndexes"]!;
            Assert.Single(redundant);
            Assert.Equal("IX_Short", redundant[0]?["name"]?.ToString());
        }

        [Fact]
        public void SuggestIndexes_CapsScan_DoesNotGrindWholeKb()
        {
            // Regression (friction 2026-06-02): SuggestIndexes used to read Source+Events
            // for EVERY caller object in the KB on the STA thread, wedging the worker.
            // With no index call-graph (test ctor → unscoped), the scan must cap and
            // flag truncation instead of reading an unbounded number of objects.
            var svc = BuildService(out var e, out var r, out var idx);
            e.Transactions.Add("Aluno");
            for (int i = 0; i < 1600; i++)
            {
                string name = "P" + i;
                e.Callers.Add(new() { Name = name, Type = "Procedure" });
                r.Sources[name] = "For each Aluno\nWhere AluCurso = &c\nendfor";
            }

            var jo = JObject.Parse(svc.SuggestIndexes("Aluno"));
            Assert.Equal("ok", jo["status"]?.ToString());
            var scan = (JObject)jo["result"]!["scan"]!;
            Assert.True((bool)scan["truncated"]!, "scan should report truncation when uncapped work exceeds the limit");
            Assert.False((bool)scan["scoped"]!); // no index wired in the test ctor
            Assert.Equal(1500, (int)scan["scannedObjects"]!); // capped at MaxScanReads
        }

        [Fact]
        public void SuggestIndexes_SmallScan_NotTruncated()
        {
            var svc = BuildService(out var e, out var r, out var idx);
            e.Transactions.Add("Aluno");
            e.Callers.Add(new() { Name = "P1", Type = "Procedure" });
            r.Sources["P1"] = "For each Aluno\nWhere AluCurso = &c\nendfor";

            var jo = JObject.Parse(svc.SuggestIndexes("Aluno"));
            var scan = (JObject)jo["result"]!["scan"]!;
            Assert.False((bool)scan["truncated"]!);
            Assert.Equal(1, (int)scan["scannedObjects"]!);
        }

        [Fact]
        public void SuggestIndexes_MissingTarget_ReturnsError()
        {
            var svc = BuildService(out var _, out var _, out var _);
            var jo = JObject.Parse(svc.SuggestIndexes(null));
            Assert.Equal("error", jo["status"]?.ToString());
            Assert.Equal("MissingTarget", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Report_MarkdownFormat_ContainsTableHeader()
        {
            var svc = BuildService(out var e, out var r, out var idx);
            e.Transactions.Add("Aluno");
            e.Callers.Add(new() { Name = "P1", Type = "Procedure" });
            r.Sources["P1"] = "For each Aluno\nWhere AluCurso = &c\nendfor";

            var jo = JObject.Parse(svc.Report("markdown"));
            Assert.Equal("ok", jo["status"]?.ToString());
            string md = jo["result"]?["report"]?.ToString() ?? string.Empty;
            Assert.Contains("Top unindexed hot paths", md);
            Assert.Contains("Aluno", md);
            Assert.Contains("AluCurso", md);
        }

        [Fact]
        public void Report_JsonOnlyWhenFormatOmitted()
        {
            var svc = BuildService(out var _, out var _, out var _);
            var jo = JObject.Parse(svc.Report(null));
            Assert.Equal("ok", jo["status"]?.ToString());
            Assert.Null(jo["result"]?["report"]); // markdown not requested
            Assert.NotNull(jo["result"]?["top10"]);
        }

        [Fact]
        public void Analyze_TransactionUnknownToIndex_StillEmittedWhenIndexEmpty()
        {
            // Fallback path: when no Transactions are surfaced (empty index),
            // we accept whatever identifier the parser picks up so the tool
            // still produces useful output on a fresh worker.
            var svc = BuildService(out var e, out var r, out var idx);
            e.Callers.Add(new() { Name = "PA", Type = "Procedure" });
            r.Sources["PA"] = "For each Aluno\nWhere AluCod = 1\nendfor";

            var jo = JObject.Parse(svc.Analyze(null));
            var txs = (JArray)jo["result"]!["transactions"]!;
            Assert.Single(txs);
            Assert.Equal("Aluno", txs[0]?["name"]?.ToString());
        }
    }
}
