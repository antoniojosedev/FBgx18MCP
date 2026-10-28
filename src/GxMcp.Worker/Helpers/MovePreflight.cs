using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>A named reason a move cannot be applied, with the next legal action.</summary>
    internal sealed class MoveBlocker
    {
        public MoveBlocker(string code, string message, string remedy)
        {
            Code = code;
            Message = message;
            Remedy = remedy;
        }

        public string Code { get; }
        public string Message { get; }
        public string Remedy { get; }

        internal JObject ToJson() => new JObject { ["code"] = Code, ["message"] = Message, ["remedy"] = Remedy };
    }

    /// <summary>An object already holding the moved object's name inside the destination's module.</summary>
    internal sealed class MoveNameConflict
    {
        public Guid Guid { get; set; }
        public string Path { get; set; }
        public string QualifiedName { get; set; }
    }

    /// <summary>
    /// Everything a move decides before it writes, already read from the KB. Pure data, so the
    /// preview and the write evaluate exactly the same facts with exactly the same code.
    /// </summary>
    internal sealed class MoveFacts
    {
        public bool DryRun { get; set; }
        public string BaseVersion { get; set; }
        public string CurrentVersion { get; set; }
        public string ObjectName { get; set; }
        /// <summary>The object's current container; null when it sits directly under the Root Module.</summary>
        public Guid? CurrentParentGuid { get; set; }
        public PlacementResolution Destination { get; set; }
        /// <summary>The destination is the moved object itself or sits somewhere below it.</summary>
        public bool DestinationInsideObject { get; set; }
        public MoveNameConflict NameConflict { get; set; }
        public bool PersistStrategyAvailable { get; set; } = true;
        /// <summary>The moved object is an imported (interface) object: the SDK treats it as read-only.</summary>
        public bool ObjectIsImported { get; set; }
        /// <summary>The path of an imported (referenced) module the destination sits in, else null.</summary>
        public string DestinationImportedPath { get; set; }
        /// <summary>The moved object is a Folder/Module that holds other objects.</summary>
        public bool ObjectHoldsChildren { get; set; }
        /// <summary>The destination belongs to another module than the object's current one.</summary>
        public bool CrossesModules { get; set; }
    }

    internal sealed class MoveVerdict
    {
        public List<MoveBlocker> Blockers { get; } = new List<MoveBlocker>();
        /// <summary>The object already is in the destination: the move would write nothing.</summary>
        public bool AlreadyInDestination { get; set; }

        public bool CanApply => Blockers.Count == 0;
        public MoveBlocker First => Blockers.FirstOrDefault();

        internal JArray BlockersJson() => new JArray(Blockers.Select(b => (JToken)b.ToJson()));
    }

    /// <summary>
    /// The checks that decide, before any write, whether <c>genexus_properties action=move</c> can run.
    /// <c>dryRun=true</c> and the real move both call <see cref="Evaluate"/>: a preview refuses what the
    /// write would refuse, with the same code, and the only difference is the version token a write requires.
    /// </summary>
    internal static class MovePreflight
    {
        internal const string BaseVersionRequiredCode = "BaseVersionRequired";
        internal const string VersionConflictCode = "VersionConflict";
        internal const string InsideMovedObjectCode = "DestinationInsideMovedObject";
        internal const string NameConflictCode = "DestinationNameConflict";
        internal const string StrategyUnavailableCode = "MoveStrategyUnavailable";
        internal const string RootUnsupportedCode = "DestinationRootUnsupported";
        internal const string ObjectReadOnlyCode = "ObjectReadOnly";
        internal const string ContainerAcrossModulesCode = "ContainerMoveAcrossModules";
        internal const string DestinationReadOnlyCode = "DestinationReadOnly";

        /// <summary>The refusal both a move and an atomic create make for a destination inside an imported module.</summary>
        internal static MoveBlocker DestinationReadOnly(string path)
            => new MoveBlocker(DestinationReadOnlyCode,
                "'" + path + "' belongs to an imported (referenced) module: the SDK validation refuses objects placed in it, so nothing was written.",
                "Choose a Folder or Module of this KB's own, outside the imported module.");

        public static MoveVerdict Evaluate(MoveFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            var verdict = new MoveVerdict();

            bool hasToken = !string.IsNullOrWhiteSpace(facts.BaseVersion);
            if (!facts.DryRun && !hasToken)
            {
                verdict.Blockers.Add(new MoveBlocker(BaseVersionRequiredCode,
                    "A move needs baseVersion, the versionToken of the object as you last read it; none was supplied, so nothing was moved.",
                    "Read the object (genexus_properties action=get, genexus_read) or run this move with dryRun=true, and pass its versionToken as baseVersion."));
            }
            else if (hasToken && !string.Equals(facts.BaseVersion, facts.CurrentVersion, StringComparison.Ordinal))
            {
                verdict.Blockers.Add(new MoveBlocker(VersionConflictCode,
                    "The object changed after it was read; the move was not attempted.",
                    "Re-read the object and retry with the new versionToken."));
            }

            var destination = facts.Destination;
            verdict.AlreadyInDestination = destination != null && destination.Ok
                && (destination.IsRoot
                    ? !facts.CurrentParentGuid.HasValue
                    : facts.CurrentParentGuid.HasValue && facts.CurrentParentGuid.Value == destination.Container.Guid);

            // Nothing is written when the object already is where it was asked to be, so the checks that
            // only guard a write (read-only, name clash, persist path) do not apply to that case.
            if (!verdict.AlreadyInDestination && facts.ObjectIsImported)
            {
                verdict.Blockers.Add(new MoveBlocker(ObjectReadOnlyCode,
                    "'" + facts.ObjectName + "' is an imported (read-only) object; the SDK does not let it be moved.",
                    "Change it in the module it was imported from, then import that module again."));
            }

            if (!verdict.AlreadyInDestination && !string.IsNullOrEmpty(facts.DestinationImportedPath))
                verdict.Blockers.Add(DestinationReadOnly(facts.DestinationImportedPath));

            if (facts.DestinationInsideObject)
            {
                verdict.Blockers.Add(new MoveBlocker(InsideMovedObjectCode,
                    "'" + facts.ObjectName + "' cannot be moved into itself or into one of its own descendants.",
                    "Choose a destination outside '" + facts.ObjectName + "'."));
            }

            if (destination != null && destination.IsRoot && facts.CurrentParentGuid.HasValue)
            {
                verdict.Blockers.Add(new MoveBlocker(RootUnsupportedCode,
                    "Moving an object back to the Root Module is not available through the SDK path: the Root Module is not a container object that can be re-parented to.",
                    "Move it back in the GeneXus IDE (KB Explorer > Move), or choose a Folder/Module as the destination."));
            }

            if (!verdict.AlreadyInDestination && facts.NameConflict != null)
            {
                string where = facts.NameConflict.Path ?? facts.NameConflict.QualifiedName ?? "the destination";
                verdict.Blockers.Add(new MoveBlocker(NameConflictCode,
                    "'" + (facts.NameConflict.QualifiedName ?? facts.ObjectName) + "' already exists in the destination (" + where + "); the SDK refuses two objects with one name in a module.",
                    "Rename one of them, or choose another destination."));
            }

            if (!verdict.AlreadyInDestination && facts.ObjectHoldsChildren && facts.CrossesModules)
            {
                verdict.Blockers.Add(new MoveBlocker(ContainerAcrossModulesCode,
                    "'" + facts.ObjectName + "' holds objects and the move would put it in another module. The database re-qualifies the objects inside, but the running Worker keeps the copies it had loaded with the old module until it is reloaded, and a later save from such a copy could write the old module back.",
                    "Move '" + facts.ObjectName + "' in the GeneXus IDE (KB Explorer > Move), into a Folder of the same module, or move its objects one by one."));
            }

            if (!verdict.AlreadyInDestination && !facts.PersistStrategyAvailable)
            {
                verdict.Blockers.Add(new MoveBlocker(StrategyUnavailableCode,
                    "This GeneXus SDK exposes no supported way to persist a new parent (EntityManager.UpdateParent / SaveWithParent / Save), so the move cannot be done through the SDK.",
                    "Move the object in the GeneXus IDE (KB Explorer > Move), or use an SDK build that exposes the parent persistence."));
            }

            return verdict;
        }
    }
}
