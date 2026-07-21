# Plan 034: `SourceParser.SkipString` uses GeneXus string-literal grammar (doubled-quote, not backslash)

> **Executor instructions**: Follow step by step, verify each step, touch only in-scope
> files, STOP on any STOP condition, commit per Git workflow. SKIP updating
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat f63f204..HEAD -- src/GxMcp.Worker/Services/SourceParser.cs`
> Mismatch vs the excerpt = STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f63f204`, 2026-07-21

## Why this matters

`SkipString` tokenizes string literals using **C-style backslash escaping**. GeneXus
does not use backslash escapes — a literal quote inside a string is escaped by doubling
it (`""`), and backslash is an ordinary character. So a GX string ending in a single
backslash before its closing quote — common in Windows paths / UNC constants like
`"C:\Temp\"` or `"\\server\share\"` — makes `SkipString` consume the backslash **and**
the closing quote as a fake 2-char escape, run past the end of the literal, and
desync the rest of tokenization (paren depth, identifiers, call detection). `SkipString`
feeds `ParseCalls`, consumed by `AnalyzeService` (call-graph/impact) and
`SourceSearchService` (source search) — both silently return wrong results for any
source containing such a string.

## Current state

- `src/GxMcp.Worker/Services/SourceParser.cs`, `SkipString` (lines 132–143):

```csharp
private static int SkipString(string s, int i, char quote, ref int line, ref int lineStart)
{
    i++;
    while (i < s.Length)
    {
        if (s[i] == '\\' && i + 1 < s.Length) { i += 2; continue; }   // <-- wrong: GX has no backslash escape
        if (s[i] == '\n') { line++; lineStart = i + 1; }
        if (s[i] == quote) return i + 1;
        i++;
    }
    return i;
}
```

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set GX path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | — |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Targeted tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceParser"` | all pass |

`MSB3027`/`MSB3021` lock → `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`.

## Scope

**In scope:**
- `src/GxMcp.Worker/Services/SourceParser.cs` — `SkipString` only.
- `src/GxMcp.Worker.Tests/` — a test (create `SourceParserStringEscapeTests.cs` if no
  `SourceParser` test exists).

**Out of scope:** other `SourceParser` methods; `ParseCalls` logic; consumers.

## Git workflow

- Branch: `advisor/034-sourceparser-gx-string-escape`
- One commit: `fix(parser): handle GeneXus doubled-quote string escapes, not backslash`
- Do NOT push.

## Steps

### Step 1: Rewrite `SkipString` for GeneXus grammar

Remove the backslash-escape branch. Replace with doubled-quote handling: when the
current char equals `quote`, look ahead one char — if the next char is also `quote`,
it's an escaped quote (consume both, stay in the string); otherwise it's the
terminator (return `i + 1`). Keep the newline line/col tracking. Target shape:

```csharp
private static int SkipString(string s, int i, char quote, ref int line, ref int lineStart)
{
    i++;
    while (i < s.Length)
    {
        if (s[i] == '\n') { line++; lineStart = i + 1; }
        if (s[i] == quote)
        {
            if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; } // escaped ""
            return i + 1;                                                    // terminator
        }
        i++;
    }
    return i;
}
```

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Test

`SkipString` is `private static`. If `ParseCalls` (or another public entry) is reachable
in tests, assert via it; otherwise the test project may already use reflection or an
`internal` seam for `SourceParser` — follow whatever the existing `SourceParser`/`ParseCalls`
tests do (search the test project). Cover, through `ParseCalls` on a source string:
- A string literal ending in a single backslash before its closing quote (e.g. a
  `msg("path is C:\Temp\")` line) followed by a real call `DoThing()` on the next line
  → `DoThing` is still detected (proves tokenization no longer desyncs).
- A doubled-quote escape inside a string (`"say ""hi"""`) is skipped correctly and a
  following call is detected.

If `ParseCalls` needs a live SDK KB (it should not — it's pure string parsing), and no
seam exists, add an `internal`-visible test entry only if `InternalsVisibleTo` is
already configured for the worker test project; if not, STOP and report rather than
adding assembly-visibility plumbing.

**Verify**: `dotnet test ...GxMcp.Worker.Tests... --filter "FullyQualifiedName~SourceParser"` → all pass.

## Done criteria

- [ ] Build exits 0
- [ ] `SkipString` no longer has a `s[i] == '\\'` branch
- [ ] A backslash-terminated-string test proves a following call is still parsed
- [ ] `git status` shows only in-scope files

## STOP conditions

- Live `SkipString` differs from the excerpt (drift).
- `ParseCalls` turns out to need a live SDK/KB (it shouldn't) — report.
- No test seam for `SourceParser` and no existing `InternalsVisibleTo` — report; do not
  add new visibility plumbing.

## Maintenance notes

- If GeneXus ever introduces a different escape convention, revisit; today it's
  doubled-quote only.
- Reviewer: confirm the doubled-quote lookahead can't run off the end of the string
  (`i + 1 < s.Length` guard).
