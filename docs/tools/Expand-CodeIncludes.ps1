# Writes a docs page with every docfx code include ([!code-csharp[](path#region)]) replaced by a fenced block holding
# that region of the sample file, dedented, so the Markdown copies listed in llms.txt read like the rendered pages.
# A path without #region includes the whole file. Fails when the file or the region is missing. An include written
# as inline code (`[!code-csharp[](...)]`, a page describing the syntax) is left as text.
param([Parameter(Mandatory)] [string] $Page, [Parameter(Mandatory)] [string] $Destination)
$ErrorActionPreference = 'Stop'

$pageDir = Split-Path -Parent (Resolve-Path $Page)
$text = Get-Content -Raw -Encoding UTF8 $Page
$expanded = [regex]::Replace($text, '(?<!`)\[!code-(?<lang>[a-z]+)\[\]\((?<path>[^)#]+)(#(?<region>[^)]+))?\)\]', {
    param($match)
    $file = Join-Path $pageDir $match.Groups['path'].Value
    if (-not (Test-Path $file)) { throw "$Page includes a missing file: $($match.Groups['path'].Value)" }
    $lines = Get-Content -Encoding UTF8 $file
    if ($match.Groups['region'].Success) {
        $name = $match.Groups['region'].Value
        $start = [array]::FindIndex([string[]]$lines, [Predicate[string]]{ param($l) $l.Trim() -eq "#region $name" })
        if ($start -lt 0) { throw "$Page includes a missing region '$name' of $($match.Groups['path'].Value)" }
        $end = [array]::FindIndex([string[]]$lines, $start + 1, [Predicate[string]]{ param($l) $l.Trim() -eq '#endregion' })
        $lines = $lines[($start + 1)..($end - 1)]
    }
    $indent = ($lines | Where-Object { $_.Trim() } | ForEach-Object { $_.Length - $_.TrimStart().Length } | Measure-Object -Minimum).Minimum
    $body = ($lines | ForEach-Object { if ($_.Length -ge $indent) { $_.Substring($indent) } else { $_.TrimStart() } }) -join "`n"
    "``````$($match.Groups['lang'].Value)`n$body`n``````"
})
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
# No BOM (Windows PowerShell's -Encoding UTF8 writes one), like the source page.
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Destination), $expanded, (New-Object System.Text.UTF8Encoding $false))
