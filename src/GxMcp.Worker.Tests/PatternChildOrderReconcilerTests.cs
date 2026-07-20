using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Exercises the auto-reconcile logic that keeps WorkWithPlus
    // childrenOrderedList in sync with the actual XML child order. Integration
    // against a live KB is in the worker smoke tests; here we cover the rules
    // that drive the rewrite — the kinds of changes the MCP applies silently
    // and surfaces in the response payload.
    public class PatternChildOrderReconcilerTests
    {
        [Fact]
        public void AddsMissingEntries_PreservingExistingTypeCodes()
        {
            // Caller added a new <textBlock> to TableContent but left the old
            // childrenOrderedList. Reconciler must prepend the new entry while
            // keeping the historical entries' type codes.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableContent' childrenOrderedList='2;28;ErrorViewer-2;01;TableAttributes'>
      <textBlock controlName='TxtNovo' />
      <errorViewer />
      <table name='TableAttributes' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);

            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single(t => (string)t.Attribute("name") == "TableContent").Attribute("childrenOrderedList");
            Assert.Equal("2;27;TxtNovo-2;28;ErrorViewer-2;01;TableAttributes", updated);
        }

        [Fact]
        public void ReordersToMatchXmlDocumentOrder()
        {
            // Children moved in XML: reconciler must reflect that in the list.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableActions' childrenOrderedList='2;18;Trn_Enter-2;18;Trn_Cancel-2;18;Trn_Delete'>
      <standardAction name='Trn_Enter' />
      <standardAction name='Trn_Delete' />
      <standardAction name='Trn_Cancel' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("2;18;Trn_Enter-2;18;Trn_Delete-2;18;Trn_Cancel", updated);
        }

        [Fact]
        public void UnnamedGroupTable_PreservedByTitle_WhenAlreadyInList()
        {
            // issue #36.3 — a group table (`<table isGroup title=…>`) has no name.
            // WWP addresses it in the parent list by its title. When the title already
            // appears in the existing childrenOrderedList (IDE wrote it), the reconciler
            // must REUSE that entry so a reorder of the siblings still rebuilds cleanly —
            // instead of bailing on the whole parent (the old additive-skip behavior).
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableMain' childrenOrderedList='2;27;TxtA-2;01;Análise e Parecer'>
      <table isGroup='True' title='Análise e Parecer' />
      <textBlock controlName='TxtA' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);

            Assert.Empty(report.Skips);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single(t => (string)t.Attribute("name") == "TableMain").Attribute("childrenOrderedList");
            Assert.Equal("2;01;Análise e Parecer-2;27;TxtA", updated);
        }

        [Fact]
        public void UnnamedGroupTable_InventsSlot_WhenTitleUniqueButNotInList()
        {
            // C16 — a group table's title is NOT in the existing list, but it IS a real
            // child of the parent and its title is unambiguous among siblings, so we now
            // INVENT a complete slot (level;typeCode;title) using the same formula named
            // children use. Without this the reconciler bailed the whole parent, so a
            // genuine edit to a SIBLING control silently didn't render.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableMain' childrenOrderedList='2;27;TxtA'>
      <textBlock controlName='TxtA' />
      <table isGroup='True' title='Unknown Group' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);

            Assert.Empty(report.Skips);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table")
                .Single(t => (string)t.Attribute("name") == "TableMain")
                .Attribute("childrenOrderedList");
            Assert.Contains("01;Unknown Group", updated);
            Assert.Contains("27;TxtA", updated);
        }

        [Fact]
        public void UnnamedGroupTable_HardSkip_WhenTitleAmbiguous()
        {
            // C16 — inventing a slot is only safe when the title is UNIQUE among the
            // parent's children. Two group tables sharing a title can't be told apart, so
            // we must NOT guess — bail with a clear, actionable skip instead.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableMain' childrenOrderedList='2;27;TxtA'>
      <textBlock controlName='TxtA' />
      <table isGroup='True' title='Dup Group' />
      <table isGroup='True' title='Dup Group' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);

            Assert.Equal(0, report.ParentsUpdated);
            Assert.Single(report.Skips);
            Assert.Contains("cannot derive identifier", report.Skips[0]);
            Assert.Contains("Dup Group", report.Skips[0]);
        }

        [Fact]
        public void DropsOrphanEntries_WhenChildIsRemoved()
        {
            // Caller deleted a <standardAction> but left it in the list.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableActions' childrenOrderedList='2;18;Trn_Enter-2;18;Trn_Cancel-2;18;Trn_Delete'>
      <standardAction name='Trn_Enter' />
      <standardAction name='Trn_Cancel' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("2;18;Trn_Enter-2;18;Trn_Cancel", updated);
        }

        [Fact]
        public void UsesContextSensitiveTypeCode_TableInSelectionIs02()
        {
            // <table> is type 01 inside <transaction> but 02 inside <selection>.
            // Same element name, different codes — context comes from ancestors.
            var doc = XDocument.Parse(@"
<instance>
  <level>
    <selection>
      <table name='TableMain' childrenOrderedList='4;02;TablePrincipal'>
        <table name='TablePrincipal' />
        <table name='TableNovo' />
      </table>
    </selection>
  </level>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single(t => (string)t.Attribute("name") == "TableMain").Attribute("childrenOrderedList");
            Assert.Equal("4;02;TablePrincipal-4;02;TableNovo", updated);
        }

        [Fact]
        public void UsesContextSensitiveTypeCode_StandardActionInSelectionIs17()
        {
            var doc = XDocument.Parse(@"
<instance>
  <level>
    <selection>
      <table name='TableActions' childrenOrderedList='4;17;Insert'>
        <standardAction name='Insert' />
        <standardAction name='Export' />
      </table>
    </selection>
  </level>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("4;17;Insert-4;17;Export", updated);
        }

        [Fact]
        public void AttributeIdentifierUsesFieldSuffix()
        {
            // <attribute> identifier in the list is the field name (suffix after
            // the GUID's last '-'), not the full GUID-prefixed value.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableAttributes' childrenOrderedList='2;22;AcaoCod'>
      <attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-AcaoCod' />
      <attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-AcaoDes' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var updated = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("2;22;AcaoCod-2;22;AcaoDes", updated);
        }

        [Fact]
        public void SkipsParentSafely_WhenChildTypeUnknown()
        {
            // Unknown element kind: reconciler must NOT emit a partial list —
            // bail and report the skip so the caller knows.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableContent' childrenOrderedList='2;28;ErrorViewer'>
      <errorViewer />
      <unknownThing name='X' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(0, report.ParentsUpdated);
            Assert.Single(report.Skips);
            // Original list left untouched.
            var still = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("2;28;ErrorViewer", still);
        }

        [Fact]
        public void NoOp_WhenListAlreadyMatchesChildren()
        {
            // Nothing to do — should report ParentsUpdated == 0.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableActions' childrenOrderedList='2;18;Trn_Enter-2;18;Trn_Cancel'>
      <standardAction name='Trn_Enter' />
      <standardAction name='Trn_Cancel' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(0, report.ParentsUpdated);
        }

        [Fact]
        public void CreatesListOnParentWithoutOne_WhenAllChildrenAreKnown()
        {
            // Container with known-kind children but no childrenOrderedList:
            // reconciler INVENTS the list so the IDE will render the children.
            // Level inherits from the nearest ancestor with a list (here, the
            // top-level <transaction> default 2 via fallback).
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableNew'>
      <textBlock controlName='TxtA' />
      <textBlock controlName='TxtB' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var list = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("2;27;TxtA-2;27;TxtB", list);
            Assert.Single(report.Changes);
            Assert.StartsWith("(created) ", report.Changes[0]);
        }

        [Fact]
        public void DoesNotCreateListForBlocklistedContainers()
        {
            // <transaction>, <rules>, <events>, <selection>, etc are blocklisted —
            // their children are positional/structural, not list-driven.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <rules>
      <rule Name='R1' />
    </rules>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(0, report.ParentsUpdated);
            Assert.Null(doc.Descendants("rules").Single().Attribute("childrenOrderedList"));
        }

        [Fact]
        public void UserActionUsesSameTypeCodeAsStandardAction()
        {
            // <userAction> sits next to <standardAction> in the TableActions row
            // and shares its context-sensitive typeCode (18 in transaction, 17 in
            // selection). Without this the reconciler would skip parents that
            // mix the two, leaving a stale list that the IDE renders incorrectly.
            var doc = XDocument.Parse(@"
<instance>
  <transaction>
    <table name='TableActions' childrenOrderedList='2;18;Trn_Enter'>
      <standardAction name='Trn_Enter' />
      <userAction name='Duplicar' />
    </table>
  </transaction>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            Assert.Equal(1, report.ParentsUpdated);
            var list = (string)doc.Descendants("table").Single().Attribute("childrenOrderedList");
            Assert.Equal("2;18;Trn_Enter-2;18;Duplicar", list);
        }

        [Fact]
        public void InheritsLevelFromNearestAncestorWithAList()
        {
            // New parent inside <selection> subtree should inherit level "4"
            // from an ancestor's list, not fall back to the static default.
            var doc = XDocument.Parse(@"
<instance>
  <level>
    <selection>
      <table name='TableOuter' childrenOrderedList='4;02;TableInner'>
        <table name='TableInner'>
          <textBlock controlName='TxtX' />
        </table>
      </table>
    </selection>
  </level>
</instance>");

            var report = PatternChildOrderReconciler.Reconcile(doc);
            // Inner table got a list invented; outer was already correct.
            var inner = (string)doc.Descendants("table").Single(t => (string)t.Attribute("name") == "TableInner").Attribute("childrenOrderedList");
            Assert.Equal("4;27;TxtX", inner);
        }
    }
}
