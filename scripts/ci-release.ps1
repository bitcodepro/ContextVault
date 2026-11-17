param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version, [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit)
$ErrorActionPreference = 'Stop'
$tag = "v$Version"
$repo = $env:GITHUB_REPOSITORY
if (-not $repo) { throw 'GITHUB_REPOSITORY is required' }
$release = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release'
$manifest = Get-Content (Join-Path $release 'release.json') -Raw | ConvertFrom-Json
$hash = (Get-FileHash (Join-Path $release 'contextvault-win-x64.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.version -cne $Version -or $manifest.commit.Trim() -cne $Commit -or $manifest.sha256 -cne $hash) { throw 'Release artifact identity mismatch' }
$existing = gh release view $tag --repo $repo --json targetCommitish,isDraft 2>$null
if ($LASTEXITCODE -eq 0) {
    $info = $existing | ConvertFrom-Json
    if ($info.targetCommitish -cne $Commit -or $info.isDraft) { throw 'Existing release has a different target or is an unfinished draft; inspect manually' }
    Write-Host "Release $tag already exists; never overwrite published assets."
    exit 0
}
gh release create $tag (Join-Path $release 'contextvault-win-x64.zip') (Join-Path $release 'SHA256SUMS.txt') (Join-Path $release 'release.json') --repo $repo --target $Commit --title "ContextVault $tag" --generate-notes --draft
if ($LASTEXITCODE -ne 0) { throw 'Could not create release draft' }
gh release edit $tag --repo $repo --draft=false
if ($LASTEXITCODE -ne 0) { throw 'Could not publish release draft' }
