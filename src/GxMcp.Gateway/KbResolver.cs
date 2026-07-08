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
            => Resolve(kbArg, openKbs, null);

        // issue #26 P3: `knownKbs` (optional) is the durable set of aliases the user has
        // opened this session — it survives worker recycles, unlike `openKbs`. An explicit
        // alias is matched against declared → open → known → path, so a KB whose worker is
        // momentarily down stays resolvable instead of failing with "Unknown KB". The
        // empty-arg (no kb passed) ambiguity/default logic still uses only `openKbs`.
        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs, IReadOnlyCollection<KbHandle>? knownKbs)
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

            // issue #26 P3: fall back to the durable known set (survives worker recycle).
            if (knownKbs != null)
            {
                var knownMatch = knownKbs.FirstOrDefault(
                    k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
                if (knownMatch != null) return knownMatch;
            }

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
