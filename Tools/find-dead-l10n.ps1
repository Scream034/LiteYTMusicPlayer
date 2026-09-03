<#
.SYNOPSIS
    Проверяет локализационные JSON-файлы на мёртвые и недостающие ключи.

.PARAMETER Fix
    Автоматически удалить мёртвые ключи из JSON-файлов.

.PARAMETER Master
    Мастер-язык (по умолчанию: ru).

.PARAMETER L10nDir
    Путь к папке с JSON-файлами относительно корня проекта.

.PARAMETER SourceDirs
    Папки с исходным кодом через запятую.

.EXAMPLE
    .\Tools\find-dead-l10n.ps1
    .\Tools\find-dead-l10n.ps1 -Fix
    .\Tools\find-dead-l10n.ps1 -Fix -Master en
#>

param(
    [switch]$Fix,
    [string]$Master     = "ru",
    [string]$L10nDir    = "Assets\Localization",
    [string]$SourceDirs = "Core,Features,UI"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Вспомогательные функции вывода ---

function Write-Dead([string]$location, [string]$key) {
    Write-Host ("  {0,-9} {1,-55} {2}" -f "МЁРТВЫЙ", $location, $key) -ForegroundColor DarkYellow
}

function Write-Missing([string]$location, [string]$key) {
    Write-Host ("  {0,-9} {1,-55} {2}" -f "ПРОПУЩЕН", $location, $key) -ForegroundColor Red
}

# --- Инициализация путей ---

$root    = Split-Path $PSScriptRoot -Parent
$l10nDir = Join-Path $root $L10nDir

Write-Host ""
Write-Host "  Корень проекта : $root"    -ForegroundColor Gray
Write-Host "  Папка локал.   : $l10nDir" -ForegroundColor Gray
Write-Host "  Мастер-язык    : $Master"  -ForegroundColor Gray

# --- Загрузка JSON-словарей ---

function Load-Json([string]$path) {
    $json = Get-Content $path -Raw -Encoding UTF8
    $dict = @{}
    $re   = [regex]'"([^"\\]+)"\s*:\s*"((?:[^"\\]|\\.)*)"'
    foreach ($m in $re.Matches($json)) {
        $dict[$m.Groups[1].Value] = $m.Groups[2].Value
    }
    return $dict
}

$jsonFiles = @(Get-ChildItem $l10nDir -Filter "*.json" | Sort-Object Name)
if ($jsonFiles.Count -eq 0) {
    Write-Host "   JSON-файлы не найдены в директории $l10nDir" -ForegroundColor Red
    exit 1
}

$langData = @{}
foreach ($f in $jsonFiles) {
    $code            = $f.BaseName
    $langData[$code] = Load-Json $f.FullName
    Write-Host "  Загружен $($f.Name)  ($($langData[$code].Count) ключей)" -ForegroundColor Gray
}

if (-not $langData.ContainsKey($Master)) {
    Write-Host "  Мастер-язык '$Master' не найден среди файлов локализации" -ForegroundColor Red
    exit 1
}

$masterDict   = $langData[$Master]
$allKnownKeys = [System.Collections.Generic.HashSet[string]]::new()
foreach ($k in $masterDict.Keys) { [void]$allKnownKeys.Add($k) }

# --- Сбор исходных файлов ---

$allFiles = [System.Collections.Generic.List[string]]::new()
foreach ($dir in ($SourceDirs -split ",")) {
    $full = Join-Path $root $dir.Trim()
    if (Test-Path $full) {
        Get-ChildItem $full -Recurse -Include "*.cs","*.axaml" |
            ForEach-Object { $allFiles.Add($_.FullName) }
    }
}
Get-ChildItem $root -Filter "*.cs"    -File | ForEach-Object { $allFiles.Add($_.FullName) }
Get-ChildItem $root -Filter "*.axaml" -File | ForEach-Object { $allFiles.Add($_.FullName) }

$sourceFiles = $allFiles | Sort-Object -Unique
Write-Host "  Исходных файлов: $(@($sourceFiles).Count)  (.cs + .axaml)" -ForegroundColor Gray

# --- Регулярные выражения для поиска ключей ---

$patterns = @(
    '(?:SL|L|LocalizationService\.Instance)\s*\[\s*"([A-Za-z][A-Za-z0-9_]+)"\s*\]',
    '\.(?:Get|RawGet|GetPlural)\(\s*"([A-Za-z][A-Za-z0-9_]+)"',
    '(?:SL|L)\[([A-Za-z][A-Za-z0-9_]+)\]',
    '\{l:Loc\s+(?:Key=)?([A-Za-z][A-Za-z0-9_]+)',
    'ShowToastAsync\(\s*"([A-Za-z][A-Za-z0-9_]+)"\s*,\s*"([A-Za-z][A-Za-z0-9_]+)"',
    'ShowPlaybackErrorAsync\(\s*"([A-Za-z][A-Za-z0-9_]+)"\s*,\s*"([A-Za-z][A-Za-z0-9_]+)"',
    '(?i)(?:title|message|recommendation)Key\s*[=:]\s*"([A-Za-z][A-Za-z0-9_]+)"',
    '^\s*(?:[?:,]|=>)?\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"\s*[,;]?\s*$',
    '[?:]\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"',
    '=>\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"',
    '\(\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"'
)

$usedKeys = @{}

# Белый список динамических ключей
$dynamicKeys = @(
    "Home_Greeting_Morning", "Home_Greeting_Afternoon", "Home_Greeting_Evening",
    "NetProfile_Low", "NetProfile_Medium", "NetProfile_High", "NetProfile_Ultra",
    "AudioQuality_BestAvailable", "AudioQuality_Standard",
    "Client_AndroidVR", "Client_TV", "Client_Web",
    "Cache_Low", "Cache_Medium", "Cache_High",
    "VolumeCurve_Linear", "VolumeCurve_Quadratic", "VolumeCurve_Logarithmic", "VolumeCurve_Cubic", "VolumeCurve_SpeedOfLight",
    "CloseAction_Exit", "CloseAction_MinimizeToTray", "CloseAction_Ask"
)

foreach ($key in $dynamicKeys) {
    if (-not $usedKeys.ContainsKey($key)) {
        $usedKeys[$key] = "dynamic (whitelist)"
    }
}

function Test-IsL10nKey([string]$key) {
    if (-not $key.Contains('_')) { return $false }
    $upper = $key.ToUpperInvariant()
    if ($key -ceq $upper) { return $false }
    if (-not [char]::IsUpper($key[0])) { return $false }
    return $true
}

$pluralSuffixes = @("_0","_1","_2","_3","_4","_5","_one","_few","_many","_other","_zero")

function Test-PluralSuffix([string]$key) {
    foreach ($s in $pluralSuffixes) {
        if ($key.EndsWith($s)) { return $true }
    }
    return $false
}

# --- Сканирование исходного кода ---

foreach ($filePath in $sourceFiles) {
    $relPath = $filePath.Substring($root.Length).TrimStart('\')
    $content = Get-Content $filePath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($content)) { continue }

    foreach ($pattern in $patterns) {
        $matches = [regex]::Matches($content, $pattern, 'Multiline')
        foreach ($m in $matches) {
            $lineNum = [regex]::Matches($content.Substring(0, $m.Index), "\r?\n").Count + 1

            for ($g = 1; $g -lt $m.Groups.Count; $g++) {
                $key = $m.Groups[$g].Value.Trim()
                if ($key.Length -gt 2 -and -not $usedKeys.ContainsKey($key)) {
                    $usedKeys[$key] = "${relPath}:${lineNum}"
                }
            }
        }
    }
}

Write-Host "  Уникальных ключей в коде    : $($usedKeys.Count)" -ForegroundColor Gray

# --- Анализ совпадений ---

$deadKeys    = [System.Collections.Generic.List[string]]::new()
$missingKeys = [System.Collections.Generic.List[string]]::new()
$pluralFP    = 0

foreach ($key in @($masterDict.Keys)) {
    if (-not $usedKeys.ContainsKey($key) -and -not (Test-PluralSuffix $key)) {
        $deadKeys.Add($key)
    }
}

foreach ($key in $usedKeys.Keys) {
    if (-not $masterDict.ContainsKey($key)) {
        if (Test-PluralSuffix $key) {
            $pluralFP++
        } elseif (Test-IsL10nKey $key) {
            $missingKeys.Add($key)
        }
    }
}

$syncIssues = 0
foreach ($code in $langData.Keys) {
    if ($code -eq $Master) { continue }
    $other = $langData[$code]
    foreach ($key in @($masterDict.Keys)) {
        if (-not $other.ContainsKey($key)) {
            Write-Host "  [!!] В файле $code.json отсутствует ключ: $key" -ForegroundColor Yellow
            $syncIssues++
        }
    }
    foreach ($key in @($other.Keys)) {
        if (-not $masterDict.ContainsKey($key)) {
            Write-Host "  [!!] В файле $code.json обнаружен лишний ключ: $key" -ForegroundColor Yellow
            $syncIssues++
        }
    }
}

# --- Вывод отчетов ---

if ($deadKeys.Count -gt 0) {
    Write-Host ""
    Write-Host ("-- Мёртвые ключи ({0}) " -f $deadKeys.Count).PadRight(60, '-') -ForegroundColor DarkYellow
    Write-Host "   Присутствуют в $Master.json, но не используются в исходном коде." -ForegroundColor Gray
    Write-Host ""
    Write-Host ("  {0,-9} {1,-55} {2}" -f "СТАТУС", "РАСПОЛОЖЕНИЕ", "КЛЮЧ") -ForegroundColor DarkGray

    $jsonLines = @(Get-Content (Join-Path $l10nDir "$Master.json") -Encoding UTF8)
    foreach ($key in $deadKeys) {
        $lineNum = 0
        for ($i = 0; $i -lt $jsonLines.Count; $i++) {
            if ($jsonLines[$i] -match ('"' + [regex]::Escape($key) + '"')) {
                $lineNum = $i + 1; break
            }
        }
        $loc = if ($lineNum -gt 0) { "$Master.json:$lineNum" } else { "$Master.json" }
        Write-Dead $loc $key
    }
}

if ($missingKeys.Count -gt 0) {
    Write-Host ""
    Write-Host ("-- Пропущенные ключи ({0}) " -f $missingKeys.Count).PadRight(60, '-') -ForegroundColor Red
    Write-Host "   Используются в коде, но отсутствуют в $Master.json." -ForegroundColor Gray
    Write-Host ""
    Write-Host ("  {0,-9} {1,-55} {2}" -f "СТАТУС", "РАСПОЛОЖЕНИЕ", "КЛЮЧ") -ForegroundColor DarkGray

    foreach ($key in ($missingKeys | Sort-Object)) {
        $loc = if ($usedKeys.ContainsKey($key)) { $usedKeys[$key] } else { "неизвестно" }
        Write-Missing $loc $key
    }
}

if ($pluralFP -gt 0) {
    Write-Host ""
    Write-Host ("-- Пропущенные Plural-формы ({0}) " -f $pluralFP).PadRight(60, '-') -ForegroundColor DarkGray
    Write-Host "   Автоматические суффиксные формы множественного числа." -ForegroundColor Gray
}

# --- Автоматическое исправление (если передан ключ -Fix) ---

if ($Fix -and $deadKeys.Count -gt 0) {
    Write-Host ""
    Write-Host "-- Применение автоисправлений " -ForegroundColor Cyan

    foreach ($code in $langData.Keys) {
        $dict    = $langData[$code]
        $removed = 0

        foreach ($key in $deadKeys) {
            if ($dict.ContainsKey($key)) {
                $dict.Remove($key)
                $removed++
            }
        }

        if ($removed -gt 0) {
            $filePath   = Join-Path $l10nDir "$code.json"
            $origLines  = @(Get-Content $filePath -Encoding UTF8)
            $deadSet    = [System.Collections.Generic.HashSet[string]]::new()
            foreach ($k in $deadKeys) { [void]$deadSet.Add($k) }

            $outLines = [System.Collections.Generic.List[string]]::new()
            $prevWasRemoved = $false

            foreach ($line in $origLines) {
                $skip = $false
                foreach ($k in $deadSet) {
                    if ($line -match ('"' + [regex]::Escape($k) + '"\s*:')) {
                        $skip = $true; break
                    }
                }

                if ($skip) {
                    $prevWasRemoved = $true
                    continue
                }

                if ($prevWasRemoved -and $line.Trim() -eq '') {
                    $prevWasRemoved = $false
                    continue
                }

                $prevWasRemoved = $false
                $outLines.Add($line)
            }

            $result = [System.Collections.Generic.List[string]]::new($outLines)
            for ($i = $result.Count - 1; $i -ge 0; $i--) {
                $trimmed = $result[$i].Trim()
                if ($trimmed -eq '}') { continue }
                if ($trimmed -eq '') { continue }
                if ($result[$i] -match ',\s*$') {
                    $result[$i] = $result[$i] -replace ',\s*$', ''
                }
                break
            }

            [System.IO.File]::WriteAllLines($filePath, $result, [System.Text.Encoding]::UTF8)
            Write-Host "  [УСПЕХ] Удалено мертвых ключей: $removed из файла $code.json" -ForegroundColor Green
        }
    }
}

# --- Итоговая сводка ---

Write-Host ""
if ($syncIssues -eq 0) {
    Write-Host "  [ОК] Все языковые файлы синхронизированы." -ForegroundColor Green
} else {
    Write-Host "  [!!] Языковые файлы РАССИНХРОНИЗИРОВАНЫ." -ForegroundColor Red
}

Write-Host ""
Write-Host ("-" * 53) -ForegroundColor DarkGray
Write-Host "  Итоговая статистика"                                                                    -ForegroundColor White
Write-Host "    Ключей в мастере ($Master) : $($masterDict.Count)"                                     -ForegroundColor Gray
Write-Host "    Использовано в коде        : $($usedKeys.Count)"                                            -ForegroundColor Gray
Write-Host "    Мёртвых ключей             : $($deadKeys.Count)"    -ForegroundColor $(if ($deadKeys.Count    -eq 0) { "Green" } else { "DarkYellow" })
Write-Host "    Пропущенных ключей         : $($missingKeys.Count)" -ForegroundColor $(if ($missingKeys.Count -eq 0) { "Green" } else { "Red" })
Write-Host "    Пропущено Plural FP        : $pluralFP"             -ForegroundColor Gray
ZWrite-Host "    Ошибок синхронизации       : $syncIssues"           -ForegroundColor $(if ($syncIssues         -eq 0) { "Green" } else { "Red" })
Write-Host ""

exit ($deadKeys.Count + $missingKeys.Count + $syncIssues)