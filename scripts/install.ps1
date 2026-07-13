#!/usr/bin/env pwsh
# install.ps1 - one-command local install for a fresh clone (Windows / PowerShell).
#
# Assumes Docker Desktop (with the `docker compose` plugin) and the .NET SDK
# are already installed. Does NOT install either of those.
#
# What it does:
#   1. Creates .env (from .env.example, with a generated password) if it does
#      not exist yet, starts PostgreSQL via `docker compose up -d --wait`,
#      and applies all migrations.
#   2. Builds and publishes Genealogy.Workspace.McpServer to
#      publish/GenealogyMcp/, generating a run.ps1 wrapper that reads DB
#      credentials from .env at launch time (nothing machine-specific is
#      baked in, so it never goes stale).
#   3. Registers (or updates) a "genealogy-workspace" entry in this repo's
#      .mcp.json, pointing at THIS clone's run.ps1 path. Any other entries in
#      that file are left untouched.
#
# Safe to re-run: every step is idempotent.

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$WorkspaceDir = Split-Path -Parent $ScriptDir
$RepoRoot = $WorkspaceDir
$McpJsonPath = Join-Path $RepoRoot ".mcp.json"
$PublishDir = Join-Path $RepoRoot "publish\GenealogyMcp"
$RunPs1Path = Join-Path $PublishDir "run.ps1"

Write-Host "== Genealogy.Workspace install =="
Write-Host "Repo root: $RepoRoot"
Write-Host ""

# ---------------------------------------------------------------------------
# Step 0: prerequisite checks (fail fast with a clear message).
# ---------------------------------------------------------------------------
$missing = @()
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { $missing += "docker" }
else {
    docker compose version *> $null
    if ($LASTEXITCODE -ne 0) { $missing += "docker compose plugin" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $missing += "dotnet SDK" }

if ($missing.Count -gt 0) {
    Write-Error "Missing prerequisite(s): $($missing -join ', '). Install them first, then re-run this script."
    exit 1
}
Write-Host "[0/3] Prerequisites OK (docker, docker compose, dotnet)."

# ---------------------------------------------------------------------------
# Step 1: database + migrations.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "[1/3] Starting PostgreSQL and applying migrations..."

$EnvFile = Join-Path $WorkspaceDir ".env"
$EnvExample = Join-Path $WorkspaceDir ".env.example"

if (-not (Test-Path $EnvFile)) {
    Write-Host "No .env found - creating one from .env.example with a generated password."
    $bytes = New-Object byte[] 16
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $password = -join ($bytes | ForEach-Object { $_.ToString("x2") })
    (Get-Content $EnvExample) -replace '^POSTGRES_PASSWORD=.*', "POSTGRES_PASSWORD=$password" |
        Set-Content -Path $EnvFile -Encoding utf8NoBOM
    Write-Host "Wrote $EnvFile (password generated; not committed to git)."
}

# Parse .env (simple KEY=VALUE lines) into a hashtable.
$envVars = @{}
Get-Content $EnvFile | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
        $envVars[$Matches[1]] = $Matches[2]
    }
}

Push-Location $WorkspaceDir
try {
    docker compose up -d --wait
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed." }

    Write-Host "Applying migrations..."
    $env:GENEALOGY_DB_HOST = "127.0.0.1"
    $env:GENEALOGY_DB_PORT = if ($envVars.ContainsKey("PGPORT")) { $envVars["PGPORT"] } else { "5432" }
    $env:GENEALOGY_DB_DATABASE = if ($envVars.ContainsKey("POSTGRES_DB")) { $envVars["POSTGRES_DB"] } else { "genealogy_workspace" }
    $env:GENEALOGY_DB_USERNAME = if ($envVars.ContainsKey("POSTGRES_USER")) { $envVars["POSTGRES_USER"] } else { "genealogy" }
    $env:GENEALOGY_DB_PASSWORD = $envVars["POSTGRES_PASSWORD"]

    dotnet run --project (Join-Path $WorkspaceDir "src\Genealogy.Workspace.Migrator") -- migrate
    if ($LASTEXITCODE -ne 0) { throw "Migration failed." }
}
finally {
    Pop-Location
}

Write-Host "Genealogy workspace is up."

# ---------------------------------------------------------------------------
# Step 2: build + publish the MCP server.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "[2/3] Publishing the MCP server..."

$CsProj = Join-Path $WorkspaceDir "src\Genealogy.Workspace.McpServer\Genealogy.Workspace.McpServer.csproj"
dotnet publish $CsProj -c Release --no-self-contained --output $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# run.ps1 reads DB credentials from .env at LAUNCH time (not baked in at
# publish time), bridging POSTGRES_* -> GENEALOGY_DB_*, mirroring the up.sh /
# publish_mcp.sh convention on macOS/Linux. An already-exported
# GENEALOGY_DB_* value in the parent process still wins over .env.
$runPs1Content = @"
# Runs the published Genealogy.Workspace.McpServer over stdio.
# Reads DB credentials from Genealogy.Workspace\.env at launch time.
`$envFile = "$EnvFile"
`$vars = @{}
if (Test-Path `$envFile) {
    Get-Content `$envFile | ForEach-Object {
        if (`$_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*`$') {
            `$vars[`$Matches[1]] = `$Matches[2]
        }
    }
}

if (-not `$env:GENEALOGY_DB_HOST) { `$env:GENEALOGY_DB_HOST = "127.0.0.1" }
if (-not `$env:GENEALOGY_DB_PORT) { `$env:GENEALOGY_DB_PORT = if (`$vars.ContainsKey("PGPORT")) { `$vars["PGPORT"] } else { "5432" } }
if (-not `$env:GENEALOGY_DB_DATABASE) { `$env:GENEALOGY_DB_DATABASE = if (`$vars.ContainsKey("POSTGRES_DB")) { `$vars["POSTGRES_DB"] } else { "genealogy_workspace" } }
if (-not `$env:GENEALOGY_DB_USERNAME) { `$env:GENEALOGY_DB_USERNAME = if (`$vars.ContainsKey("POSTGRES_USER")) { `$vars["POSTGRES_USER"] } else { "genealogy" } }
if (-not `$env:GENEALOGY_DB_PASSWORD) { `$env:GENEALOGY_DB_PASSWORD = `$vars["POSTGRES_PASSWORD"] }

if (-not `$env:GENEALOGY_DB_PASSWORD) {
    Write-Error "GENEALOGY_DB_PASSWORD is not set and `$envFile has no POSTGRES_PASSWORD. Run scripts\install.ps1 once to create .env, or set GENEALOGY_DB_PASSWORD yourself."
    exit 1
}

`$dll = Join-Path `$PSScriptRoot "Genealogy.Workspace.McpServer.dll"
& dotnet `$dll @args
"@
Set-Content -Path $RunPs1Path -Value $runPs1Content -Encoding utf8NoBOM

Write-Host "Done. Entry point: $RunPs1Path"

# ---------------------------------------------------------------------------
# Step 3: register the server in .mcp.json for this clone's absolute path.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "[3/3] Registering genealogy-workspace in $McpJsonPath..."

$config = [ordered]@{ mcpServers = [ordered]@{} }
if (Test-Path $McpJsonPath) {
    $raw = Get-Content $McpJsonPath -Raw
    if ($raw.Trim().Length -gt 0) {
        $parsed = $raw | ConvertFrom-Json -AsHashtable
        if ($null -ne $parsed) { $config = $parsed }
    }
}
if (-not $config.Contains("mcpServers")) { $config["mcpServers"] = @{} }

# MCP clients on Windows generally invoke "pwsh"/"powershell" with -File; keep
# the command portable by shelling through pwsh explicitly.
$config["mcpServers"]["genealogy-workspace"] = @{
    command = "pwsh"
    args    = @("-NoLogo", "-NoProfile", "-File", $RunPs1Path)
}

$config | ConvertTo-Json -Depth 10 | Set-Content -Path $McpJsonPath -Encoding utf8NoBOM
Write-Host "Wrote $McpJsonPath"

Write-Host ""
Write-Host "== Install complete =="
Write-Host "MCP server entry point: $RunPs1Path"
Write-Host "Restart/reconnect your MCP client to pick up the ""genealogy-workspace"" server."
Write-Host "Try it: dotnet run --project src\Genealogy.Workspace.Migrator -- quickstart   (imports a sample tree + stores an evidence screenshot)"
