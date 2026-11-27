# Moves docs/ai-layer.version to a newer vela-agentic tag and prepares everything the bump PR needs: fetches the pages
# (Get-AgentsDocs.ps1), checks the tags they cite (Test-AgentsDocsTag.ps1), regenerates docs/llms.txt and writes the
# CHANGELOG entry. It lists the pages whose Mermaid diagrams changed since the previous pin, since that is the one
# change a person has to look at rendered. The "AI layer version" workflow runs it every day; it also runs by hand.
# Exits 1 when the tag check fails (nothing is worth committing then). Writes the changed pages, one per line, to
# -DiagramReport when given.
# Windows PowerShell 5.1 and PowerShell 7 alike, like Get-AgentsDocs.ps1.
param(
    [Parameter(Mandatory = $true)] [string] $Tag,
    [string] $DiagramReport
)
$ErrorActionPreference = 'Stop'

$docs = Join-Path (Get-Location) 'docs'
$pinFile = Join-Path $docs 'ai-layer.version'
$pages = Join-Path $docs 'ai-layer'
if (-not (Test-Path $pinFile)) { throw 'Run this script from the repository root: docs/ai-layer.version not found.' }
if ($Tag -cnotmatch '^v\d+\.\d+\.\d+$') { throw "Not a vX.Y.Z tag: '$Tag'." }
$previous = (Get-Content -Raw $pinFile).Trim()

# The Mermaid blocks of every page, keyed by the page's path under docs/ai-layer.
function Get-Diagrams {
    $result = @{}
    foreach ($file in Get-ChildItem -Recurse -File -Filter *.md $pages) {
        $blocks = [regex]::Matches((Get-Content -Raw $file.FullName), '(?ms)^```mermaid\r?\n(.*?)^```') | ForEach-Object { $_.Groups[1].Value.Trim() }
        if ($blocks) { $result[$file.FullName.Substring($pages.Length + 1)] = ($blocks -join "`n---`n") }
    }
    $result
}

& (Join-Path $PSScriptRoot 'Get-AgentsDocs.ps1')
$before = Get-Diagrams

Set-Content -Path $pinFile -Value $Tag -Encoding ASCII
& (Join-Path $PSScriptRoot 'Get-AgentsDocs.ps1')
& (Join-Path $PSScriptRoot 'Test-AgentsDocsTag.ps1')
if ($LASTEXITCODE -ne 0) { exit 1 }
$after = Get-Diagrams

$changed = @($before.Keys + $after.Keys | Sort-Object -Unique | Where-Object { $before[$_] -cne $after[$_] })
if ($DiagramReport) { Set-Content -Path $DiagramReport -Value ($changed -join "`n") -Encoding UTF8 }
Write-Host $(if ($changed) { "Diagrams changed since ${previous}: $($changed -join ', ')." } else { "No diagram changed since $previous." })

# The CHANGELOG entry: one line under ### Docs of [Unreleased], replacing the previous "follows vela-agentic" line.
$changelogPath = Join-Path (Get-Location) 'CHANGELOG.md'
$changelog = [System.IO.File]::ReadAllText($changelogPath)
# A Windows checkout may have CRLF line endings (no .gitattributes): work on LF and write the file back as it was.
$crlf = $changelog.Contains("`r`n")
$changelog = $changelog.Replace("`r`n", "`n")
$entry = "- The **AI layer** section follows vela-agentic ${Tag}: see its`n  [release notes](https://github.com/allandecastro/vela-agentic/releases/tag/$Tag)."
$start = $changelog.IndexOf("## [Unreleased]`n")
if ($start -lt 0) { throw 'CHANGELOG.md has no "## [Unreleased]" section.' }
$end = $changelog.IndexOf("`n## [", $start + 1)
if ($end -lt 0) { $end = $changelog.Length }
$unreleased = $changelog.Substring($start, $end - $start)
$existing = [regex]::Match($unreleased, '(?m)^- The \*\*AI layer\*\* section follows vela-agentic .*(\n  .*)*')
if ($existing.Success) {
    $unreleased = $unreleased.Remove($existing.Index, $existing.Length).Insert($existing.Index, $entry)
} elseif ($unreleased -match '(?m)^### Docs\n') {
    $unreleased = ([regex] '(?m)^### Docs\n\n').Replace($unreleased, "### Docs`n`n$entry`n", 1)
} else {
    # ### Docs is the last heading of a section.
    $unreleased = $unreleased.TrimEnd("`n") + "`n`n### Docs`n`n$entry`n"
}
$changelog = $changelog.Substring(0, $start) + $unreleased + $changelog.Substring($end)
if ($crlf) { $changelog = $changelog.Replace("`n", "`r`n") }
[System.IO.File]::WriteAllText($changelogPath, $changelog)

dotnet run docs/tools/UpdateLlmsTxt.cs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
exit 0
