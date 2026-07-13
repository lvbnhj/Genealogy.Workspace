<#
.SYNOPSIS
  Download the Genealogy.Workspace source and install it, WITHOUT needing git.

.DESCRIPTION
  Intended to be run straight from the network:

    irm https://raw.githubusercontent.com/lvbnhj/Genealogy.Workspace/main/scripts/bootstrap.ps1 | iex

  It downloads a source zip from GitHub, extracts it, and runs
  scripts/install.ps1 (which brings up PostgreSQL, applies migrations, publishes
  the MCP server, and registers it in .mcp.json). Only PowerShell 5+ is required
  for this step; install.ps1 then checks for Docker and the .NET SDK.

  Configuration (all optional, via environment variables):
    REPO         GitHub "owner/repo"          (default: lvbnhj/Genealogy.Workspace)
    REF          branch or tag to download    (default: main)
    TARGET_DIR   directory to extract into    (default: genealogy-workspace)
    GITHUB_TOKEN token for a PRIVATE repo     (default: none — anonymous, public repo)
    ZIPBALL_URL  override the download URL     (default: GitHub zipball for REPO@REF)
    RUN_INSTALL  set to 0 to only download/extract and skip install (default: 1)

  Examples:
    $env:REPO="alice/Genealogy.Workspace"; irm .../bootstrap.ps1 | iex
    $env:REPO="alice/Genealogy.Workspace"; $env:REF="v1.0"; .\bootstrap.ps1
#>
$ErrorActionPreference = "Stop"

function Get-EnvOr($name, $default) {
  $v = [Environment]::GetEnvironmentVariable($name)
  if ([string]::IsNullOrEmpty($v)) { return $default } else { return $v }
}

$Repo       = Get-EnvOr "REPO"       "lvbnhj/Genealogy.Workspace"
$Ref        = Get-EnvOr "REF"        "main"
$TargetDir  = Get-EnvOr "TARGET_DIR" "genealogy-workspace"
$RunInstall = Get-EnvOr "RUN_INSTALL" "1"
$Token      = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN")
$ZipUrl     = Get-EnvOr "ZIPBALL_URL" "https://api.github.com/repos/$Repo/zipball/$Ref"

Write-Host "== Genealogy.Workspace bootstrap =="

# --- refuse to clobber a non-empty target -----------------------------------
if ((Test-Path $TargetDir) -and (Get-ChildItem -Force $TargetDir | Select-Object -First 1)) {
  Write-Error "Target directory '$TargetDir' exists and is not empty. Set `$env:TARGET_DIR or remove it."
  exit 1
}
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
$AbsTarget = (Resolve-Path $TargetDir).Path

# --- download + extract -----------------------------------------------------
$tmpZip = Join-Path ([System.IO.Path]::GetTempPath()) ("gw-" + [System.IO.Path]::GetRandomFileName() + ".zip")
$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gw-" + [System.IO.Path]::GetRandomFileName())

$headers = @{ "User-Agent" = "genealogy-bootstrap" }
if (-not [string]::IsNullOrEmpty($Token)) { $headers["Authorization"] = "Bearer $Token" }

Write-Host "Downloading $Repo@$Ref ..."
try {
  Invoke-WebRequest -Uri $ZipUrl -Headers $headers -OutFile $tmpZip -UseBasicParsing
  New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
  Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
} catch {
  Write-Error "Download/extract failed for $ZipUrl. Check REPO/REF, that the repo is public (or GITHUB_TOKEN is set), and your network. $_"
  exit 1
}

# GitHub zips wrap everything in a single <owner>-<repo>-<sha>/ folder — unwrap it.
$inner = Get-ChildItem -Directory $tmpDir | Select-Object -First 1
if ($null -eq $inner) { Write-Error "Extracted archive was empty."; exit 1 }
Copy-Item -Path (Join-Path $inner.FullName "*") -Destination $AbsTarget -Recurse -Force
Remove-Item -Recurse -Force $tmpZip, $tmpDir

Write-Host "Extracted into $AbsTarget"

$installPs1 = Join-Path $AbsTarget "scripts\install.ps1"
if (-not (Test-Path $installPs1)) {
  Write-Error "Extracted tree has no scripts\install.ps1 — is REPO the Genealogy.Workspace repo?"
  exit 1
}

if ($RunInstall -ne "1") {
  Write-Host "RUN_INSTALL=0 — skipping install. Next: cd `"$AbsTarget`"; .\scripts\install.ps1"
  exit 0
}

Write-Host "Running install..."
Set-Location $AbsTarget
& $installPs1
exit $LASTEXITCODE
