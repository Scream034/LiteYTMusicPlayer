<#
.SYNOPSIS
    Инструмент миграции реактивных свойств с ReactiveUI.Fody на ReactiveUI.SourceGenerators.

.DESCRIPTION
    - До модификации добавляет ключевое слово 'partial' ко всем свойствам с атрибутом [Reactive].
    - Добавляет 'partial' строго к объявляющему классу/структуре/записи, используя анализ глубин вложенности фигурных скобок.
    - Корректно обрабатывает комбинированные атрибуты ([JsonIgnore, Reactive], [Reactive, JsonIgnore]).
    - Удаляет using-директивы 'ReactiveUI.Fody.*'.
    - Идемпотентен (безопасен для многократного запуска).

.PARAMETER Root
    Корневая директория проекта. По умолчанию: родительская папка Tools/.

.PARAMETER DryRun
    Предпросмотр изменений без изменения файлов.

.EXAMPLE
    .\Tools\migrate-fody-to-sourcegen.ps1 -DryRun
    .\Tools\migrate-fody-to-sourcegen.ps1
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Мигратор Fody -> SourceGenerators           " -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host ""

if ($DryRun) {
    Write-Host "[РЕЖИМ] Сухой запуск (Dry Run) — файлы не изменяются" -ForegroundColor Yellow
    Write-Host ""
}

# --- Сбор файлов .cs (с исключением папок bin и obj) ---
$files = Get-ChildItem -Path $Root -Filter "*.cs" -Recurse | Where-Object {
    $segments = $_.FullName.Split([System.IO.Path]::DirectorySeparatorChar)
    -not ($segments -contains 'bin' -or $segments -contains 'obj')
} | Where-Object {
    $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    $content -and ($content -match 'ReactiveUI\.Fody' -or $content -match '\[.*\bReactive\b.*\]\s*public\s')
}

$totalPropertyChanges = 0
$totalClassChanges    = 0
$totalUsingRemovals   = 0
$filesModified        = 0

foreach ($file in $files) {
    $lines   = [System.IO.File]::ReadAllLines($file.FullName)
    $changed = $false

    # --- Построение карты глубины вложенности фигурных скобок ---
    $lineDepths = [int[]]::new($lines.Count)
    $depth = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $lineDepths[$i] = $depth
        $open  = ([regex]::Matches($lines[$i], '\{')).Count
        $close = ([regex]::Matches($lines[$i], '\}')).Count
        $depth += ($open - $close)
    }

    $classLineIndices    = [System.Collections.Generic.List[int]]::new()
    $reactiveLineIndices = [System.Collections.Generic.List[int]]::new()
    $fodyUsingIndices    = [System.Collections.Generic.List[int]]::new()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '\[.*\bReactive\b.*\]\s*public\s') {
            $reactiveLineIndices.Add($i)
        }

        if ($line -match '^\s*(public|internal|private|protected)\s+.*\b(class|record|struct)\s+\w+') {
            $classLineIndices.Add($i)
        }

        if ($line -match '^\s*using\s+ReactiveUI\.Fody') {
            $fodyUsingIndices.Add($i)
        }
    }

    # --- Шаг 1: Удаление using-директив Fody ---
    foreach ($i in $fodyUsingIndices) {
        $lines[$i] = $null
        $totalUsingRemovals++
        $changed = $true
    }

    # --- Шаг 2: Добавление 'partial' к реактивным свойствам ---
    foreach ($i in $reactiveLineIndices) {
        $line = $lines[$i]
        if ($null -ne $line -and $line -match '\[.*\bReactive\b.*\]\s*public\s+(?!partial\b)') {
            $lines[$i] = $line -replace '(\[.*\bReactive\b.*\]\s*public\s+)(?!partial\b)', '$1partial '
            $totalPropertyChanges++
            $changed = $true
        }
    }

    # --- Шаг 3: Добавление 'partial' к содержащему классу ---
    $patchedClasses = [System.Collections.Generic.HashSet[int]]::new()

    foreach ($rLine in $reactiveLineIndices) {
        $reactiveDepth    = $lineDepths[$rLine]
        $targetClassDepth = $reactiveDepth - 1
        $containingIdx    = -1

        foreach ($cLine in $classLineIndices) {
            if ($cLine -lt $rLine -and $lineDepths[$cLine] -eq $targetClassDepth) {
                $containingIdx = $cLine
            }
        }

        if ($containingIdx -ge 0 -and -not $patchedClasses.Contains($containingIdx)) {
            $classDecl = $lines[$containingIdx]
            if ($null -ne $classDecl -and $classDecl -notmatch '\bpartial\b') {
                $lines[$containingIdx] = $classDecl -replace '\b(class|record|struct)\b', 'partial $1'
                $patchedClasses.Add($containingIdx) | Out-Null
                $totalClassChanges++
                $changed = $true
            }
        }
    }

    # --- Запись результатов ---
    if ($changed) {
        $outputLines = $lines | Where-Object { $null -ne $_ }
        $rel = $file.FullName.Substring($Root.Length).TrimStart([char]'\', [char]'/')

        if ($DryRun) {
            Write-Host "  [ПРЕВС] $rel" -ForegroundColor Yellow
        }
        else {
            $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
            [System.IO.File]::WriteAllLines($file.FullName, $outputLines, $utf8NoBom)
            Write-Host "  [ОК]    $rel" -ForegroundColor Green
        }
        $filesModified++
    }
}

# --- Сводка выполнения ---
Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Файлов изменено : $filesModified"          -ForegroundColor White
Write-Host "  Свойств патронировано: $totalPropertyChanges (+partial)" -ForegroundColor White
Write-Host "  Классов патронировано: $totalClassChanges    (+partial)" -ForegroundColor White
Write-Host "  Удалено using   : $totalUsingRemovals  (Fody)" -ForegroundColor White
Write-Host "===============================================" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "  Сухой запуск завершен — файлы не изменены." -ForegroundColor Yellow
}
else {
    Write-Host "  Миграция завершена. Выполните dotnet build для проверки." -ForegroundColor Green
}
Write-Host ""