using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using GeneXus.Application;
using GeneXus.Utils;

namespace SdkOracle;

// Measures a real GeneXus runtime entry point - the same GeneXus.Utils library
// that generated applications link against - so an engine capability can be
// compared against the implementation it would replace. Every live result is
// also checked against the corpus' recorded expected text.
internal static class Program
{
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "generate-text-extended")
        {
            GenerateTextExtended();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-numeric-math")
        {
            GenerateNumericMath();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-collection-ops")
        {
            GenerateCollectionOps();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-calendar-ops")
        {
            GenerateCalendarOps();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-conditional-state")
        {
            GenerateConditionalState();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-sdt-json")
        {
            GenerateSdtJson();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-sdt-xml")
        {
            GenerateSdtXml();
            return 0;
        }

        if (args.Length > 0 && args[0] == "generate-date-conversions")
        {
            GenerateDateConversions();
            return 0;
        }

        if (args.Length >= 2 && args[0] == "bench-text")
        {
            var iters = int.Parse(args[1], CultureInfo.InvariantCulture);
            var report = BenchText(iters);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.ExpectedMismatches == 0 ? 0 : 1;
        }

        if (args.Length >= 2 && args[0] == "bench-math")
        {
            var iters = int.Parse(args[1], CultureInfo.InvariantCulture);
            var report = BenchMath(iters);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.ExpectedMismatches == 0 ? 0 : 1;
        }
        if (args.Length >= 2 && args[0] == "bench-calendar")
        {
            var iters = int.Parse(args[1], CultureInfo.InvariantCulture);
            var report = BenchCalendar(iters);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.ExpectedMismatches == 0 ? 0 : 1;
        }

        if (args.Length >= 2 && args[0] == "bench-collection")
        {
            var iters = int.Parse(args[1], CultureInfo.InvariantCulture);
            var report = BenchCollection(iters);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.ExpectedMismatches == 0 ? 0 : 1;
        }

        if (args.Length >= 2 && args[0] == "bench-sdt")
        {
            var iters = int.Parse(args[1], CultureInfo.InvariantCulture);
            var report = BenchSdt(iters);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.ExpectedMismatches == 0 ? 0 : 1;
        }

        if (args.Length >= 3 && args[0] == "bench-str")
        {
            var iterations = int.Parse(args[2], CultureInfo.InvariantCulture);
            if (iterations <= 0)
            {
                Console.Error.WriteLine("iterations must be positive");
                return 2;
            }

            var report = BenchStr(ReadCorpus(args[1]), iterations);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return report.ExpectedMismatches == 0 ? 0 : 1;
        }

        Console.Error.WriteLine("usage: sdk-oracle bench-str <corpus.tsv> <iterations> | bench-text <iterations> | bench-math <iterations> | bench-calendar <iterations> | bench-collection <iterations> | bench-sdt <iterations> | generate-*");
        return 2;
    }

    private static BenchReport BenchStr(Row[] rows, int iterations)
    {
        // One full pass warms the JIT so measured passes reflect steady state.
        var warmupMs = Measure(rows, out _, out _, out _);
        var passMs = new double[iterations];
        long chars = 0;
        ulong hash = 0;
        var expectedMismatches = 0;
        for (var pass = 0; pass < iterations; pass++)
        {
            passMs[pass] = Measure(rows, out var passChars, out var passHash, out var passMismatches);
            chars = passChars;
            hash = passHash;
            expectedMismatches += passMismatches;
        }

        var sorted = (double[])passMs.Clone();
        Array.Sort(sorted);
        var medianMs = Median(sorted);
        return new BenchReport(
            Impl: "gxclasses-stringutil",
            Version: ProductVersion(),
            Rows: rows.Length,
            Iters: iterations,
            Warmup: 1,
            WarmupMs: warmupMs,
            PassMs: passMs,
            MedianMs: medianMs,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            NsPerRow: medianMs * 1_000_000.0 / rows.Length,
            Chars: chars,
            Hash: $"0x{hash:x16}",
            HashUnits: "utf16",
            ExpectedMismatches: expectedMismatches,
            Errors: 0);
    }

    private static BenchReport BenchText(int iterations)
    {
        var warmupMs = MeasureText(out _, out _);
        var passMs = new double[iterations];
        long chars = 0;
        ulong hash = 0;
        for (var pass = 0; pass < iterations; pass++)
        {
            passMs[pass] = MeasureText(out chars, out hash);
        }

        var sorted = (double[])passMs.Clone();
        Array.Sort(sorted);
        var medianMs = Median(sorted);
        const int ops = 500;
        return new BenchReport(
            Impl: "gxclasses-text",
            Version: ProductVersion(),
            Rows: ops,
            Iters: iterations,
            Warmup: 1,
            WarmupMs: warmupMs,
            PassMs: passMs,
            MedianMs: medianMs,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            NsPerRow: medianMs * 1_000_000.0 / ops,
            Chars: chars,
            Hash: $"0x{hash:x16}",
            HashUnits: "utf16",
            ExpectedMismatches: 0,
            Errors: 0);
    }

    private static double MeasureText(out long chars, out ulong hash)
    {
        chars = 0;
        hash = FnvOffset;
        var watch = Stopwatch.StartNew();
        for (var loop = 0; loop < 25; loop++)
        {
            var t1 = StringUtil.Trim("   GeneXus 18 Standard Classes   ");
            var t2 = StringUtil.LTrim("   Leading spaces to trim");
            var t3 = StringUtil.RTrim("Trailing spaces to trim   ");
            var p1 = StringUtil.PadL("12345", 10, "0");
            var p2 = StringUtil.PadR("GeneXus", 15, " ");
            var c1 = StringUtil.Concat("Header_", "Payload_", "Trailer");
            var c2 = StringUtil.Concat("Left", "Right");
            var s1 = StringUtil.Substring("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 5, 10);
            var s2 = StringUtil.Left("Hello GeneXus World", 5);
            var s3 = StringUtil.Right("Hello GeneXus World", 5);
            var b1 = StringUtil.StartsWith("GeneXus Standard Classes", "GeneXus").ToString();
            var b2 = StringUtil.EndsWith("GeneXus Standard Classes", "Classes").ToString();
            var b3 = StringUtil.Contains("GeneXus Standard Classes", "Standard").ToString();
            var pos = StringUtil.StringSearch("The quick brown fox jumps over the lazy dog", "fox", 1).ToString();
            var u1 = StringUtil.Upper("mixed CASE text for BENCHMARKING");
            var l1 = StringUtil.Lower("mixed CASE text for BENCHMARKING");

            string[] results = [t1, t2, t3, p1, p2, c1, c2, s1, s2, s3, b1, b2, b3, pos, u1, l1];
            foreach (var r in results)
            {
                chars += r.Length;
                foreach (var ch in r)
                {
                    hash ^= ch;
                    hash *= FnvPrime;
                }
            }
        }
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static BenchReport BenchMath(int iterations)
    {
        var warmupMs = MeasureMath(out _, out _);
        var passMs = new double[iterations];
        long chars = 0;
        ulong hash = 0;
        for (var pass = 0; pass < iterations; pass++)
        {
            passMs[pass] = MeasureMath(out chars, out hash);
        }

        var sorted = (double[])passMs.Clone();
        Array.Sort(sorted);
        var medianMs = Median(sorted);
        const int ops = 500;
        return new BenchReport(
            Impl: "gxclasses-math",
            Version: ProductVersion(),
            Rows: ops,
            Iters: iterations,
            Warmup: 1,
            WarmupMs: warmupMs,
            PassMs: passMs,
            MedianMs: medianMs,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            NsPerRow: medianMs * 1_000_000.0 / ops,
            Chars: chars,
            Hash: $"0x{hash:x16}",
            HashUnits: "utf16",
            ExpectedMismatches: 0,
            Errors: 0);
    }

    private static double MeasureMath(out long chars, out ulong hash)
    {
        chars = 0;
        hash = FnvOffset;
        var watch = Stopwatch.StartNew();
        for (var loop = 0; loop < 25; loop++)
        {
            var i1 = NumberUtil.Int(12345.6789m);
            var i2 = NumberUtil.Int(-9876.5432m);
            var i3 = NumberUtil.Int(0.9999m);
            var r1 = NumberUtil.Round(12345.6789m, 2);
            var r2 = NumberUtil.Round(-9876.5432m, 1);
            var r3 = NumberUtil.Round(3.555m, 2);
            var u1 = NumberUtil.Trunc(12345.6789m, 2);
            var u2 = NumberUtil.Trunc(-9876.5432m, 0);
            var a1 = (12345.67m * 89.12m) + 456.78m - (12.34m / 5.0m);
            var v1 = NumberUtil.Val("123456.789");
            var v2 = NumberUtil.Val("-98765.432");

            decimal[] values = [i1, i2, i3, r1, r2, r3, u1, u2, a1, v1, v2];
            foreach (var val in values)
            {
                var s = val.ToString(CultureInfo.InvariantCulture);
                chars += s.Length;
                foreach (var ch in s)
                {
                    hash ^= ch;
                    hash *= FnvPrime;
                }
            }
        }
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static BenchReport BenchCalendar(int iterations)
    {
        var warmupMs = MeasureCalendar(out _, out _);
        var passMs = new double[iterations];
        long chars = 0;
        ulong hash = 0;
        for (var pass = 0; pass < iterations; pass++)
        {
            passMs[pass] = MeasureCalendar(out chars, out hash);
        }

        var sorted = (double[])passMs.Clone();
        Array.Sort(sorted);
        var medianMs = Median(sorted);
        const int ops = 500;
        return new BenchReport(
            Impl: "gxclasses-calendar",
            Version: ProductVersion(),
            Rows: ops,
            Iters: iterations,
            Warmup: 1,
            WarmupMs: warmupMs,
            PassMs: passMs,
            MedianMs: medianMs,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            NsPerRow: medianMs * 1_000_000.0 / ops,
            Chars: chars,
            Hash: $"0x{hash:x16}",
            HashUnits: "utf16",
            ExpectedMismatches: 0,
            Errors: 0);
    }

    private static double MeasureCalendar(out long chars, out ulong hash)
    {
        chars = 0;
        hash = FnvOffset;
        var watch = Stopwatch.StartNew();
        var dtBase = new DateTime(2024, 2, 29, 14, 30, 45);
        var dtTarget = new DateTime(2023, 1, 1, 0, 0, 0);
        var birth = new DateTime(2000, 5, 15);
        var current = new DateTime(2024, 5, 15);

        for (var loop = 0; loop < 25; loop++)
        {
            var y = DateTimeUtil.Year(dtBase);
            var m = DateTimeUtil.Month(dtBase);
            var d = DateTimeUtil.Day(dtBase);
            var dow = DateTimeUtil.Dow(dtBase);
            var addYr = DateTimeUtil.AddYr(dtBase, 1);
            var addMth = DateTimeUtil.AddMth(dtBase, 1);
            var dadd = DateTimeUtil.DAdd(dtBase, 10);
            var ddiff = (int)DateTimeUtil.DDiff(dtBase, dtTarget);
            var age = DateTimeUtil.Age(birth, current);
            var eom = DateTimeUtil.EndOfMonth(dtBase);

            int[] values = [y, m, d, dow, DateTimeUtil.Year(addYr), DateTimeUtil.Month(addMth), DateTimeUtil.Day(dadd), ddiff, age, DateTimeUtil.Day(eom)];
            foreach (var val in values)
            {
                var s = val.ToString(CultureInfo.InvariantCulture);
                chars += s.Length;
                foreach (var ch in s)
                {
                    hash ^= ch;
                    hash *= FnvPrime;
                }
            }
        }
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static BenchReport BenchCollection(int iterations)
    {
        var warmupMs = MeasureCollection(out _, out _);
        var passMs = new double[iterations];
        long chars = 0;
        ulong hash = 0;
        for (var pass = 0; pass < iterations; pass++)
        {
            passMs[pass] = MeasureCollection(out chars, out hash);
        }

        var sorted = (double[])passMs.Clone();
        Array.Sort(sorted);
        var medianMs = Median(sorted);
        const int ops = 500;
        return new BenchReport(
            Impl: "gxclasses-collection",
            Version: ProductVersion(),
            Rows: ops,
            Iters: iterations,
            Warmup: 1,
            WarmupMs: warmupMs,
            PassMs: passMs,
            MedianMs: medianMs,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            NsPerRow: medianMs * 1_000_000.0 / ops,
            Chars: chars,
            Hash: $"0x{hash:x16}",
            HashUnits: "utf16",
            ExpectedMismatches: 0,
            Errors: 0);
    }

    private static double MeasureCollection(out long chars, out ulong hash)
    {
        chars = 0;
        hash = FnvOffset;
        var watch = Stopwatch.StartNew();
        for (var loop = 0; loop < 25; loop++)
        {
            var col = new GxSimpleCollection<string>();
            for (var i = 0; i < 20; i++)
            {
                col.Add($"item_{i}");
            }
            var item1 = (string)col.Item(1);
            var item10 = (string)col.Item(10);
            var item20 = (string)col.Item(20);
            var idx = col.IndexOf("item_10").ToString();
            col.RemoveItem(1);
            col.RemoveItem(col.Count);
            var count = col.Count.ToString();
            col.Clear();

            string[] items = [item1, item10, item20, idx, count];
            foreach (var s in items)
            {
                chars += s.Length;
                foreach (var ch in s)
                {
                    hash ^= ch;
                    hash *= FnvPrime;
                }
            }
        }
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static BenchReport BenchSdt(int iterations)
    {
        var warmupMs = MeasureSdt(out _, out _);
        var passMs = new double[iterations];
        long chars = 0;
        ulong hash = 0;
        for (var pass = 0; pass < iterations; pass++)
        {
            passMs[pass] = MeasureSdt(out chars, out hash);
        }

        var sorted = (double[])passMs.Clone();
        Array.Sort(sorted);
        var medianMs = Median(sorted);
        const int ops = 500;
        return new BenchReport(
            Impl: "gxclasses-sdt",
            Version: ProductVersion(),
            Rows: ops,
            Iters: iterations,
            Warmup: 1,
            WarmupMs: warmupMs,
            PassMs: passMs,
            MedianMs: medianMs,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            NsPerRow: medianMs * 1_000_000.0 / ops,
            Chars: chars,
            Hash: $"0x{hash:x16}",
            HashUnits: "utf16",
            ExpectedMismatches: 0,
            Errors: 0);
    }

    private static double MeasureSdt(out long chars, out ulong hash)
    {
        chars = 0;
        hash = FnvOffset;
        var watch = Stopwatch.StartNew();
        for (var loop = 0; loop < 25; loop++)
        {
            var parent = new TestParentSdt();
            parent.gxTpr_Code = "PAR_001";
            parent.gxTpr_Child.gxTpr_Amount = 42;
            parent.gxTpr_Child.gxTpr_Label = "Child Item";
            parent.gxTpr_Child.gxTpr_Flag = true;
            parent.gxTpr_Child.gxTpr_Day = new DateTime(2026, 3, 29);

            var item1 = new TestItemSdt { gxTpr_Amount = 101, gxTpr_Label = "Line 1", gxTpr_Fixed = "A" };
            var item2 = new TestItemSdt { gxTpr_Amount = 102, gxTpr_Label = "Line 2", gxTpr_Fixed = "B" };
            var item3 = new TestItemSdt { gxTpr_Amount = 103, gxTpr_Label = "Line 3", gxTpr_Fixed = "C" };
            parent.gxTpr_Items.Add(item1);
            parent.gxTpr_Items.Add(item2);
            parent.gxTpr_Items.Add(item3);

            string json = parent.ToJSonString();
            chars += json.Length;
            foreach (var ch in json)
            {
                hash ^= ch;
                hash *= FnvPrime;
            }

            var round = new TestParentSdt();
            round.FromJSonString(json);
            var code = round.gxTpr_Code;
            var amount = round.gxTpr_Child.gxTpr_Amount.ToString();
            var count = round.gxTpr_Items.Count.ToString();
            string[] check = [code, amount, count];
            foreach (var s in check)
            {
                chars += s.Length;
                foreach (var ch in s)
                {
                    hash ^= ch;
                    hash *= FnvPrime;
                }
            }
        }
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static string ProductVersion()
    {
        var assembly = typeof(StringUtil).Assembly;
        return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static double Measure(Row[] rows, out long chars, out ulong hash, out int expectedMismatches)
    {
        chars = 0;
        hash = FnvOffset;
        expectedMismatches = 0;
        var watch = Stopwatch.StartNew();
        foreach (var row in rows)
        {
            var text = StringUtil.Str(row.Value, row.Width, row.Scale);
            chars += text.Length;
            foreach (var unit in text)
            {
                hash ^= unit;
                hash *= FnvPrime;
            }

            if (text != row.Expected)
            {
                expectedMismatches++;
            }
        }

        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static Row[] ReadCorpus(string path)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var (left, right) = SplitOnce(line, "=[");
            var parts = left.Split('|');
            rows.Add(new Row(
                decimal.Parse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture),
                right[..^1]));
        }

        if (rows.Count == 0)
        {
            throw new InvalidDataException($"empty corpus: {path}");
        }

        return rows.ToArray();
    }

    private static (string Left, string Right) SplitOnce(string text, string separator)
    {
        var index = text.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidDataException($"corpus line lacks '{separator}': {text}");
        }

        return (text[..index], text[(index + separator.Length)..]);
    }

    private static double Median(double[] sorted)
    {
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private sealed record Row(decimal Value, int Width, int Scale, string Expected);

    private sealed record BenchReport(
        [property: JsonPropertyName("impl")] string Impl,
        string Version,
        int Rows,
        int Iters,
        int Warmup,
        double WarmupMs,
        double[] PassMs,
        double MedianMs,
        double MinMs,
        double MaxMs,
        double NsPerRow,
        long Chars,
        string Hash,
        string HashUnits,
        int ExpectedMismatches,
        int Errors);

    private static void GenerateTextExtended()
    {
        string[] trimInputs = ["   hello", "hello", "hello   ", "   hello   ", "   ", "", "\thello", "\nhello", "  a  b  ", "hello\t", "hello\n"];
        foreach (var s in trimInputs)
        {
            Console.WriteLine($"ltrim|{Escape(s)}=[{Escape(StringUtil.LTrim(s))}]");
            Console.WriteLine($"rtrim|{Escape(s)}=[{Escape(StringUtil.RTrim(s))}]");
        }

        (string text, int size, string fill)[] padInputs = [
            ("abc", 5, " "),
            ("abc", 5, "*"),
            ("abc", 6, "-="),
            ("  abc  ", 5, "0"),
            ("  abc  ", 6, "0"),
            ("abcdef", 4, " "),
            ("abc", 3, " "),
            ("abc", 0, " "),
            ("abc", -1, " "),
            ("", 5, "#"),
            ("hello", 10, "123")
        ];
        foreach (var (text, size, fill) in padInputs)
        {
            Console.WriteLine($"padl|{Escape(text)}|{size}|{Escape(fill)}=[{Escape(StringUtil.PadL(text, size, fill))}]");
            Console.WriteLine($"padr|{Escape(text)}|{size}|{Escape(fill)}=[{Escape(StringUtil.PadR(text, size, fill))}]");
        }

        int[] spaceCounts = [0, 1, 5, 10, -5];
        foreach (var count in spaceCounts)
        {
            var res = count > 0 ? StringUtil.Space(count) : "";
            Console.WriteLine($"space|{count}=[{Escape(res)}]");
        }

        (string s1, string s2)[] concat2 = [
            ("hello", "world"),
            ("hello  ", "world"),
            ("hello", "  world"),
            ("", "world"),
            ("hello", "")
        ];
        foreach (var (s1, s2) in concat2)
        {
            Console.WriteLine($"concat|{Escape(s1)}|{Escape(s2)}=[{Escape(StringUtil.Concat(s1, s2))}]");
        }

        (string s1, string s2, string sep)[] concat3 = [
            ("hello", "world", " "),
            ("hello  ", "world", "-"),
            ("hello", "world", ""),
            ("", "world", ":")
        ];
        foreach (var (s1, s2, sep) in concat3)
        {
            Console.WriteLine($"concat3|{Escape(s1)}|{Escape(s2)}|{Escape(sep)}=[{Escape(StringUtil.Concat(s1, s2, sep))}]");
        }

        (string s1, string s2)[] boolInputs = [
            ("GeneXus", "Gene"),
            ("GeneXus", "gene"),
            ("GeneXus", ""),
            ("", ""),
            ("GeneXus", "Xus"),
            ("GeneXus", "xus"),
            ("GeneXus", "neX"),
            ("GeneXus", "nex"),
            ("GeneXus", "18")
        ];
        foreach (var (s1, s2) in boolInputs)
        {
            Console.WriteLine($"startswith|{Escape(s1)}|{Escape(s2)}=[{StringUtil.StartsWith(s1, s2)}]");
            Console.WriteLine($"endswith|{Escape(s1)}|{Escape(s2)}=[{StringUtil.EndsWith(s1, s2)}]");
            Console.WriteLine($"contains|{Escape(s1)}|{Escape(s2)}=[{StringUtil.Contains(s1, s2)}]");
        }

        (string s1, string s2, int start)[] searchInputs = [
            ("banana", "na", 1),
            ("banana", "na", 4),
            ("banana", "na", 6),
            ("banana", "xyz", 1),
            ("banana", "ba", 1),
            ("banana", "", 1),
            ("banana", "na", 0),
            ("banana", "na", -5),
            ("banana", "na", 10),
            ("banana", "an", 2)
        ];
        foreach (var (s1, s2, start) in searchInputs)
        {
            Console.WriteLine($"strsearch|{Escape(s1)}|{Escape(s2)}|{start}=[{StringUtil.StringSearch(s1, s2, start)}]");
        }
    }

    private static void GenerateNumericMath()
    {
        decimal[] intInputs = [
            3.14m, 3.99m, 3.0m, 0.0m, 0.5m, -3.14m, -3.99m, -3.0m, -0.5m, -0.01m,
            123456m, -123456m, 100.0m, -100.0m
        ];
        foreach (var val in intInputs)
        {
            Console.WriteLine($"int|{val.ToString(CultureInfo.InvariantCulture)}=[{NumberUtil.Int(val)}]");
        }

        (decimal val, int digits)[] roundInputs = [
            (123.456m, 2), (123.454m, 2), (123.455m, 2), (-123.455m, 2), (-123.454m, 2),
            (3.5m, 0), (3.4m, 0), (-3.5m, 0), (-3.4m, 0),
            (1234.56m, -2), (1250m, -2), (1249m, -2), (-1250m, -2), (-1249m, -2),
            (0.123456789012345m, 10), (0.123456789012345m, 15), (0m, 2)
        ];
        foreach (var (val, digits) in roundInputs)
        {
            Console.WriteLine($"round|{val.ToString(CultureInfo.InvariantCulture)}|{digits}=[{NumberUtil.Round(val, digits).ToString(CultureInfo.InvariantCulture)}]");
        }

        (decimal val, int digits)[] truncInputs = [
            (123.456m, 2), (123.456m, 0), (123.456m, 1),
            (-123.456m, 2), (-123.456m, 0), (-123.456m, 1),
            (1234.56m, -2), (1250m, -2), (1299m, -2), (-1250m, -2), (-1299m, -2),
            (0.99m, 0), (-0.99m, 0), (0m, 2)
        ];
        foreach (var (val, digits) in truncInputs)
        {
            Console.WriteLine($"trunc|{val.ToString(CultureInfo.InvariantCulture)}|{digits}=[{NumberUtil.Trunc(val, digits).ToString(CultureInfo.InvariantCulture)}]");
        }

        string[] valInputs = [
            "123", "-123", "  123  ", "123.45", "-123.45",
            "123.45abc", "123abc45", "abc123", "12.34.56",
            "", "   ", "-", "-.", "0", "0.00"
        ];
        foreach (var str in valInputs)
        {
            Console.WriteLine($"val|{Escape(str)}=[{NumberUtil.Val(str).ToString(CultureInfo.InvariantCulture)}]");
        }

        (string str, string dSep)[] val2Inputs = [
            ("123,45", ","), ("123.45", "."), ("123,45abc", ","), ("-123,45", ",")
        ];
        foreach (var (str, dSep) in val2Inputs)
        {
            Console.WriteLine($"val2|{Escape(str)}|{dSep}=[{NumberUtil.Val(str, dSep).ToString(CultureInfo.InvariantCulture)}]");
        }

        (string text, int size)[] leftRightInputs = [
            ("hello", 3), ("hello", 0), ("hello", 5), ("hello", 10), ("hello", -1),
            ("", 3), ("   abc   ", 4)
        ];
        foreach (var (text, size) in leftRightInputs)
        {
            try
            {
                Console.WriteLine($"left|{Escape(text)}|{size}=[{Escape(StringUtil.Left(text, size))}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"left|{Escape(text)}|{size}=[ERR:{ex.GetType().Name}]");
            }

            try
            {
                Console.WriteLine($"right|{Escape(text)}|{size}=[{Escape(StringUtil.Right(text, size))}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"right|{Escape(text)}|{size}=[ERR:{ex.GetType().Name}]");
            }
        }

        string[] ascInputs = ["A", "a", "0", " ", "", "ABC", "Z", "z"];
        foreach (var str in ascInputs)
        {
            Console.WriteLine($"asc|{Escape(str)}=[{StringUtil.Asc(str)}]");
        }

        int[] chrInputs = [65, 97, 48, 32, 10, 9, 66, 90];
        foreach (var ascii in chrInputs)
        {
            Console.WriteLine($"chr|{ascii}=[{Escape(StringUtil.Chr(ascii))}]");
        }

        string[] notNumericInputs = ["123", "123.45", "123a", "   123   ", "", "-123", "0", "abc"];
        foreach (var str in notNumericInputs)
        {
            Console.WriteLine($"notnumeric|{Escape(str)}=[{StringUtil.NotNumeric(str)}]");
        }

        (decimal val, int len, int dec)[] ltrimStrInputs = [
            (123.45m, 10, 2), (123.45m, 6, 2), (123.45m, 5, 2), (-123.45m, 10, 2), (0m, 5, 0)
        ];
        foreach (var (val, len, dec) in ltrimStrInputs)
        {
            Console.WriteLine($"ltrimstr|{val.ToString(CultureInfo.InvariantCulture)}|{len}|{dec}=[{StringUtil.LTrimStr(val, len, dec)}]");
        }
    }

    private static void GenerateCollectionOps()
    {
        var col = new GxSimpleCollection<string>();
        Console.WriteLine($"count_initial|=[{col.Count}]");

        col.Add("alpha");
        col.Add("beta");
        col.Add("gamma");
        Console.WriteLine($"count_after_3_adds|=[{col.Count}]");
        Console.WriteLine($"item|1=[{col.Item(1)}]");
        Console.WriteLine($"item|2=[{col.Item(2)}]");
        Console.WriteLine($"item|3=[{col.Item(3)}]");

        Console.WriteLine($"index_of|alpha=[{col.IndexOf("alpha")}]");
        Console.WriteLine($"index_of|beta=[{col.IndexOf("beta")}]");
        Console.WriteLine($"index_of|gamma=[{col.IndexOf("gamma")}]");
        Console.WriteLine($"index_of|missing=[{col.IndexOf("missing")}]");

        col.Add("first", 1);
        Console.WriteLine($"count_after_insert_1|=[{col.Count}]");
        Console.WriteLine($"item_after_insert_1|1=[{col.Item(1)}]");
        Console.WriteLine($"item_after_insert_1|2=[{col.Item(2)}]");

        col.Add("third", 3);
        Console.WriteLine($"count_after_insert_3|=[{col.Count}]");
        Console.WriteLine($"item_after_insert_3|3=[{col.Item(3)}]");

        col.Add("end", 0);
        Console.WriteLine($"count_after_insert_0|=[{col.Count}]");
        Console.WriteLine($"item_after_insert_0|last=[{col.Item(col.Count)}]");

        col.RemoveItem(1);
        Console.WriteLine($"count_after_remove_1|=[{col.Count}]");
        Console.WriteLine($"item_after_remove_1|1=[{col.Item(1)}]");

        col.RemoveItem(col.Count);
        Console.WriteLine($"count_after_remove_last|=[{col.Count}]");
        Console.WriteLine($"item_after_remove_last|last=[{col.Item(col.Count)}]");

        col.RemoveItem(0);
        col.RemoveItem(-1);
        col.RemoveItem(999);
        Console.WriteLine($"count_after_invalid_removes|=[{col.Count}]");

        col.Clear();
        Console.WriteLine($"count_after_clear|=[{col.Count}]");
        Console.WriteLine($"index_of_cleared|alpha=[{col.IndexOf("alpha")}]");
        col.RemoveItem(1);
        Console.WriteLine($"count_after_remove_empty|=[{col.Count}]");
    }

    private static void GenerateCalendarOps()
    {
        // 1. Component extraction: Day, Month, Year, Hour, Minute, Second, Dow
        DateTime[] dates = [
            new DateTime(2024, 2, 29, 14, 30, 45), // leap year Thursday (Dow=5)
            new DateTime(2023, 1, 1, 0, 0, 0),     // Sunday (Dow=1)
            new DateTime(2023, 12, 31, 23, 59, 59), // Sunday (Dow=1)
            new DateTime(2024, 6, 15, 12, 0, 0),   // Saturday (Dow=7)
            DateTimeUtil.NullDate()                   // empty / nullDate
        ];

        foreach (var dt in dates)
        {
            string label = dt == DateTimeUtil.NullDate() ? "empty" : dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"day|{label}=[{DateTimeUtil.Day(dt)}]");
            Console.WriteLine($"month|{label}=[{DateTimeUtil.Month(dt)}]");
            Console.WriteLine($"year|{label}=[{DateTimeUtil.Year(dt)}]");
            Console.WriteLine($"hour|{label}=[{DateTimeUtil.Hour(dt)}]");
            Console.WriteLine($"minute|{label}=[{DateTimeUtil.Minute(dt)}]");
            Console.WriteLine($"second|{label}=[{DateTimeUtil.Second(dt)}]");
            Console.WriteLine($"dow|{label}=[{DateTimeUtil.Dow(dt)}]");
        }

        // 2. AddYr
        (DateTime dt, int n)[] addYrInputs = [
            (new DateTime(2024, 2, 29, 10, 0, 0), 1),  // 2024-02-29 + 1 yr -> 2025-02-28
            (new DateTime(2024, 2, 29, 10, 0, 0), 4),  // 2024-02-29 + 4 yr -> 2028-02-29
            (new DateTime(2024, 2, 29, 10, 0, 0), -1), // 2024-02-29 - 1 yr -> 2023-02-28
            (new DateTime(2023, 5, 15, 0, 0, 0), 5),
            (DateTimeUtil.NullDate(), 1),
            (DateTimeUtil.NullDate(), -1),
        ];
        foreach (var (dt, n) in addYrInputs)
        {
            string label = dt == DateTimeUtil.NullDate() ? "empty" : dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            var res = DateTimeUtil.AddYr(dt, n);
            string resStr = res == DateTimeUtil.NullDate() ? "empty" : res.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"addyr|{label}|{n}=[{resStr}]");
        }

        // 3. AddMth
        (DateTime dt, int n)[] addMthInputs = [
            (new DateTime(2024, 1, 31, 10, 0, 0), 1),  // Jan 31 + 1 mth -> Feb 29 (leap)
            (new DateTime(2023, 1, 31, 10, 0, 0), 1),  // Jan 31 + 1 mth -> Feb 28 (non-leap)
            (new DateTime(2024, 3, 31, 10, 0, 0), 1),  // Mar 31 + 1 mth -> Apr 30
            (new DateTime(2024, 5, 31, 10, 0, 0), -3), // May 31 - 3 mth -> Feb 29
            (new DateTime(2023, 5, 31, 10, 0, 0), -3), // May 31 - 3 mth -> Feb 28
            (new DateTime(2023, 10, 15, 0, 0, 0), 14), // cross-year
            (DateTimeUtil.NullDate(), 1),
            (DateTimeUtil.NullDate(), -1),
        ];
        foreach (var (dt, n) in addMthInputs)
        {
            string label = dt == DateTimeUtil.NullDate() ? "empty" : dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            var res = DateTimeUtil.AddMth(dt, n);
            string resStr = res == DateTimeUtil.NullDate() ? "empty" : res.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"addmth|{label}|{n}=[{resStr}]");
        }

        // 4. DAdd
        (DateTime dt, int n)[] daddInputs = [
            (new DateTime(2024, 2, 28, 10, 0, 0), 1),  // Feb 28 + 1 -> Feb 29 (leap)
            (new DateTime(2024, 2, 28, 10, 0, 0), 2),  // Feb 28 + 2 -> Mar 1 (leap)
            (new DateTime(2023, 2, 28, 10, 0, 0), 1),  // Feb 28 + 1 -> Mar 1 (non-leap)
            (new DateTime(2024, 1, 1, 0, 0, 0), -1),   // cross-year backward
            (new DateTime(2023, 12, 31, 0, 0, 0), 1),  // cross-year forward
            (DateTimeUtil.NullDate(), 1),
            (DateTimeUtil.NullDate(), -1),
        ];
        foreach (var (dt, n) in daddInputs)
        {
            string label = dt == DateTimeUtil.NullDate() ? "empty" : dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            var res = DateTimeUtil.DAdd(dt, n);
            string resStr = res == DateTimeUtil.NullDate() ? "empty" : res.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"dadd|{label}|{n}=[{resStr}]");
        }

        // 5. TAdd
        (DateTime dt, int s)[] taddInputs = [
            (new DateTime(2024, 1, 1, 0, 0, 0), 60),
            (new DateTime(2024, 1, 1, 23, 59, 30), 60), // rolls over day
            (new DateTime(2024, 1, 1, 0, 0, 30), -60),  // rolls back day
            (DateTimeUtil.NullDate(), 10),
        ];
        foreach (var (dt, s) in taddInputs)
        {
            string label = dt == DateTimeUtil.NullDate() ? "empty" : dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            var res = DateTimeUtil.TAdd(dt, s);
            string resStr = res == DateTimeUtil.NullDate() ? "empty" : res.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"tadd|{label}|{s}=[{resStr}]");
        }

        // 6. DDiff and TDiff
        (DateTime dt1, DateTime dt2)[] diffInputs = [
            (new DateTime(2024, 3, 1, 0, 0, 0), new DateTime(2024, 2, 28, 0, 0, 0)), // 2 days in leap year
            (new DateTime(2023, 3, 1, 0, 0, 0), new DateTime(2023, 2, 28, 0, 0, 0)), // 1 day in non-leap year
            (new DateTime(2023, 2, 28, 0, 0, 0), new DateTime(2023, 3, 1, 0, 0, 0)), // negative
            (new DateTime(2024, 1, 1, 12, 0, 0), new DateTime(2024, 1, 1, 11, 30, 0)), // 1800s
        ];
        foreach (var (dt1, dt2) in diffInputs)
        {
            string l1 = dt1.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            string l2 = dt2.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"ddiff|{l1}|{l2}=[{DateTimeUtil.DDiff(dt1, dt2)}]");
            Console.WriteLine($"tdiff|{l1}|{l2}=[{DateTimeUtil.TDiff(dt1, dt2)}]");
        }

        // 7. Age
        (DateTime birth, DateTime current)[] ageInputs = [
            (new DateTime(2000, 5, 15, 0, 0, 0), new DateTime(2024, 5, 15, 0, 0, 0)), // exact birthday -> 24
            (new DateTime(2000, 5, 15, 0, 0, 0), new DateTime(2024, 5, 14, 0, 0, 0)), // day before -> 23
            (new DateTime(2000, 5, 15, 0, 0, 0), new DateTime(2024, 5, 16, 0, 0, 0)), // day after -> 24
            (new DateTime(2000, 2, 29, 0, 0, 0), new DateTime(2023, 2, 28, 0, 0, 0)), // leap birth in non-leap -> 22
            (new DateTime(2000, 2, 29, 0, 0, 0), new DateTime(2024, 2, 29, 0, 0, 0)), // leap birth in leap -> 24
        ];
        foreach (var (birth, current) in ageInputs)
        {
            string lb = birth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string lc = current.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Console.WriteLine($"age|{lb}|{lc}=[{DateTimeUtil.Age(birth, current)}]");
        }

        // 8. EndOfMonth and ResetTime
        DateTime[] endOfMonthInputs = [
            new DateTime(2024, 2, 10, 14, 30, 0), // leap Feb -> 29
            new DateTime(2023, 2, 10, 14, 30, 0), // non-leap Feb -> 28
            new DateTime(2024, 4, 5, 12, 0, 0),   // Apr -> 30
            new DateTime(2024, 1, 15, 8, 15, 0),  // Jan -> 31
            new DateTime(2024, 12, 1, 0, 0, 0),   // Dec -> 31
        ];
        foreach (var dt in endOfMonthInputs)
        {
            string label = dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            var eom = DateTimeUtil.EndOfMonth(dt);
            Console.WriteLine($"endofmonth|{label}=[{eom.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture)}]");
            var rt = DateTimeUtil.ResetTime(dt);
            Console.WriteLine($"resettime|{label}=[{rt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture)}]");
        }
    }

    private static void GenerateDateConversions()
    {
        // Date/Time <-> Character conversions exposed by the GeneXus runtime
        // (`CToD`, `DToC` and `TToC` resolve to these helpers in generated code).
        //
        // Deliberately outside this corpus, because they cannot be characterized
        // without a live Knowledge Base: `CToT` (the context-less helper attaches
        // the current date), the millisecond `TToC3` variant, timezone-offset
        // forms such as `2024-02-29-01`, and the two-digit-year boundary that the
        // helper resolves by century instead of by the measured environment
        // window. See VALIDATION.md.
        string[] ctodInputs = [
            "2024-02-29", "20240229", "02/29/24", "2/29/2024", "29/02/2024", "2024/02/29",
            "2024-2-9", "2024-02-29 14:30:45", "2024-02-30", "2024-13-01", "24-02-29",
            "0000-00-00", "00010101", "9999-12-31", "not a date", "1", "12345",
            "", "   ", " 2024-02-29 ", "2024-02-29T14:30:45",
            "2024-02/29", "2024.02.29", "02-29-2024", "02.29.2024", "2024-02-29x",
            "x2024-02-29", "2/9/24", "13/45/2024", "2024-02-29 14:30",
            "01/01/00", "12/31/39",
            "2024-02-29 zzz", "2024-02-29 25:00", "2024-02-29T", "2024-02-29 ",
            "2024-02-29.5", "2024-02-29 14:30:45.123", "2024-02-29T14:30:45.123"
        ];
        foreach (var value in ctodInputs)
        {
            try
            {
                var parsed = DateTimeUtil.CToD2(value);
                string shown = parsed == DateTimeUtil.NullDate()
                    ? "empty"
                    : parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Console.WriteLine($"ctod|{EscapeSpaces(value)}=[{shown}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ctod|{EscapeSpaces(value)}=[ERR:{ex.GetType().Name}]");
            }
        }

        DateTime[] dateOutputs = [
            new DateTime(2024, 2, 29, 0, 0, 0),
            new DateTime(1999, 12, 31, 0, 0, 0),
            new DateTime(2000, 1, 1, 0, 0, 0),
            new DateTime(1970, 1, 1, 0, 0, 0),
            new DateTime(2099, 12, 31, 0, 0, 0),
            DateTimeUtil.NullDate()
        ];
        foreach (var dt in dateOutputs)
        {
            string label = dt == DateTimeUtil.NullDate()
                ? "empty"
                : dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Console.WriteLine($"dtoc|{label}=[{Escape(DateTimeUtil.DToC2(dt))}]");
        }

        DateTime[] timeOutputs = [
            new DateTime(2024, 2, 29, 14, 30, 45),
            new DateTime(2024, 2, 29, 0, 0, 0),
            new DateTime(2024, 2, 29, 23, 59, 59),
            new DateTime(2024, 2, 29, 9, 5, 3),
            DateTimeUtil.NullDate()
        ];
        foreach (var dt in timeOutputs)
        {
            string label = dt == DateTimeUtil.NullDate()
                ? "empty"
                : dt.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture);
            Console.WriteLine($"ttoc|{label}=[{Escape(DateTimeUtil.TToC2(dt))}]");
        }
    }

    private static void GenerateConditionalState()
    {
        // 1. StringUtil.Format(value, v1..v9)
        (string pattern, string[] args)[] formatCases = [
            ("Selection List %1", ["Customer"]),
            ("Order %1 for %2: %3", ["1001", "Alice", "$250.00"]),
            ("%1-%2-%3-%4-%5-%6-%7-%8-%9", ["A", "B", "C", "D", "E", "F", "G", "H", "I"]),
            ("Repeated %1 and %2 and %1", ["X", "Y"]),
            ("Missing %2 with only %1", ["One"]),
            ("Escaped \\%1 and real %1", ["Value"]),
            ("Backslash \\not_pct and %1", ["Val"]),
            ("No placeholders here", ["Ignored"]),
            ("", ["Ignored"]),
            ("DotNet {0} and {1}", ["First", "Second"]),
        ];
        foreach (var (pattern, args) in formatCases)
        {
            string[] padded = new string[9];
            for (int i = 0; i < 9; i++)
            {
                padded[i] = i < args.Length ? args[i] : "";
            }
            string res = StringUtil.Format(
                pattern,
                padded[0], padded[1], padded[2],
                padded[3], padded[4], padded[5],
                padded[6], padded[7], padded[8]);
            string joinedArgs = string.Join(",", args.Select(Escape));
            Console.WriteLine($"format|{Escape(pattern)}|{joinedArgs}=[{Escape(res)}]");
        }

        // 2. StringUtil.Replicate
        (string str, int count)[] replicateCases = [
            ("ab", 3),
            ("x", 5),
            ("abc", 0),
            ("abc", -2),
            ("", 4),
            ("Gx", 1),
        ];
        foreach (var (str, count) in replicateCases)
        {
            string res = StringUtil.Replicate(str, count);
            Console.WriteLine($"replicate|{Escape(str)}|{count}=[{Escape(res)}]");
        }

        // 3. StringUtil.CharAt
        (string str, int idx)[] charAtCases = [
            ("GeneXus", 1),
            ("GeneXus", 4),
            ("GeneXus", 7),
            ("GeneXus", 0),
            ("GeneXus", -1),
            ("GeneXus", 8),
            ("", 1),
        ];
        foreach (var (str, idx) in charAtCases)
        {
            string res = StringUtil.CharAt(str, idx);
            Console.WriteLine($"charat|{Escape(str)}|{idx}=[{Escape(res)}]");
        }

        // 4. StringUtil.StringSearchRev
        (string s1, string s2, int start)[] searchRevCases = [
            ("ababa", "aba", -1),
            ("ababa", "aba", 5),
            ("ababa", "aba", 4),
            ("ababa", "aba", 3),
            ("ababa", "aba", 2),
            ("ababa", "aba", 0),
            ("hello world hello", "hello", -1),
            ("hello world hello", "hello", 10),
            ("hello world hello", "xyz", -1),
            ("abc", "abc", 100),
        ];
        foreach (var (s1, s2, start) in searchRevCases)
        {
            int res = StringUtil.StringSearchRev(s1, s2, start);
            Console.WriteLine($"strsearchrev|{Escape(s1)}|{Escape(s2)}|{start}=[{res}]");
        }

        // 5. BoolToStr and StrToBool
        Console.WriteLine($"booltostr|true=[{StringUtil.BoolToStr(true)}]");
        Console.WriteLine($"booltostr|false=[{StringUtil.BoolToStr(false)}]");
        string[] strToBoolInputs = ["true", "TRUE", "True", "1", "false", "FALSE", "0", "", "yes"];
        foreach (var s in strToBoolInputs)
        {
            bool res = StringUtil.StrToBool(s);
            Console.WriteLine($"strtobool|{Escape(s)}=[{(res ? "true" : "false")}]");
        }

        // 6. NewLine
        Console.WriteLine($"newline|=[{Escape(StringUtil.NewLine())}]");

        // 7. IsEmpty across types (matching GeneXus C# generated expressions)
        string[] textIsEmptyInputs = ["", "   ", " a ", "abc", "\t"];
        foreach (var s in textIsEmptyInputs)
        {
            bool isEmpty = string.IsNullOrEmpty(StringUtil.RTrim(s));
            Console.WriteLine($"isempty_text|{Escape(s)}=[{(isEmpty ? "true" : "false")}]");
        }
        decimal[] numIsEmptyInputs = [0m, 0.00m, 1m, -0.01m, 42.5m];
        foreach (var n in numIsEmptyInputs)
        {
            bool isEmpty = (n == 0m);
            Console.WriteLine($"isempty_num|{n.ToString(CultureInfo.InvariantCulture)}=[{(isEmpty ? "true" : "false")}]");
        }
        bool[] boolIsEmptyInputs = [false, true];
        foreach (var b in boolIsEmptyInputs)
        {
            bool isEmpty = !b;
            Console.WriteLine($"isempty_bool|{(b ? "true" : "false")}=[{(isEmpty ? "true" : "false")}]");
        }
    }

    private static void GenerateSdtJson()
    {
        // 1. Empty SDT ToJson
        var sdtEmpty = new TestItemSdt();
        Console.WriteLine($"sdt_empty_to_json|=[{Escape(sdtEmpty.ToJSonString())}]");

        // 2. Populated scalar SDT ToJson
        var sdt = new TestItemSdt();
        sdt.gxTpr_Amount = 42;
        sdt.gxTpr_Label = "Hello \"world\"";
        sdt.gxTpr_Fixed = "ABC";
        sdt.gxTpr_Flag = true;
        sdt.gxTpr_Day = new DateTime(2026, 3, 29);
        sdt.gxTpr_Stamp = new DateTime(2026, 3, 29, 14, 30, 45);
        string sdtJson = sdt.ToJSonString();
        Console.WriteLine($"sdt_to_json|=[{Escape(sdtJson)}]");

        // 3. FromJson roundtrip
        var sdtRound = new TestItemSdt();
        bool okRound = sdtRound.FromJSonString(sdtJson);
        Console.WriteLine($"sdt_from_json_ok|=[{okRound}]");
        Console.WriteLine($"sdt_round_amount|=[{sdtRound.gxTpr_Amount}]");
        Console.WriteLine($"sdt_round_label|=[{Escape(sdtRound.gxTpr_Label)}]");
        Console.WriteLine($"sdt_round_fixed|=[{Escape(sdtRound.gxTpr_Fixed)}]");
        Console.WriteLine($"sdt_round_flag|=[{sdtRound.gxTpr_Flag}]");
        Console.WriteLine($"sdt_round_day|=[{DateTimeUtil.Year(sdtRound.gxTpr_Day):D4}-{DateTimeUtil.Month(sdtRound.gxTpr_Day):D2}-{DateTimeUtil.Day(sdtRound.gxTpr_Day):D2}]");
        Console.WriteLine($"sdt_round_stamp|=[{DateTimeUtil.Year(sdtRound.gxTpr_Stamp):D4}-{DateTimeUtil.Month(sdtRound.gxTpr_Stamp):D2}-{DateTimeUtil.Day(sdtRound.gxTpr_Stamp):D2}T{DateTimeUtil.Hour(sdtRound.gxTpr_Stamp):D2}:{DateTimeUtil.Minute(sdtRound.gxTpr_Stamp):D2}:{DateTimeUtil.Second(sdtRound.gxTpr_Stamp):D2}]");

        // 4. Case-insensitive keys and partial update
        var sdtPartial = new TestItemSdt();
        sdtPartial.gxTpr_Amount = 100;
        sdtPartial.gxTpr_Label = "Initial";
        bool okPartial = sdtPartial.FromJSonString("{\"label\":\"Updated\",\"flag\":true,\"unknown\":\"ignored\"}");
        Console.WriteLine($"sdt_partial_ok|=[{okPartial}]");
        Console.WriteLine($"sdt_partial_amount_preserved|=[{sdtPartial.gxTpr_Amount}]");
        Console.WriteLine($"sdt_partial_label_updated|=[{Escape(sdtPartial.gxTpr_Label)}]");
        Console.WriteLine($"sdt_partial_flag_updated|=[{sdtPartial.gxTpr_Flag}]");

        // 5. Invalid JSON handling
        var sdtInvalid = new TestItemSdt();
        bool okEmpty = sdtInvalid.FromJSonString("");
        bool okMalformed = sdtInvalid.FromJSonString("{invalid");
        bool okNotObj = sdtInvalid.FromJSonString("123");
        Console.WriteLine($"sdt_from_json_empty|=[{okEmpty}]");
        Console.WriteLine($"sdt_from_json_malformed|=[{okMalformed}]");
        Console.WriteLine($"sdt_from_json_not_obj|=[{okNotObj}]");

        // 6. Nested SDT ToJson and FromJson
        var parent = new TestParentSdt();
        parent.gxTpr_Code = "P01";
        parent.gxTpr_Child.gxTpr_Amount = 10;
        parent.gxTpr_Child.gxTpr_Label = "Sub";
        var parentItem = new TestItemSdt();
        parentItem.gxTpr_Amount = 100;
        parentItem.gxTpr_Label = "Row1";
        parent.gxTpr_Items.Add(parentItem);
        string parentJson = parent.ToJSonString();
        Console.WriteLine($"parent_to_json|=[{Escape(parentJson)}]");

        var parentRound = new TestParentSdt();
        bool okParent = parentRound.FromJSonString(parentJson);
        Console.WriteLine($"parent_from_json_ok|=[{okParent}]");
        Console.WriteLine($"parent_round_code|=[{Escape(parentRound.gxTpr_Code)}]");
        Console.WriteLine($"parent_round_child_amount|=[{parentRound.gxTpr_Child.gxTpr_Amount}]");
        Console.WriteLine($"parent_round_child_label|=[{Escape(parentRound.gxTpr_Child.gxTpr_Label)}]");
        Console.WriteLine($"parent_round_items_count|=[{parentRound.gxTpr_Items.Count}]");
        Console.WriteLine($"parent_round_item1_amount|=[{parentRound.gxTpr_Items[0].gxTpr_Amount}]");
        Console.WriteLine($"parent_round_item1_label|=[{Escape(parentRound.gxTpr_Items[0].gxTpr_Label)}]");

        // 7. Collection of SDTs ToJson and FromJson
        var col = new GXBaseCollection<TestItemSdt>();
        Console.WriteLine($"col_empty_to_json|=[{Escape(col.ToJSonString())}]");

        var item1 = new TestItemSdt();
        item1.gxTpr_Amount = 1;
        item1.gxTpr_Label = "One";
        var item2 = new TestItemSdt();
        item2.gxTpr_Amount = 2;
        item2.gxTpr_Label = "Two";
        col.Add(item1);
        col.Add(item2);
        string colJson = col.ToJSonString();
        Console.WriteLine($"col_to_json|=[{Escape(colJson)}]");

        var colRound = new GXBaseCollection<TestItemSdt>();
        bool okCol = colRound.FromJSonString(colJson);
        Console.WriteLine($"col_from_json_ok|=[{okCol}]");
        Console.WriteLine($"col_round_count|=[{colRound.Count}]");
        Console.WriteLine($"col_round_item1_amount|=[{colRound[0].gxTpr_Amount}]");
        Console.WriteLine($"col_round_item1_label|=[{Escape(colRound[0].gxTpr_Label)}]");
        Console.WriteLine($"col_round_item2_amount|=[{colRound[1].gxTpr_Amount}]");
        Console.WriteLine($"col_round_item2_label|=[{Escape(colRound[1].gxTpr_Label)}]");

        // 8. Collection invalid JSON
        bool okColEmpty = colRound.FromJSonString("");
        bool okColNotArray = colRound.FromJSonString("{\"Amount\":1}");
        Console.WriteLine($"col_from_json_empty|=[{okColEmpty}]");
        Console.WriteLine($"col_from_json_not_array|=[{okColNotArray}]");
    }

    private static string Escape(string s) => s.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    // Date-conversion rows encode spaces so trimming cases survive
    // whitespace-insensitive storage and editors.
    private static string EscapeSpaces(string s) => Escape(s).Replace(" ", "\\s");

    private static void GenerateSdtXml()
    {
        var context = new GxContext();

        // 1. Empty item ToXml
        var emptyItem = new TestItemSdt(context);
        string emptyItemXml = emptyItem.ToXml("TestItem");
        Console.WriteLine($"sdt_empty_to_xml|=[{Escape(emptyItemXml)}]");

        // 2. Filled item ToXml
        var item = new TestItemSdt(context);
        item.gxTpr_Amount = 42;
        item.gxTpr_Label = "Hello World";
        item.gxTpr_Fixed = "ABC";
        item.gxTpr_Flag = true;
        item.gxTpr_Day = new DateTime(2026, 3, 29);
        item.gxTpr_Stamp = new DateTime(2026, 3, 29, 14, 30, 45);
        string filledItemXml = item.ToXml("TestItem");
        Console.WriteLine($"sdt_filled_to_xml|=[{Escape(filledItemXml)}]");

        // 3. Special characters XML escaping (<, >, &, ', ")
        var specialItem = new TestItemSdt(context);
        specialItem.gxTpr_Amount = 10;
        specialItem.gxTpr_Label = "<xml & 'special' \"test\">";
        specialItem.gxTpr_Fixed = "X&Y";
        string specialItemXml = specialItem.ToXml("TestItem");
        Console.WriteLine($"sdt_special_to_xml|=[{Escape(specialItemXml)}]");

        // 4. FromXml roundtrip
        var itemRound = new TestItemSdt(context);
        bool okFromXml = itemRound.FromXml(filledItemXml, "TestItem");
        Console.WriteLine($"sdt_from_xml_roundtrip_ok|=[{okFromXml}]");
        Console.WriteLine($"sdt_round_amount|=[{itemRound.gxTpr_Amount}]");
        Console.WriteLine($"sdt_round_label|=[{Escape(itemRound.gxTpr_Label)}]");
        Console.WriteLine($"sdt_round_fixed|=[{Escape(itemRound.gxTpr_Fixed)}]");
        Console.WriteLine($"sdt_round_flag|=[{itemRound.gxTpr_Flag}]");
        Console.WriteLine($"sdt_round_day|=[{itemRound.gxTpr_Day:yyyy-MM-dd}]");
        Console.WriteLine($"sdt_round_stamp|=[{itemRound.gxTpr_Stamp:yyyy-MM-ddTHH:mm:ss}]");

        // 5. FromXml special characters roundtrip
        var specialRound = new TestItemSdt(context);
        bool okSpecial = specialRound.FromXml(specialItemXml, "TestItem");
        Console.WriteLine($"sdt_special_from_xml_ok|=[{okSpecial}]");
        Console.WriteLine($"sdt_special_round_label|=[{Escape(specialRound.gxTpr_Label)}]");
        Console.WriteLine($"sdt_special_round_fixed|=[{Escape(specialRound.gxTpr_Fixed)}]");

        // 6. Partial FromXml
        var partialItem = new TestItemSdt(context);
        partialItem.gxTpr_Amount = 5;
        partialItem.gxTpr_Label = "Initial";
        bool okPartial = partialItem.FromXml("<TestItem><Amount>99</Amount></TestItem>", "TestItem");
        Console.WriteLine($"sdt_from_xml_partial_ok|=[{okPartial}]");
        Console.WriteLine($"sdt_partial_amount|=[{partialItem.gxTpr_Amount}]");
        Console.WriteLine($"sdt_partial_label|=[{Escape(partialItem.gxTpr_Label)}]");

        // 7. Malformed / Mismatch / Empty FromXml
        var badItem = new TestItemSdt(context);
        bool okMalformed = badItem.FromXml("not xml", "TestItem");
        bool okMismatch = badItem.FromXml("<WrongRoot><Amount>1</Amount></WrongRoot>", "TestItem");
        bool okEmpty = badItem.FromXml("", "TestItem");
        Console.WriteLine($"sdt_from_xml_malformed|=[{okMalformed}]");
        Console.WriteLine($"sdt_from_xml_mismatch_root|=[{okMismatch}]");
        Console.WriteLine($"sdt_from_xml_empty|=[{okEmpty}]");

        // 8. Nested parent SDT
        var parent = new TestParentSdt(context);
        parent.gxTpr_Code = "P100";
        parent.gxTpr_Child.gxTpr_Amount = 55;
        parent.gxTpr_Child.gxTpr_Label = "ChildLabel";

        var childItem1 = new TestItemSdt(context);
        childItem1.gxTpr_Amount = 11;
        childItem1.gxTpr_Label = "FirstItem";
        var childItem2 = new TestItemSdt(context);
        childItem2.gxTpr_Amount = 22;
        childItem2.gxTpr_Label = "SecondItem";
        parent.gxTpr_Items.Add(childItem1);
        parent.gxTpr_Items.Add(childItem2);

        string parentXml = parent.ToXml("TestParent");
        Console.WriteLine($"parent_filled_to_xml|=[{Escape(parentXml)}]");

        var parentRound = new TestParentSdt(context);
        bool okParent = parentRound.FromXml(parentXml, "TestParent");
        Console.WriteLine($"parent_from_xml_ok|=[{okParent}]");
        Console.WriteLine($"parent_round_code|=[{Escape(parentRound.gxTpr_Code)}]");
        Console.WriteLine($"parent_round_child_amount|=[{parentRound.gxTpr_Child.gxTpr_Amount}]");
        Console.WriteLine($"parent_round_child_label|=[{Escape(parentRound.gxTpr_Child.gxTpr_Label)}]");
        Console.WriteLine($"parent_round_items_count|=[{parentRound.gxTpr_Items.Count}]");
        Console.WriteLine($"parent_round_item1_amount|=[{parentRound.gxTpr_Items[0].gxTpr_Amount}]");
        Console.WriteLine($"parent_round_item1_label|=[{Escape(parentRound.gxTpr_Items[0].gxTpr_Label)}]");
        Console.WriteLine($"parent_round_item2_amount|=[{parentRound.gxTpr_Items[1].gxTpr_Amount}]");
        Console.WriteLine($"parent_round_item2_label|=[{Escape(parentRound.gxTpr_Items[1].gxTpr_Label)}]");

        // 9. Collections ToXml & FromXml
        var col = new GXBaseCollection<TestItemSdt>(context, "TestItem", "");
        string emptyColXml = col.ToXml("TestItems");
        Console.WriteLine($"col_empty_to_xml|=[{Escape(emptyColXml)}]");

        col.Add(childItem1);
        col.Add(childItem2);
        string colXml = col.ToXml("TestItems");
        Console.WriteLine($"col_filled_to_xml|=[{Escape(colXml)}]");

        var colRound = new GXBaseCollection<TestItemSdt>(context, "TestItem", "");
        bool okCol = colRound.FromXml(colXml, "TestItems");
        Console.WriteLine($"col_from_xml_ok|=[{okCol}]");
        Console.WriteLine($"col_round_count|=[{colRound.Count}]");
        Console.WriteLine($"col_round_item1_amount|=[{colRound[0].gxTpr_Amount}]");
        Console.WriteLine($"col_round_item1_label|=[{Escape(colRound[0].gxTpr_Label)}]");
        Console.WriteLine($"col_round_item2_amount|=[{colRound[1].gxTpr_Amount}]");
        Console.WriteLine($"col_round_item2_label|=[{Escape(colRound[1].gxTpr_Label)}]");

        // 10. Collection empty FromXml and malformed
        var colEmptyRound = new GXBaseCollection<TestItemSdt>(context, "TestItem", "");
        bool okColEmpty = colEmptyRound.FromXml("<TestItems />", "TestItems");
        bool okColBad = colEmptyRound.FromXml("not xml", "TestItems");
        Console.WriteLine($"col_from_xml_empty|=[{okColEmpty}]");
        Console.WriteLine($"col_empty_round_count|=[{colEmptyRound.Count}]");
        Console.WriteLine($"col_from_xml_malformed|=[{okColBad}]");
    }
}

[XmlRoot(ElementName = "TestItem")]
[XmlType(TypeName = "TestItem", Namespace = "")]
[Serializable]
public class TestItemSdt : GxUserType
{
    private short gxTv_Amount;
    private string gxTv_Label = "";
    private string gxTv_Fixed = "";
    private bool gxTv_Flag;
    private DateTime gxTv_Day = DateTime.MinValue;
    private DateTime gxTv_Stamp = DateTime.MinValue;

    public TestItemSdt() { }
    public TestItemSdt(IGxContext? context) { this.context = context; }

    [XmlElement(ElementName = "Amount")]
    public short gxTpr_Amount { get => gxTv_Amount; set { gxTv_Amount = value; SetDirty("Amount"); } }

    [XmlElement(ElementName = "Day")]
    public DateTime gxTpr_Day { get => gxTv_Day; set { gxTv_Day = value; SetDirty("Day"); } }

    [XmlElement(ElementName = "Fixed")]
    public string gxTpr_Fixed { get => gxTv_Fixed; set { gxTv_Fixed = value; SetDirty("Fixed"); } }

    [XmlElement(ElementName = "Flag")]
    public bool gxTpr_Flag { get => gxTv_Flag; set { gxTv_Flag = value; SetDirty("Flag"); } }

    [XmlElement(ElementName = "Label")]
    public string gxTpr_Label { get => gxTv_Label; set { gxTv_Label = value; SetDirty("Label"); } }

    [XmlElement(ElementName = "Stamp")]
    public DateTime gxTpr_Stamp { get => gxTv_Stamp; set { gxTv_Stamp = value; SetDirty("Stamp"); } }

    public override void ToJSON(bool includeState)
    {
        AddObjectProperty("Amount", gxTpr_Amount, false);
        AddObjectProperty("Label", gxTpr_Label, false);
        AddObjectProperty("Fixed", gxTpr_Fixed, false);
        AddObjectProperty("Flag", gxTpr_Flag, false);

        string sDateCnv = $"{DateTimeUtil.Year(gxTpr_Day):D4}-{DateTimeUtil.Month(gxTpr_Day):D2}-{DateTimeUtil.Day(gxTpr_Day):D2}";
        AddObjectProperty("Day", sDateCnv, false);

        string sStampCnv = $"{DateTimeUtil.Year(gxTpr_Stamp):D4}-{DateTimeUtil.Month(gxTpr_Stamp):D2}-{DateTimeUtil.Day(gxTpr_Stamp):D2}T{DateTimeUtil.Hour(gxTpr_Stamp):D2}:{DateTimeUtil.Minute(gxTpr_Stamp):D2}:{DateTimeUtil.Second(gxTpr_Stamp):D2}";
        AddObjectProperty("Stamp", sStampCnv, false);
    }

    public override void ToJSON(bool includeState, bool includeNoInitialized)
    {
        ToJSON(includeState);
    }
}

[XmlRoot(ElementName = "TestParent")]
[XmlType(TypeName = "TestParent", Namespace = "")]
[Serializable]
public class TestParentSdt : GxUserType
{
    private string gxTv_Code = "";
    private TestItemSdt? gxTv_Child;
    private GXBaseCollection<TestItemSdt>? gxTv_Items;

    public TestParentSdt() { }
    public TestParentSdt(IGxContext? context) { this.context = context; }

    [XmlElement(ElementName = "Child")]
    public TestItemSdt gxTpr_Child { get => gxTv_Child ??= new TestItemSdt(context); set { gxTv_Child = value; SetDirty("Child"); } }

    [XmlElement(ElementName = "Code")]
    public string gxTpr_Code { get => gxTv_Code; set { gxTv_Code = value; SetDirty("Code"); } }

    [XmlArray(ElementName = "Items")]
    [XmlArrayItem(ElementName = "TestItem", IsNullable = false)]
    public GXBaseCollection<TestItemSdt> gxTpr_Items { get => gxTv_Items ??= new GXBaseCollection<TestItemSdt>(context, "TestItem", ""); set { gxTv_Items = value; SetDirty("Items"); } }

    public override void ToJSON(bool includeState)
    {
        AddObjectProperty("Code", gxTpr_Code, false);
        if (gxTv_Child != null) AddObjectProperty("Child", gxTv_Child, false);
        if (gxTv_Items != null) AddObjectProperty("Items", gxTv_Items, false);
    }

    public override void ToJSON(bool includeState, bool includeNoInitialized)
    {
        ToJSON(includeState);
    }
}

