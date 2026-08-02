param (
    [string]$LocalizationDir = "$PSScriptRoot\..\Assets\Localization"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $LocalizationDir)) {
    Write-Error "Localization directory not found: $LocalizationDir"
    exit 1
}

$jsonFiles = Get-ChildItem -Path $LocalizationDir -Filter "*.json"

foreach ($file in $jsonFiles) {
    Write-Host "Processing: $($file.Name)..." -ForegroundColor Cyan

    $rawContent = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $jsonObject = $rawContent | ConvertFrom-Json

    # Universal property extraction compatible with Windows PowerShell 5.1
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

        # Extract prefix before '_' for block grouping
        $prefix = if ($key.Contains("_")) { $key.Split("_")[0] } else { "Other" }

        # Insert blank line when prefix changes
        if ($previousPrefix -ne "" -and $prefix -ne $previousPrefix) {
            [void]$sb.AppendLine()
        }
        $previousPrefix = $prefix

        # Handle value formatting
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

    # Save formatted JSON with UTF-8 encoding without BOM
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($file.FullName, $sb.ToString(), $utf8NoBom)

    Write-Host "Successfully sorted and grouped: $($file.Name)" -ForegroundColor Green
}