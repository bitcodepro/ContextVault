param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/ci-package'
$release = Join-Path $root 'artifacts/release'
if ((Test-Path $out) -or (Test-Path $release)) { throw 'CI package output must be fresh' }
New-Item -ItemType Directory -Path $out,$release | Out-Null
Push-Location $root
try {
    dotnet publish src/KnowledgeMemory.Host/KnowledgeMemory.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none "-p:Version=$Version" -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md $out
    Copy-Item docs (Join-Path $out 'docs') -Recurse
    $bad = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -match '\.db($|-)|api-token|^appsettings\.json$|instance\.lock|^\.env' }
    if ($bad) { throw 'Package contains forbidden runtime data or configuration' }
    if (-not (Test-Path (Join-Path $out 'KnowledgeMemory.exe'))) { throw 'Missing Windows executable' }
    if (-not (Test-Path (Join-Path $out 'appsettings.example.json'))) { throw 'Missing example configuration' }
    $zip = Join-Path $release 'contextvault-win-x64.zip'
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  contextvault-win-x64.zip" | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
    @{version=$Version; commit=(git rev-parse HEAD); file='contextvault-win-x64.zip'; sha256=$hash} | ConvertTo-Json | Set-Content (Join-Path $release 'release.json') -Encoding utf8
}
finally { Pop-Location }
