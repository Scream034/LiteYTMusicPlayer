<#
.SYNOPSIS
    Скрипт для поиска неиспользуемых (мёртвых) событий в C#-коде и AXAML-разметке.

.DESCRIPTION
    Сканирует все .cs и .axaml файлы в проекте, исключая служебные папки (bin, obj и др.),
    находит объявления событий с ключом 'event' и проверяет наличие подписок (оператор +=)
    и декларативных биндингов в разметке.

.EXAMPLE
    .\Tools\find-dead-events.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Поднимаемся в корень проекта из папки Tools
$root = Split-Path $PSScriptRoot -Parent

# Собираем исходный код C#
$files = Get-ChildItem -Path $root -Recurse -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|External|Docs|Assets|Tests)\\' }

# Собираем файлы разметки Avalonia AXAML
$axamlFiles = Get-ChildItem -Path $root -Recurse -Include *.axaml |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|External|Docs|Assets|Tests)\\' }

# Кэшируем контент C# файлов
$allContent = @{}
foreach ($file in $files) {
    $allContent[$file.FullName] = Get-Content $file -Raw
}

# Кэшируем контент AXAML файлов
$allAxamlContent = @{}
foreach ($axamlFile in $axamlFiles) {
    $allAxamlContent[$axamlFile.FullName] = Get-Content $axamlFile -Raw
}

# Извлекаем все объявления событий из C#-файлов
$events = @()
foreach ($file in $files) {
    $lines = Get-Content $file
    $relPath = $file.FullName.Substring($root.Length + 1)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '\bevent\s+\S+\s+(\w+)\s*;') {
            $events += [PSCustomObject]@{
                Name = $Matches[1]
                File = $relPath
                Line = $i + 1
            }
        }
    }
}

$events = $events | Sort-Object Name -Unique

Write-Host "`n=== Проверка $($events.Count) событий (включая привязки AXAML) ===`n"

$deadCount = 0
foreach ($evt in $events) {
    $subCount = 0
    
    # 1. Проверяем подписки через оператор += в C#
    foreach ($file in $files) {
        $content = $allContent[$file.FullName]
        $subCount += ([regex]::Matches($content, "\b$($evt.Name)\s*\+=")).Count
        $subCount += ([regex]::Matches($content, "\b$($evt.Name)\s*\+=\s*h\b")).Count
    }

    # 2. Проверяем декларативные подписки в разметке AXAML (формат Event="Handler")
    foreach ($axamlFile in $axamlFiles) {
        $content = $allAxamlContent[$axamlFile.FullName]
        $subCount += ([regex]::Matches($content, "\b$($evt.Name)\s*=\s*`"[^`"]+`"|\b$($evt.Name)\s*=\s*'[^']+'")).Count
    }

    if ($subCount -eq 0) {
        $deadCount++
        Write-Host "  МЁРТВОЕ " -ForegroundColor Red -NoNewline
        Write-Host "$($evt.File):$($evt.Line)" -ForegroundColor Cyan -NoNewline
        Write-Host "  $($evt.Name)" -ForegroundColor Yellow
    }
}

if ($deadCount -eq 0) {
    Write-Host "  Все события имеют активных подписчиков!" -ForegroundColor Green
} else {
    Write-Host "`n  Обнаружено мертвых событий: $deadCount" -ForegroundColor Red
}
Write-Host ""

exit $deadCount