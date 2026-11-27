# Fills docs/ai-layer/ (the "AI layer" section, git-ignored) with the docs/ folder of vela-agentic at the tag pinned in
# docs/ai-layer.version. Run it from the repository root before building the site or regenerating llms.txt. -Source
# copies from a local vela-agentic checkout instead, to preview the pack's pages before tagging them.
# Windows PowerShell 5.1 and PowerShell 7 alike: the Docs workflow runs it on the self-hosted runner's 5.1.
param([string] $Source)
$ErrorActionPreference = 'Stop'

$docs = Join-Path (Get-Location) 'docs'
$target = Join-Path $docs 'ai-layer'
if (-not (Test-Path (Join-Path $docs 'ai-layer.version'))) { throw 'Run this script from the repository root: docs/ai-layer.version not found.' }

$temp = $null
try {
    if ($Source) {
        $from = Join-Path $Source 'docs'
    } else {
        $tag = (Get-Content -Raw (Join-Path $docs 'ai-layer.version')).Trim()
        if ($tag -cnotmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "docs/ai-layer.version must hold a vela-agentic tag (vX.Y.Z), not '$tag'." }
        $temp = Join-Path ([System.IO.Path]::GetTempPath()) "vela-agentic-$([guid]::NewGuid().ToString('N'))"
        # git writes progress to stderr, which Windows PowerShell turns into an error under 'Stop': check the exit code.
        $ErrorActionPreference = 'Continue'
        git -c advice.detachedHead=false clone --quiet --depth 1 --branch $tag https://github.com/allandecastro/vela-agentic.git $temp 2>&1 | Out-Host
        $exit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($exit -ne 0) { throw "Could not clone vela-agentic at $tag (git exit code $exit)." }
        $from = Join-Path $temp 'docs'
    }
    if (-not (Test-Path (Join-Path $from 'toc.yml'))) { throw "$from has no toc.yml: not a vela-agentic docs folder." }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item -Recurse $from $target
    Write-Host "docs/ai-layer/ filled from $(if ($Source) { $from } else { "vela-agentic $tag" })."
} finally {
    if ($temp -and (Test-Path $temp)) { Remove-Item -Recurse -Force $temp }
}
