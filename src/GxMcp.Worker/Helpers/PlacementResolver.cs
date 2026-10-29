using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// One Folder or Module of the KB Explorer tree. Immutable; <see cref="PlacementCatalog"/>
    /// fills <see cref="Path"/> from the parent chain.
    /// </summary>
    internal sealed class PlacementContainer
    {
        public const string FolderKind = "Folder";
        public const string ModuleKind = "Module";

        public PlacementContainer(Guid guid, string name, string kind, Guid? parentGuid, string path = null)
        {
            Guid = guid;
            Name = name;
            Kind = kind;
            ParentGuid = parentGuid;
            Path = path;
        }

        public Guid Guid { get; }
        public string Name { get; }
        /// <summary><c>Folder</c> or <c>Module</c>.</summary>
        public string Kind { get; }
        /// <summary>The enclosing container; null when the container sits directly under the Root Module.</summary>
        public Guid? ParentGuid { get; }
        /// <summary><c>Root Module/&lt;container&gt;/.../&lt;Name&gt;</c>.</summary>
        public string Path { get; }

        internal PlacementContainer With(string path, Guid? parentGuid)
            => new PlacementContainer(Guid, Name, Kind, parentGuid, path);

        internal JObject ToJson() => new JObject
        {
            ["guid"] = Guid.ToString(),
            ["name"] = Name,
            ["kind"] = Kind,
            ["path"] = Path
        };
    }

    /// <summary>The Folder/Module tree of a KB, resolved once so every caller sees the same answer.</summary>
    internal sealed class PlacementCatalog
    {
        internal const string RootName = "Root Module";

        private readonly List<PlacementContainer> _containers;
        private readonly Dictionary<Guid, PlacementContainer> _byGuid;

        public PlacementCatalog(IEnumerable<PlacementContainer> containers)
        {
            var raw = (containers ?? Enumerable.Empty<PlacementContainer>())
                .Where(c => c != null && c.Guid != Guid.Empty && !string.IsNullOrWhiteSpace(c.Name))
                .GroupBy(c => c.Guid)
                .Select(g => g.First())
                .ToList();
            var rawByGuid = raw.ToDictionary(c => c.Guid);
            _containers = raw
                .Select(c => c.With(ComposePath(c, rawByGuid), c.ParentGuid.HasValue && rawByGuid.ContainsKey(c.ParentGuid.Value) && c.ParentGuid.Value != c.Guid ? c.ParentGuid : null))
                .ToList();
            _byGuid = _containers.ToDictionary(c => c.Guid);
        }

        public IReadOnlyList<PlacementContainer> Containers => _containers;

        public PlacementContainer Get(Guid guid) => _byGuid.TryGetValue(guid, out var found) ? found : null;

        public IEnumerable<PlacementContainer> ChildrenOf(Guid? parent)
            => _containers.Where(c => c.ParentGuid == parent);

        /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or sits anywhere below it.</summary>
        public bool IsSelfOrDescendant(Guid candidate, Guid ancestor)
        {
            var seen = new HashSet<Guid>();
            Guid? current = candidate;
            while (current.HasValue && seen.Add(current.Value))
            {
                if (current.Value == ancestor) return true;
                current = _byGuid.TryGetValue(current.Value, out var node) ? node.ParentGuid : null;
            }
            return false;
        }

        private static string ComposePath(PlacementContainer container, IReadOnlyDictionary<Guid, PlacementContainer> byGuid)
        {
            var names = new List<string> { container.Name };
            var seen = new HashSet<Guid> { container.Guid };
            Guid? parent = container.ParentGuid;
            while (parent.HasValue && byGuid.TryGetValue(parent.Value, out var node) && seen.Add(node.Guid))
            {
                names.Insert(0, node.Name);
                parent = node.ParentGuid;
            }
            return RootName + "/" + string.Join("/", names);
        }
    }

    internal enum PlacementStatus
    {
        Resolved,
        Root,
        NotFound,
        Ambiguous,
        KindMismatch,
        Invalid
    }

    /// <summary>What a destination text names in the KB Explorer tree, or why it names nothing usable.</summary>
    internal sealed class PlacementResolution
    {
        public PlacementStatus Status { get; internal set; }
        public string Requested { get; internal set; }
        public string RequestedKind { get; internal set; }
        public PlacementContainer Container { get; internal set; }
        public IReadOnlyList<PlacementContainer> Candidates { get; internal set; } = Array.Empty<PlacementContainer>();
        public string FailedSegment { get; internal set; }
        public string ResolvedPrefix { get; internal set; }
        public IReadOnlyList<string> Available { get; internal set; } = Array.Empty<string>();
        public string ResolvedKind { get; internal set; }
        public string Message { get; internal set; }
        public string Hint { get; internal set; }

        public bool IsRoot => Status == PlacementStatus.Root;
        public bool Ok => Status == PlacementStatus.Resolved || Status == PlacementStatus.Root;

        /// <summary>The error code both genexus_properties move and genexus_create object_atomic report.</summary>
        public string Code
        {
            get
            {
                switch (Status)
                {
                    case PlacementStatus.NotFound: return "DestinationNotFound";
                    case PlacementStatus.Ambiguous: return "DestinationAmbiguous";
                    case PlacementStatus.KindMismatch: return "DestinationKindMismatch";
                    case PlacementStatus.Invalid: return "DestinationInvalid";
                    default: return null;
                }
            }
        }

        public string ResolvedPath => IsRoot ? PlacementCatalog.RootName : Container?.Path;

        /// <summary>The destination as a receipt reports it.</summary>
        public JObject Describe()
        {
            var json = new JObject
            {
                ["requested"] = Requested,
                ["kind"] = RequestedKind
            };
            if (IsRoot)
            {
                json["resolved"] = new JObject { ["kind"] = "Root", ["name"] = PlacementCatalog.RootName, ["path"] = PlacementCatalog.RootName };
            }
            else if (Container != null)
            {
                json["resolved"] = Container.ToJson();
            }
            return json;
        }

        /// <summary>The diagnostic block of a refused destination (goes under <c>error.placement</c>).</summary>
        public JObject ToDiagnostic()
        {
            var json = new JObject { ["requested"] = Requested, ["requestedKind"] = RequestedKind };
            if (!string.IsNullOrEmpty(FailedSegment)) json["failedSegment"] = FailedSegment;
            if (!string.IsNullOrEmpty(ResolvedPrefix)) json["resolvedPrefix"] = ResolvedPrefix;
            if (!string.IsNullOrEmpty(ResolvedKind)) json["resolvedKind"] = ResolvedKind;
            if (Available.Count > 0) json["availableChildren"] = new JArray(Available);
            if (Candidates.Count > 0) json["candidates"] = new JArray(Candidates.Select(c => (JToken)c.ToJson()));
            return json;
        }
    }

    /// <summary>
    /// Resolves the text a caller typed as a destination (a path, a bare name or a GUID) to one Folder
    /// or Module. A single implementation: the dry-run of a move, the move itself and the placement of an
    /// atomic create all call it, so a preview can never accept a destination the write resolves differently.
    /// </summary>
    internal static class PlacementResolver
    {
        private const int MaxListed = 25;

        public static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return null;
            string value = kind.Trim();
            if (value.Equals(PlacementContainer.FolderKind, StringComparison.OrdinalIgnoreCase)) return PlacementContainer.FolderKind;
            if (value.Equals(PlacementContainer.ModuleKind, StringComparison.OrdinalIgnoreCase)) return PlacementContainer.ModuleKind;
            return value;
        }

        /// <summary>Splits a typed path; the leading <c>Root Module</c> segment is reported through <paramref name="rooted"/>.</summary>
        internal static List<string> SplitSegments(string raw, out bool rooted)
        {
            rooted = false;
            var segments = (raw ?? string.Empty).Replace('\\', '/')
                .Split('/')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (segments.Count > 0 && segments[0].Equals(PlacementCatalog.RootName, StringComparison.OrdinalIgnoreCase))
            {
                rooted = true;
                segments.RemoveAt(0);
            }
            return segments;
        }

        public static PlacementResolution Resolve(string requested, string kind, PlacementCatalog catalog)
        {
            string wantedKind = NormalizeKind(kind);
            var result = new PlacementResolution { Requested = requested, RequestedKind = wantedKind };
            catalog = catalog ?? new PlacementCatalog(null);

            if (string.IsNullOrWhiteSpace(requested))
                return Invalid(result, "The destination is empty.", "Pass a Folder or Module name, its path (Root Module/<Module>/<Folder>) or its GUID.");
            if (wantedKind != null && wantedKind != PlacementContainer.FolderKind && wantedKind != PlacementContainer.ModuleKind)
                return Invalid(result, "destKind '" + kind + "' is not Folder or Module.", "Use destKind=Folder or destKind=Module, or omit it.");

            string text = requested.Trim();
            if (Guid.TryParse(text, out var byGuid)) return ResolveGuid(result, byGuid, wantedKind, catalog);

            var segments = SplitSegments(text, out bool rooted);
            if (segments.Count == 0)
            {
                // "Root Module" on its own names the root of the tree, which is not a container object.
                if (wantedKind != null)
                    return Invalid(result, "The Root Module is not a " + wantedKind + ".", "Omit destKind to place the object in the Root Module.");
                result.Status = PlacementStatus.Root;
                return result;
            }

            // "Module.Folder" is accepted when no container is literally named that way.
            if (!rooted && segments.Count == 1 && segments[0].IndexOf('.') > 0
                && !catalog.Containers.Any(c => c.Name.Equals(segments[0], StringComparison.OrdinalIgnoreCase)))
            {
                segments = segments[0].Split('.').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }

            return rooted || segments.Count > 1
                ? ResolvePath(result, segments, wantedKind, catalog)
                : ResolveBareName(result, segments[0], wantedKind, catalog);
        }

        private static PlacementResolution ResolveGuid(PlacementResolution result, Guid guid, string wantedKind, PlacementCatalog catalog)
        {
            var found = catalog.Get(guid);
            if (found == null)
            {
                result.Status = PlacementStatus.NotFound;
                result.FailedSegment = guid.ToString();
                result.Message = "No Folder or Module has the GUID " + guid + ".";
                result.Hint = "Use the guid of a Folder/Module returned by genexus_query type:Folder or type:Module.";
                return result;
            }
            return Accept(result, found, wantedKind);
        }

        private static PlacementResolution ResolvePath(PlacementResolution result, List<string> segments, string wantedKind, PlacementCatalog catalog)
        {
            Guid? parent = null;
            string prefix = PlacementCatalog.RootName;
            PlacementContainer current = null;
            for (int i = 0; i < segments.Count; i++)
            {
                bool last = i == segments.Count - 1;
                string segment = segments[i];
                var children = catalog.ChildrenOf(parent).ToList();
                var named = children.Where(c => c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 0)
                {
                    result.Status = PlacementStatus.NotFound;
                    result.FailedSegment = segment;
                    result.ResolvedPrefix = prefix;
                    result.Available = children.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(MaxListed).ToList();
                    result.Message = "'" + segment + "' is not a Folder or Module under '" + prefix + "'"
                        + (result.Available.Count > 0 ? " (it holds: " + string.Join(", ", result.Available) + ")." : " (it holds no Folder or Module).");
                    result.Hint = "Create the missing container first (genexus_create type=Folder|Module) or fix the path.";
                    return result;
                }

                if (last && wantedKind != null)
                {
                    var ofKind = named.Where(c => c.Kind == wantedKind).ToList();
                    if (ofKind.Count == 0) return KindMismatch(result, named[0], wantedKind);
                    named = ofKind;
                }

                if (named.Count > 1) return Ambiguous(result, named, segment);
                current = named[0];
                parent = current.Guid;
                prefix = current.Path;
            }
            result.Status = PlacementStatus.Resolved;
            result.Container = current;
            return result;
        }

        private static PlacementResolution ResolveBareName(PlacementResolution result, string name, string wantedKind, PlacementCatalog catalog)
        {
            var named = catalog.Containers.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
            {
                result.Status = PlacementStatus.NotFound;
                result.FailedSegment = name;
                result.ResolvedPrefix = PlacementCatalog.RootName;
                result.Available = catalog.ChildrenOf(null).Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(MaxListed).ToList();
                result.Message = "No Folder or Module named '" + name + "' in this KB.";
                result.Hint = "Create it first (genexus_create type=Folder|Module name=" + name + "), or pass its path (Root Module/<Module>/<Folder>).";
                return result;
            }
            if (wantedKind != null)
            {
                var ofKind = named.Where(c => c.Kind == wantedKind).ToList();
                if (ofKind.Count == 0) return KindMismatch(result, named[0], wantedKind);
                named = ofKind;
            }
            if (named.Count > 1) return Ambiguous(result, named, name);
            return Accept(result, named[0], wantedKind);
        }

        private static PlacementResolution Accept(PlacementResolution result, PlacementContainer container, string wantedKind)
        {
            if (wantedKind != null && container.Kind != wantedKind) return KindMismatch(result, container, wantedKind);
            result.Status = PlacementStatus.Resolved;
            result.Container = container;
            return result;
        }

        private static PlacementResolution KindMismatch(PlacementResolution result, PlacementContainer found, string wantedKind)
        {
            result.Status = PlacementStatus.KindMismatch;
            result.Container = found;
            result.ResolvedKind = found.Kind;
            result.Message = "'" + found.Path + "' is a " + found.Kind + ", not a " + wantedKind + ".";
            result.Hint = "Drop destKind (or use destKind=" + found.Kind + ") to place the object there.";
            return result;
        }

        private static PlacementResolution Ambiguous(PlacementResolution result, List<PlacementContainer> candidates, string text)
        {
            result.Status = PlacementStatus.Ambiguous;
            result.FailedSegment = text;
            result.Candidates = candidates
                .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxListed)
                .ToList();
            result.Message = "'" + text + "' matches " + candidates.Count + " Folders/Modules: "
                + string.Join(", ", result.Candidates.Select(c => c.Path + " (" + c.Kind + ")")) + ".";
            result.Hint = "Pass the full path (Root Module/<Module>/<Folder>), the container guid, or destKind=Folder|Module.";
            return result;
        }

        private static PlacementResolution Invalid(PlacementResolution result, string message, string hint)
        {
            result.Status = PlacementStatus.Invalid;
            result.Message = message;
            result.Hint = hint;
            return result;
        }
    }
}
