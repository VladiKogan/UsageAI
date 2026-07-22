[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $Tag,

    [string] $NotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedTag = "v$Version"
if ($Tag -cne $expectedTag) {
    throw "Tag '$Tag' does not match project version '$Version'."
}

$projectPath = Join-Path $repositoryRoot 'UsageAI.csproj'
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$projectVersionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $projectVersionNode -or $projectVersionNode.InnerText -cne $Version) {
    throw "UsageAI.csproj version does not match '$Version'."
}

$tagType = (& git -C $repositoryRoot cat-file -t $Tag 2>$null)
if ($LASTEXITCODE -ne 0 -or $tagType.Trim() -cne 'tag') {
    throw "'$Tag' must exist as an annotated Git tag."
}

$tagCommit = (& git -C $repositoryRoot rev-list -n 1 $Tag).Trim()
$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $headCommit) {
    throw "'$Tag' does not point to the checked-out release commit."
}

$changelogPath = Join-Path $repositoryRoot 'Changelog.md'
$changelog = Get-Content -LiteralPath $changelogPath -Raw
$escapedVersion = [regex]::Escape($Version)
$entryPattern = "(?ms)^## \[$escapedVersion\] - (?<date>\d{4}-\d{2}-\d{2})\r?\n(?<body>.*?)(?=^## \[|\z)"
$entry = [regex]::Match($changelog, $entryPattern)
if (!$entry.Success) {
    throw "Changelog.md has no dated entry for version '$Version'."
}

$releaseDate = [DateTime]::MinValue
if (![DateTime]::TryParseExact(
        $entry.Groups['date'].Value,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref] $releaseDate)) {
    throw "The changelog date for '$Version' is invalid."
}

if (![string]::IsNullOrWhiteSpace($NotesPath)) {
    $resolvedNotesPath = if ([IO.Path]::IsPathRooted($NotesPath)) {
        [IO.Path]::GetFullPath($NotesPath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $NotesPath))
    }
    $notesDirectory = Split-Path -Parent $resolvedNotesPath
    [IO.Directory]::CreateDirectory($notesDirectory) | Out-Null
    $notes = $entry.Groups['body'].Value.Trim() + [Environment]::NewLine
    [IO.File]::WriteAllText(
        $resolvedNotesPath,
        $notes,
        [Text.UTF8Encoding]::new($false))
}

Write-Host "Validated annotated release $Tag at $headCommit."
