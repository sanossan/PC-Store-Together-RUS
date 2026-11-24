[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputFolder = Join-Path $root 'dist'
$output = Join-Path $outputFolder 'PC-TOGETHER-RUS-v1.0.0-Setup.exe'
$cecil = Join-Path $root 'lib\Mono.Cecil.dll'
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null

$options = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll', "/reference:$cecil", "/out:$output",
    "/resource:$cecil,Mono.Cecil.dll",
    "/resource:$(Join-Path $root 'lib\LICENSE.txt'),Mono.Cecil.LICENSE.txt",
    "/resource:$(Join-Path $root 'ru.json'),ru.json",
    "/resource:$(Join-Path $root 'release.json'),release.json",
    (Join-Path $root 'PcStoreLabels.cs'),
    (Join-Path $root 'PcStoreLocale.cs'),
    (Join-Path $PSScriptRoot 'InstallTransaction.cs'),
    (Join-Path $PSScriptRoot 'Setup.cs')
)
& $compiler @options
if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE" }
$sha = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$name = Split-Path -Leaf $output
"$sha  $name" | Set-Content -Encoding Ascii -LiteralPath "$output.sha256"
Get-Item -LiteralPath $output,"$output.sha256"
