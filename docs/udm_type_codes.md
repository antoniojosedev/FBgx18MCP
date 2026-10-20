# Stored type codes: SDK evidence and status

Scope: the stored type codes that `gx-engine` refuses or maps for SDT items and Procedure
variables, and what the SDK evidences for each. Companion to
[`udm_storage_notes.md`](udm_storage_notes.md) (SDT item mapping). Code:
[`src/gx-engine/src/udm/type_evidence.rs`](../src/gx-engine/src/udm/type_evidence.rs).

Two kinds of evidence are recorded here:

- **GeneXus type name.** The SDK's own text for a declaration with that stored code (the SDT
  `Structure` text, and the structured `variables` of Procedures), read-only, on the disposable
  Knowledge Bases `KBTeste` (GX18) and `KBTeste16` (GX16). The stored code and the SDK type name
  are paired on the same declaration.
- **C# type.** Not established by any of the sources available to this lane. A code is mapped only
  when its C# type is evidenced; otherwise it stays refused by name.

## Codes

| Stored code | SDK type | Declarations that carry it (SDK text) | C# type | Status |
| :--- | :--- | :--- | :--- | :--- |
| 5 (no stored length, no `AttMaxLen`, no `Decimals`), **variable** | CHARACTER(20) | `FilterName`, `FilterValue` of `AddDeviceTargetFilter` and `RemoveDeviceTargetFilter` (four declarations, each with SDK length 20, observed in both GX16 and GX18) | `string`, length 20 | **mapped** (`type_evidence::variable_props`) |
| 5 (no stored length), **SDT item** | CHARACTER(20) | `AnalyticsPurchase.CurrencyCode` and others | `string`, length 20 | mapped (earlier evidence, `stored_sdt::evidenced_length`) |
| 7 | BITMAP | `ContactInfo.Photo : BITMAP(1000)`; `ImagesCollection.Image : BITMAP(4)`; `CardInformation.CardImage`, `CardLogo`; `AudioPlayerCustomAction.Image` | `string` with a `_gxi` companion (Nemo mapping, CSharpWeb `SDTs.stg`/`Properties.stg`) | mapped as `Image`; refused as a transaction or web panel attribute, and refused where read or written in a procedure |
| 8 | LONGVARCHAR | `NotificationInfo.Message`, `OAuthAuthorizationConfig.ClientId`, `Oauth20AccessTokenSDT.access_token`; variable `RefreshToken` (GX18) | `string`, as VARCHAR (Nemo mapping code 8 -> `string`); no stored length on the rows that carry it | mapped as `LongVarchar`; refused as a transaction or web panel attribute, and no SQL column type is evidenced |
| 45 | GUID | `BeaconInfo.UUID : GUID(4)`; `SynchronizationEventList.EventId : GUID(4)` | `Guid`, default `Guid.Empty` (Nemo mapping, CSharpWeb templates) | mapped as `Guid`; refused where read or written in a procedure, as an attribute, and as a SQL column |
| 102 | GEOGRAPHY | `MapGeographies.Feature : GEOGRAPHY(255)` | `Geospatial`, default `new Geospatial("")` (Nemo mapping codes 102-105 -> `geospatial`) | mapped as `Geospatial`; same refusals as code 45 |
| 103 | GEOPOINT | `LocationInfo.Location : GEOPOINT(50)`; `DirectionsRequestParameters.sourceLocation : GEOPOINT(255)`; `MapRegion.NorthEast : GEOPOINT(255)`; `LocationProximityAlert.GeoLocation : GEOPOINT(50)` | `Geospatial`, default `new Geospatial("")` (as code 102) | mapped as `Geospatial`; same refusals as code 45 |
| 255 | GX_USRDEFTYP (user-defined type, `<Guid>70</Guid>`) | variable `FilterTags` of `SetDeviceTargetMultipleFilters` (GX16 and GX18) | not evidenced | refused by name |

Update (this change): codes 7, 8, 45, 102 and 103 are now mapped in `udm/stored_variables.rs` and
`csharp/types.rs`. The C# types come from the SDK's Nemo type mapping and the CSharpWeb templates, not
from a compiled SDK assembly, which is why the "Why the C# type is not evidenced" section below still
holds for compiled output. Codes 104 and 105 (GEOLINE, GEOPOLYGON) are mapped to `Geospatial` on the
mapping alone; no KB declaration of them was observed. The GeneXus library SDTs that carry these types
(`ExternalNamespace` = `GeneXus`) are not generated into `KBTeste/NetModel/Web`; the SDK's build list
there (`GeneXus.Programs.Common.sdts.targets`) names only the 13 user SDTs.

A refusal keeps the text `type code N is not mapped by this engine` and, when the SDK name is
known, adds `(SDK type NAME: its C# type is not evidenced)`.

## Why the C# type is not evidenced

- No SDK-compiled assembly declares a member of any of these types. Reflection over the 67 procedure
  assemblies under `KBTeste/NetModel/build/*/bin/Release/net8.0`, and their SDT classes, finds only
  `String`, `Int16`, `Int32`, `DateTime`, `Boolean` and collections of SDT items (`GeneXus.Programs`).
- `KBTeste16/CSharpModel` and `KBTeste17/CSharpModel` hold no generated `.cs`. The `.cs` files under
  `KBTeste/NetModel/Web` are procedures and web objects that use none of these codes.
- The GeneXus install (`Artech.Generator.DotNetCore.dll`) contains the type names only as identifiers;
  its type-to-C# mapping is code, not data, and was not decoded.

Closing a row requires an SDK-generated declaration of that type: a compiled object or SDT that holds
a member of the type (read by reflection, as for the `gxTpr_` members), or a generated C# file. That
is a live SDK build of a disposable KB, which this lane did not run.

## SDT leaf defaults: read values are not generation evidence

On GX18, the SDK reads the plain `Amount` leaf of `RustRecordProbe1015` as
NUMERIC(4,0), even though the stored declaration omits Length and Decimals.
The control `RustFreshSdt091817` also reads as 4/0. SQL/design snapshots stayed
unchanged after those reads: a resolved default is not a persisted property.
Read-only SDK metadata inspection found the same default values in GX16, GX17
and GX18; only GX18 was runtime-checked.

The bounded read contract is:

- A plain typed NUMERIC leaf with no stored Length/Decimals reads as 4/0.
- A plain leaf with no stored custom type also defaults to NUMERIC(4,0), based
  on metadata inspection only, not a runtime untyped-item observation.
- Stored values take precedence. Existing CHARACTER20/VARCHAR40 defaults agree
  with the inspected SDK metadata.
- BasedOn, Domain, VarBasedOn, SuperType and ExoItemBasedOn paths are outside
  this evidence. Do not apply a plain-leaf default to inherited types.

This does **not** establish emitted C# width or authorize a generation refusal
lift. No generated declaration was observed for the missing-Length leaf.
The engine's refusal is stricter than the SDK read contract; `short` remains an
unverified generation expectation. Evidence and six KB-free fixture cases:
local `C:/tmp/gxe-len/FINDINGS.md`, runtime readback
`C:/tmp/gxe-sdt/readback/readback-clean.log`.

## Variable default (CHARACTER without a stored length)

The evidence: on both KBs the SDK reports `CHARACTER(20)` for each of the four non-collection CHARACTER
variables whose stored declaration has no `Length`, no `AttMaxLen` and no `Decimals`: `FilterName` and
`FilterValue` in `AddDeviceTargetFilter` and `RemoveDeviceTargetFilter` (8 observations; probe output
`gxe-types-probe/var_compare.json`, which lists every CHARACTER variable of that shape it found).

That the default covers every such variable is an **inference** from those four declarations. No other
variable of that shape was seen with a different SDK length (a search of the probe output found none).

Two more variables of that shape, `Groups` in `AddDeviceGroups` and `RemoveDeviceGroups`, are collections
(`AttCollection True` in the stored rows, refused as "collections are not mapped"). The default is applied
after that refusal, so they stay refused. A declaration with `AttMaxLen` but no `Length` stays refused, and
so does one with `Decimals`. The SDT item default for the same shape was already evidenced in
[`udm_storage_notes.md`](udm_storage_notes.md).
