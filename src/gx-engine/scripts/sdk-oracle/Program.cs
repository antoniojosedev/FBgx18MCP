using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        if (args.Length < 3 || args[0] != "bench-str")
        {
            Console.Error.WriteLine("usage: sdk-oracle bench-str <corpus.tsv> <iterations> | generate-text-extended | generate-numeric-math | generate-collection-ops | generate-calendar-ops");
            return 2;
        }

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

    private static string Escape(string s) => s.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
}
