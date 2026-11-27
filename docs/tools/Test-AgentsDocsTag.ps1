# Checks that the AI layer pages (docs/ai-layer/, filled by Get-AgentsDocs.ps1) cite the vela-agentic tag pinned in
# docs/ai-layer.version: the "at tag `vX.Y.Z`" sentence and the links to the pack's files (blob/, tree/, raw/). A pack
# release whose pages still name the previous tag would show that tag on the site. Exits 1 and lists each stale
# reference; report them to vela-agentic rather than editing the copied pages here.
# Windows PowerShell 5.1 and PowerShell 7 alike, like Get-AgentsDocs.ps1.
$ErrorActionPreference = 'Stop'

$docs = Join-Path (Get-Location) 'docs'
$target = Join-Path $docs 'ai-layer'
if (-not (Test-Path (Join-Path $docs 'ai-layer.version'))) { throw 'Run this script from the repository root: docs/ai-layer.version not found.' }
if (-not (Test-Path $target)) { throw 'docs/ai-layer/ is empty: run docs/tools/Get-AgentsDocs.ps1 first.' }
$tag = (Get-Content -Raw (Join-Path $docs 'ai-layer.version')).Trim()

$pattern = 'vela-agentic/(?:blob|tree|raw)/(v\d+\.\d+\.\d+[0-9A-Za-z.-]*)/|at tag `(v\d+\.\d+\.\d+[0-9A-Za-z.-]*)`'
$stale = @()
foreach ($hit in (Get-ChildItem -Recurse -File $target | Select-String -Pattern $pattern -AllMatches)) {
    foreach ($match in $hit.Matches) {
        $cited = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        if ($cited -cne $tag) {
            $stale += "$($hit.Path.Substring((Get-Location).Path.Length + 1)):$($hit.LineNumber): cites $cited"
        }
    }
}

if ($stale.Count -gt 0) {
    Write-Host "::error file=docs/ai-layer.version::The vela-agentic $tag pages cite another tag ($($stale.Count) place(s)). Report it to vela-agentic and pin a release that fixes it."
    $stale | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "Every vela-agentic tag the AI layer pages cite is $tag."
# Explicit: a caller's earlier native command (git diff in docs.yml) leaves $LASTEXITCODE at 1, which the Actions
# PowerShell wrapper would return.
exit 0
