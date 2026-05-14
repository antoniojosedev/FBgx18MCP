using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.Gateway
{
    public sealed class KbResolutionException : Exception
    {
        public string Code { get; }
        public KbResolutionException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class KbResolver
    {
        private readonly Configuration _config;

        public KbResolver(Configuration config) { _config = config; }

        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs)
        {
            if (string.IsNullOrWhiteSpace(kbArg))
            {
                if (openKbs.Count == 1) return openKbs.First();
                if (openKbs.Count == 0)
                {
                    var def = _config.Environment?.DefaultKb;
                    if (!string.IsNullOrWhiteSpace(def))
                    {
                        var entry = _config.Environment!.KBs.FirstOrDefault(
                            k => string.Equals(k.Alias, def, StringComparison.OrdinalIgnoreCase));
                        if (entry == null)
                        {
                            throw new KbResolutionException("KB_NOT_FOUND",
                                $"DefaultKb '{def}' not declared in Environment.KBs[]");
                        }

                        return new KbHandle(entry.Alias, entry.Path);
                    }

                    // No default and no open KBs: fall back to first declared KB if any.
                    var first = _config.Environment?.KBs?.FirstOrDefault();
                    if (first != null) return new KbHandle(first.Alias, first.Path);

                    throw new KbResolutionException("KB_AMBIGUOUS",
                        "No 'kb' parameter, no DefaultKb configured, and no KB currently open.");
                }

                throw new KbResolutionException("KB_AMBIGUOUS",
                    $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter is required.");
            }

            var declared = _config.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return new KbHandle(declared.Alias, declared.Path);

            var openMatch = openKbs.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (openMatch != null) return openMatch;

            if (Path.IsPathRooted(kbArg) && Directory.Exists(kbArg))
            {
                string alias = Path.GetFileName(kbArg.TrimEnd('\\', '/')).ToLowerInvariant();
                if (string.IsNullOrEmpty(alias)) alias = "adhoc";
                return new KbHandle(alias, kbArg);
            }

            throw new KbResolutionException("KB_NOT_FOUND",
                $"Unknown KB '{kbArg}'. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.");
        }
    }
}
