[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GamePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with Windows PowerShell 5.1 (powershell.exe).'
}

$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GamePath).ProviderPath
$data = Join-Path $game 'PC Store Simulator Together_Data'
$manifestPath = Join-Path $root 'release.json'
$localePath = Join-Path $root 'ru.json'
$originalJson = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$release = $originalJson | ConvertFrom-Json
$cecil = Join-Path $root 'lib\Mono.Cecil.dll'
Add-Type -Path $cecil
$sources = @(
    (Join-Path $root 'PcStoreLocale.cs'),
    (Join-Path $root 'UnityFsBundle.cs'),
    (Join-Path $root 'PcStoreLabels.cs'),
    (Join-Path $PSScriptRoot 'InstallTransaction.cs')
)
Add-Type -Path $sources -ReferencedAssemblies @($cecil, 'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll')
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

if ($release.originalLocaleEntries -ne [PcTogetherRus.LocalePatch]::EntryCount) {
    throw 'release.json originalLocaleEntries does not match the patch source.'
}
$dictionary = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($row in (Get-Content -LiteralPath $localePath -Raw -Encoding UTF8 | ConvertFrom-Json)) {
    $dictionary.Add($row.key, $row.value)
}

$dist = (New-Item -ItemType Directory -Path (Join-Path $root 'dist') -Force).FullName
$stage = (New-Item -ItemType Directory -Path (Join-Path $dist ('.release-' + [guid]::NewGuid().ToString('N')))).FullName
try {
    $assetHash = [PcTogetherRus.LocalePatch]::Build(
        (Join-Path $data ([PcTogetherRus.LocalePatch]::AssetName)), (Join-Path $stage 'patched.unity3d'), $dictionary)
    $assemblyHash = [PcTogetherRus.LabelPatch]::Build(
        (Join-Path $data 'Managed\Assembly-CSharp.dll'), (Join-Path $stage 'patched.dll'))
    $refreshManifest = $release.patchedAssetSha256 -cne $assetHash -or $release.patchedAssemblySha256 -cne $assemblyHash
    $json = $originalJson
    if ($refreshManifest) {
        $release.patchedAssetSha256 = $assetHash
        $release.patchedAssemblySha256 = $assemblyHash
        $json = ($release | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
    }
    $validated = [PcTogetherRus.ReleaseManifest]::Parse($json)
    $stagedManifest = Join-Path $stage 'release.json'
    [IO.File]::WriteAllText($stagedManifest, $json, [Text.UTF8Encoding]::new($false))

    $exeFiles = & (Join-Path $PSScriptRoot 'Build-Exe.ps1') -ManifestPath $stagedManifest -OutputDirectory $stage
    $zipName = "PC-TOGETHER-RUS-v$($validated.version).zip"
    $zipPath = Join-Path $stage $zipName
    $files = @(
        'README.md',
        'Install-Russian.ps1',
        'PcStoreLocale.cs',
        'UnityFsBundle.cs',
        'PcStoreLabels.cs',
        'installer/InstallTransaction.cs',
        'ru.json',
        'lib/Mono.Cecil.dll',
        'lib/LICENSE.txt'
    )
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $entry), $entry) | Out-Null
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $stagedManifest, 'release.json') | Out-Null
    }
    finally {
        $zip.Dispose()
    }
    $sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding Ascii

    if ((Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8) -cne $originalJson) {
        throw 'release.json changed during the build. Run the build again.'
    }
    if ($refreshManifest) {
        [IO.File]::Replace($stagedManifest, $manifestPath, [NullString]::Value)
    }
    foreach ($artifact in @($exeFiles.FullName) + @($zipPath, "$zipPath.sha256")) {
        $destination = Join-Path $dist (Split-Path -Leaf $artifact)
        if ([IO.File]::Exists($destination)) {
            [IO.File]::Replace($artifact, $destination, [NullString]::Value)
        }
        else {
            [IO.File]::Move($artifact, $destination)
        }
        Get-Item -LiteralPath $destination
    }
}
finally {
    $stageItem = Get-Item -LiteralPath $stage
    if ($stageItem.Parent.FullName -eq $dist -and $stageItem.Name -match '^\.release-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $stageItem.FullName -Recurse -Force
    }
}
