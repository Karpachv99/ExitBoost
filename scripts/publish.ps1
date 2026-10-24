# Сборка ExitBoost: приложение, CLI, отдельный updater, ZIP, установщик
# и манифест обновления update.json.
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 1.0.18 -Notes "Что нового"
#
# Собираем в staging-папку, затем подменяем готовые папки в publish\ —
# так запущенная копия программы никогда не увидит полузаписанные файлы.
#
# Манифест и release.json кладутся в publish\release вместе с архивами.
# Загружать нужно ВСЕ четыре файла: ZIP, setup, CLI ZIP и update.json.

param(
    [string]$Version = "",
    [string]$MinVersion = "",
    [string]$Notes = "",
    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'stable',
    [switch]$Mandatory,
    [switch]$SkipStop,
    [switch]$PublishGitHub
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# GitLab: project ID и путь нужны для прямых ссылок Package Registry.
$projectId = 88008209
$projectPath = 'karpachv99/ExitBoost'
$packageName = 'exitboost'

# GitHub — зеркало обновлений на случай, если GitLab недоступен.
$githubOwner = 'Karpachv99'
$githubRepo = 'ExitBoost'

if (-not $Version) {
    $m = Select-String -Path 'src\ExitBoost.App\ExitBoost.App.csproj' -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $Version = $m.Matches[0].Groups[1].Value } else { $Version = '1.0.0' }
}

if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "Версия должна выглядеть как 1.0.18 (получено: '$Version')"
}

$tag = "v$Version"
$assemblyVersion = if ($Version.Split('.').Count -ge 4) { $Version } else { "$Version.0" }

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
    'tools\ExitBoost.Cli\bin', 'tools\ExitBoost.Cli\obj',
    'tools\ExitBoost.Updater\bin', 'tools\ExitBoost.Updater\obj')) {
    Remove-Item (Join-Path $root $dir) -Recurse -Force -ErrorAction SilentlyContinue
}

# --- приложение -------------------------------------------------------------
dotnet publish src\ExitBoost.App -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$assemblyVersion `
    -o "$stage\ExitBoost" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.App' }

# --- CLI --------------------------------------------------------------------
dotnet publish tools\ExitBoost.Cli -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$assemblyVersion `
    -o "$stage\ExitBoost.Cli" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.Cli' }

# --- updater ----------------------------------------------------------------
# Отдельный маленький exe: он идёт в ZIP рядом с приложением и именно он
# заменяет папку установки после выхода программы.
dotnet publish tools\ExitBoost.Updater -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$Version -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$assemblyVersion `
    -o "$stage\_updater" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ExitBoost.Updater' }

$updaterExe = Join-Path $stage '_updater\ExitBoost.Updater.exe'
if (-not (Test-Path $updaterExe)) { throw 'ExitBoost.Updater.exe не собран' }
Copy-Item $updaterExe (Join-Path $stage 'ExitBoost\ExitBoost.Updater.exe') -Force

foreach ($name in @('ExitBoost', 'ExitBoost.Cli')) {
    $target = Join-Path $root "publish\$name"
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item (Join-Path $stage $name) $target -Force
}
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# --- архивы -----------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $release -Force | Out-Null
Get-ChildItem $release -File | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $release 'github') -Recurse -Force -ErrorAction SilentlyContinue

$zip = Join-Path $release "ExitBoost-$Version-win-x64.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $root 'publish\ExitBoost'), $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

$cliZip = Join-Path $release "ExitBoost.Cli-$Version-win-x64.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $root 'publish\ExitBoost.Cli'), $cliZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

# --- установщик -------------------------------------------------------------
$setup = Join-Path $release "ExitBoost-$Version-setup.exe"
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

# --- манифест обновления ----------------------------------------------------
$zipItem = Get-Item $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash.Length -ne 64) { throw 'Не удалось вычислить SHA-256 архива' }
if ($zipItem.Length -le 0) { throw 'Архив обновления пуст' }

$zipName = $zipItem.Name
$assetUrl = "https://gitlab.com/api/v4/projects/$projectId/packages/generic/$packageName/$Version/$zipName"
$releaseUrl = "https://gitlab.com/$projectPath/-/releases/$tag"

if (-not $Notes) {
    $Notes = "Надёжные обновления: манифест update.json, проверка SHA-256, " +
             "резервная копия и автоматический откат при неудаче."
}

$notesText = @"
$Notes

Размер архива: $($zipItem.Length) байт
SHA-256: $hash
"@

$manifest = [ordered]@{
    schema     = 1
    channel    = $Channel
    version    = $Version
    tag        = $tag
    releasedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    minVersion = $MinVersion
    notes      = $Notes
    notesUrl   = $releaseUrl
    mandatory  = [bool]$Mandatory
    asset      = [ordered]@{
        name   = $zipName
        url    = $assetUrl
        size   = $zipItem.Length
        sha256 = $hash
    }
}

# GitLab API и System.Text.Json не принимают UTF-8 BOM, поэтому пишем файлы
# через UTF8Encoding($false): Set-Content -Encoding utf8 в PowerShell 5.1
# добавляет BOM и ломает и манифест, и тело релиза.
function Write-JsonFile {
    param([string]$Path, [string]$Json)
    [System.IO.File]::WriteAllText($Path, $Json, (New-Object System.Text.UTF8Encoding($false)))
}

$manifestPath = Join-Path $release 'update.json'
Write-JsonFile -Path $manifestPath -Json ($manifest | ConvertTo-Json -Depth 6)
Write-Host "Манифест: $manifestPath" -ForegroundColor Green

# --- манифест зеркала GitHub ------------------------------------------------
# GitHub-релиз содержит свой update.json: ссылки внутри него ведут на ассеты
# GitHub, иначе при недоступном GitLab скачивание всё равно упало бы.
$githubReleaseUrl = "https://github.com/$githubOwner/$githubRepo/releases/tag/$tag"
$githubAssetUrl = "https://github.com/$githubOwner/$githubRepo/releases/download/$tag/$zipName"
$githubDir = Join-Path $release 'github'
New-Item -ItemType Directory -Path $githubDir -Force | Out-Null

$githubManifest = [ordered]@{
    schema     = 1
    channel    = $Channel
    version    = $Version
    tag        = $tag
    releasedAt = $manifest.releasedAt
    minVersion = $MinVersion
    notes      = $Notes
    notesUrl   = $githubReleaseUrl
    mandatory  = [bool]$Mandatory
    asset      = [ordered]@{
        name   = $zipName
        url    = $githubAssetUrl
        size   = $zipItem.Length
        sha256 = $hash
    }
}
$githubManifestPath = Join-Path $githubDir 'update.json'
Write-JsonFile -Path $githubManifestPath -Json ($githubManifest | ConvertTo-Json -Depth 6)
Write-Host "Манифест GitHub: $githubManifestPath" -ForegroundColor Green

# Старые клиенты (<= 1.0.16) читают размер из текста релиза. Маркеры оставляем,
# чтобы они хотя бы корректно показывали размер и не скачивали обрезанный архив.
$legacyDescription = $notesText + @"

<!-- exitboost-asset-size=$($zipItem.Length) -->
<!-- exitboost-asset-sha256=$hash -->
"@

$githubNotesPath = Join-Path $githubDir 'release.md'
$githubNotesText = $legacyDescription + @"

**Файлы релиза**

- [ExitBoost-$Version-win-x64.zip](https://github.com/$githubOwner/$githubRepo/releases/download/$tag/$zipName)
- [ExitBoost-$Version-setup.exe](https://github.com/$githubOwner/$githubRepo/releases/download/$tag/ExitBoost-$Version-setup.exe)
- [ExitBoost.Cli-$Version-win-x64.zip](https://github.com/$githubOwner/$githubRepo/releases/download/$tag/ExitBoost.Cli-$Version-win-x64.zip)
- [update.json](https://github.com/$githubOwner/$githubRepo/releases/download/$tag/update.json)
"@
[System.IO.File]::WriteAllText($githubNotesPath, $githubNotesText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Описание GitHub: $githubNotesPath" -ForegroundColor Green

$releaseLinks = @(
    [ordered]@{ name = 'update.json'; url = "https://gitlab.com/api/v4/projects/$projectId/packages/generic/$packageName/$Version/update.json"; link_type = 'other' },
    [ordered]@{ name = $zipName; url = $assetUrl; link_type = 'package' }
)
if (Test-Path $setup) {
    $setupName = Split-Path $setup -Leaf
    $releaseLinks += [ordered]@{
        name = $setupName
        url = "https://gitlab.com/api/v4/projects/$projectId/packages/generic/$packageName/$Version/$setupName"
        link_type = 'package'
    }
}
$cliName = Split-Path $cliZip -Leaf
$releaseLinks += [ordered]@{
    name = $cliName
    url = "https://gitlab.com/api/v4/projects/$projectId/packages/generic/$packageName/$Version/$cliName"
    link_type = 'package'
}

$releaseBody = [ordered]@{
    name        = "ExitBoost $Version"
    tag_name    = $tag
    description = $legacyDescription
    assets      = [ordered]@{ links = $releaseLinks }
}
$releasePath = Join-Path $release 'release.json'
Write-JsonFile -Path $releasePath -Json ($releaseBody | ConvertTo-Json -Depth 8)
Write-Host "Описание релиза: $releasePath" -ForegroundColor Green

# --- итог -------------------------------------------------------------------
$files = @(
    $zip,
    $manifestPath,
    $setup,
    $cliZip
) | Where-Object { $_ -and (Test-Path $_) }

Write-Host ''
Write-Host "Готово." -ForegroundColor Green
Write-Host "  программа:  publish\ExitBoost (внутри ExitBoost.exe и ExitBoost.Updater.exe)"
Write-Host "  CLI:        publish\ExitBoost.Cli"
Write-Host "  архив:      $zip"
Write-Host "  установщик: $(if (Test-Path $setup) { $setup } else { 'не собран' })"
Write-Host "  манифест:   $manifestPath"
Write-Host "  GitHub:     $githubManifestPath"
Write-Host "  SHA-256:    $hash"
Write-Host "  размер:     $($zipItem.Length) байт"
Write-Host ''
Write-Host "Загрузка в Package Registry (нужен Deploy Token со scope write_package_registry):" -ForegroundColor Cyan
foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    Write-Host "  curl.exe --http1.1 --ssl-no-revoke --fail --header `"DEPLOY-TOKEN: `$env:GITLAB_DEPLOY_TOKEN`" --upload-file `"$file`" https://gitlab.com/api/v4/projects/$projectId/packages/generic/$packageName/$Version/$name"
}
Write-Host ''
Write-Host "Создание релиза (нужен токен с api):" -ForegroundColor Cyan
Write-Host "  curl.exe --http1.1 --ssl-no-revoke --fail --request POST --header `"PRIVATE-TOKEN: `$env:GITLAB_API_TOKEN`" --header `"Content-Type: application/json`" --data-binary `"@$releasePath`" https://gitlab.com/api/v4/projects/$projectId/releases"

$ghAssets = @($zip, $githubManifestPath)
if (Test-Path $setup) { $ghAssets += $setup }
$ghAssets += $cliZip
$ghQuoted = ($ghAssets | ForEach-Object { "`"$_`"" }) -join ' '

Write-Host ''
Write-Host "Зеркало GitHub (gh CLI, аккаунт с правом repo):" -ForegroundColor Cyan
Write-Host "  gh release create $tag --repo $githubOwner/$githubRepo --title `"ExitBoost $Version`" --notes-file `"$githubNotesPath`" $ghQuoted"
Write-Host "  # если релиз уже есть:"
Write-Host "  gh release upload $tag --repo $githubOwner/$githubRepo --clobber $ghQuoted"
Write-Host "  gh release edit $tag --repo $githubOwner/$githubRepo --title `"ExitBoost $Version`" --notes-file `"$githubNotesPath`""

if ($PublishGitHub) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { throw 'GitHub CLI (gh) не найден. Установите: winget install GitHub.cli' }

    Write-Host ''
    Write-Host "Публикация в GitHub..." -ForegroundColor Cyan
    gh release view $tag --repo "$githubOwner/$githubRepo" *> $null
    $releaseExists = ($LASTEXITCODE -eq 0)

    if ($releaseExists) {
        gh release upload $tag --repo "$githubOwner/$githubRepo" --clobber @ghAssets
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось загрузить файлы в релиз GitHub' }

        gh release edit $tag --repo "$githubOwner/$githubRepo" `
            --title "ExitBoost $Version" --notes-file $githubNotesPath
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось обновить описание релиза GitHub' }
    }
    else {
        gh release create $tag --repo "$githubOwner/$githubRepo" `
            --title "ExitBoost $Version" --notes-file $githubNotesPath @ghAssets
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать релиз GitHub' }
    }

    Write-Host "GitHub: https://github.com/$githubOwner/$githubRepo/releases/tag/$tag" -ForegroundColor Green
}
