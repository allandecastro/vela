# Prints the mutation areas to run, as the JSON matrix of .github/workflows/mutation.yml (Windows PowerShell 5.1).
#
#   select-areas.ps1                     every area (scheduled and manual runs)
#   select-areas.ps1 -ChangedFiles FILE  the areas with a changed source file or Stryker configuration (pull requests);
#                                        FILE lists one repository path per line (git diff --name-only).
#
# The areas are declared in areas.json next to this script: `sources` is the regex of the source paths an area
# mutates, `exclude` (optional) the paths it leaves out, and tests/stryker/<area>.yml its Stryker configuration.
param([string] $ChangedFiles)

$ErrorActionPreference = 'Stop'
$areas = @(Get-Content -Raw -Path (Join-Path $PSScriptRoot 'areas.json') | ConvertFrom-Json)

if ($ChangedFiles) {
    $changed = @(Get-Content -Path $ChangedFiles | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $areas = @($areas | Where-Object {
        $area = $_
        @($changed | Where-Object {
            ($_ -eq "tests/stryker/$($area.area).yml") -or
            (($_ -match $area.sources) -and -not ($area.exclude -and ($_ -match $area.exclude)))
        }).Count -gt 0
    })
}

$matrix = @($areas | ForEach-Object { [pscustomobject]@{ area = $_.area; tests = $_.tests; timeout = $_.timeout } })
# -InputObject keeps a one-element (or empty) list a JSON array.
ConvertTo-Json -InputObject $matrix -Compress
