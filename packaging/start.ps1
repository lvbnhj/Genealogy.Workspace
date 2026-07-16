$ErrorActionPreference = "Stop"
$RootDir = $PSScriptRoot
$EnvFile = Join-Path $RootDir ".env"

if (-not (Test-Path $EnvFile)) {
    throw "Missing .env. Copy .env.example to .env and set POSTGRES_PASSWORD first."
}

Get-Content $EnvFile | ForEach-Object {
    if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
        [Environment]::SetEnvironmentVariable($matches[1].Trim(), $matches[2].Trim(), "Process")
    }
}

docker compose --project-directory $RootDir up -d --wait
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed." }

$env:GENEALOGY_DB_HOST = "127.0.0.1"
$env:GENEALOGY_DB_PORT = if ($env:PGPORT) { $env:PGPORT } else { "5432" }
$env:GENEALOGY_DB_DATABASE = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "genealogy_workspace" }
$env:GENEALOGY_DB_USERNAME = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { "genealogy" }
$env:GENEALOGY_DB_PASSWORD = $env:POSTGRES_PASSWORD

dotnet (Join-Path $RootDir "app\migrator\Genealogy.Workspace.Migrator.dll") migrate
if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }

Write-Host "Genealogy workspace is up."

