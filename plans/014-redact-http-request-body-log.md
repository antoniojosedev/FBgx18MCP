# Plan 014: Stop writing raw JSON-RPC request bodies (possible credentials) to the gateway log

> **Executor instructions**: Follow step by step; run every verification command;
> honor STOP conditions. Update this plan's row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 9fe6817..HEAD -- src/GxMcp.Gateway/Program.Http.cs`
> On any change, diff the "Current state" excerpt against live code; mismatch = STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `9fe6817`, 2026-07-20

## Why this matters

The HTTP `/mcp` handler logs the first 100 characters of every raw request body to
the durable `gateway_debug.log` (rotating, persists across restarts). Some tools
accept live credentials as JSON-RPC arguments (`genexus_gxserver`
commit/update/resolve take `user`/`password`/`token`; `genexus_run_object` takes a
`gamSession` `user`/`pass`; `genexus_ai_complete` proxy calls). Depending on tool
name length and argument key ordering — both caller-controlled — a plaintext
credential can land within that logged prefix and be written to disk. The team
already redacts on the *response* side (`AiCompleteService` gates full-body logging
behind `GXMCP_AI_COMPLETE_DEBUG`); this applies the same discipline to the inbound
request-logging path. The fix is purely subtractive to a diagnostic line.

## Current state

- `src/GxMcp.Gateway/Program.Http.cs` — HTTP MCP endpoint handler.

Excerpt (`Program.Http.cs:131-134`):

```csharp
id = requestObj["id"]?.ToString() ?? "no-id";
string method = requestObj["method"]?.ToString() ?? "unknown";
string bodyBrief = body.Length > 100 ? body.Substring(0, 100) + "..." : body;
Log($"[HTTP] Received {method} (ID: {id}) - Body: {bodyBrief}");
```

- `requestObj` is a parsed `Newtonsoft.Json.Linq.JObject` (the file already uses
  `requestObj["..."]`). `Log(...)` is the gateway's file logger.
- Convention: this file uses Newtonsoft JSON (`JObject`/`JToken`), string
  interpolation for log lines.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | Build succeeded |
| Test | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~Redact"` | all pass |

If the build fails with an MSB lock on `GxMcp.Gateway.exe`, stop the dev gateway
(`Stop-Process -Name GxMcp.Gateway -Force`) and retry.

## Scope

**In scope**:
- `src/GxMcp.Gateway/Program.Http.cs`
- One new small helper for redaction — put it as a `private static string` method
  in the same partial `Program` class in `Program.Http.cs`, OR a tiny internal
  static class in a new file `src/GxMcp.Gateway/LogRedaction.cs` (choose whichever
  matches how the file already organizes helpers — check the end of `Program.Http.cs`
  for existing local helpers first).
- `src/GxMcp.Gateway.Tests/LogRedactionTests.cs` (create) if you extract a helper.

**Out of scope**:
- The `Log()` implementation and log-rotation logic (`Program.cs`).
- Response-side / SSE logging (already redacted).
- Any change to what is actually processed — only the logged string changes.

## Steps

### Step 1: Replace the raw-body log with a redacted summary

Instead of logging a raw 100-char substring, log the method, id, and the argument
*key names* with sensitive values masked. Add a helper that walks
`params.arguments` and masks values whose key matches a sensitive set:

```csharp
// Sensitive-key substrings (case-insensitive). Values under a matching key are masked.
private static readonly string[] SensitiveKeys = { "password", "passwd", "pass", "token", "secret", "key", "credential", "authorization", "apikey" };

private static string RedactBodyForLog(JObject requestObj)
{
    try
    {
        var args = requestObj?["params"]?["arguments"] as JObject;
        if (args == null) return "(no arguments)";
        var parts = new List<string>();
        foreach (var prop in args.Properties())
        {
            bool sensitive = SensitiveKeys.Any(k => prop.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            string shown = sensitive || prop.Value.Type == JTokenType.Object || prop.Value.Type == JTokenType.Array
                ? "***"
                : Truncate(prop.Value.ToString(), 40);
            parts.Add(prop.Name + "=" + shown);
        }
        return "{" + string.Join(", ", parts) + "}";
    }
    catch { return "(unparseable)"; }
}

private static string Truncate(string s, int n) => s == null ? "" : (s.Length > n ? s.Substring(0, n) + "…" : s);
```

Then change the log call:

```csharp
string method = requestObj["method"]?.ToString() ?? "unknown";
Log($"[HTTP] Received {method} (ID: {id}) - Args: {RedactBodyForLog(requestObj)}");
```

Remove the `bodyBrief`/`body.Substring(0,100)` line entirely. Add `using System.Linq;`
and `using System.Collections.Generic;` at the top of the file if not already present
(check first).

Note: even a *non-sensitive* nested object/array value is shown as `***` above, so a
credential nested one level deep (e.g. `gamSession: { pass: ... }`) is never logged.
That is intentional — keep it.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → Build succeeded.

## Test plan

- If you put `RedactBodyForLog` as `private static`, make it `internal static` and
  ensure `[assembly: InternalsVisibleTo("GxMcp.Gateway.Tests")]` exists (grep:
  `grep -rn "InternalsVisibleTo" src/GxMcp.Gateway`); add it only if missing. Or, if
  you created `LogRedaction.cs` with a public/internal static class, test that
  directly.
- Create `src/GxMcp.Gateway.Tests/LogRedactionTests.cs`. Cases (build the input
  `JObject` with `JObject.Parse`):
  - A `tools/call` body with `arguments: { "user": "alice", "password": "hunter2", "target": "Foo" }`
    → result contains `user=alice` and `target=Foo` but **does NOT contain**
    `hunter2` (assert `Assert.DoesNotContain("hunter2", result)`), and shows
    `password=***`.
  - Nested `arguments: { "gamSession": { "pass": "x" } }` → result does not contain
    the value; `gamSession=***`.
  - No `arguments` → `"(no arguments)"`, no throw.
  - `token`/`apikey`/`secret` keys masked.
- **Verification**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~Redact"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] `grep -n "Substring(0, 100)\|bodyBrief" src/GxMcp.Gateway/Program.Http.cs` → no matches
- [ ] New redaction test exists; a `password`/`token` value never appears in the produced log string (asserted with `DoesNotContain`)
- [ ] Full gateway suite green: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj`
- [ ] `plans/README.md` row updated

## STOP conditions

- `Program.Http.cs:131-134` doesn't match the "Current state" excerpt (drift).
- `body` (the raw string) is used somewhere *after* this line and removing the
  substring would break it — grep `grep -n "\bbody\b" src/GxMcp.Gateway/Program.Http.cs`
  first; only the log line should use `bodyBrief`.

## Maintenance notes

- When a new tool adds a credential-bearing argument, confirm its key contains one
  of the `SensitiveKeys` substrings; if not, add the key.
- Reviewer: verify no other log line in the gateway echoes raw request bodies
  (`grep -rn "Received.*Body\|requestBody\|rawBody" src/GxMcp.Gateway`).
