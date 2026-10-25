using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Surgical edits to a container's WorkWithPlus <c>childrenOrderedList</c> for the typed
    /// layout actions (issue #595): one entry inserted for a child that was just added, or
    /// one entry removed for a child that was just moved away. Every other entry is kept
    /// verbatim, in its place - including entries without a direct XML child, which is why
    /// the full rebuild in <see cref="PatternChildOrderReconciler"/> is not used here.
    ///
    /// <para>
    /// A container without the attribute is left without it: the pattern materializes its
    /// own baseline on save, and inventing one would be authoring SDK-owned metadata. An
    /// entry the format rules cannot derive (an unparsable list, a child without an
    /// identifier or type code, an identifier the list already carries) is a refusal, never
    /// a guess: the IDE hides children that are missing from the list, so a half-right list
    /// is worse than a clear error.
    /// </para>
    /// </summary>
    internal static class PatternChildOrderEdit
    {
        internal sealed class Change
        {
            /// <summary>The attribute before the edit; null when the container has no list.</summary>
            internal string Before;
            /// <summary>The attribute after the edit; equals <see cref="Before"/> when nothing had to change.</summary>
            internal string After;
            /// <summary>The <c>level;typeCode;identifier</c> entry inserted or removed, or null.</summary>
            internal string Entry;
            /// <summary>The identifier of the listed sibling the entry was inserted before, or null when appended.</summary>
            internal string InsertedBefore;
            internal bool Changed => !string.Equals(Before, After, StringComparison.Ordinal);
        }

        /// <summary>
        /// Inserts the entry for <paramref name="child"/>, which must already be a child of
        /// <paramref name="parent"/>, before the entry of the first listed sibling that follows
        /// it in document order (appended when none follows). Returns false with a reason
        /// when the list exists but cannot be maintained.
        /// </summary>
        internal static bool TryInsert(XElement parent, XElement child, out Change change, out string reason)
        {
            change = new Change { Before = (string)parent.Attribute("childrenOrderedList") };
            change.After = change.Before;
            reason = null;
            if (change.Before == null || PatternChildOrderReconciler.NonOrderedKinds.Contains(child.Name.LocalName)) return true;
            if (!TryParse(change.Before, out List<string[]> entries, out reason)) return false;

            string identifier = PatternChildOrderReconciler.GetIdentifier(child, parent);
            if (identifier == null)
            {
                reason = "no identifier can be derived for <" + child.Name.LocalName + "> in childrenOrderedList";
                return false;
            }
            if (entries.Any(e => IdentifierOf(e).Equals(identifier, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "childrenOrderedList already references '" + identifier + "'";
                return false;
            }

            string context = PatternChildOrderReconciler.GetContext(parent);
            string level = PatternChildOrderReconciler.ExtractLevel(change.Before)
                ?? PatternChildOrderReconciler.InheritLevel(parent)
                ?? PatternChildOrderReconciler.DefaultLevel(context);
            // A sibling of the same kind that is already listed is the best evidence of the
            // code this container uses; the context default is the fallback.
            string typeCode = parent.Elements()
                .Where(sibling => sibling != child && sibling.Name == child.Name)
                .Select(sibling => PatternChildOrderReconciler.GetIdentifier(sibling, parent))
                .Where(id => id != null)
                .Select(id => entries.FirstOrDefault(e => IdentifierOf(e).Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Where(e => e != null)
                .Select(e => e[1])
                .FirstOrDefault()
                ?? NestedTableTypeCode(parent, child)
                ?? PatternChildOrderReconciler.GetTypeCode(child, context);
            if (string.IsNullOrEmpty(typeCode))
            {
                reason = "no childrenOrderedList type code is known for <" + child.Name.LocalName + ">";
                return false;
            }

            change.Entry = level + ";" + typeCode + ";" + identifier;
            change.InsertedBefore = child.ElementsAfterSelf()
                .Select(sibling => PatternChildOrderReconciler.GetIdentifier(sibling, parent))
                .FirstOrDefault(id => id != null && entries.Any(e => IdentifierOf(e).Equals(id, StringComparison.OrdinalIgnoreCase)));
            change.After = InsertEntry(change.Before, change.Entry, change.InsertedBefore);
            parent.SetAttributeValue("childrenOrderedList", change.After);
            return true;
        }

        /// <summary>
        /// The code a new table is listed with when no sibling table is listed yet (issue #638).
        /// The code is the pattern's element code for a table, and a WebPanel does not use the
        /// transaction's 01/02: its WPRoot lists the main table as <c>7;66;TableMain</c>. The
        /// nearest enclosing table that is itself listed in its parent's list is the evidence
        /// (the container first, then its ancestors); null when no ancestor is listed, which
        /// leaves the context default in force.
        /// </summary>
        private static string NestedTableTypeCode(XElement parent, XElement child)
        {
            if (!child.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase)) return null;
            for (XElement table = parent; table?.Parent != null; table = table.Parent)
            {
                if (!table.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase)) continue;
                string identifier = PatternChildOrderReconciler.GetIdentifier(table, table.Parent);
                string[] entry = PatternChildOrderReconciler.SplitEntries((string)table.Parent.Attribute("childrenOrderedList"))
                    .FirstOrDefault(e => e.Length >= 3 && !string.IsNullOrWhiteSpace(e[1])
                        && IdentifierOf(e).Equals(identifier, StringComparison.OrdinalIgnoreCase));
                if (entry != null) return entry[1];
            }
            return null;
        }

        /// <summary>
        /// Removes the entry for <paramref name="child"/> from <paramref name="parent"/>'s list.
        /// A child the list does not reference is not an error: the list simply stays as it is.
        /// </summary>
        internal static bool TryRemove(XElement parent, XElement child, out Change change, out string reason)
        {
            change = new Change { Before = (string)parent.Attribute("childrenOrderedList") };
            change.After = change.Before;
            reason = null;
            if (change.Before == null || PatternChildOrderReconciler.NonOrderedKinds.Contains(child.Name.LocalName)) return true;
            if (!TryParse(change.Before, out List<string[]> entries, out reason)) return false;

            string identifier = PatternChildOrderReconciler.GetIdentifier(child, parent);
            if (identifier == null)
            {
                reason = "no identifier can be derived for <" + child.Name.LocalName + "> in childrenOrderedList";
                return false;
            }
            string[] entry = entries.FirstOrDefault(e => IdentifierOf(e).Equals(identifier, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return true;
            change.Entry = string.Join(";", entry);
            change.After = RemoveEntry(change.Before, identifier);
            parent.SetAttributeValue("childrenOrderedList", change.After);
            return true;
        }

        /// <summary>Pure string form of the insertion, so the native path can apply the same edit to the native value.</summary>
        internal static string InsertEntry(string list, string entry, string beforeIdentifier)
        {
            var entries = Entries(list).ToList();
            int index = beforeIdentifier == null ? -1
                : entries.FindIndex(e => IdentifierOf(e.Split(';')).Equals(beforeIdentifier, StringComparison.OrdinalIgnoreCase));
            if (index < 0) entries.Add(entry); else entries.Insert(index, entry);
            return string.Join("-", entries);
        }

        internal static string RemoveEntry(string list, string identifier)
        {
            return string.Join("-", Entries(list)
                .Where(e => !IdentifierOf(e.Split(';')).Equals(identifier, StringComparison.OrdinalIgnoreCase)));
        }

        internal static IEnumerable<string> Entries(string list)
        {
            if (string.IsNullOrEmpty(list)) yield break;
            foreach (string raw in list.Split('-'))
                if (!string.IsNullOrWhiteSpace(raw)) yield return raw;
        }

        /// <summary>The identifier part of one <c>level;typeCode;identifier</c> entry.</summary>
        internal static string Identifier(string entry) => IdentifierOf((entry ?? string.Empty).Split(';'));

        /// <summary>Whether the list carries an entry addressed by <paramref name="identifier"/>.</summary>
        internal static bool References(string list, string identifier)
        {
            return !string.IsNullOrEmpty(identifier)
                && Entries(list).Any(e => IdentifierOf(e.Split(';')).Equals(identifier, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParse(string list, out List<string[]> entries, out string reason)
        {
            entries = PatternChildOrderReconciler.SplitEntries(list).ToList();
            reason = null;
            string[] malformed = entries.FirstOrDefault(e => e.Length < 3 || string.IsNullOrWhiteSpace(e[0]) || string.IsNullOrWhiteSpace(e[1]));
            if (malformed == null) return true;
            reason = "childrenOrderedList entry '" + string.Join(";", malformed) + "' is not in the level;typeCode;identifier form";
            return false;
        }

        private static string IdentifierOf(string[] entry) => entry.Length < 3 ? string.Empty : string.Join(";", entry.Skip(2));
    }
}
