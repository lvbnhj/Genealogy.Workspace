using System.Diagnostics;
using System.Text;
using Genealogy.Workspace.Data;
using Npgsql;
using Xunit;

namespace Genealogy.Workspace.IntegrationTests;

/// <summary>
/// Ensures the local PostgreSQL workspace container is up and reachable before
/// any database-lifecycle test runs, then hands out connection options built
/// from <c>Genealogy.Workspace/.env</c> (not from GENEALOGY_DB_* process
/// environment variables, which this fixture does not require).
///
/// This fixture never skips: if <c>.env</c> is missing it runs
/// <c>scripts/up.sh</c> to create it and start the container, and it always
/// runs <c>docker compose up -d --wait</c> so the container is guaranteed
/// running and healthy before tests proceed.
/// </summary>
public sealed class WorkspaceEnvironmentFixture : IAsyncLifetime
{
    /// <summary>Maintenance database every PostgreSQL server ships with.</summary>
    private const string MaintenanceDatabase = "postgres";

    private IReadOnlyDictionary<string, string> _env = new Dictionary<string, string>();

    public string WorkspaceDirectory { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        WorkspaceDirectory = ResolveWorkspaceDirectory();

        var envFile = Path.Combine(WorkspaceDirectory, ".env");
        if (!File.Exists(envFile))
        {
            // scripts/up.sh's own last step ("Applying migrations...") runs the
            // Migrator via `dotnet run ... -- migrate`, which reads
            // WorkspaceDbOptions.FromEnvironment() (GENEALOGY_DB_*). up.sh only
            // ever writes .env with POSTGRES_* keys (consumed by docker compose)
            // and never exports GENEALOGY_DB_* for that subprocess, so that step
            // reliably fails with "Password is required (GENEALOGY_DB_PASSWORD)"
            // in any environment that hasn't separately exported GENEALOGY_DB_*.
            // We do not modify scripts/up.sh (hard constraint), and this
            // fixture doesn't need that step anyway — it runs MigrationEngine
            // itself against each ephemeral test database. So: run up.sh
            // tolerating a non-zero exit, then verify the two preconditions we
            // actually depend on (.env written, container reachable) ourselves
            // before continuing. A genuine bootstrap failure (e.g. Docker not
            // installed) still fails loudly because those preconditions won't
            // be met and/or the follow-up `docker compose up -d --wait` below
            // will throw.
            var bootstrap = await RunProcessTolerantAsync(
                fileName: "bash",
                arguments: "scripts/up.sh",
                workingDirectory: WorkspaceDirectory);

            if (!File.Exists(envFile))
            {
                throw new InvalidOperationException(
                    "scripts/up.sh did not produce Genealogy.Workspace/.env." +
                    $"{Environment.NewLine}exit code: {bootstrap.ExitCode}" +
                    $"{Environment.NewLine}stdout:{Environment.NewLine}{bootstrap.StandardOutput}" +
                    $"{Environment.NewLine}stderr:{Environment.NewLine}{bootstrap.StandardError}");
            }
        }

        // Idempotent regardless of whether up.sh just ran (and regardless of
        // whether its later migrate substep failed): guarantees the container
        // exists, is started, and is healthy before we proceed. This call is
        // expected to succeed; a failure here is a real environment problem.
        await RunProcessAsync(
            fileName: "docker",
            arguments: $"compose --project-directory \"{WorkspaceDirectory}\" up -d --wait",
            workingDirectory: WorkspaceDirectory,
            description: "docker compose up -d --wait");

        _env = DotEnvFile.Parse(envFile);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Builds workspace DB options for the given database name, using the host
    /// port and superuser credentials parsed from <c>.env</c>.
    /// </summary>
    public WorkspaceDbOptions BuildOptionsForDatabase(string databaseName)
    {
        return new WorkspaceDbOptions
        {
            Host = "127.0.0.1",
            Port = int.Parse(GetRequired("PGPORT", "5432")),
            Database = databaseName,
            Username = GetRequired("POSTGRES_USER"),
            Password = GetRequired("POSTGRES_PASSWORD"),
        };
    }

    private string AdminConnectionString =>
        NpgsqlConnectionFactory.BuildConnectionString(BuildOptionsForDatabase(MaintenanceDatabase));

    /// <summary>
    /// Creates a fresh, empty database owned by the workspace superuser.
    /// The caller is responsible for dropping it (see <see cref="DropDatabaseAsync"/>).
    /// </summary>
    public async Task CreateDatabaseAsync(string databaseName)
    {
        EnsureSafeIdentifier(databaseName);

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Drops the database, forcibly disconnecting any remaining sessions
    /// (PostgreSQL 13+ <c>WITH (FORCE)</c>). Safe to call even if the database
    /// was never created or was already dropped.
    /// </summary>
    public async Task DropDatabaseAsync(string databaseName)
    {
        EnsureSafeIdentifier(databaseName);

        // Ensure pooled connections to the target database made by this
        // process are returned before we force-drop it.
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);";
        await command.ExecuteNonQueryAsync();
    }

    private string GetRequired(string key, string? fallback = null)
    {
        if (_env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (fallback is not null)
        {
            return fallback;
        }

        throw new InvalidOperationException(
            $"Genealogy.Workspace/.env is missing required key '{key}'.");
    }

    private static void EnsureSafeIdentifier(string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"Refusing to use '{name}' as a database identifier: only letters, digits and underscores are allowed.",
                nameof(name));
        }
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the workspace root,
    /// identified by <c>docker-compose.yml</c> and <c>scripts/up.sh</c> living
    /// next to each other (folder name irrelevant). Works both standalone (the
    /// repo root itself) and nested (a parent's <c>Genealogy.Workspace/</c>
    /// subfolder), mirroring how <c>MigrationEngine</c> locates its migrations.
    /// </summary>
    private static string ResolveWorkspaceDirectory()
    {
        static bool IsWorkspace(string path) =>
            Directory.Exists(path) &&
            File.Exists(Path.Combine(path, "docker-compose.yml")) &&
            File.Exists(Path.Combine(path, "scripts", "up.sh"));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (IsWorkspace(dir.FullName))
            {
                return dir.FullName;
            }

            var nested = Path.Combine(dir.FullName, "Genealogy.Workspace");
            if (IsWorkspace(nested))
            {
                return nested;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the workspace root (docker-compose.yml + scripts/up.sh) " +
            $"above {AppContext.BaseDirectory}.");
    }

    private static async Task RunProcessAsync(
        string fileName, string arguments, string workingDirectory, string description)
    {
        var result = await RunProcessTolerantAsync(fileName, arguments, workingDirectory);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"[{description}] failed with exit code {result.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{result.StandardError}");
        }
    }

    /// <summary>
    /// Runs a process to completion and returns its exit code and captured
    /// output without throwing on a non-zero exit code (throws only on a
    /// hard timeout, since that indicates the process is stuck rather than
    /// having failed cleanly).
    /// </summary>
    private static async Task<ProcessResult> RunProcessTolerantAsync(
        string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // docker pulls / container startup can be slow on a cold cache.
        var timeout = TimeSpan.FromMinutes(3);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for {fileName} {arguments}.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
