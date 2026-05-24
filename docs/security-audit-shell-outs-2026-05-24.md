# Security audit — shell-out paths (2026-05-24)

Threat model: every MCP tool argument is LLM-generated and may be adversarial
(prompt-injection in tool args is a real and recurring threat). This audit
walks every `Process.Start` / `ProcessStartInfo` site under
`src/GxMcp.Worker/Services/` and rates each LLM-controlled argument against
the documented threat list (argument injection, path traversal, leading-`-`
flag confusion, env-var injection, log leaks).

`UseShellExecute=false` is used everywhere, so cmd.exe shell metacharacters
(`&`, `|`, `;`, redirection) are NOT interpreted — but Windows' own
`CommandLineToArgv` parser still tokenises the single `Arguments` string,
which means a value ending in `\` or containing `"` can flip quoting modes
and bleed into the next argument. That's the dominant class of bug fixed in
this pass.

## Summary

| Severity | Count | Status |
| -------- | ----- | ------ |
| CRITICAL | 2     | Fixed (TestService XML-injection, BlameService path-traversal) |
| HIGH     | 2     | Fixed (GithubService trailing-`\` quoting + leading-`-`; TimeTravelService `at`/`name` validation) |
| MEDIUM   | 3     | Fixed in-pass (shared `ArgvQuote` helper rolled out to BlameService, CrossBrowserService, GeneratedDiffService, TimeTravelService) |
| LOW      | 4     | Left as follow-up (listed at the bottom) |

Files changed:
- `src/GxMcp.Worker/Services/GithubService.cs` — new `ArgvQuote` helper, leading-`-` validation on title/baseBranch.
- `src/GxMcp.Worker/Services/TimeTravelService.cs` — entrypoint validation for `name`/`at`, `--` separator on `git ls-tree`, shared `ArgvQuote`.
- `src/GxMcp.Worker/Services/BlameService.cs` — anchor resolved `filePath` under git root, shared `ArgvQuote`.
- `src/GxMcp.Worker/Services/TestService.cs` — allowlist on `target`, `SecurityElement.Escape` on every interpolated XML value.
- `src/GxMcp.Worker/Services/CrossBrowserService.cs` — shared `ArgvQuote`.
- `src/GxMcp.Worker/Services/GeneratedDiffService.cs` — shared `ArgvQuote`.

Test baseline preserved: 905 passed / 4 skipped (same as pre-audit baseline).

---

## TimeTravelService.cs

External invocations:
- `git --no-pager -c color.ui=false log --before=<at> -1 --pretty=%H`
- `git ... ls-tree -r --name-only <commit> -- <rel>` (the `--` separator is added by this audit)
- `git ... show <commit>:<path>`

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `at` (when not 7-40 hex) | LLM | Arg injection via embedded quote / leading `-` | **Fixed** — `IsSafeAtValue` allowlist at entry rejects metacharacters and leading `-`. Previously the regex only gated the *sha* path; the ISO branch piped `at` verbatim into `--before=` | The naive quoting (`Replace("\"","\\\"")`) also had the trailing-backslash bug; now uses shared `ArgvQuote`. |
| `name` | LLM | Path traversal via `Path.Combine(kbPath,"Objects",typeName,name)` | **Fixed** — `IsSafeObjectName` allowlist (`[A-Za-z0-9._-]{1,200}`). Previously `name = "..\\..\\Windows\\System32"` could Directory.Exists and feed an out-of-tree path into `git ls-tree`. The `MakeRelative` fallback returned the absolute path verbatim, so git would have been invoked with an out-of-repo absolute path. | LOW info-disclosure pre-fix (git rejects out-of-repo paths). |
| `commit` (from git stdout) | git | leading `-` flag confusion in `git show <commit>:<path>` | Defended in depth — `commit` is now either regex-validated hex or the trimmed stdout of `git log` (always 40-hex). Path positional clarified with `--`. | LOW. |

Attack strings tried:
- `at = "x\""` → before: produced `--before=x"`; git errors out. AFTER: rejected at entry.
- `at = "--upload-pack=..."` → before: passed as `--before=--upload-pack=...` (single token, harmless). AFTER: rejected.
- `name = "..\\..\\..\\Windows\\System32"` → before: `Directory.Exists` would succeed and git would see an absolute out-of-repo path. AFTER: rejected.

---

## GithubService.cs

External invocations:
- `gh pr create --title <title> [--body <body>] [--base <baseBranch>]`

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `title` | LLM | Trailing `\` lets close-quote escape; subsequent `--body` token consumed into title (classic Windows arg-confusion). Or leading `-` reparsed as a `gh` flag. | **Fixed** — `ArgvQuote` doubles backslashes preceding the closing quote. Entry rejects values starting with `-`. | The prior helper `Replace("\"","\\\"")` left trailing backslashes unescaped. |
| `body` | LLM | Same trailing-`\` arg-confusion. | **Fixed** via `ArgvQuote`. | Multi-line bodies are fine — `ArgvQuote` correctly quotes newline-containing values. |
| `baseBranch` | LLM | Leading `-` could be parsed as a `gh` flag (e.g. `--repo evil/repo`). | **Fixed** — rejected at entry. | LOW-risk in practice (gh validates branch names server-side) but trivial to defend. |
| `workingDir` | LLM | `ProcessStartInfo.WorkingDirectory` set to attacker-chosen path. Not an injection but lets gh run inside an arbitrary repo. | **Not fixed** — out of threat model (the tool's documented behavior is "run gh in this dir"). | LOW. |

Attack strings tried:
- `title = "normal\\"` (trailing backslash). Before: serialised as `"normal\\"` → CommandLineToArgv reads `\\\"` as `\` + close-quote → title bleeds. AFTER: `ArgvQuote` doubles to `"normal\\\\"` → reads as `normal\` correctly.
- `title = "--repo evil/repo"`. Before: passed as `"--repo evil/repo"` (single token, harmless). Now blocked at entry as extra hardening.

---

## CrossBrowserService.cs

External invocations:
- `chrome-devtools-axi screenshot <url> <path>`
- `npx playwright screenshot --browser=<engine> <url> <path>`
- `cmd.exe /c where <exe>` (inside `ResolveCli`)

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `browser` | LLM | Out-of-allowlist value reaches `--browser=` flag | OK — `RunOne` only routes `chrome`/`firefox`/`webkit`/`safari`; everything else returns `UnknownBrowser` without shelling. | LOW. |
| `url` (derived from `targetObject`) | LLM via `RunObjectService.Resolve` | Trailing-`\` arg-confusion if URL contained a backslash. | **Fixed** — shared `ArgvQuote`. | The URL is built by the system from the resolved KB object, so the practical risk was already low. |
| `shotPath` | system (`Path.GetTempPath()` + ticks + `.png`) | none | n/a | |
| `cmd.exe /c where <exe>` in `ResolveCli` | hardcoded `"chrome-devtools-axi"` / `"npx"` | none | n/a | Not LLM-controlled. |

---

## BlameService.cs

External invocations:
- `git --no-pager -c color.ui=false ls-files`
- `git ... blame --porcelain -L <start>,<end> -- <rel>`

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `req.FilePath` | LLM | **Arbitrary file read.** `File.ReadAllLines(targetFile)` runs before any anchor check, and the resulting lines are echoed back to the caller in `entries[].snippetContext`. `filePath = "C:\\Users\\me\\.ssh\\id_rsa"` would have returned the private key. | **Fixed** — `Path.GetFullPath(...)` then `StartsWith(gitRoot)` check before `File.ReadAllLines`. | CRITICAL info-disclosure pre-fix. |
| `req.Name` / `req.Part` | LLM | Substring filters over `git ls-files` output. No shell exposure. | OK. | LOW. |
| `req.Line` | LLM (int) | Negative → clamped to 0. Larger than file → typed error. | OK. | |
| `--` separator on blame | n/a | Defended — blame already uses `--` before path. | OK. | |

Attack strings tried:
- `filePath = "..\\..\\..\\..\\Users\\me\\.ssh\\id_rsa"`. Before: silently returned the file contents in the snippet. AFTER: rejected with `PathOutsideRepo`.
- `filePath = "C:\\Windows\\System32\\drivers\\etc\\hosts"`. Same as above — fixed.
- `filePath` with quotes / metacharacters: now also goes through `ArgvQuote` for git invocation.

---

## PrDescriptionService.cs

External invocations:
- `git --no-pager -c color.ui=false log -<last> --pretty=format:%H%x09%s%x09%b%x1e`

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `last` | LLM (int) | OOB integer reaches `-N` flag | OK — clamped to `[1, 100]` at the entrypoint. | |
| `workingDir` | LLM (string) | Runs git log in attacker-chosen directory; reads commit metadata. | **Not fixed** — out of threat model (this is the tool's documented behavior). | LOW info-disclosure (commit subjects only — not file contents). |

No additional fixes. The single `args` string never embeds user-supplied
strings, only the clamped int.

---

## TestService.cs

External invocations:
- `<gxPath>\\MSBuild.exe /nologo /verbosity:normal "<tempFile>"` where `<tempFile>` is an MSBuild XML document generated with `string.Format`.

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `target` | LLM | **MSBuild XML injection → arbitrary task execution → RCE.** `target = "x'/><Target Name='pwn'><Exec Command='calc.exe'/></Target><Target Name='Run' Outputs='" ` would have closed the `ExecuteTests` element and injected a new `<Exec>` task that runs under the worker's privileges. | **Fixed** — `IsSafeTestTarget` allowlist (`[A-Za-z0-9_., ;-]{1,256}`) at entry, plus `SecurityElement.Escape` on every interpolated value (defense in depth). | CRITICAL RCE pre-fix. |
| `gxPath` / `kbPath` | env + KbService | Trusted inputs, but escaped anyway for defense in depth. | OK. | |
| `tempFile` arg to MSBuild | system | Guid-generated; no LLM exposure. | OK. | |

Attack strings tried:
- `target = "x'/><Target Name='p'><Exec Command='calc.exe'/></Target><Target Name='Run' Outputs='"`. Before: rendered into the XML as-is, MSBuild would have executed `calc.exe`. AFTER: rejected at entry by `IsSafeTestTarget` (the `'`, `<`, `>`, `=` characters are all outside the allowlist). Even if widened, `SecurityElement.Escape` would render the values as text content, not markup.

---

## GeneratedDiffService.cs

External invocations:
- `git show "HEAD:<repoRelativePath>"`

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `repoRelativePath` | derived from filesystem walk (`FindGeneratedFiles` under `kbPath`) | Trailing-`\` arg-confusion in the naive quoting helper. Not directly LLM-controlled, but `target` flows into the file lookup via `SanitizeName`. | **Fixed** via shared `ArgvQuote`. | MEDIUM defense-in-depth. |
| `target` | LLM | Sanitised via `Path.GetInvalidFileNameChars`. | OK. | |

---

## ObjectService.cs — WorkerReload

External invocations:
- `powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command "<long script>"` where `$src` and `$dst` are single-quoted PowerShell literals carrying the LLM-supplied `sourceDir`.

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `sourceDir` | LLM | Single-quote escape (`Replace("'","''")`) is correct for PowerShell single-quoted strings; the literal then drives `Copy-Item $src\* $dst\`. Because copying arbitrary binaries into the worker `publish/` directory is the **documented behavior of this tool**, this is out of threat model — but it should still be considered when reviewing permission grants. | n/a | Note in CLAUDE.md: this tool already requires explicit user invocation and is gated behind `mode=hard`. |

LOW — left as-is (tool's intended function).

---

## BuildService.cs

External invocations:
- `<msbuildPath> /nologo /m /v:n /nodeReuse:false /target:Execute "<tempFile>"`

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| target names | LLM | XML injection in MSBuild plan | OK — already goes through `SecurityElement.Escape` before being joined into `<SpecifyOneOnly ObjectNames="...">`. | |
| `_msbuildPath` / `_gxDir` | env / config | trusted | OK. | |

No fix needed.

---

## VisualVerifyService.cs / PreviewService.cs / BrowserDriverInvoker.cs

External invocations: chrome-devtools-axi / npx, plus `cmd.exe /c where <cmd>`.

These services use a different quoting helper (`Quote` — `Replace("\\","\\\\").Replace("\"","\\\"")`) which **does** double backslashes before quotes, so the trailing-`\` arg-confusion bug is not present here. URLs are built from KB-resolved data plus LLM-supplied object names that flow through GeneXus name lookup (heavily constrained).

| Arg | Source | Threat | Defended? | Note |
| --- | ------ | ------ | --------- | ---- |
| `url` | system (config baseUrl + objectName + `.aspx`) | URL is wrapped in `Quote` then passed as a single token to chrome-devtools-axi via cmd.exe. cmd.exe's `&`/`|`/`%` are NOT interpreted inside `"..."` quotes. | OK. | |
| `objectName` for js fill/click | LLM | JavaScript injection into `eval` payload via `EscapeJs`. `EscapeJs` escapes `\` and `'` but not `</script>`. The eval is wrapped in `(function(){...})()` not a script tag, so HTML-escape isn't needed; JS escape covers what matters. | OK. | MEDIUM — see follow-ups. |
| `emulate` / `network` | LLM | Reach the CLI as `--emulate <profile>` if and only if in `EmulateProfiles` / `NetworkProfiles`. | OK — allowlisted. | |
| `cmd.exe /c where <cmd>` in `Which` | hardcoded literals | none | n/a | |

LOW follow-up: `EscapeJs` only handles single-quoted JS strings. Calls like `'{0}'.toLowerCase()` in `clickJs` (line 649 of PreviewService.cs) build JS source via string format, but every LLM-controlled interpolation goes through `EscapeJs` so single-quote breakouts are prevented.

---

## Remaining LOW follow-ups (not fixed)

1. **PreviewService.EscapeJs**: only escapes `\\` and `'`; doesn't escape `</script>`. The current call sites build JS expressions (not HTML), so this isn't exploitable today, but if a future caller ever embeds the result inside a `<script>` tag rendered into a captured HTML buffer, it would be. Consider HTML-escaping `<` as well.
2. **GithubService.workingDir**: LLM can point `gh pr create` at any local git repo; tool's documented purpose. Leave as-is, document the trust requirement on the tool description.
3. **PrDescriptionService.workingDir**: same as above for `git log`. Leave as-is.
4. **CrossBrowserService.ResolveCli**: uses `cmd.exe /c where <exe>` with hardcoded `exe` strings — no LLM input — but the pattern reads as if it's vulnerable. Consider routing through `BrowserDriverInvoker.ResolveDriverPath` so there's one canonical "find a CLI" path.

---

## Defense pattern adopted: `GithubService.ArgvQuote`

Implements the canonical Windows CommandLineToArgv-compatible escape
documented at
<https://learn.microsoft.com/en-us/archive/blogs/twistylittlepassagesallalike/everyone-quotes-command-line-arguments-the-wrong-way>:

- Backslashes are escaped (doubled) only when they immediately precede a
  quote — or the *closing* quote of the wrapped argument. This is what
  closes the trailing-`\` arg-confusion hole.
- Embedded quotes are escaped with a single `\` after their preceding
  backslash run is doubled.
- Arguments without `' '`, `\t`, `\n`, `\v` or `"` are not quoted (passes
  through unchanged).

Rolled out as the single source of truth across `GithubService`,
`TimeTravelService`, `BlameService`, `CrossBrowserService`, and
`GeneratedDiffService`. `VisualVerifyService.Quote` /
`PreviewService.Quote` were already correct (they do `\\\\` then `\\\"`),
so they're left alone to avoid churn.
