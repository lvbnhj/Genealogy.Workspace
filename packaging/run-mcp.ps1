$ErrorActionPreference = "Stop"
$RootDir = $PSScriptRoot

if (Test-Path (Join-Path $RootDir ".env")) {
    Get-Content (Join-Path $RootDir ".env") | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
            [Environment]::SetEnvironmentVariable($matches[1].Trim(), $matches[2].Trim(), "Process")
        }
    }
}

if (-not $env:GENEALOGY_DB_HOST) { $env:GENEALOGY_DB_HOST = "127.0.0.1" }
if (-not $env:GENEALOGY_DB_PORT) { $env:GENEALOGY_DB_PORT = if ($env:PGPORT) { $env:PGPORT } else { "5432" } }
if (-not $env:GENEALOGY_DB_DATABASE) { $env:GENEALOGY_DB_DATABASE = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "genealogy_workspace" } }
if (-not $env:GENEALOGY_DB_USERNAME) { $env:GENEALOGY_DB_USERNAME = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { "genealogy" } }
if (-not $env:GENEALOGY_DB_PASSWORD) { $env:GENEALOGY_DB_PASSWORD = $env:POSTGRES_PASSWORD }

if (-not $env:GENEALOGY_DB_PASSWORD) {
    throw "Database password is missing. Copy .env.example to .env and set POSTGRES_PASSWORD."
}

dotnet (Join-Path $RootDir "app\mcp\Genealogy.Workspace.McpServer.dll") @args
exit $LASTEXITCODE

