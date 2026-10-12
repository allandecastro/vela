# Prints the mutation areas to run, as the JSON matrix of .github/workflows/mutation.yml (Windows PowerShell 5.1).
#
#   select-areas.ps1                     every area (scheduled and manual runs)
#   select-areas.ps1 -Only "a,b"         the named areas only (a manual run with the `areas` input); an unknown
#                                        name fails, so a typo cannot silently run nothing
#   select-areas.ps1 -ChangedFiles FILE  the areas with a changed source file or Stryker configuration (pull requests);
#                                        FILE lists one repository path per line (git diff --name-only). A change to
#                                        the shared mutation setup ($SharedFiles below) selects every area, so a PR
#                                        that edits the selector, the catalog, the workflow or the pinned Stryker
#                                        version still runs the mutation jobs.
#
# The areas are declared in areas.json next to this script: `sources` is the regex of the source paths an area
# mutates, `exclude` (optional) the paths it leaves out, and tests/stryker/<area>.yml its Stryker configuration.
param([string] $ChangedFiles, [string] $Only)

$ErrorActionPreference = 'Stop'
# The files every area depends on: the area catalog, this selector, the workflow and the pinned Stryker version.
$SharedFiles = @(
    'tests/stryker/areas.json',
    'tests/stryker/select-areas.ps1',
    '.github/workflows/mutation.yml',
    '.config/dotnet-tools.json'
)
# Windows PowerShell 5.1's ConvertFrom-Json emits a JSON array as ONE object, so @(...) alone would wrap the whole
# list as a single area (its fields then become arrays and the matrix is unusable). ForEach-Object { $_ } enumerates
# it on 5.1 and is a pass-through on PowerShell 7.
$areas = @(Get-Content -Raw -Path (Join-Path $PSScriptRoot 'areas.json') | ConvertFrom-Json | ForEach-Object { $_ })

if ($ChangedFiles) {
    $changed = @(Get-Content -Path $ChangedFiles | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $sharedChanged = @($changed | Where-Object { $SharedFiles -contains $_ }).Count -gt 0
    if (-not $sharedChanged) {
        $areas = @($areas | Where-Object {
            $area = $_
            @($changed | Where-Object {
                ($_ -eq "tests/stryker/$($area.area).yml") -or
                (($_ -match $area.sources) -and -not ($area.exclude -and ($_ -match $area.exclude)))
            }).Count -gt 0
        })
    }
}

if ($Only) {
    $names = @($Only -split '[,\s]+' | Where-Object { $_ })
    $unknown = @($names | Where-Object { $name = $_; -not @($areas | Where-Object { $_.area -eq $name }).Count })
    if ($unknown.Count -gt 0) {
        throw "Unknown mutation area(s): $($unknown -join ', '). Known: $(($areas | ForEach-Object { $_.area }) -join ', ')"
    }
    $areas = @($areas | Where-Object { $names -contains $_.area })
}

$matrix = @($areas | ForEach-Object { [pscustomobject]@{ area = $_.area; tests = $_.tests; timeout = $_.timeout } })
# -InputObject keeps a one-element (or empty) list a JSON array.
ConvertTo-Json -InputObject $matrix -Compress
