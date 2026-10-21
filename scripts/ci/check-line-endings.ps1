[CmdletBinding()]
param(
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

# Tracked text files are LF in the Git index (.gitattributes: `* text=auto eol=lf`).
# Files an attribute marks `-text` (CHANGELOG.md, byte-compared SDK fixtures, binaries)
# are exempt; a CRLF or mixed blob anywhere else is drift the release tooling and
# byte-identity checks cannot tolerate.

$ErrorActionPreference = 'Stop'
$entries = @(& git -C $Repository ls-files --eol 2>&1 | ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) { throw "git ls-files --eol failed: $($entries -join ' ')" }
if ($entries.Count -eq 0) { throw 'git ls-files --eol returned no tracked files.' }

$violations = @(
    foreach ($entry in $entries) {
        # Format: "i/<eol> w/<eol> attr/<attributes>\t<path>"
        $columns = $entry -split "`t", 2
        if ($columns.Count -ne 2) { throw "Unparseable git ls-files --eol entry: $entry" }
        $index = ($columns[0] -split '\s+')[0]
        $attribute = $columns[0]
        if ($attribute -match 'attr/-text') { continue }
        if ($index -in @('i/crlf', 'i/mixed')) { "$($columns[1]) ($index)" }
    }
)
if ($violations.Count -gt 0) {
    Write-Host ("::error::Tracked text files with CRLF or mixed line endings in the index:`n" + ($violations -join "`n"))
    exit 1
}
Write-Host "line-endings: $($entries.Count) tracked files, no CRLF/mixed text blobs in the index." -ForegroundColor Green
