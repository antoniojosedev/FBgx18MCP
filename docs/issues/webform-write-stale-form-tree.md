# WebForm write does not persist: `m_Document` clobbered by stale Form tree on save

**Status:** blocked on obfuscated SDK layer · **Component:** `GxMcp.Worker` / `WebFormXmlHelper` · **Severity:** blocks programmatic visual edits

## Implementation status (2026-05-12, session 3 — wall hit at byte-persistence layer)

Byte-level instrumentation around `obj.Save(ForceSave=true)` (see `WebFormSaveDiagnostics.cs`)
proves the in-memory state is correct at every checkpoint AND the SDK Save lifecycle
runs to completion. Despite this, disk state never changes.

**Diagnostic log evidence (Saldo: → Saldo TESTE3:):**

```
[Diag/BEFORE-SAVE] m_Document     probe=Saldo TESTE3:    hash=80F8B687
[Diag/BEFORE-SAVE] Document(prop) probe=Saldo TESTE3:    hash=80F8B687 sameRef=True
[Diag/BEFORE-SAVE] SerializeData() bytes=36631 hash=A4A5D150 probe=Saldo TESTE3:
[Diag/BEFORE-SAVE] part.Mode=Modified part.Modifications=Data,Property,Unknown part.Dirty=True
[Diag/BEFORE-SAVE] obj.Mode=Modified  obj.PartsMode=Modified
[Diag/BEFORE-SAVE] StructurePart present=False    ← OnBeforeSaveEntity early-returns; no clobber
[VisualWrite] obj.Save(KBObjectSavePreferences{ForceSave=true}) completed.
[Diag/AFTER-SAVE]  SerializeData() bytes=36631 hash=A4A5D150 probe=Saldo TESTE3:   ← still correct
[Diag/AFTER-SAVE]  part.Mode=Unchanged  obj.Mode=Unchanged    ← InternalSave ran to line 521
[DirectSave] part.TypeId=71 part.TypeVersionId=4
[DirectSave] SaveModelEntityOutput(typeId=71, version=4, bytes=36631) completed.
[BACKGROUND-FLUSH] Model.Commit() successful. KB.Commit() successful.
```

After worker kill (`Stop-Process -Force GxMcp.Worker`) + fresh `genexus_open_kb` +
`genexus_read part=WebForm`: **disk still shows `Saldo:`**. The fresh-from-MDF read confirms
nothing of the write reached the SQL Server data store.

### What we proved this session

1. **Property cache invalidation isn't the problem.** SerializeData bytes contain our
   mutation both before and after save (same hash). No clobber happens during the save
   lifecycle (StructurePart=False ⇒ `OnBeforeSaveEntity` early-returns at line 249706 of
   `Artech.Genexus.Common.dll`).
2. **Save lifecycle runs to completion.** `kbObject.Mode` flips Modified → Unchanged
   (InternalSave line 521), confirming `PrepareSave` returned true and `PerformSave`
   executed through `transaction.Commit()`.
3. **Document property setter doesn't unblock it.** Cloning m_Document and reassigning
   via the `Document` property setter fires `OnPropertyValueChanged` (adds `Property`
   flag to `part.Modifications`), but disk state still unchanged.
4. **Direct SaveModelEntityOutput bypass doesn't unblock it.** Calling
   `webFormPart.SaveModelEntityOutput(71, 4, DateTime.UtcNow, freshBytes)` directly
   (the entity-level primitive used by SaveWithParent internally) returns successfully
   but bytes don't reach disk either.
5. **Hard block:** `Artech.Udm.Framework.dll`, `Artech.Layers.BL.dll` ARE decompilable
   (`Layers.BL` has full IL — `InternalSave/PrepareSave/PerformSave` visible), but
   `Artech.Udm.Framework.dll` has method bodies stripped (`[MethodImpl(NoInlining)] {
   }` shells; `mEqmoE9UxRmX9ogcto.M7EWM2ogCI()` decryptor pattern in static ctors).
   `EntityManager.SaveWithParent`, `Entity.SaveModelEntityOutput`, and friends — the
   actual byte→disk pipeline — cannot be traced via ILSpy.

### Files added/modified this session

- `src/GxMcp.Worker/Helpers/WebFormSaveDiagnostics.cs` (new) — `DumpState` logs
  m_Document / Document(prop) / SerializeData bytes / Mode / Modifications / Dirty /
  StructurePart presence at any checkpoint. `TryDirectSaveModelEntityOutput` is the
  Hipótese-3 bypass attempt.
- `src/GxMcp.Worker/Helpers/WebFormTypedPropertyWriter.cs` — added Document property
  setter trigger at end of `TryApply` (clones m_Document, assigns via setter to fire
  OnPropertyValueChanged) and syncs m_EditableContent / clears m_EditableToStoredNeeded.
- `src/GxMcp.Worker/Services/WriteService.cs` — BEFORE-SAVE and AFTER-SAVE diagnostic
  hooks around `obj.Save(prefs)`, plus `TryDirectSaveModelEntityOutput` after save.

### Recommended follow-ups (in order of expected payoff)

1. **Runtime IL capture of the obfuscated bodies.** Attach dnSpy or use a ClrMD-based
   helper at runtime to dump the JIT-decrypted IL of `EntityManager.SaveWithParent`,
   `Entity.SaveModelEntityOutput`, `KBStateManager.AcquireState`. The decryptor
   (`mEqmoE9UxRmX9ogcto.M7EWM2ogCI`) runs at module load — by the time JIT compiles
   these methods their IL exists in process memory.
2. **ETW/SQL profiling.** Capture all SQL traffic during `obj.Save(ForceSave=true)` for
   WebForm vs SDT. SDT writes work today; the SQL diff will show what extra/different
   command WebForm needs.
3. **Live IDE attach diff.** Attach dnSpy debugger to a running `Genexus.exe` IDE,
   set a breakpoint inside SaveWithParent, modify a WebForm caption from the IDE,
   and step through. Then repeat from the headless worker. Whatever branch the worker
   takes that the IDE doesn't is the gate.
4. **Workaround for users today:** until 1–3 yield, document that WebForm/Layout
   programmatic edits via MCP are read-only-with-dry-run. Property edits on existing
   controls via `genexus_layout set_property` continue to work (different code path,
   not affected). Surface a clear "Use the IDE for WebForm structural changes"
   error message in `WriteVisualPart` when the verify step fails.



## Implementation status (2026-05-12, session 2)

Two critical findings from this session:

### 1. Gateway `content` vs `payload` bug (FIXED)

`src/GxMcp.Gateway/Routers/ObjectRouter.cs:147` was emitting `content = …` for the
legacy text-patch dispatch path, but `src/GxMcp.Worker/Services/CommandDispatcher.cs:154`
reads the replacement text from `request["payload"]`. Result: every WebForm/Layout
patch arrived at the worker with `content = null`, making `source.Replace(context, "")`
DELETE the matched element instead of replacing it.

Fix: route emits `payload = (patchTok is JValue ? patchTok.ToString() : null) ?? args["content"]`.
The Patch operation now correctly modifies the element. Verify dumps in `last-current.xml` /
`last-patch-output.xml` show the gxTextBlock retains its identity and the attribute changes
land in `part.Document`.

### 4. SDK save lifecycle decompiled via ILSpy

`Artech.Layers.BL.dll` → `KBObjectManager.InternalSave` (line 473) — the canonical save:

```csharp
private void InternalSave(KBObject kbObject, KBObjectSavePreferences preferences) {
    CheckValidSave(kbObject, ...);
    if (!PrepareSave(kbObject, preferences, out var saveType))  // ← GATE
        return;                                                 // SILENTLY SKIPPED
    ...
    using (KnowledgeBase.Transaction transaction = kbObject.KB.BeginTransaction()) {
        foreach (KBObjectPart part in kbObject.Parts)
            part.BeforeSaveKBObject();                          // ← WebFormPart fixup
        PerformSave(kbObject, saveType, preferences);
        ...
        transaction.Commit();
    }
    kbObject.Mode = Mode.Unchanged;
}
```

**Gate at `PrepareSave`:**
```csharp
if (kbObject.Mode == Mode.Unchanged && !preferences.ForceSave && !preferences.ForceSaveDefaultParts)
    return false;  // SAVE SKIPPED - no exception thrown
```

So passing `KBObjectSavePreferences { ForceSave = true, ForceSaveDefaultParts = true }` to
`obj.Save(prefs)` bypasses the Mode==Unchanged check. Confirmed via runtime logs:
the save call now `[VisualWrite] obj.Save(KBObjectSavePreferences{ForceSave=true}) completed`
AND the async `Model.Commit()` + `KB.Commit()` both return successful.

But fresh-from-disk read after worker restart STILL shows the original value. The SAVE call
travels through `PerformSave` → `EntityManager.SaveWithParent(part, kbObject, prefs2)` for
each non-virtual part. The bytes WebFormPart.SerializeData() would return (= m_Document
.OuterXml — which has our mutation) should be what gets persisted.

**Two confirmed clobber/revert sites:**
1. `WebFormPart.OnBeforeSaveEntity` iterates tags and calls `item.SaveProperties()`
   for `CanHaveEntityReferences(item.Type) == true` types (TextBlock IS in that list).
   `SaveProperties()` writes typed `Properties` collection → tag.Node.Attributes.
2. `WebTagNavigator.Create(KBObject, XmlDocument)` calls
   `MultiFormSerializer.ExpandForms(kbObj, doc, GetXmlOptions.None).DocumentElement`
   — this CLONES m_Document. So tags from `EnumerateWebTag(part)` live in a clone,
   not in m_Document. Our XmlNode invalidation hit the wrong instances.

**Remaining unknown:** why `obj.Save(ForceSave=true)` reports success but disk has
original value. Either (a) SaveWithParent silently drops the part write under some condition
not yet found, (b) the in-flight transaction is being rolled back by something, or
(c) the IDE has a private write-path that's not in any of `Artech.Layers.BL.dll`,
`Artech.Udm.Layers.BL.dll`, or `Artech.Udm.BL.dll` (need to scan more BL DLLs).

### 3. Dirty-flag mechanism does NOT unblock persistence

Extended the SDK probe to walk all base types of `WebFormPart` and scanned for
modification/dirty hooks. Found canonical Entity-level surface:
- `Entity.Dirty` (R/W bool)
- `Entity.SetModeModified(Modification, Object)`
- `Entity.InternalSetModeModified(Modification, Object)`
- `Entity.Mode` (R/W), `Entity.Modifications` (R), `Modification` enum (`None|Data|Property|Category|Unknown|Any`)
- `KBObjectPart.InternalSetModeModified(Modification, Object)` override

Tried every combination after the m_Document mutation:
- `webFormPart.SetModeModified(Data, null)` + `webFormPart.Dirty = true` (the part)
- `obj.SetModeModified(Any, null)` + `obj.Dirty = true` (the parent KBObject)
- `webFormPart.Save()` returns cleanly (no exception)
- `obj.EnsureSave(true)` + `transaction.Commit()` + `ScheduleFlush()`
- After worker restart + fresh disk read: ORIGINAL value still on disk.

So whatever the IDE uses to make WebFormPart persistence work is NOT reachable via:
(a) marking the entity dirty, (b) calling Save on the part, (c) EnsureSave on the
parent, or (d) any combination of those. The persistence path appears to require
either an IDE-only API surface or a state precondition we haven't discovered.

### 2. Persistence blocked at the SDK level (BLOCKED on dirty-flag / KBModel cache)

Empirically confirmed in this session (with the gateway bug fixed so the patch actually
reaches WriteVisualPart with the right XML):

- `WebFormHelper.EnumerateWebTag(KBObject, XmlDocument)` returns tags that, depending on
  the overload, may be rooted in a CLONE of `m_Document` instead of the field itself.
  Accessing `m_Document` via reflection (vs the `Document` property) and mutating the
  XmlElement attribute lands in the part's authoritative tree — `verify` reads the new
  value back.
- After `obj.EnsureSave(true) + transaction.Commit() + ScheduleFlush()`, the SAME `obj`
  instance's `Document` still shows our mutation (length / hash confirm).
- `_objectService.FindObject(target)` returns a DIFFERENT, separately-cached KBObject
  whose `Document` still shows the original value. After a worker restart (which forces
  a fresh disk read), the WebForm XML on disk is also unchanged.

So the SDK's headless save lifecycle is not picking up our XmlNode-level mutations as
"dirty". The typed-Property path (`tag.SetProperties(IDictionary)` / `WebFormEditable
.SetTagProperty`) doesn't help either because their typed Property values for `Class`
are resolved ClassReferences whose serialization round-trips back to the original GUID.

### Hipótese A — wired through the canonical SDK surface (probe-verified — see

The canonical SDK path is now in place — `WebFormHelper.EnumerateWebTag(KBObject, XmlDocument)`,
match by `id`, mutate the XmlElement that lives in `part.Document`, invalidate
`m_PropertiesLoaded`/`m_Props`/`m_EditableToStoredNeeded`, sync `m_EditableContent`,
bump `InvalidateLastModification`. Verify logs confirm `part.Document` holds the new value
RIGHT BEFORE `obj.EnsureSave(true)` runs.

After save, the persisted XML still shows the original value. The clobber happens
deeper than the in-memory state we have access to — likely a `PropertyValueConverter` or
`ScopedModelObjectCache` (seen as a field of `WebFormHelper+<GetPartControls>d__0` in the
probe dump) at the KBModel level that the save lifecycle consults instead of m_Document.

### Hipótese A — wired through the canonical SDK surface (probe-verified — see
`webform-sdk-probe.log` in `publish/worker/`):

- `WebFormPropertyDeltaDetector` — structural diff that captures any attribute change
  on existing controls; rejects add/remove/move with a Reason that gets logged.
- `WebFormTypedPropertyWriter` — enumerates `WebFormHelper.EnumerateWebTag(part)`,
  matches by `tag.Node.Attributes["id"]` / `ControlName`, then invokes
  `WebFormEditable.SetTagProperty(tag, tag.Properties, null, propName, value, ref changed, null)`.
  On save, `WebFormPart.BeforeSaveKBObject` → `tag.SaveProperties()` writes the typed
  Properties back into `m_Document` — exactly the SDK's own clobber site, now working
  *for* us instead of against us.
- `currentXml` for delta detection is normalized through `XDocument.Parse(...).ToString()`
  so it byte-matches the `ReadEditableXml` baseline PatchService used.

Live verification still pending (MCP genexus18 disconnected mid-session). Structural
changes (add/remove/move controls) still fall back to the broken `EditableContent` path —
Hipótese C (priming `AttributeVariableConverter` cache) remains untouched.

## Symptom

`genexus_edit part=WebForm mode=patch|full` returns:

```
Visual write verification failed — The SDK save path completed, but the persisted WebForm XML
does not match the requested content.
Diff: Child count differs at /GxMultiForm/Form/body/.../td (1 vs 0)
```

The write **always** fails verification regardless of the change (even a single caption swap on
an existing TextBlock). Persisted XML reads back as the unchanged original.

## Root cause (verified via SDK IL disassembly)

The `Artech.Genexus.Common.Parts.WebFormPart` (GeneXus 18.0.7) maintains **two parallel
representations** of the form:

| Storage     | Field                                                    | Populated by                                                  | Read by                                                       |
| ----------- | -------------------------------------------------------- | ------------------------------------------------------------- | ------------------------------------------------------------- |
| Raw XML     | `XmlDocument m_Document`                                 | `Document` getter/setter, `EditableContent` setter, `LoadXml` | `SerializeData` (via `Convert.ToByteArray(m_Document, this)`) |
| Parsed tree | per-control `IWebTag` collection with typed `Properties` | `DeserializeDataFromDocument`, IDE control editors            | `BeforeSaveKBObject` (via `IWebTag.SaveProperties`)           |

The save lifecycle, traced from IL:

1. Caller invokes `obj.EnsureSave(true)` (in `WriteService.WriteVisualPart` line 1004).
2. SDK fires `BeforeSaveKBObject` on every part.
3. `WebFormPart.BeforeSaveKBObject` iterates each `IWebTag` and calls `SaveProperties()`.
4. `IWebTag.SaveProperties()` writes the in-memory typed `Properties` collection **back into the
   `XmlAttribute` collection of the underlying node in `m_Document`** — overwriting whatever was
   there.
5. `SerializeData()` then takes `m_Document` (now with overwritten attributes) and converts it
   to bytes for KB storage.

If the in-memory Properties are stale (which they always are when we modify only the
`m_Document` XML), step 4 silently reverts our changes before step 5 serializes.

## What we tried

All combinations of the following — none persist a change:

- `Document.RemoveAll(); Document.LoadXml(newXml)` (direct DOM rewrite)
- `EditableContent = newXml` (canonical IDE string setter; verified via IL that it does
  `set_Document(new XmlDocument().LoadXml(value)); m_EditableToStoredNeeded = true`)
- `EditableToStored()` after either of the above — **throws `GxException: "Atributo desconhecido
'att:13937'"`** from
  `Artech.Genexus.Common.CustomTypes.AttributeVariableConverter.GetAttVarByName`. The lookup
  walks `att:NNNN` references and fails because the worker context's `KBModel` returns null for
  attribute IDs that exist in the KB (probably needs `KBModel.Resolve()` or equivalent priming
  the IDE does that we don't replicate). The throw poisons the part: control list partially
  mutated, save then drops the affected controls.
- `DeserializeDataFromDocument()` (the more tolerant cousin) — runs without throwing, logs
  success, but the next `obj.EnsureSave(true)` still produces the unchanged XML. This implies
  the typed `Properties` reload from `m_Document` isn't actually happening for the controls we
  modify, OR `SaveProperties` re-serializes from a copy taken before our reload.
- Reflection nullification of fields matching `form|layout|parsed|cached` — no effect.
- Invocation of `Invalidate`/`Refresh`/`Reload`/`MarkDirty`/`Touch`/`OnDocumentChanged` — no
  effect.
- `webFormPart.Save()` directly (in addition to `obj.EnsureSave`) — no effect.

## Verified SDK surface

```
namespace Artech.Genexus.Common.Parts;
public class WebFormPart : KBObjectPart {
    // properties
    public string EditableContent { get; set; }          // set: new XmlDoc → set_Document → flag
    public XmlDocument Document { get; set; }            // set: stfld m_Document + OnPropertyValueChanged
    public int LastModification { get; }
    public IndexableContent Content { get; }
    public IContentAnalyzer Analyzer { get; }
    public IEnumerable<...> Variables { get; }
    public string PartDescriptionCache { get; set; }

    // fields
    string m_EditableContent;
    bool m_EditableToStoredNeeded;
    int m_LastModification;
    bool m_FixPending;
    XmlNodeChangedEventHandler InvalidateHandler;
    XmlDocument m_Document;

    // methods (relevant subset)
    void EditableToStored();                             // calls (msg) overload
    void EditableToStored(OutputMessages messages);      // FixClassProperty per tag + WebFormEditable.EditableToStored
    void DeserializeDataFromDocument();                  // TrackDocumentChanges + tag.SaveProperties +
                                                         // FixDuplicatedControl + FixNoNameControl + FixClassProperty +
                                                         // FixWebFormData + WebFormEditable.ConvertAfterDeserialize +
                                                         // InvalidateLastModification
    void BeforeSaveKBObject();                           // ITERATES TAGS, READS PropDefinitionCollection
    void OnBeforeSaveEntity(EntityEventArgs args);       // ITERATES TAGS, CALLS tag.SaveProperties  ← clobber site
    void TrackDocumentChanges();                         // hooks XmlDocument.NodeInserted/Removed → InvalidateLastModification
    void UntrackDocumentChanges(XmlDocument doc);
    Byte[] SerializeData();                              // Convert.ToByteArray(m_Document, this)  ← OK if m_Document fresh
    void DeserializeData(byte[] data);
    void InvalidateData();
    void InitializeData();
    void FixWebFormData();
    void InvalidateLastModification();                   // m_LastModification++
    Variable GetVariable(...);
    IEnumerable<Variable> GetReferencedVariables();
    IEnumerable<...> GetPartReferences();
}

// static helper that does the actual editable→stored conversion:
namespace Artech.Genexus.Common.Parts.WebForm;
public static class WebFormEditable {
    void EditableToStored(WebFormPart webPart, OutputMessages messages);
    // implementation: GetVersions(part.Document); GetForms(part.Document); for each form: Form.Import(handler, kbObject, addError);
    void EditableToStored(KBObject kbObj, XmlElement webFormElement, Action<...> addError);
}
```

## Hypothesis for fix

Two viable paths, both significantly more work than the patch-as-text approach we have today:

### A. Bypass `SaveProperties` clobber by updating typed Properties

For each control we want to mutate:

1. Read the WebForm XML, identify the target node by `id` attribute.
2. Locate the in-memory `IWebTag` whose `Node` matches that id (walk
   `WebFormHelper.EnumerateWebTag(part)`).
3. For each XML attribute the user changed (e.g. `CaptionExpression`,
   `PATTERN_ELEMENT_CUSTOM_PROPERTIES`), look up the matching `IPropertyDefinition` in
   `tag.PropertiesDefinition` and call `tag.Properties[def] = newValue` via the SDK's
   `PropertyValueConverter` for the declared property type.
4. Save normally — `BeforeSaveKBObject.SaveProperties` will now write OUR values back to the
   document.

This requires building a mapping table from `<gxTextBlock>` / `<gxAttribute>` / `<fieldset>` /
etc. XML attribute names → typed Property keys (`Caption`, `Class`, `ControlType`, `Width`, …)
for every supported control type. The mapping changes between control types (a TextBlock
exposes `Caption` as a `Tokens` expression; a SimpleGrid item exposes `ControlType`,
`ControlValues`, `ColumnClass`, …). Effort: ~1–2 weeks of careful work + tests against a
catalog of real forms.

### B. Force a model rebuild by deleting and re-importing the part

Theoretically possible via `KBObject.Parts.Delete(part)` then `Parts.Add(newPart)`, but most
SDK part types are immutable post-creation and the K2BTools/WorkWithPlus patterns attached to
the WebPanel reference the part by GUID — this would break pattern instances. **Not
recommended.**

### C. Pre-prime the KBModel so EditableToStored doesn't throw

Inject the missing `att:NNNN` resolutions into whatever cache `AttributeVariableConverter` uses
before invoking `EditableToStored`. Reflection target unknown — need to follow the call chain
from `GetAttVarByName` into the resolver to find the cache. If we can prime it, then
`EditableToStored` would not throw, would update the typed Properties, and the rest of the
save would persist them correctly. This is the cleanest path if reachable.

## Reproduction

1. Open KB `AcademicoHomolog1` in GeneXus 18.0.7.
2. Via MCP, call `genexus_edit name=ListaAtiCPAlunoUniGra part=WebForm mode=patch
verifyRollback=true` with any caption change on `TextBlockSaldoHoras`.
3. Observe `Visual write verification failed` with diff at the deeply-nested td. Logs show
   `EditableToStored() threw: GxException: Atributo desconhecido 'att:13937'` if using
   the `EditableContent` + `EditableToStored` path.

## Workaround in place today

`genexus_edit part=WebForm` **read** works (dry-run patches succeed). For **write**, the user
must drag controls onto the form in the IDE; properties on existing controls can be tweaked
via `genexus_layout set_property` (which goes through a different code path that DOES work for
property mutation on already-parsed controls).

## Files touched while investigating

- `src/GxMcp.Worker/Helpers/WebFormXmlHelper.cs` — added `TrySetEditableContent`,
  `PushDocumentToStoredModel`, and unwrapping of `TargetInvocationException` for diagnostics.
- `src/GxMcp.Worker/Services/PatchService.cs` — added WebForm/Layout branch to
  `ReadSourceFast` (read path works).
- `src/GxMcp.Worker/Services/WriteService.cs` — `WriteVisualPart` is the verification site (no
  changes needed here, the diff message is accurate).

## Next steps for a future session

1. Reach for path **A** or **C** above.
2. Add a `genexus_layout set_control_property` action that takes `controlId + propertyName +
value` and goes through `IWebTag.Properties` directly — even without solving the global
   write problem, this would let an LLM mutate properties on existing controls reliably.
3. Build a small `WebFormControlMap` registry: `controlTagName → (xmlAttrName →
typedPropertyKey)`. Start with `gxTextBlock`, `gxAttribute`, `fieldset`, `IMG`,
   `simplegrid item`. Each entry is a few lines.
