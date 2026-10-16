using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(args[0]))
    ?? throw new InvalidDataException("case file must contain an array");
var results = cases.Select(Run).ToArray();
Console.WriteLine(JsonSerializer.Serialize(results));

static Result Run(Case item)
{
    try
    {
        var left = decimal.Parse(item.Left, NumberStyles.Number, CultureInfo.InvariantCulture);
        var right = decimal.Parse(item.Right, NumberStyles.Number, CultureInfo.InvariantCulture);
        var value = item.Operation switch
        {
            "add" => left + right,
            "sub" => left - right,
            "mul" => left * right,
            "div" => left / right,
            _ => throw new InvalidDataException($"unknown operation: {item.Operation}")
        };
        var bits = decimal.GetBits(value);
        return new Result(item.Id, true, value.ToString(CultureInfo.InvariantCulture),
            bits[0], bits[1], bits[2], bits[3], null);
    }
    catch (Exception error) when (error is OverflowException or DivideByZeroException)
    {
        return new Result(item.Id, false, null, null, null, null, null, error.GetType().Name);
    }
}

internal sealed record Case(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("left")] string Left,
    [property: JsonPropertyName("right")] string Right);

internal sealed record Result(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("lo")] int? Lo,
    [property: JsonPropertyName("mid")] int? Mid,
    [property: JsonPropertyName("hi")] int? Hi,
    [property: JsonPropertyName("flags")] int? Flags,
    [property: JsonPropertyName("error")] string? Error);
