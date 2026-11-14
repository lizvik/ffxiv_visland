param(
    [string]$ExpectedTag,
    [switch]$UpdateRepositoryMetadata
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$projectPath = Join-Path $root 'ffxiv_visland/visland.csproj'
$repoPath = Join-Path $root 'repo.json'

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNodes = $project.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1) {
    throw 'visland.csproj must define exactly one Version property.'
}
$version = $versionNodes[0].InnerText.Trim()
if ($version -cmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    $version += '.0'
} elseif ($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Project Version '$version' must have three or four numeric parts."
}
foreach ($part in $version.Split('.')) {
    if ([long]$part -gt 65534) {
        throw 'Assembly version components must be between 0 and 65534.'
    }
}
$tag = "v$version"
if ($ExpectedTag -and $ExpectedTag -cne $tag) {
    throw "Tag '$ExpectedTag' does not match project version '$tag'."
}

$entries = @(ConvertFrom-Json -InputObject (Get-Content -LiteralPath $repoPath -Raw))
if ($entries.Count -ne 1 -or $entries[0].InternalName -cne 'visland') {
    throw 'repo.json must contain exactly one visland plugin entry.'
}
foreach ($field in @('Author', 'Name', 'InternalName', 'Description', 'Punchline', 'RepoUrl', 'IconUrl')) {
    $property = $project.SelectSingleNode("/Project/PropertyGroup/$field")
    if ($null -eq $property -or $entries[0].$field -cne $property.InnerText) {
        throw "repo.json $field must match visland.csproj."
    }
}
if ($UpdateRepositoryMetadata) {
    foreach ($field in @('AssemblyVersion', 'TestingAssemblyVersion')) {
        $entries[0].$field = $version
    }
    $json = ConvertTo-Json -InputObject $entries -Depth 10
    [System.IO.File]::WriteAllText($repoPath, "$json`n", [System.Text.UTF8Encoding]::new($false))
}
foreach ($field in @('AssemblyVersion', 'TestingAssemblyVersion')) {
    if ($entries[0].$field -cne $version) {
        throw "repo.json $field must be $version (found '$($entries[0].$field)')."
    }
}
Write-Output $tag
