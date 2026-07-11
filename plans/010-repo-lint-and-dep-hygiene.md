# Plan 010: Repo-wide JS/TS lint + editorconfig, ESLint 9, central package versions

> **Executor instructions**: Land in small independent commits (each part is separable).
> If a lint run surfaces pre-existing violations, wire the gate non-blocking first, then
> tighten. Update `plans/README.md`.
>
> **Drift check**: `git diff --stat b326cd4..HEAD -- package.json cli src/nexus-ide`

## Status

- **Priority**: P3
- **Effort**: S-M
- **Risk**: MED for the ESLint 9 flat-config migration; LOW for the rest
- **Depends on**: none
- **Category**: dx / dependencies
- **Planned at**: commit `b326cd4`, 2026-07-10

## Why this matters

Three separate gaps (TOOL-03, DEP-02):
- `cli/` (JS, incl. `cli/lib/config.js` ~1203 lines and `cli/commands/axi.js` ~2231
  lines) has no ESLint config or `.editorconfig` at the repo root — consistency relies
  entirely on manual review by a solo maintainer.
- `src/nexus-ide` pins `eslint@^8.x`, which is EOL (no further fixes).
- The two C# test projects (`GxMcp.Gateway.Tests`, `GxMcp.Worker.Tests`) pin xunit /
  test-SDK versions independently with no `Directory.Packages.props` to keep them in
  lockstep.

## Current state

- Root `package.json` — 0 runtime deps, no `devDependencies`, no lockfile,
  `scripts.test = node --test cli/run.test.js`. CONTRIBUTING.md mandates "no TypeScript
  in cli/, keep deps minimal".
- `src/nexus-ide/package.json` — `eslint@^8.54.0`, `npm run lint` exists and currently
  exits 0 (59 warnings, 0 errors) as of the audit.
- `src/GxMcp.*.Tests.csproj` — xunit 2.9.2 / Microsoft.NET.Test.Sdk 17.11.1 /
  coverlet.collector 6.0.2, duplicated across both.

## Parts (independent)

### Part A — root JS lint + editorconfig (TOOL-03)

Add a minimal root `.eslintrc.json` (or flat `eslint.config.js`) for Node/CommonJS
covering `no-unused-vars`/`no-undef` for `cli/`, plus `.editorconfig` matching the
repo's observed style (spaces, LF). Add `scripts.lint` to root `package.json`
(dev-dependency footprint minimal — respect the "keep it lean" philosophy). If the
first run is red, land the CI step `continue-on-error: true`, clean up, then make it
blocking. Add the step to `.github/workflows/ci.yml` after the CLI tests.

### Part B — ESLint 9 in nexus-ide (DEP-02)

Migrate `src/nexus-ide` from ESLint 8 to 9 (flat config `eslint.config.js`). This is a
breaking config-format change; keep the current rule set and `npm run lint` exit-0
status. The nexus-ide lint step was added to CI on the audit branch — confirm it still
passes after the bump.

### Part C — central test package versions

Add `Directory.Packages.props` at `src/` enabling central package management for the
two test projects (or at minimum a shared `Directory.Build.props` pinning the xunit /
test-SDK / coverlet versions) so they can't silently diverge.

## Commands

| Purpose | Command | Expected |
|---|---|---|
| Root lint | `npm run lint` (after Part A) | exit 0 (or documented warnings) |
| Nexus lint | `cd src/nexus-ide; npm ci; npm run lint` | exit 0 |
| Build | `dotnet build Genexus18MCP.sln -v:minimal` (after Part C, GX_PATH set) | 0 errors |
| CLI tests | `npm test` | pass |

## Scope

**In scope:** root `.eslintrc*`/`eslint.config.js`, `.editorconfig`, root `package.json`
scripts, `src/nexus-ide` eslint config + package.json, `Directory.Packages.props` /
`Directory.Build.props`, `.github/workflows/ci.yml`.
**Out of scope:** reformatting `cli/` source to satisfy new rules in the same commit as
adding the config — do style fixes in a separate `chore:` commit (CONTRIBUTING.md rule).

## Done criteria

- [x] Root `npm run lint` runs and is wired into CI
- [x] `.editorconfig` present at repo root
- [x] nexus-ide on ESLint 9, `npm run lint` exit 0, CI lint step green
- [x] Both test csproj resolve xunit/test-SDK from one central version source
- [x] `dotnet build` + `npm test` still green

## STOP conditions

- ESLint 9 flat-config migration in nexus-ide balloons beyond a config rewrite (rule
  incompatibilities requiring source changes) → land Parts A and C, mark Part B BLOCKED
  with what broke.

## Maintenance notes

- Keep root dev-dependency footprint minimal per CONTRIBUTING.md; don't pull a large
  eslint plugin set into a repo that prides itself on 0 runtime deps.
