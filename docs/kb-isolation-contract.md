# KB isolation and local authorization contract

This document defines the target contract for issue #146. It separates
configuration registration from the user's explicit choice of a local
Knowledge Base.

## Configuration and transport

The new configuration format uses `ConfigSchemaVersion` so it does not collide
with the MCP response `_meta.schemaVersion` (`mcp-axi/2`). `GatewayMode` is
explicit and is not inferred from `HttpPort` or `McpStdio`.

The supported combinations are:

| GatewayMode | ResolutionPolicy | Meaning |
| --- | --- | --- |
| `stdio-isolated` | `strict` | default isolated local process |
| `stdio-isolated` | `legacy` | explicit compatibility mode without gateway lease or HTTP |
| `http-shared` | `strict` | shared endpoint with explicit context and ownership |
| `http-shared` | `legacy` | compatibility behavior with legacy fallback warnings |

`stdio-isolated` never uses `GatewayProcessLease`, HTTP, master/proxy,
promotion, or port recovery. A hybrid configuration is invalid in strict mode.

## Local-friendly authorization

The default local-friendly policy treats an explicit absolute path to a valid
local GeneXus KB as sufficient user authorization for `genexus_kb action=open`.
It does not require a separate trust-root command for normal single-user use.

The gateway still validates the final physical path and refuses ambiguous
identity, alias rebind, malformed/relative paths, and cross-owner lease use.
Those checks prevent accidental cross-context operations; they are not a
second confirmation prompt for the same explicit user request.

An optional hardened policy may require pre-provisioned trusted roots,
additional ACL checks, and stricter local authorization. Hardened policy is
not the default and must be visible in diagnostics.

## Identity and selection

Alias is a display label, not physical identity. The target model uses an
opaque stable `kbId` for the physical KB and a separate worker/context
generation. New selectors are typed as `{ "kbId": "..." }` or
`{ "alias": "..." }`; an untyped string remains legacy-only.

The gateway stores lease and selection context internally when possible. The
client should not need to copy a lease token into every ordinary tool call.
Explicit selection prevents ambiguity, while automatic lease renewal and
release preserve the normal local workflow.

## Compatibility

Legacy configuration and untyped selectors remain available only through the
explicit legacy path. The client registration itself must point to the neutral
runtime configuration and must not contain a KB path, alias, default, or
catalog.