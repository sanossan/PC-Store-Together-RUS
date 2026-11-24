[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GamePath,

    [ValidateSet('Install', 'Restore')]
    [string] $Action = 'Install'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$game = (Resolve-Path -LiteralPath $GamePath).ProviderPath
$data = Join-Path $game 'PC Store Simulator Together_Data'
if (-not (Test-Path -LiteralPath (Join-Path $game 'PC Store Simulator Together.exe') -PathType Leaf)) {
    throw 'Укажите корневую папку PC Store Together с файлом PC Store Simulator Together.exe.'
}
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Запустите скрипт в Windows PowerShell 5.1 (powershell.exe).'
}

function Assert-GameClosed {
    if (Get-Process -Name 'PC Store Simulator Together' -ErrorAction SilentlyContinue) {
        throw 'Закройте PC Store Together перед установкой или восстановлением.'
    }
}

function Read-RussianDictionary {
    $dictionary = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $rows = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ru.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($row in $rows) {
        $dictionary.Add($row.key, $row.value)
    }
    return $dictionary
}

function Invoke-RussianPatch {
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $token = [Guid]::NewGuid().ToString('N')
    $files = @(
        @{
            Kind = 'Locale'
            Path = Join-Path $data ([PcTogetherRus.LocalePatch]::AssetName)
            Original = [PcTogetherRus.LocalePatch]::OriginalHash
            Installed = $manifest.patchedAssetSha256
        }
        @{
            Kind = 'Labels'
            Path = Join-Path $data 'Managed\Assembly-CSharp.dll'
            Original = [PcTogetherRus.LocalePatch]::AssemblyHash
            Installed = $manifest.patchedAssemblySha256
        }
    )

    $pending = @(
        foreach ($file in $files) {
            $file.Backup = "$($file.Path).pc-together-rus-original"
            $file.Stage = "$($file.Path).pc-together-rus-$token.tmp"
            $file.Previous = "$($file.Path).pc-together-rus-$token.previous"
            $file.BackupStage = "$($file.Path).pc-together-rus-$token.backup"
            if (-not [IO.File]::Exists($file.Path)) {
                throw "Отсутствует файл игры $($file.Path)."
            }
            $file.Current = [PcTogetherRus.LocalePatch]::FileHash($file.Path)
            $file.HasBackup = [IO.File]::Exists($file.Backup)
            if ($file.HasBackup -and [PcTogetherRus.LocalePatch]::FileHash($file.Backup) -ne $file.Original) {
                throw "Повреждена резервная копия $($file.Backup)."
            }
            if ($file.Current -ne $file.Original -and ($file.Current -ne $file.Installed -or -not $file.HasBackup)) {
                throw "Файл $($file.Path) отличается от Steam-сборки 26897952 или установлен без резервной копии."
            }
            $file.Target = if ($Action -eq 'Install') { $file.Installed } else { $file.Original }
            if ($file.Current -ne $file.Target) {
                $file
            }
        }
    )

    if ($pending.Count -eq 0) {
        if ($Action -eq 'Install') {
            Write-Host 'Русификатор уже установлен. Выберите «Русский» в настройках языка игры.'
        }
        else {
            Write-Host 'Игра уже использует оригинальные файлы.'
        }
        return
    }

    $replaced = [Collections.Generic.List[hashtable]]::new()
    try {
        foreach ($file in $pending) {
            if ($Action -eq 'Restore') {
                [IO.File]::Copy($file.Backup, $file.Stage)
                $stagedHash = [PcTogetherRus.LocalePatch]::FileHash($file.Stage)
            }
            elseif ($file.Kind -eq 'Locale') {
                $stagedHash = [PcTogetherRus.LocalePatch]::Build($file.Path, $file.Stage, (Read-RussianDictionary))
            }
            else {
                $stagedHash = [PcTogetherRus.LabelPatch]::Build($file.Path, $file.Stage)
            }
            if ($stagedHash -ne $file.Target) {
                throw "Контрольная сумма подготовленного файла $($file.Path) отличается от выпуска."
            }
            if (-not $file.HasBackup) {
                [IO.File]::Copy($file.Path, $file.BackupStage)
                if ([PcTogetherRus.LocalePatch]::FileHash($file.BackupStage) -ne $file.Original) {
                    throw "Не удалось проверить резервную копию $($file.Path)."
                }
                [IO.File]::Move($file.BackupStage, $file.Backup)
            }
        }

        Assert-GameClosed
        foreach ($file in $files) {
            if ([PcTogetherRus.LocalePatch]::FileHash($file.Path) -ne $file.Current) {
                throw "Файл $($file.Path) изменился во время подготовки."
            }
        }
        foreach ($file in $pending) {
            [IO.File]::Replace($file.Stage, $file.Path, $file.Previous)
            $replaced.Add($file)
            if ([PcTogetherRus.LocalePatch]::FileHash($file.Path) -ne $file.Target) {
                throw "Не совпадает контрольная сумма заменённого файла $($file.Path)."
            }
        }
    }
    catch {
        for ($index = $replaced.Count - 1; $index -ge 0; $index--) {
            $file = $replaced[$index]
            if ([IO.File]::Exists($file.Previous)) {
                try {
                    [IO.File]::Replace($file.Previous, $file.Path, [NullString]::Value)
                }
                catch {
                    Write-Warning "Откат $($file.Path) прервался. Резервные файлы сохранены. Запустите восстановление повторно."
                }
            }
        }
        throw
    }
    finally {
        foreach ($file in $pending) {
            Remove-Item -LiteralPath $file.Stage, $file.BackupStage -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($file in $replaced) {
        Remove-Item -LiteralPath $file.Previous -Force -ErrorAction SilentlyContinue
    }

    if ($Action -eq 'Install') {
        Write-Host 'Русификатор установлен. Выберите «Русский» в настройках языка игры.'
    }
    else {
        Write-Host 'Оригинальные файлы восстановлены. При необходимости выберите язык в игре заново.'
    }
}

Assert-GameClosed
if (-not ('PcTogetherRus.LocalePatch' -as [type])) {
    $cecil = Join-Path $PSScriptRoot 'lib\Mono.Cecil.dll'
    if ((Get-FileHash -LiteralPath $cecil -Algorithm SHA256).Hash -ne 'C41BDB9FFD3C5F6E17D2382C1012D73703E035E3F1100245FDD4E08C8DC6EB5B') {
        throw 'Библиотека Mono.Cecil отличается от версии, включённой в выпуск.'
    }
    Add-Type -Path $cecil
    $sources = @((Join-Path $PSScriptRoot 'PcStoreLocale.cs'), (Join-Path $PSScriptRoot 'PcStoreLabels.cs'))
    Add-Type -Path $sources -ReferencedAssemblies @($cecil, 'System.dll', 'System.Core.dll')
}

$lockPath = Join-Path $data '.pc-together-rus.lock'
$lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
try {
    Invoke-RussianPatch
}
finally {
    $lock.Dispose()
}
