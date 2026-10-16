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

        if (args.Length < 3 || args[0] != "bench-str")
        {
            Console.Error.WriteLine("usage: sdk-oracle bench-str <corpus.tsv> <iterations> | generate-text-extended");
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

    private static string Escape(string s) => s.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
}
