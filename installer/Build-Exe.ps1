[CmdletBinding()]
param(
    [string] $ManifestPath,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not $ManifestPath) { $ManifestPath = Join-Path $root 'release.json' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$ManifestPath = (Resolve-Path -LiteralPath $ManifestPath).ProviderPath
$release = Get-Content -Raw -Encoding UTF8 -LiteralPath $ManifestPath | ConvertFrom-Json
$version = [version]$release.version
if ($version.Build -lt 0 -or $version.Revision -ge 0 -or $version.ToString(3) -cne $release.version -or
    $version.Major -gt 65534 -or $version.Minor -gt 65534 -or $version.Build -gt 65534) {
    throw 'release.json must have an X.Y.Z version supported by Windows assembly metadata.'
}
if ($release.steamBuildId -isnot [int] -and $release.steamBuildId -isnot [long]) {
    throw 'release.json must have a numeric steamBuildId.'
}
if ($release.gameVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($release.gameVersion)) {
    throw 'release.json must have a gameVersion from PlayerSettings.bundleVersion.'
}
$outputFolder = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$output = Join-Path $outputFolder "PC-TOGETHER-RUS-v$version-Setup.exe"
$cecil = Join-Path $root 'lib\Mono.Cecil.dll'
$assemblyInfo = Join-Path $outputFolder ("AssemblyInfo-{0}.cs" -f [guid]::NewGuid().ToString('N'))
$assemblyVersion = "$version.0"
$attributes = @"
using System.Reflection;
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$version")]
[assembly: AssemblyDescription("PC Store Together Russian localization for Steam build $($release.steamBuildId) and compatible newer builds")]
"@

$options = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll', "/reference:$cecil", "/out:$output",
    "/resource:$cecil,Mono.Cecil.dll",
    "/resource:$(Join-Path $root 'lib\LICENSE.txt'),Mono.Cecil.LICENSE.txt",
    "/resource:$(Join-Path $root 'ru.json'),ru.json",
    "/resource:$ManifestPath,release.json",
    (Join-Path $root 'PcStoreLabels.cs'),
    (Join-Path $root 'PcStoreLocale.cs'),
    (Join-Path $root 'UnityFsBundle.cs'),
    (Join-Path $PSScriptRoot 'InstallTransaction.cs'),
    (Join-Path $PSScriptRoot 'Setup.cs'),
    $assemblyInfo
)
try {
    [IO.File]::WriteAllText($assemblyInfo, $attributes)
    & $compiler @options
    if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE" }
}
finally {
    [IO.File]::Delete($assemblyInfo)
}
$sha = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$name = Split-Path -Leaf $output
"$sha  $name" | Set-Content -Encoding Ascii -LiteralPath "$output.sha256"
Get-Item -LiteralPath $output,"$output.sha256"
