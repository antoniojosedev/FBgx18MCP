using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Persists an object's folder/module placement (its KB Explorer parent).
    ///
    /// History: issues #30/#50 concluded folder placement was unsupported because
    /// decompiling <c>Artech.Architecture.Common.dll</c> showed <c>KBObject.set_Parent</c>
    /// as an empty body. That DLL is a FACADE/reference assembly — every member is a
    /// stub (getters return null, setters empty) and the real implementation binds at
    /// runtime from elsewhere. The empty setter proves nothing about runtime behaviour.
    ///
    /// The IDE moves an object by setting its parent and persisting via the low-level
    /// Udm EntityManager. This helper mirrors that: set <c>Parent</c>, then call
    /// <c>EntityManager.SaveWithParent(entity, parentEntity, prefs)</c> (which passes the
    /// parent explicitly, so it does not depend on the property setter), falling back to
    /// <c>UpdateParent(entity, prefs)</c> and finally <c>obj.Save()</c>. The caller must
    /// re-read the parent afterwards to confirm the move actually persisted.
    /// </summary>
    internal static class ObjectMover
    {
        public struct MoveResult
        {
            public bool Ok;
            public string Strategy;   // which persist path was taken
            public string Error;      // populated when Ok == false
            // Every persist path that was tried and what it answered, in order. A refused
            // move reports it, so the caller sees which SDK path said no and why.
            public IReadOnlyList<string> Attempts;
        }

        /// <summary>
        /// Issue #712: true when the re-read parent is the container the move resolved.
        /// The destination may be a path ("Root Module/A/B") that FindObject resolves, so
        /// the persisted parent is matched by the container's identity first; the legacy
        /// name comparison (bare names, a Root Module surfaced as DesignModel) still applies.
        /// </summary>
        internal static bool IsRequestedParent(Guid? actualParentGuid, string actualParentName,
            Guid containerGuid, string destination)
        {
            if (actualParentGuid.HasValue && actualParentGuid.Value != Guid.Empty
                && actualParentGuid.Value == containerGuid)
                return true;
            return !string.IsNullOrWhiteSpace(actualParentName)
                && string.Equals(actualParentName.Trim(), (destination ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Set <paramref name="obj"/>'s parent to <paramref name="container"/> and persist.
        /// Both are KBObject instances (folder/module are KBObjects that derive from the
        /// Udm Entity type). Does NOT verify persistence - the caller re-reads the parent.
        /// </summary>
        public static MoveResult SetParentAndSave(object obj, object container)
        {
            var attempts = new List<string>();
            if (obj == null) return Failed("object is null", attempts);
            if (container == null) return Failed("destination container is null", attempts);

            // 1. Set the in-memory parent (works at runtime; the facade-DLL no-op is a
            //    decompilation artefact). Best-effort - SaveWithParent also passes the
            //    parent explicitly, so this is belt-and-suspenders.
            string setError = TrySetParent(obj, container);
            attempts.Add("KBObject.Parent = " + (setError == null ? "set" : "refused (" + setError + ")"));

            // 2. Resolve the Udm EntityManager (same lookup WebFormSaveDiagnostics uses).
            Type emType = FindEntityManagerType();
            if (emType == null)
                return Failed("EntityManager type not found in loaded assemblies", attempts);

            object prefs = BuildSavePreferences();

            // 3a. UpdateParent changes only placement metadata. Prefer it over
            // SaveWithParent: GeneXus 18 U16 can rebuild default Procedure parts during
            // SaveWithParent, emptying Source/Rules/Variables on a freshly-created object.
            var up = FindMethod(emType, "UpdateParent", obj);
            if (up != null)
            {
                var inv = InvokeSave(emType, up, BuildArgs(up, obj, null, prefs));
                if (inv == null)
                {
                    attempts.Add("EntityManager.UpdateParent = ok");
                    return new MoveResult { Ok = true, Strategy = "EntityManager.UpdateParent", Attempts = attempts };
                }
                attempts.Add("EntityManager.UpdateParent = " + Describe(inv));
            }
            else attempts.Add("EntityManager.UpdateParent = not available in this SDK");

            // 3b. SaveWithParent(entity, parentEntity[, prefs]) - compatibility fallback
            // for SDK builds without a usable UpdateParent overload. The caller snapshots
            // and verifies every part, so any destructive normalization is rejected.
            var swp = FindMethod(emType, "SaveWithParent", obj, container);
            if (swp != null)
            {
                var inv = InvokeSave(emType, swp, BuildArgs(swp, obj, container, prefs));
                if (inv == null)
                {
                    attempts.Add("EntityManager.SaveWithParent = ok");
                    return new MoveResult { Ok = true, Strategy = "EntityManager.SaveWithParent", Attempts = attempts };
                }
                attempts.Add("EntityManager.SaveWithParent = " + Describe(inv));
            }
            else attempts.Add("EntityManager.SaveWithParent = not available in this SDK");

            // 3c. Last resort: plain Save on the object (some SDK builds persist the
            //     parent as part of a normal header save once Parent is set).
            try
            {
                var save = obj.GetType().GetMethod("Save", Type.EmptyTypes);
                if (save != null)
                {
                    save.Invoke(obj, null);
                    attempts.Add("KBObject.Save = ok");
                    return new MoveResult { Ok = true, Strategy = "KBObject.Save", Attempts = attempts };
                }
                attempts.Add("KBObject.Save = not available");
            }
            catch (Exception ex)
            {
                attempts.Add("KBObject.Save = " + Describe(Unwrap(ex)));
                return Failed("obj.Save() threw: " + Unwrap(ex).Message, attempts);
            }

            return Failed("no persist path (SaveWithParent/UpdateParent/Save) resolved on " + emType.FullName, attempts);
        }

        /// <summary>
        /// True when this SDK exposes at least one way to persist a new parent. Resolves the
        /// persist path without calling it, so a dry run can tell a move that cannot work
        /// from one that merely has not been tried.
        /// </summary>
        public static bool CanPersistParent(object obj, object container, out string detail)
        {
            detail = null;
            Type emType = FindEntityManagerType();
            if (emType == null)
            {
                detail = "EntityManager type not found in loaded assemblies";
                return false;
            }
            if (obj != null && FindMethod(emType, "UpdateParent", obj, null, logBinding: false) != null) return true;
            if (obj != null && container != null && FindMethod(emType, "SaveWithParent", obj, container, logBinding: false) != null) return true;
            if (obj != null && obj.GetType().GetMethod("Save", Type.EmptyTypes) != null) return true;
            detail = "no UpdateParent/SaveWithParent/Save path resolved on " + emType.FullName;
            return false;
        }

        /// <summary>
        /// Sets the in-memory parent of <paramref name="obj"/>. Returns null when the setter accepted
        /// the container, else why not. Used by object creation to place a new object before its first save.
        /// </summary>
        public static string TrySetParent(object obj, object container)
        {
            try
            {
                var p = obj.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return "the object has no writable Parent";
                if (!p.PropertyType.IsInstanceOfType(container)) return "the destination is not a " + p.PropertyType.Name;
                p.SetValue(obj, container, null);
                return null;
            }
            catch (Exception ex)
            {
                string reason = Unwrap(ex).Message;
                Logger.Info("[Move] set_Parent threw: " + reason);
                return reason;
            }
        }

        // ---- internals -------------------------------------------------------

        // Plan 072: reflection enumerates methods in an UNSPECIFIED order, so the former
        // "first match wins" could bind a different SaveWithParent/UpdateParent overload
        // across runs (a 2-arg vs 3-arg variant) and the move would silently use different
        // persist semantics. Score candidates deterministically instead: +1 base for a
        // compatible first param, +2 when the second param accepts the parent, and break
        // ties by preferring the LONGEST overload (the one declaring the most of the
        // parameters we can supply — SaveWithParent(entity, parent, prefs) over the 2-arg).
        // The chosen binding is logged so a wrong pick is diagnosable in the worker log.
        internal static MethodInfo FindMethod(Type emType, string name, object arg0, object arg1 = null, bool logBinding = true)
        {
            MethodInfo best = null;
            int bestScore = -1;
            foreach (var mi in emType.GetMethods(Compatibility.SdkMemberProbe.StaticOrInstanceAnyVisibility))
            {
                if (mi.Name != name) continue;
                var ps = mi.GetParameters();
                if (ps.Length < 1) continue;
                if (!ps[0].ParameterType.IsInstanceOfType(arg0)) continue;
                int score = 1;
                if (arg1 != null && ps.Length >= 2 && ps[1].ParameterType.IsInstanceOfType(arg1)) score += 2;
                if (score > bestScore
                    || (score == bestScore && best != null && ps.Length > best.GetParameters().Length))
                {
                    bestScore = score;
                    best = mi;
                }
            }
            if (best != null && logBinding)
            {
                Logger.Info("[Move] bound " + name + " -> " + Signature(best));
            }
            return best;
        }

        private static Type FindEntityManagerType()
            => FindType("Artech.Layers.BL.EntityManager")
               ?? FindType("Artech.Udm.Framework.EntityManager")
               ?? FindFirstType("EntityManager");

        private static MoveResult Failed(string error, IReadOnlyList<string> attempts)
            => new MoveResult { Ok = false, Error = error, Attempts = attempts };

        private static string Describe(Exception ex)
            => "threw " + ex.GetType().Name + ": " + ex.Message;

        private static string Signature(MethodInfo mi)
        {
            var ps = mi.GetParameters();
            return (mi.DeclaringType?.Name ?? "?") + "." + mi.Name
                + "(" + string.Join(", ", ps.Select(p => p.ParameterType.Name)) + ")";
        }

        // Returns null on success, or the thrown exception on failure.
        private static Exception InvokeSave(Type emType, MethodInfo mi, object[] args)
        {
            try
            {
                object instance = mi.IsStatic ? null : ResolveInstance(emType);
                if (!mi.IsStatic && instance == null)
                    return new InvalidOperationException("could not resolve EntityManager instance for " + mi.Name);
                mi.Invoke(instance, args);
                return null;
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                Logger.Info("[Move] " + mi.Name + " threw: " + inner.GetType().Name + ": " + inner.Message);
                return inner;
            }
        }

        private static object[] BuildArgs(MethodInfo mi, object entity, object parent, object prefs)
        {
            var ps = mi.GetParameters();
            var args = new object[ps.Length];
            args[0] = entity;
            for (int i = 1; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (parent != null && pt.IsInstanceOfType(parent) && args.Length > 1 && i == 1)
                    args[i] = parent;
                else if (prefs != null && pt.IsInstanceOfType(prefs))
                    args[i] = prefs;
                else if (pt.Name.IndexOf("Preferences", StringComparison.OrdinalIgnoreCase) >= 0)
                    args[i] = prefs; // may be null; SDK tolerates default prefs
                else
                    args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            return args;
        }

        private static object BuildSavePreferences()
        {
            try
            {
                var t = FindType("Artech.Architecture.Common.Objects.KBObjectSavePreferences");
                if (t == null) return null;
                var prefs = Activator.CreateInstance(t);
                TrySetBool(prefs, "SkipValidation", true);
                return prefs;
            }
            catch { return null; }
        }

        private static object ResolveInstance(Type emType)
        {
            var instProp = emType.GetProperty("Instance", Compatibility.SdkMemberProbe.Static);
            if (instProp != null)
            {
                var v = instProp.GetValue(null, null);
                if (v != null) return v;
            }
            var instField = emType.GetField("Instance", Compatibility.SdkMemberProbe.Static)
                          ?? emType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                          ?? emType.GetField("m_Instance", BindingFlags.NonPublic | BindingFlags.Static);
            return instField?.GetValue(null);
        }

        private static void TrySetBool(object o, string propName, bool value)
        {
            try
            {
                var p = o.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(o, value, null);
            }
            catch { }
        }

        private static Exception Unwrap(Exception ex) => ex?.InnerException ?? ex;

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        // Plan 072: the bare simple-name scan used to accept ANY type named "EntityManager"
        // in ANY loaded assembly — a non-GeneXus type with the same simple name (e.g. an ORM
        // or data-access EntityManager) would then have been reflectively invoked as the Udm
        // EntityManager. Constrain the fallback to GeneXus SDK types: only accept a type
        // whose namespace is Artech.* (and only scan Artech/GxMcp assemblies for it).
        private static Type FindFirstType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsArtechAssembly(asm)) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                if (types == null) continue;
                foreach (var t in types)
                    if (IsArtechSdkType(t) && t.Name == simpleName) return t;
            }
            return null;
        }

        internal static bool IsArtechAssembly(Assembly asm)
        {
            if (asm == null) return false;
            try
            {
                string name = asm.GetName().Name ?? "";
                return name.StartsWith("Artech", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("GxMcp", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Exposed as a seam so tests can pin the namespace constraint without spawning a
        // fake assembly. OrdinalIgnoreCase to stay consistent with IsArtechAssembly (SDK
        // assembly/type names are case-stable, but a case-mismatched Artech.* should never
        // silently fall out of the scan).
        internal static bool IsArtechSdkType(Type t)
        {
            if (t == null) return false;
            string ns = t.Namespace ?? "";
            return ns.StartsWith("Artech", StringComparison.OrdinalIgnoreCase);
        }
    }
}
