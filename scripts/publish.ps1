# Builds a self-contained win-x64 release and a ZIP without any data, tokens or databases.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\KnowledgeMemory-win-x64'
$zip = Join-Path $root 'artifacts\KnowledgeMemory-win-x64.zip'

Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet test KnowledgeMemory.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish src\KnowledgeMemory.Host\KnowledgeMemory.Host.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

    Copy-Item README.md $out
    New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
    Copy-Item docs\*.md (Join-Path $out 'docs')

    # Safety: the package must not contain databases, tokens or local configs.
    $bad = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -match '\.db($|-)|api-token|^appsettings\.json$|instance\.lock' }
    if ($bad) { throw "Package contains forbidden files: $($bad.FullName -join ', ')" }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "Published: $out"
    Write-Host "ZIP:       $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally { Pop-Location }
