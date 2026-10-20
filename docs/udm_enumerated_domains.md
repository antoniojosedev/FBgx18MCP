# Enumerated domains in the UDM reader

Scope: Domain rows that carry `IDEnumDefinedValues` (enumerations), as read by
`gx-engine` from a native Knowledge Base. The question: what base type does the
SDK give an SDT item or a Procedure variable based on an enumeration, and can the
engine map it with evidence? This note records the census, the decision and the
evidence behind it. It is a companion to [`udm_storage_notes.md`](udm_storage_notes.md)
("SDT item mapping"), which it corrects in one row (see [Correction](#correction-to-udm_storage_notesmd)).

## Decision

- **No new mapping.** An enumeration with no stored type (no `ATTCUSTOMTYPE`) stays
  refused by name. No SDK-generated C# showing an item based on an enumeration was found
  on disk (the SDT classes in the disposable KBs are probe SDTs), so the SDK base type and
  the emitted members are not proven.
- **Typed enumerations keep their stored type.** An enumeration whose row declares
  `ATTCUSTOMTYPE` and a `Length` is mapped by the typed-domain rule, as before
  (34 of 141 enumeration rows: GX18 19 of 80, GX16 15 of 61).
- **Guarded.** `src/gx-engine/src/udm/enum_domain_tests.rs` pins the refusals and the
  typed positive controls with the stored rows verbatim (6 tests). Three mutations
  were each caught (see [Mutation checks](#mutation-checks)).
- **Open decision (allowlist).** `DOMAIN_ROW_PROPERTIES` refuses a whole Domain row
  that carries a property it does not name. That refuses 22 typed enumerations whose
  declared type is otherwise readable (see [Allowlist](#allowlist-refusals)). Changing
  the allowlist is a decision for the user, with the evidence named below.

## Census

Read with `SELECT` only, from the active version of each Domain row (the version the
model holds, as in `reader.rs` `domain_type`):

- `GX_KB_KBTeste` (GX18), `ModelId = 1`, `EntityTypeId = 33`: 109 Domain rows, 80 enumerations.
- `GX_KB_KBTeste16` (GX16), `ModelId = 1`, `EntityTypeId = 26`: 73 Domain rows, 61 enumerations.

```sql
SELECT m.EntityId, m.EntityVersionId,
       CONVERT(VARCHAR(MAX), CAST(v.EntityVersionProperties AS NVARCHAR(MAX))) AS P
FROM ModelEntityVersion m
JOIN EntityVersion v ON v.EntityTypeId = m.EntityTypeId
  AND v.EntityId = m.EntityId AND v.EntityVersionId = m.EntityVersionId
WHERE m.ModelId = 1 AND m.EntityTypeId = 33   -- 26 on GX16
ORDER BY m.EntityId;
```

Each enumeration row is classified by the production `domain_data_type` (the function
the reader calls) on the verbatim row. "Unlisted" means the row carries a property
`DOMAIN_ROW_PROPERTIES` does not name, so the whole row is refused before its type is read.

| Stored shape | GX18 | GX16 | Outcome |
| :--- | ---: | ---: | :--- |
| Typed (`ATTCUSTOMTYPE`), with `Length`, all properties listed | 19 | 15 | **Mapped** by the typed-domain rule |
| Typed, with `Length`, and an unlisted property (Description 9/9, ControlType 1/1, EmptyAsNull 1/0, Signed 1/0) | 12 | 10 | Refused: allowlist |
| Typed, no `Length` (CHARACTER 11/9, NUMERIC 3/3, VARCHAR 6/2) | 20 | 14 | Refused: "has no stored length" |
| Untyped, with `Length`, all properties listed | 2 | 2 | Refused: no stored type |
| Untyped, with `Length`, and an unlisted property (ControlType, Description) | 5 | 4 | Refused: no stored type and allowlist |
| Untyped, no `Length`, all properties listed | 18 | 13 | Refused: no stored type |
| Untyped, no `Length`, and an unlisted property (Description 3, Signed 1 on GX18; Description 3 on GX16) | 4 | 3 | Refused: no stored type and allowlist |
| **Total** | **80** | **61** | **mapped 19 / 15** |

Untyped rows with `Length` include `MessageTypes` (GX18 id 7, GX16 id 41: Length 2) and
`SmartDeviceType` (GX16 id 48: Length 1). Domain ids differ per Knowledge Base; use the
row's own name, not its id, across KBs.

The census script only reads the rows; the classifications were produced by the
`gx-engine` test build against the verbatim rows (no KB was written).

## Live refusals (SDT level)

`lcG18q.log` (GX18 `KBTeste`) and `lcG16q.log` (GX16 `KBTeste16`) are the main lane's
Rebuild All runs; they were read, not re-run. Each SDT stops at its first refusal, so
an enumeration behind another refusal is not counted. Lines that name an unmapped
domain:

| Shape (refusal line names the domain) | GX18 lines | GX16 lines |
| :--- | ---: | ---: |
| Untyped, no `Length` (MapType, MediaFinishReason, MediaStreamType, NotificationBadgeType, PlaybackState, StoreProductType, StorePurchasePlatform, VideoFormat) | 8 | 6 |
| Untyped, with `Length` (MessageTypes; SmartDeviceType on GX16) | 1 | 6 |
| Typed, no `Length` (PushNotificationPriority, text) | 3 | 1 |
| Typed, no `Length` (TargetType, numeric) | 3 | 1 |
| **Total** | **15** | **14** |

The refusal text is the same for every cause ("the domain has no stored type this engine
maps"), so the log cannot tell the allowlist cause from the no-type cause; the census can.

## SDK evidence

What exists, and what it proves:

- **Items based on an enumeration, SDK text.** `sdk_pairs.json` and `sdt_domain_cmp.txt`
  (under `C:/tmp`) show the SDK text of such an item as the domain name only
  (`MediaQueueState.State : PlaybackState`). That is not a type.
- **Resolved SDK types recorded in `udm_storage_notes.md`** (from earlier
  `genexus_inspect` runs; **not re-verified in this lane**, no MCP access here):
  `PlaybackState` and `StoreProductType` items resolve to `NUMERIC(4)`; `MessageTypes`
  (GX16) to `NUMERIC(2)`; `SmartDeviceType` (GX16) to `NUMERIC(1)`. `BeaconProximity`
  (typed, `NUMERIC(1)`) is the one typed enumeration checked on both KBs.
- **Untyped SDT items (no `ATTCUSTOMTYPE`, no `idBasedOn`)** resolve to `NUMERIC(stored
  Length)` or `NUMERIC(4)` when there is none. SDK rows: `GeolocationProximityAlert.Radius`
  and `GridState.OrderedBy` `NUMERIC(4)`; `TrackingParameters.Distance` `NUMERIC(8)`;
  `StorePurchase.AcknowledgementState` `NUMERIC(2)` (`C:/tmp/sdt_refuse_check.txt`).
- **SDK-generated C#.** The SDT classes under `C:/KBs/KBTeste/NetModel/Web` are probe SDTs
  with no enumeration-typed member; `C:/KBs/KBTeste16/CSharpModel` is empty and
  `KBTeste17/CSharpModel` holds only styles and themes. A search of the repository for the
  enumeration names found no fixture or golden file that shows the emitted member type of
  an enumeration-based item. Procedure C# under `NetModel/Web` was not searched by variable.

What is **not** known: the SDK's own resolved type for an item based on an untyped
enumeration (re-verification needed), the emitted member type in C#, and whether the SDK
emits enumeration constants in the generated class.

## Candidate rule (not applied)

Untyped enumeration → `NUMERIC(stored Length, or 4 when none)`, decimals 0. It fits the
four SDK rows above and the untyped-item rule. It is not applied because the SDK rows are
not re-verifiable here, no C# shows the emitted member, and the constants question is
open. A wrong type changes the generated class silently, so the refusal stays.

Proof plan (needs the user's approval where it writes):

1. Read-only: `genexus_inspect include=["structure"]` on one SDT item per shape (MapType,
   PlaybackState, MessageTypes, SmartDeviceType) to record the SDK's resolved type.
2. Scratch SDT in a disposable Knowledge Base (`C:\KBs\KBTeste*` only, with approval):
   generate it and compare each member's C# type, and any enumeration constants, with the
   candidate rule.
3. Only then change `domain_data_type` for untyped enumerations, with the mutation check in
   `enum_domain_tests.rs` flipped to a positive test.

## Allowlist refusals

`DOMAIN_ROW_PROPERTIES` is a deliberate fail-closed list: "any other name still refuses the
row, so an unseen property is never ignored silently". Typed enumerations refused only by
it: GX18 `CameraAPIQuality`, `NetworkAPIConnectionType`, `MediaMetadataKey`, `IMEMode`,
`Encoding`, `CryptoEncryptAlgorithm`, `CryptoHashAlgorithm`, `CryptoSignAlgorithm`,
`TrnMode`, `HttpMethod` (`ControlType`/`Description`), `LocationAccuracy` (`Signed`),
`AccessTokenProvider` (`EmptyAsNull`, plus `AUTONUMBER*`, `AttAvgLen`, `AttCollection`,
`idDATEFORMAT`); GX16 the same set minus `LocationAccuracy` and `AccessTokenProvider`.

Naming `Description` or `ControlType` would unlock most of them. Nothing on disk shows
that either property leaves the type unchanged: `Description` also appears on a non-enum
typed domain (`OneTimeCode`), and no SDK resolved type exists for one of these rows. So
the allowlist stays. The guard test `a_typed_enumeration_with_an_unlisted_property_is_refused_before_its_type_is_read`
(`CameraAPIQuality`) is the decision point: it fails if `Description` is added without a change to the test.

## Correction to `udm_storage_notes.md`

The row "`idBasedOn` an enumeration with a `Length` but no `ATTCUSTOMTYPE` (`MessageTypes`,
Length 2)" says that `SmartDeviceType` "stores Length 1 and Decimals 1". The stored rows
(GX16 id 48, GX18 id 23) carry `Length 1` and `AttMaxLen 1` and **no `Decimals`**. The
rationale built on the Decimals claim no longer holds. Replace that sentence with: "`SmartDeviceType`
stores `Length 1`, `AttMaxLen 1` and no `Decimals`." The row itself stays refused.

## Mutation checks

Run against `src/gx-engine/src/udm/stored_variables.rs`, then restored byte-for-byte
(verified with `cmp`, and the file matches the seed in git):

| Mutation | Caught by |
| :--- | :--- |
| M1: an untyped enumeration decodes as `NUMERIC(Length or 4)` | `an_untyped_enumeration_is_refused_whatever_length_its_row_stores`, `an_untyped_enumeration_with_decimals_is_refused`, `an_untyped_enumeration_row_from_each_knowledge_base_is_refused` |
| M2: `Description` added to `DOMAIN_ROW_PROPERTIES` | `a_typed_enumeration_with_an_unlisted_property_is_refused_before_its_type_is_read` |
| M3: a typed row with no `Length` defaults to `Length 4` | `a_typed_enumeration_without_a_stored_length_is_refused` |

## Reproduce

1. The SQL above, per KB, `sqlcmd -S "(LocalDB)\MSSQLLocalDB" -E -d GX_KB_<name>`.
2. Classify each row with `domain_data_type` (a temporary test that reads the rows and
   prints one line per enumeration; not kept in the tree).
3. `cargo test --lib enum_domain_tests` in `src/gx-engine`.

## Probe evidence: SDK structure view (KBTesteFix16, GeneXus 16.0.11.144151)

Scratch SDT `RustProbeEnum1` (id 47) was created in `KBTesteFix16` (kept, not deleted).
Its six items are based on the domains below. `genexus_structure get_visual` returned
these types. They are the SDK's structure view, not emitted C#.

| Item | Domain | Stored row (KBTeste16 dump) | SDK structure type |
| :--- | :--- | :--- | :--- |
| MapTypeItem | MapType | untyped, no Length | NUMERIC(4,0) |
| PlaybackStateItem | PlaybackState | untyped, no Length | NUMERIC(4,0) |
| MessageTypesItem | MessageTypes | untyped, Length 2 | NUMERIC(2,0) |
| SmartDeviceTypeItem | SmartDeviceType | untyped, Length 1 | NUMERIC(1,0) |
| CameraAPIQualityItem | CameraAPIQuality | typed NUMERIC(4), Length 1, extra `Description` | NUMERIC(1,0) |
| EncodingItem | Encoding | typed CHARACTER(5), Length 256, extra `Description` | CHARACTER(256) |

- The four untyped rows match the candidate rule (untyped → NUMERIC(Length or 4), decimals 0).
- The two typed rows with an extra `Description` keep the type that ATTCUSTOMTYPE and Length
  imply. This is evidence for widening the allowlist for `Description` only, not a proof.
- **Emitted C# not obtained.** `genexus_lifecycle action=build` on `RustProbeEnum1` (operation
  `1e690c8d`) reached a GeneXus Cloud request (`Solicitação de Nuvem GeneXus`) and failed with
  "missing authentication information". Stages reached: OpeningKB, Specifying, Generating,
  Compiling, Finishing. No C# was written. The build was not repeated.
- The Fix16 MDF carries the internal name `GX_KB_KBTeste16`. The stored rows above come from
  the KBTeste16 dump; Fix16 rows were not compared one by one.
- Consequence: no refusal is lifted and `DOMAIN_ROW_PROPERTIES` is not widened. Both need
  emitted-C# evidence first.

### Follow-up: emitted-C# search and one specify (no Cloud reached)

- **Read-only search of SDK output.** No `.cs` file under `KBTeste`, `KBTeste16`, `KBTeste17`
  or `KBTesteProbe2` mentions MapType, PlaybackState, MessageTypes, SmartDeviceType,
  CameraAPIQuality or Encoding. `KBTeste16/.gx-engine/build/` holds Rust-engine output, not
  SDK output, so it is not evidence.
- **One specify.** `genexus_lifecycle action=specify` on `RustProbeEnum1` (task `a2b9a69e`)
  failed at once: "never built, not specified". GeneXus creates the environment copy on the
  first build, and that build (`1e690c8d`) is the step that reached the Cloud request. The
  specify was not retried, and no other object was tried.
- **Result:** no emitted C# for any of the six shapes. No refusal is lifted and
  `DOMAIN_ROW_PROPERTIES` is not widened.
- **Side effects on KBTesteFix16:** the first SDK open created `Locks/` and `GXSPC002/` and
  rewrote `KBTesteFix16.gxw` (307 to 345 bytes). The MDF changed at 09:48:34, after the
  backup `C:/tmp/kbbak-KBTesteFix16-0948`. The KB was closed and the scratch gateway stopped.
  A 0-byte lock `ce028c18-...lock` remains in `Locks/`.

### Follow-up 2: local MSBuild build against Genexus.Tasks.targets (stopped at Cloud)

- **Command:** `"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" /nologo /m:1 /v:n /nodeReuse:false /target:Execute "C:\tmp\gxe-enum\msbuild\rustprobe-fix16-build.proj" "/flp:logfile=C:\tmp\gxe-enum\msbuild\rustprobe-fix16-build.msbuild.log;verbosity=detailed"`. The 32-bit MSBuild 4.0 is used because the GeneXus task assembly is 32-bit.
- **Project:** imports `C:\Program Files (x86)\GeneXus\GeneXus16\Genexus.Tasks.targets`; `OpenKnowledgeBase Directory=C:\KBs\KBTesteFix16 Output=IDE`; `BuildOne ObjectName=RustProbeEnum1 ForceRebuild=false Output=IDE`; `CloseKnowledgeBase`. `OnError` runs `CloseKnowledgeBase`. `SetActiveEnvironment` was not used.
- **Result:** exit code 1, 0 errors reported by MSBuild, 11.50 s elapsed.
- **Stop point:** `BuildOne` reached `Solicitação de Nuvem GeneXus` → `Informações ausentes para autenticação.` → `Referência de objeto não definida para uma instância de um objeto.` The Cloud login was not opened and no credentials were sent. The message says no authentication information was available, so no login was attempted.
- **KB side effects:** the MDF and LDF were rewritten at 10:05:09 (sizes unchanged). `OnError` closed the KB. `KBTesteFix16.gxw` and the stale lock are unchanged since 09:49:34.
- **Result for the proof:** no SDK-generated C# exists for `RustProbeEnum1`, so no refusal is lifted.

### Follow-up 3: GX16 local build diagnosis (Generate returned false; no C# emitted)

- **Run:** `MSBuild.exe rust-probe-enum-fix16.proj /nologo /verbosity:minimal` (GX16 `Genexus.Tasks.targets`, the proven inline CodeTaskFactory path with the object name, KB path and SDT GUID changed). Exit code 1.
- **Stages:** open OK; `DeployToCloud=Yes` read; copy `success=True` in 0.19 ms; specify `success=True`; generate `success=False`. No Cloud text, no compile.
- **Cause (strong evidence, not yet re-run):** the working model received no objects. The target model holds 4 rows (module skeleton), and the design model holds 1121 rows including `RustProbeEnum1`. GX16 `DevelopmentWorkingSet` has a one-argument constructor whose parameter is the **working** model, and the probe passed the design model, so the copy ran design to design (a no-op). The probe's GX18 path used the same call, so this is a GX16 signature difference.
- **GX16 overloads found by reflection:** `DevelopmentWorkingSet(KBModel designModel, KBModel workingModel)`, `DevelopmentWorkingSet(KBModel workingModel)`, `CopyModelCommand(DevelopmentWorkingSet).Do()`, `IGeneratorsService.Generate(KBModel, int, bool)`, `ISpecifierService.SpecifyObjects(KBModel, IEnumerable, BuildOptions)` and `SpecifyObjects(KBModel, KBModel, IEnumerable, BuildOptions)`.
- **Proposed fix (not run):** build the working set from the target model, `new DevelopmentWorkingSet(design, target)`, and keep generator 41. Generator 41 is unverified for GX16; the `CSharpModel/web/log4j2.xml` created by the run shows the C# generator started.
- **KB side effects:** the scratch open created `Data001`, `GXSPC001`, `swluc/`, `GXSPC002/GEN-1/NVG`, `GXLOCK/LOCKS`, `CSharpModel/` and `nav_objs.xml` (0 bytes), `MM2.ari`, `M1.ari`, `generators_translations.ari`. The MDF and LDF kept changing after the run.
