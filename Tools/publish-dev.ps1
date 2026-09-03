<#
.SYNOPSIS
    Автономный скрипт сборки, архивации и публикации Dev-релизов LMP.

.DESCRIPTION
    Совместим с Windows PowerShell 5.1.
    Реализует схему двухканальной доставки:
    1. Постоянный плавающий релиз 'dev' с неизменными ссылками на *-latest.7z.
    2. Версионированные релизы 'dev-<commitCount>' для хранения истории и быстрого отката.

.PARAMETER SkipBuild
    Пропустить компиляцию (если бинарники уже собраны).

.PARAMETER ReleaseOnly
    Собирать только Release-версию (без отладочного Debug-пакета).

.PARAMETER RetentionCount
    Количество последних версий истории dev-* для хранения на GitHub (по умолчанию: 10).

.EXAMPLE
    .\Tools\publish-dev.ps1
    .\Tools\publish-dev.ps1 -ReleaseOnly
    .\Tools\publish-dev.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild = $false,
    [switch]$ReleaseOnly = $false,
    [int]$RetentionCount = 10
)

$ErrorActionPreference = "Stop"

# Корень репозитория
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# Изолированная папка артефактов (в .gitignore)
$ArtifactsDir = Join-Path $RepoRoot "publish-dev"
if (Test-Path $ArtifactsDir) {
    Remove-Item "$ArtifactsDir\*" -Recurse -Force -ErrorAction SilentlyContinue
} else {
    New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
}

# Включение TLS 1.2 для GitHub API в PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Resolve-7Zip {
    if (Get-Command 7z -ErrorAction SilentlyContinue) { return }

    $knownPaths = @(
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe",
        "$env:LOCALAPPDATA\Programs\7-Zip\7z.exe"
    )

    foreach ($path in $knownPaths) {
        if (Test-Path $path) {
            $env:PATH = "$(Split-Path $path);$env:PATH"
            return
        }
    }

    Write-Error "7-Zip не найден. Установите 7-Zip или добавьте 7z.exe в PATH."
    exit 1
}

function Resolve-GitHubCli {
    $ghDir = Join-Path $env:LOCALAPPDATA "Programs\gh"
    $ghExe = Join-Path $ghDir "gh.exe"

    if (Test-Path $ghExe) {
        $env:PATH = "$ghDir;$env:PATH"
        return
    }

    if (Get-Command gh -ErrorAction SilentlyContinue) { return }

    Write-Host ">>> Загрузка GitHub CLI..." -ForegroundColor Yellow

    if (-not (Test-Path $ghDir)) {
        New-Item -ItemType Directory -Path $ghDir -Force | Out-Null
    }

    $tempZip     = Join-Path $env:TEMP "gh_portable.zip"
    $tempExtract = Join-Path $env:TEMP "gh_extract"

    try {
        $headers = @{ "User-Agent" = "LMP-Build-Script" }
        $releaseMeta = Invoke-RestMethod -Uri "https://api.github.com/repos/cli/cli/releases/latest" -Headers $headers -UseBasicParsing
        $asset = $releaseMeta.assets | Where-Object { $_.name -like "*_windows_amd64.zip" } | Select-Object -First 1

        if (-not $asset) {
            Write-Error "Не удалось найти Windows-ассет в релизах GitHub CLI."
            exit 1
        }

        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tempZip -Headers $headers -UseBasicParsing

        if (Test-Path $tempExtract) {
            Remove-Item $tempExtract -Recurse -Force
        }

        Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force
        $foundGh = Get-ChildItem -Path $tempExtract -Filter "gh.exe" -Recurse | Select-Object -First 1
        if (-not $foundGh) {
            Write-Error "gh.exe не найден в архиве."
            exit 1
        }

        Copy-Item -Path $foundGh.FullName -Destination $ghExe -Force
        Write-Host "✓ GitHub CLI установлен в $ghExe" -ForegroundColor Green
    }
    finally {
        if (Test-Path $tempZip) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
        if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $currentUserPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
    if ($currentUserPath -notlike "*$ghDir*") {
        [Environment]::SetEnvironmentVariable("Path", "$currentUserPath;$ghDir", [EnvironmentVariableTarget]::User)
    }

    $env:PATH = "$ghDir;$env:PATH"
}

# 1. Зависимости
Resolve-7Zip
Resolve-GitHubCli

# Проверка авторизации
$authStatus = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Требуется вход в GitHub CLI. Запустите 'gh auth login'." -ForegroundColor Yellow
    gh auth login
}

# Получаем slug репозитория (например, paralax034/LMP) для генерации ссылок
$repoSlug = ""
try {
    $repoJson = gh repo view --json owner,name 2>$null
    if (-not [string]::IsNullOrEmpty($repoJson)) {
        $repoObj = $repoJson | ConvertFrom-Json
        $repoSlug = "$($repoObj.owner.login)/$($repoObj.name)"
    }
} catch { }

# 2. Вычисление версии
$commitCount = (git rev-list --count HEAD).Trim()
$shortHash   = (git rev-parse --short=7 HEAD).Trim()
$fullVersion = "$commitCount-$shortHash"
$historyTag  = "dev-$commitCount"
$latestTag   = "dev"

$sourceEnv = if ($env:GITHUB_ACTIONS) { "CI Runner" } else { "Local Workstation" }

Write-Host "==============================================================" -ForegroundColor DarkGray
Write-Host " LMP Dev Pipeline: v$fullVersion" -ForegroundColor Cyan
Write-Host " Тег снапшота истории: $historyTag" -ForegroundColor Cyan
Write-Host " Плавающий постоянный:  $latestTag" -ForegroundColor Cyan
Write-Host "==============================================================" -ForegroundColor DarkGray

# 3. Компиляция проекта
$buildScript = Join-Path $RepoRoot "build.bat"

if (-not $SkipBuild) {
    if (-not $ReleaseOnly) {
        Write-Host ">>> Сборка Debug-конфигурации..." -ForegroundColor Yellow
        & $buildScript "debug" "nopause"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Write-Host ">>> Сборка Release (publish)..." -ForegroundColor Yellow
    & $buildScript "publish" "nopause"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# 4. Упаковка файлов в 7-Zip
$publishDir = Join-Path $RepoRoot "publish"
$debugDir   = Join-Path $RepoRoot "bin\Debug\net11.0"

# 4.1. Версионированные архивы (для истории)
$versionedReleaseArchive = Join-Path $ArtifactsDir "LMP-Release-v$fullVersion.7z"
Write-Host ">>> Упаковка Release-снапшота..." -ForegroundColor Yellow
7z a -t7z -mx=9 $versionedReleaseArchive "$publishDir/*" | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$versionedArchives = [System.Collections.Generic.List[string]]::new()
$versionedArchives.Add($versionedReleaseArchive)

$versionedDebugArchive = ""
if (-not $ReleaseOnly -and (Test-Path $debugDir)) {
    $versionedDebugArchive = Join-Path $ArtifactsDir "LMP-Debug-v$fullVersion.7z"
    Write-Host ">>> Упаковка Debug-снапшота..." -ForegroundColor Yellow
    7z a -t7z -mx=9 $versionedDebugArchive "$debugDir/*" | Out-Null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $versionedArchives.Add($versionedDebugArchive)
}

# 4.2. Статичные архивы (для постоянной ссылки latest)
$latestReleaseArchive = Join-Path $ArtifactsDir "LMP-Release-latest.7z"
Copy-Item $versionedReleaseArchive $latestReleaseArchive -Force

$latestArchives = [System.Collections.Generic.List[string]]::new()
$latestArchives.Add($latestReleaseArchive)

if (-not [string]::IsNullOrEmpty($versionedDebugArchive)) {
    $latestDebugArchive = Join-Path $ArtifactsDir "LMP-Debug-latest.7z"
    Copy-Item $versionedDebugArchive $latestDebugArchive -Force
    $latestArchives.Add($latestDebugArchive)
}

# 5. Описание для снапшота истории
$historyNotesTemplate = @"
**Снапшот dev-сборки для тестирования и отката**

| Параметр | Значение |
|----------|----------|
| Версия | `{0}` |
| Тег | `{1}` |
| Коммит | `{2}` |
| Всего коммитов | {3} |
| Среда сборки | {4} |

Постоянная ссылка на последнюю версию всегда доступна в релизе [`{5}`](https://github.com/{6}/releases/tag/{5}).
"@
$historyNotes = $historyNotesTemplate -f $fullVersion, $historyTag, $shortHash, $commitCount, $sourceEnv, $latestTag, $repoSlug

# 6. Описание для постоянного релиза dev
$latestNotesTemplate = @"
# 🎵 LMP Dev Build (Latest)

Актуальная предрелизная сборка. Ссылки на файлы в этом релизе постоянны и не меняются со временем.

| Параметр | Значение |
|----------|----------|
| Актуальная версия | `{0}` |
| Номер коммита | `{1}` |
| Архивный снапшот | [`{2}`](https://github.com/{5}/releases/tag/{2}) |
| Среда сборки | {3} |
| Дата обновления | {4} |

### 📥 Постоянные ссылки на загрузку:
- **[Скачать LMP-Release-latest.7z](https://github.com/{5}/releases/download/{6}/LMP-Release-latest.7z)** — рекомендуемая сборка
- **[Скачать LMP-Debug-latest.7z](https://github.com/{5}/releases/download/{6}/LMP-Debug-latest.7z)** — отладочная версия с логами
"@
$latestNotes = $latestNotesTemplate -f $fullVersion, $shortHash, $historyTag, $sourceEnv, (Get-Date -Format "yyyy-MM-dd HH:mm UTC"), $repoSlug, $latestTag

# 7. Публикация истории dev-$commitCount
Write-Host ">>> Публикация снапшота истории '$historyTag'..." -ForegroundColor Green
$existingHistory = gh release list --limit 50 --json tagName 2>$null
$historyExists = $false
if (-not [string]::IsNullOrEmpty($existingHistory)) {
    $historyExists = @(@($existingHistory | ConvertFrom-Json) | Where-Object { $_.tagName -eq $historyTag }).Count -gt 0
}

if ($historyExists) {
    foreach ($archive in $versionedArchives) {
        gh release upload $historyTag $archive --clobber
    }
    gh release edit $historyTag --title "Dev Build v$fullVersion" --notes $historyNotes --prerelease
} else {
    gh release create $historyTag $versionedArchives `
        --title "Dev Build v$fullVersion" `
        --prerelease `
        --notes $historyNotes
}

# 8. Публикация в постоянный релиз dev (latest)
Write-Host ">>> Обновление постоянного релиза '$latestTag'..." -ForegroundColor Green
$existingLatest = gh release list --limit 50 --json tagName 2>$null
$latestExists = $false
if (-not [string]::IsNullOrEmpty($existingLatest)) {
    $latestExists = @(@($existingLatest | ConvertFrom-Json) | Where-Object { $_.tagName -eq $latestTag }).Count -gt 0
}

if ($latestExists) {
    foreach ($archive in $latestArchives) {
        gh release upload $latestTag $archive --clobber
    }
    gh release edit $latestTag --title "LMP Dev Build (Latest)" --notes $latestNotes --prerelease
} else {
    gh release create $latestTag $latestArchives `
        --title "LMP Dev Build (Latest)" `
        --prerelease `
        --notes $latestNotes
}

# 9. Ротация истории (удаление только dev-<число>, не трогая постоянный dev)
Write-Host ">>> Проверка ротации релизов (хранить последние $RetentionCount сборок истории)..." -ForegroundColor DarkGray
try {
    $releasesRaw = gh release list --limit 100 --json tagName 2>$null
    if (-not [string]::IsNullOrEmpty($releasesRaw)) {
        # Строгая фильтрация по шаблону dev-<цифры> — тег "dev" никогда не попадет под удаление
        $historyReleases = @(@($releasesRaw | ConvertFrom-Json) | Where-Object { $_.tagName -match '^dev-\d+$' })

        if ($historyReleases.Count -gt $RetentionCount) {
            $toDelete = @($historyReleases | Select-Object -Skip $RetentionCount)
            foreach ($oldRel in $toDelete) {
                Write-Host ">>> Удаление устаревшего снапшота истории: $($oldRel.tagName)" -ForegroundColor DarkYellow
                gh release delete $oldRel.tagName -y --cleanup-tag
            }
        }
    }
} catch {
    Write-Host "[ПРЕДУПРЕЖДЕНИЕ] Ошибка ротации: $_" -ForegroundColor DarkGray
}

Write-Host "==============================================================" -ForegroundColor DarkGray
Write-Host " Релизы успешно опубликованы!" -ForegroundColor Green
Write-Host " Ссылка на Latest: https://github.com/$repoSlug/releases/tag/$latestTag" -ForegroundColor White
Write-Host " Ссылка на Архив:  https://github.com/$repoSlug/releases/tag/$historyTag" -ForegroundColor White
Write-Host "==============================================================" -ForegroundColor DarkGray