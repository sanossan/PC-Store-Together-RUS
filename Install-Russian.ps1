[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GamePath,

    [ValidateSet('Install', 'Restore')]
    [string] $Action = 'Install'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Запустите скрипт в Windows PowerShell 5.1 (powershell.exe).'
}

if (-not ('PcTogetherRus.InstallTransaction' -as [type])) {
    $cecil = Join-Path $PSScriptRoot 'lib\Mono.Cecil.dll'
    if ((Get-FileHash -LiteralPath $cecil -Algorithm SHA256).Hash -ne 'C41BDB9FFD3C5F6E17D2382C1012D73703E035E3F1100245FDD4E08C8DC6EB5B') {
        throw 'Библиотека Mono.Cecil отличается от версии, включённой в выпуск.'
    }
    Add-Type -Path $cecil
    $sources = @(
        (Join-Path $PSScriptRoot 'PcStoreLocale.cs'),
        (Join-Path $PSScriptRoot 'UnityFsBundle.cs'),
        (Join-Path $PSScriptRoot 'PcStoreLabels.cs'),
        (Join-Path $PSScriptRoot 'installer\InstallTransaction.cs')
    )
    Add-Type -Path $sources -ReferencedAssemblies @($cecil, 'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll')
}

$game = (Resolve-Path -LiteralPath $GamePath).ProviderPath
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw -Encoding UTF8
$restore = $Action -eq 'Restore'
$russian = if (-not $restore) {
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ru.json') -Raw -Encoding UTF8
}
try {
    [PcTogetherRus.InstallTransaction]::Run($game, $restore, $release, $russian)
}
catch [Management.Automation.MethodInvocationException] {
    throw $_.Exception.InnerException
}
