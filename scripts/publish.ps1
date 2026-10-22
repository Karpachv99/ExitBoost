# Сборка ExitBoost: безопасная публикация + архив для GitHub Releases.
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 1.1
#
# Собираем в staging-папку, затем подменяем готовые папки в publish\ —
# так запущенная копия программы никогда не увидит полузаписанные файлы.

param(
    [string]$Version = "",
    [switch]$SkipKill
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $m = Select-String -Path 'src\ExitBoost.App\ExitBoost.App.csproj' -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $Version = $m.Matches[0].Groups[1].Value } else { $Version = '1.0.0' }
}

Write-Host "ExitBoost $Version — публикация" -ForegroundColor Cyan

if (-not $SkipKill) {
    $running = Get-Process ExitBoost -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Закрываю запущенный ExitBoost (PID $($running.Id -join ', '))"
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

$stage = Join-Path $root 'publish\_stage'
$release = Join-Path $root 'publish\release'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Полная пересборка: инкрементальная проверка здесь ненадёжна (часы системы
# могут быть «в будущем», и MSBuild считает старые файлы новее исходников).
dotnet build src\ExitBoost.Core -c Release --no-incremental --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.Core' }
dotnet build src\ExitBoost.App -c Release -r win-x64 --self-contained true --no-incremental -p:Version=$Version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.App' }
dotnet build tools\ExitBoost.Cli -c Release -r win-x64 --self-contained true --no-incremental -p:Version=$Version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.Cli' }

dotnet publish src\ExitBoost.App -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o "$stage\ExitBoost" --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось опубликовать ExitBoost.App' }

dotnet publish tools\ExitBoost.Cli -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o "$stage\ExitBoost.Cli" --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось опубликовать ExitBoost.Cli' }

foreach ($name in @('ExitBoost', 'ExitBoost.Cli')) {
    $target = Join-Path $root "publish\$name"
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item (Join-Path $stage $name) $target -Force
}
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# Архив для GitHub Releases: внутри — файлы без лишней папки.
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $release -Force | Out-Null
$zip = Join-Path $release "ExitBoost-$Version-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $root 'publish\ExitBoost'), $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

$cliZip = Join-Path $release "ExitBoost.Cli-$Version-win-x64.zip"
Remove-Item $cliZip -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $root 'publish\ExitBoost.Cli'), $cliZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host ''
Write-Host "Готово." -ForegroundColor Green
Write-Host "  программа: publish\ExitBoost"
Write-Host "  CLI:       publish\ExitBoost.Cli"
Write-Host "  релиз:     $zip"
Write-Host "             $cliZip"
Write-Host ''
Write-Host "Загрузка на GitHub:  gh release create v$Version `"$zip`" `"$cliZip`" --title `"ExitBoost $Version`" --notes `"...`""
