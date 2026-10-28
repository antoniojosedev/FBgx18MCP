using System.Collections.Generic;
using System.Globalization;

namespace GxMcp.Gateway
{
    /// <summary>The two command-line tokens of a gx-engine integer flag, formatted culture-independently.</summary>
    internal static class EngineIntFlag
    {
        internal static IEnumerable<string> Tokens(string flag, int value) =>
            new[] { flag, value.ToString(CultureInfo.InvariantCulture) };
    }
}
