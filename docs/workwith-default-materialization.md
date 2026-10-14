# WorkWith PatternInstance default materialization — SDK investigation and acceptance

> Investigation record for issue [#477](https://github.com/lennix1337/Genexus18MCP/issues/477):
> Materialization of PatternInstance XML defaults (e.g. `page="<default>"` → `10`) during `genexus_workwith add_action`.

## Problem summary

When adding a custom action to a standard WorkWith (WorkWith for Web) instance via `genexus_workwith add_action` (e.g. action `ExecutaTeste` on tab `General`), the instance XML is re-serialized. In addition to inserting the requested action, an independent post-write re-read revealed:

1. Dynamic default markers were replaced with concrete defaults:
   - `Selection @page`: `<default>` → `10`
   - Tab grid `Orders @page`: `<default>` → `10`
2. Root and container actions were explicitly expanded:
   - Root `afterInsert` / `afterUpdate` default navigation (`Return to Caller`)
   - Default actions on Selection and tab grids
   - Total line count moved from ~143 lines to ~203 lines (54 element/attribute additions).
3. The mutation receipt (`WorkWithActionAdded`) did not identify the execution route (`native-sdk` vs `xml-fallback`) or declare the verification scope.

## Controlled fixture reproduction

A controlled fixture containing explicit default markers and neighbor tab structures was evaluated:

```xml
<WorkWithInstance xmlns="http://schemas.genexus.com/Patterns/WorkWith/v1.0">
  <level>
    <selection page="&lt;default&gt;">
      <actions>
        <action name="Export" caption="Export" />
      </actions>
    </selection>
    <view>
      <tabs>
        <tab code="General" name="General" type="Tabular">
          <actions>
            <action name="Update" caption="Update" />
            <action name="Delete" caption="Delete" />
          </actions>
        </tab>
        <tab code="Orders" name="Orders" type="Grid" page="&lt;default&gt;">
          <actions>
            <action name="ViewOrder" caption="View Order" />
          </actions>
        </tab>
      </tabs>
    </view>
  </level>
</WorkWithInstance>
```

### Findings by execution route

| Aspect | Native SDK route (`WorkWithInstance.Load` + `SaveTo`) | XML fallback route (`TryAddActionToXml`) |
|---|---|---|
| **Mechanism** | Loads strongly-typed object model via `Artech.Patterns.WorkWith.WorkWithInstance.Load(instanceObj)`, invokes `tab.AddAction(...)`, and serializes back using `SaveTo(instanceObj)`. | Parses XML using `XDocument.Parse(LoadOptions.PreserveWhitespace)`, mutates only the targeted `<actions>` node, and outputs using `SaveOptions.DisableFormatting`. |
| **`page="<default>"` on Selection** | Materialized to concrete `10` by the GeneXus Pattern serialization engine. | Preserved verbatim as `<default>`. |
| **Neighbor tabs & attributes** | Default actions and properties expanded by the GeneXus SDK serializer. | Preserved verbatim; no unrequested element or attribute is altered or reformatted. |
| **Custom action placement** | Appended to target tab's actions collection. | Appended to target tab's actions collection. |
| **Performance** | STA thread serial execution; GeneXus transaction lifecycle. | In-memory XML transformation (sub-millisecond). |

## Analysis of SDK behavior and semantics

### 1. Is materialization expected SDK behavior?

**Yes.** In GeneXus's Pattern architecture:
- When `WorkWithInstance.Load` deserializes a `PatternInstance`, property values set to `<default>` resolve to the pattern's template default values in memory (e.g., standard page size `10`, default standard action chains).
- When `SaveTo` writes the in-memory graph back to the `PatternInstance` part, GeneXus's serializer writes out explicit attributes for those properties rather than re-abstracting them to `<default>`.
- This matches standard GeneXus IDE behavior: opening an instance in the Pattern Editor and saving it often normalizes default attributes and action containers into explicit XML nodes.

### 2. Does materialization preserve pattern bindings?

**Partly — and not in the way the receipt originally implied.**

Preserved: the instance identity (GUID), the parent Transaction link, tab
definitions, existing actions, and the newly added custom action remain intact.
The generated web components and list continue to function identically.

**Not preserved: the link to the template default.** When an attribute changes
from `<default>` to a concrete `10`, GeneXus treats it as an explicit
instance-level override. If the pattern template's default page size is later
modified at the environment level, instances with explicit `page="10"` retain
`10`. This is standard GeneXus override precedence, not a defect of the SDK, but
it is a real loss of dynamic binding for that attribute.

This is why `defaultNormalization` no longer claims that "pattern bindings are
preserved". An agent that believes the instance still tracks the template default
will reason incorrectly about the effect of a later change to the pattern.

**Not verified:** the "later template default change" case was not executed in an
isolated fixture. The conclusion above is read from GeneXus override precedence
and from the observed before/after serialization, not from a measured run.
Closing that gap needs a live Knowledge Base with an authored pattern; see
"Residue" below.

## MCP contract improvements

To ensure complete auditability, predictability, and safety:

1. **Route identification**:
   Both `dryRun` plan and write receipt now explicitly report `route`:
   - `"native-sdk"` when executed on the GeneXus STA thread through `Artech.Patterns.WorkWith`.
   - `"xml-fallback"` when executed via structured XML fallback in headless or test environments.

2. **Default normalization disclosure**:
   The receipt and plan carry `defaultNormalization`, stating that native SDK serialization
   normalizes pattern defaults (such as `page="<default>"` to concrete values) upon save
   while the instance stays functional, **and that a materialized attribute becomes an
   explicit instance override which no longer follows a later change to the pattern
   template default**. Byte-by-byte literal preservation of unmutated parts is not promised
   when the native SDK serializer expands defaults, and neither is continued binding to the
   template default.

3. **Neighbor tab integrity verification**:
   The post-write verification re-read now explicitly verifies that the count and codes of neighbor tabs are preserved (`verifyTabs.Count >= tabs.Count`), failing closed with `VerificationFailed` if any neighbor tab was dropped.

4. **XML fallback formatting preservation**:
   `TryAddActionToXml` uses `SaveOptions.DisableFormatting` to guarantee that unmutated elements, whitespace, and literal `<default>` attributes are preserved without reformatting.

## Acceptance criteria checklist

- [x] Reproduce in controlled fixture with `page="<default>"` on Selection and a neighbor tab (`TryAddActionToXml_ControlledFixtureWithDefaultsAndNeighborTabs_PreservesDefaultsAndNeighborStructure`).
- [x] Determine that materialization is standard GeneXus Pattern SDK serializer behavior.
- [x] Document semantic equivalence, and state the override consequence that the
      earlier draft answered as "preserved".
- [x] Expose `route`, `verificationScope`, and `defaultNormalization` in `genexus_workwith` plans and receipts.
- [x] Fail-closed verification guarding neighbor tabs from truncation.

## Residue

- [ ] **Later template default change, in an isolated fixture.** The issue asked to
      determine whether the link survives a subsequent change to the pattern template
      default. It was answered from override precedence and observed serialization, not
      from a measured run. Needs a live GeneXus Knowledge Base with an authored pattern
      and a second save after the template default changes. Until then, treat
      "no longer follows the template default" as documented inference, not measurement.
