using System;
using System.Collections.Generic;
using System.IO;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Keeps the GeneXus build launchers resolvable from the Worker process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The .NET Framework generator compiles with <c>gxexec "&lt;web&gt;\bld&lt;main&gt;.cs" ...</c>, started
    /// by bare name. <c>GXExec.exe</c> lives in <c>&lt;GeneXus install&gt;\gxnet</c>, which the IDE
    /// has on its <c>PATH</c> but a Gateway-spawned Worker does not, so Specification and
    /// Generation succeeded while Compilation ended with "could not find the file specified".
    /// </para>
    /// <para>
    /// <see cref="Compose"/> is the pure rule: prepend <c>&lt;install&gt;\gxnet</c> and then
    /// <c>&lt;install&gt;</c> (only directories that exist and are not already listed) and leave
    /// the rest of the value untouched. <see cref="Apply"/> wires it to an injectable
    /// getter/setter, and <see cref="EnsureOnProcessPath"/> to this process. Only the
    /// process-level <c>PATH</c> is ever written, never the machine or user one.
    /// </para>
    /// </remarks>
    internal static class GxBuildPathEnvironment
    {
        private const string PathVariable = "PATH";
        private const string GxNetDirectory = "gxnet";

        // Windows rejects an environment variable value of 32,767 characters or more.
        private const int MaxPathValueLength = 32766;

        private static readonly object Gate = new object();

        /// <summary>
        /// Returns <paramref name="currentPath"/> with the missing GeneXus launcher directories
        /// prepended, or <paramref name="currentPath"/> itself when there is nothing to add.
        /// </summary>
        /// <param name="installationPath">Absolute GeneXus installation directory.</param>
        /// <param name="currentPath">The current <c>PATH</c> value; kept verbatim after the additions.</param>
        /// <param name="directoryExists">Existence probe; defaults to <see cref="Directory.Exists(string)"/>.</param>
        /// <param name="added">The directories prepended, in the order they now appear.</param>
        internal static string Compose(string installationPath, string currentPath,
            Func<string, bool> directoryExists, out IReadOnlyList<string> added)
        {
            added = Array.Empty<string>();

            string root = NormalizeInstallationRoot(installationPath);
            if (root == null) return currentPath;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(currentPath))
            {
                foreach (string entry in currentPath.Split(';'))
                {
                    string key = EntryKey(entry);
                    if (key.Length > 0) present.Add(key);
                }
            }

            var toAdd = new List<string>(2);
            foreach (string candidate in new[] { Path.Combine(root, GxNetDirectory), root })
            {
                string key = EntryKey(candidate);
                if (present.Contains(key)) continue;
                if (!Exists(directoryExists, candidate)) continue;
                present.Add(key);
                toAdd.Add(candidate);
            }
            if (toAdd.Count == 0) return currentPath;

            string prefix = string.Join(";", toAdd);
            string composed = string.IsNullOrEmpty(currentPath) ? prefix : prefix + ";" + currentPath;
            if (composed.Length > MaxPathValueLength) return currentPath;

            added = toAdd;
            return composed;
        }

        /// <summary>
        /// Reads the <c>PATH</c> through <paramref name="getPath"/>, writes the composed value through
        /// <paramref name="setPath"/> only when something was added, and logs one <c>[BUILD-PATH]</c>
        /// line in that case and none otherwise. Never throws; idempotent.
        /// </summary>
        internal static IReadOnlyList<string> Apply(string installationPath, Func<string> getPath,
            Action<string> setPath, Func<string, bool> directoryExists, Action<string> info, Action<string> warn)
        {
            try
            {
                lock (Gate)
                {
                    string composed = Compose(installationPath, getPath(), directoryExists, out IReadOnlyList<string> added);
                    if (added.Count == 0) return added;

                    setPath(composed);
                    info?.Invoke("[BUILD-PATH] Prepended to the Worker process PATH so the GeneXus build can launch gxexec: "
                        + string.Join("; ", added));
                    return added;
                }
            }
            catch (Exception ex)
            {
                try { warn?.Invoke("[BUILD-PATH] Could not update the Worker process PATH (non-fatal): " + ex.Message); }
                catch { }
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Applies <see cref="Apply"/> to this process' <c>PATH</c>, using the installation directory the
        /// Worker already trusts (<see cref="GeneXusInstallPath.Resolve"/>); nothing is hardcoded.
        /// </summary>
        internal static IReadOnlyList<string> EnsureOnProcessPath()
        {
            string installationPath = null;
            try { installationPath = GeneXusInstallPath.Resolve(); }
            catch (Exception ex) { Logger.Warn("[BUILD-PATH] GeneXus installation path unavailable (non-fatal): " + ex.Message); }

            return EnsureOnProcessVariable(PathVariable, installationPath);
        }

        /// <summary>
        /// The real process-level read/write behind <see cref="EnsureOnProcessPath"/>, with the variable
        /// name injectable so tests can exercise it on a throwaway variable instead of the host's PATH.
        /// </summary>
        internal static IReadOnlyList<string> EnsureOnProcessVariable(string variableName, string installationPath)
        {
            return Apply(installationPath,
                () => Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process),
                value => Environment.SetEnvironmentVariable(variableName, value, EnvironmentVariableTarget.Process),
                Directory.Exists,
                Logger.Info,
                Logger.Warn);
        }

        // Only an absolute, well-formed directory is trusted: a relative value would resolve against
        // whatever the working directory is, and a ';' would split into a second PATH entry.
        private static string NormalizeInstallationRoot(string installationPath)
        {
            if (string.IsNullOrWhiteSpace(installationPath)) return null;

            string trimmed = installationPath.Trim();
            if (trimmed.IndexOf(';') >= 0 || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (!IsFullyQualified(trimmed)) return null;

            try
            {
                string full = Path.GetFullPath(trimmed);
                return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Drive-absolute (C:\x, C:/x) or UNC (\\server\share); not rooted-only (\x), drive-relative
        // (C:x), or device (\\?\, \\.\) forms.
        private static bool IsFullyQualified(string path)
        {
            if (path.Length < 3) return false;
            if (char.IsLetter(path[0]) && path[1] == ':' && IsSeparator(path[2])) return true;
            return IsSeparator(path[0]) && IsSeparator(path[1])
                && !IsSeparator(path[2]) && path[2] != '?' && path[2] != '.';
        }

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        // Case-, quote- and trailing-separator-insensitive identity of a PATH entry.
        private static string EntryKey(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return string.Empty;
            return entry.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        }

        private static bool Exists(Func<string, bool> directoryExists, string directory)
        {
            try { return (directoryExists ?? Directory.Exists)(directory); }
            catch (Exception) { return false; }
        }
    }
}
