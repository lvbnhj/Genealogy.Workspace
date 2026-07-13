using System.Text.RegularExpressions;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;

namespace Genealogy.Workspace.Data;

/// <summary>
/// Applies ordered, immutable SQL migrations to the PostgreSQL workspace.
/// Migrations are plain <c>.sql</c> files ordered by their numeric filename
/// prefix (e.g. <c>0001_create_schemas.sql</c>) and journaled to
/// <c>public.schema_version</c>. Each script runs in its own transaction.
/// </summary>
public sealed partial class MigrationEngine
{
    /// <summary>Schema that holds the migration journal table.</summary>
    public const string JournalSchema = "public";

    /// <summary>Migration journal table name.</summary>
    public const string JournalTable = "schema_version";

    private readonly string _connectionString;
    private readonly string _migrationsDirectory;
    private readonly IUpgradeLog? _log;

    /// <param name="connectionString">Npgsql connection string.</param>
    /// <param name="migrationsDirectory">
    /// Directory containing numbered <c>.sql</c> migrations. When null, resolved
    /// relative to the entry assembly (see <see cref="ResolveDefaultDirectory"/>).
    /// </param>
    /// <param name="log">Optional DbUp log sink.</param>
    public MigrationEngine(string connectionString, string? migrationsDirectory = null, IUpgradeLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _migrationsDirectory = migrationsDirectory ?? ResolveDefaultDirectory();
        _log = log;

        if (!Directory.Exists(_migrationsDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Migrations directory not found: {_migrationsDirectory}");
        }
    }

    /// <summary>The migrations directory this engine reads from.</summary>
    public string MigrationsDirectory => _migrationsDirectory;

    /// <summary>
    /// Applies every pending migration in order and returns the names of the
    /// scripts that were applied. Throws if any script fails.
    /// </summary>
    public IReadOnlyList<string> Migrate()
    {
        var upgrader = BuildUpgrader();
        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migration failed on script '{result.ErrorScript?.Name ?? "<unknown>"}': {result.Error?.Message}",
                result.Error);
        }

        return result.Scripts.Select(s => s.Name).ToList();
    }

    /// <summary>
    /// Returns the applied and pending migration scripts, each ordered by numeric
    /// filename prefix.
    /// </summary>
    public MigrationStatus Status()
    {
        var upgrader = BuildUpgrader();

        var applied = upgrader.GetExecutedScripts();
        var pending = upgrader.GetScriptsToExecute().Select(s => s.Name);

        return new MigrationStatus(
            Order(applied).ToList(),
            Order(pending).ToList());
    }

    private UpgradeEngine BuildUpgrader()
    {
        var builder = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsFromFileSystem(_migrationsDirectory)
            .JournalToPostgresqlTable(JournalSchema, JournalTable)
            .WithTransactionPerScript();

        if (_log is not null)
        {
            builder = builder.LogTo(_log);
        }

        return builder.Build();
    }

    /// <summary>
    /// Orders script names by their leading numeric prefix, then by full name.
    /// Names without a numeric prefix sort last, preserving determinism.
    /// </summary>
    private static IEnumerable<string> Order(IEnumerable<string> names) =>
        names
            .OrderBy(NumericPrefix)
            .ThenBy(n => n, StringComparer.Ordinal);

    private static long NumericPrefix(string name)
    {
        var leaf = Path.GetFileName(name);
        var match = PrefixRegex().Match(leaf);
        return match.Success && long.TryParse(match.Value, out var value)
            ? value
            : long.MaxValue;
    }

    /// <summary>
    /// Resolves the default migrations directory (<c>database/migrations</c>) by
    /// walking up from the entry assembly location. Works both when the
    /// repository root IS the workspace (standalone: <c>&lt;root&gt;/database/migrations</c>,
    /// regardless of the folder's name) and when the workspace is nested under a
    /// parent (monorepo: <c>&lt;parent&gt;/Genealogy.Workspace/database/migrations</c>).
    /// </summary>
    private static string ResolveDefaultDirectory()
    {
        var nested = Path.Combine("Genealogy.Workspace", "database", "migrations");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Standalone / running from within the workspace: this directory
            // directly contains database/migrations (folder name is irrelevant).
            var here = Path.Combine(dir.FullName, "database", "migrations");
            if (Directory.Exists(here))
            {
                return here;
            }

            // Monorepo: a parent directory contains Genealogy.Workspace/database/migrations.
            var nestedCandidate = Path.Combine(dir.FullName, nested);
            if (Directory.Exists(nestedCandidate))
            {
                return nestedCandidate;
            }

            dir = dir.Parent;
        }

        // Fall back to a path next to the entry assembly so the error message
        // from the constructor is meaningful.
        return Path.Combine(AppContext.BaseDirectory, "database", "migrations");
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex PrefixRegex();
}
