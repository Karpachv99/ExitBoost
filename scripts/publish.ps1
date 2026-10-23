# Сборка ExitBoost: безопасная публикация + архив для GitHub Releases.
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 1.1
#
# Собираем в staging-папку, затем подменяем готовые папки в publish\ —
# так запущенная копия программы никогда не увидит полузаписанные файлы.

param(
    [string]$Version = "",
    [switch]$SkipStop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $m = Select-String -Path 'src\ExitBoost.App\ExitBoost.App.csproj' -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $Version = $m.Matches[0].Groups[1].Value } else { $Version = '1.0.0' }
}

Write-Host "ExitBoost $Version — публикация" -ForegroundColor Cyan

if (-not $SkipStop) {
    $running = Get-Process ExitBoost -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Закрываю запущенный ExitBoost (PID $($running.Id -join ', '))"
        try {
            $running | Stop-Process -Force -ErrorAction Stop
            Start-Sleep -Seconds 2
        }
        catch {
            Write-Warning 'Запущенный ExitBoost закрыть не удалось (он работает с правами администратора). Сборка продолжится, но перед проверкой закройте его вручную.'
        }
    }
}

$stage = Join-Path $root 'publish\_stage'
$release = Join-Path $root 'publish\release'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Чистая сборка: инкрементальная проверка здесь ненадёжна (часы системы могут
# быть «в будущем», и MSBuild считает старые файлы новее исходников).
foreach ($dir in @(
    'src\ExitBoost.Core\bin', 'src\ExitBoost.Core\obj',
    'src\ExitBoost.App\bin', 'src\ExitBoost.App\obj',
    'tools\ExitBoost.Cli\bin', 'tools\ExitBoost.Cli\obj')) {
    Remove-Item (Join-Path $root $dir) -Recurse -Force -ErrorAction SilentlyContinue
}

dotnet publish src\ExitBoost.App -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o "$stage\ExitBoost" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.App' }

dotnet publish tools\ExitBoost.Cli -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o "$stage\ExitBoost.Cli" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.Cli' }

foreach ($name in @('ExitBoost', 'ExitBoost.Cli')) {
    $target = Join-Path $root "publish\$name"
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item (Join-Path $stage $name) $target -Force
}
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# Архив для GitHub Releases: внутри — файлы без лишней папки.
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $release -Force | Out-Null
Get-ChildItem $release -File | Remove-Item -Force -ErrorAction SilentlyContinue
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

# Установщик (Inno Setup): ставится как обычная программа, ярлыки + удаление.
$setup = Join-Path $release "ExitBoost-$Version-setup.exe"
Remove-Item $setup -Force -ErrorAction SilentlyContinue
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    Write-Host "Собираю установщик: $iscc" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$root\publish\ExitBoost" "/O$release" 'installer\ExitBoost.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать установщик (ISCC)' }
}
else {
    Write-Warning 'Inno Setup не найден — установщик не собран. Установите: winget install JRSoftware.InnoSetup'
}

Write-Host ''
Write-Host "Готово." -ForegroundColor Green
Write-Host "  программа: publish\ExitBoost"
Write-Host "  CLI:       publish\ExitBoost.Cli"
Write-Host "  релиз:     $zip"
Write-Host "             $cliZip"
if (Test-Path $setup) { Write-Host "  установщик: $setup" }
Write-Host ''
Write-Host "Загрузка на GitHub:  gh release create v$Version `"$setup`" `"$zip`" `"$cliZip`" --title `"ExitBoost $Version`" --notes `"...`""
