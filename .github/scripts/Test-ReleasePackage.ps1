$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$tag = & (Join-Path $PSScriptRoot 'Check-ReleaseVersion.ps1')
$expectedVersion = $tag.Substring(1)
$outputDirectory = Join-Path $root 'ffxiv_visland/bin/x64/Release'
$packagePath = Join-Path $outputDirectory 'visland/latest.zip'
$assemblyPath = Join-Path $outputDirectory 'visland.dll'

if (!(Test-Path -LiteralPath $packagePath) -or (Get-Item -LiteralPath $packagePath).Length -eq 0) {
    throw 'Release package is missing or empty.'
}
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
if ($assemblyVersion -cne $expectedVersion) {
    throw "Built assembly version $assemblyVersion does not match $tag."
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $manifestEntry = $archive.GetEntry('visland.json')
    $assemblyEntry = $archive.GetEntry('visland.dll')
    if ($null -eq $manifestEntry -or $null -eq $assemblyEntry -or $assemblyEntry.Length -eq 0) {
        throw 'Release archive must contain visland.dll and visland.json at its root.'
    }
    $ecommonsEntry = $archive.GetEntry('ECommons.dll')
    if ($null -eq $ecommonsEntry -or $ecommonsEntry.Length -eq 0) {
        throw 'Release archive must contain the ECommons dependency.'
    }
    $dependencyStream = $ecommonsEntry.Open()
    $builtDependencyStream = [System.IO.File]::OpenRead((Join-Path $outputDirectory 'ECommons.dll'))
    try {
        $dependencyHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($dependencyStream))
        $builtDependencyHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($builtDependencyStream))
        if ($dependencyHash -cne $builtDependencyHash) {
            throw 'Release archive contains an outdated ECommons dependency.'
        }
    } finally {
        $dependencyStream.Dispose()
        $builtDependencyStream.Dispose()
    }
    # clib uses these assemblies even when its optional sheet manager is not enabled.
    foreach ($dependencyName in @('clib.dll', 'AllaganLib.GameSheets.dll', 'AllaganLib.Shared.dll', 'LuminaSupplemental.Excel.dll')) {
        $entry = $archive.GetEntry($dependencyName)
        if ($null -eq $entry -or $entry.Length -eq 0) {
            throw "Release archive is missing dependency $dependencyName."
        }
        $packagedDependency = $entry.Open()
        $builtDependency = [System.IO.File]::OpenRead((Join-Path $outputDirectory $dependencyName))
        try {
            $packagedHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($packagedDependency))
            $builtHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($builtDependency))
            if ($packagedHash -cne $builtHash) {
                throw "Release archive contains an outdated $dependencyName."
            }
        } finally {
            $packagedDependency.Dispose()
            $builtDependency.Dispose()
        }
    }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try {
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    } finally {
        $reader.Dispose()
    }
    $repository = @(Get-Content -LiteralPath (Join-Path $root 'repo.json') -Raw | ConvertFrom-Json)[0]
    foreach ($field in @('InternalName', 'AssemblyVersion', 'DalamudApiLevel', 'RepoUrl', 'IconUrl')) {
        if ($manifest.$field -cne $repository.$field) {
            throw "Packaged manifest $field does not match repo.json."
        }
    }
    foreach ($field in @('Tags', 'CategoryTags')) {
        if (!$manifest.$field -or !$repository.$field) {
            throw "Packaged manifest and repo.json must contain $field."
        }
        if (@(Compare-Object -ReferenceObject @($repository.$field) -DifferenceObject @($manifest.$field) -CaseSensitive).Count -ne 0) {
            throw "Packaged manifest $field does not match repo.json."
        }
    }
    # Verify that the packaged DLL is the same assembly that was just built.
    $packageStream = $assemblyEntry.Open()
    $assemblyStream = [System.IO.File]::OpenRead($assemblyPath)
    try {
        $packageHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($packageStream))
        $assemblyHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($assemblyStream))
        if ($packageHash -cne $assemblyHash) {
            throw 'Release archive contains an outdated assembly.'
        }
    } finally {
        $packageStream.Dispose()
        $assemblyStream.Dispose()
    }
} finally {
    $archive.Dispose()
}
Write-Output "Verified $tag release package."
