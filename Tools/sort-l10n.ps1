<#
.SYNOPSIS
    Скрипт для сортировки и группировки ключей в JSON-файлах локализации.

.PARAMETER LocalizationDir
    Путь к директории с файлами локализации.

.EXAMPLE
    .\Tools\sort-l10n.ps1
#>

param (
    [string]$LocalizationDir = "$PSScriptRoot\..\Assets\Localization"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $LocalizationDir)) {
    Write-Error "Директория локализации не найдена: $LocalizationDir"
    exit 1
}

$jsonFiles = Get-ChildItem -Path $LocalizationDir -Filter "*.json"

foreach ($file in $jsonFiles) {
    Write-Host "Обработка файла: $($file.Name)..." -ForegroundColor Cyan

    $rawContent = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $jsonObject = $rawContent | ConvertFrom-Json

    # Универсальное извлечение свойств для совместимости с Windows PowerShell 5.1
    $sortedProperties = $jsonObject.psobject.properties | Sort-Object Name

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("{")

    $previousPrefix = ""
    $count = $sortedProperties.Count
    $index = 0

    foreach ($prop in $sortedProperties) {
        $index++
        $key = $prop.Name
        $value = $prop.Value

        # Извлечение префикса до символа '_' для логической группировки блоков
        $prefix = if ($key.Contains("_")) { $key.Split("_")[0] } else { "Other" }

        # Вставка пустой строки при смене префикса
        if ($previousPrefix -ne "" -and $prefix -ne $previousPrefix) {
            [void]$sb.AppendLine()
        }
        $previousPrefix = $prefix

        # Форматирование значений
        if ($null -eq $value) {
            $formattedValue = "null"
        } elseif ($value -is [bool]) {
            $formattedValue = $value.ToString().ToLower()
        } elseif ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) {
            $formattedValue = $value.ToString()
        } else {
            $escapedValue = $value.ToString().Replace("\", "\\").Replace('"', '\"').Replace("`n", "\n").Replace("`r", "\r").Replace("`t", "\t")
            $formattedValue = """$escapedValue"""
        }

        $comma = if ($index -lt $count) { "," } else { "" }
        [void]$sb.AppendLine("  ""$key"": $formattedValue$comma")
    }

    [void]$sb.AppendLine("}")

    # Сохранение отформатированного JSON в UTF-8 без BOM
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($file.FullName, $sb.ToString(), $utf8NoBom)

    Write-Host "Успешно отсортировано и сгруппировано: $($file.Name)" -ForegroundColor Green
}