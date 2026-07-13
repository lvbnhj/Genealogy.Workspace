using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Research;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Staging;
using Npgsql;
using NpgsqlTypes;

const int ExitOk = 0;
const int ExitError = 1;
const int ExitUsage = 2;

var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : null;

switch (command)
{
    case "migrate":
        return RunMigrate();
    case "status":
        return RunStatus();
    case "quickstart":
        return await RunQuickstartAsync(args);
    default:
        PrintUsage();
        return ExitUsage;
}

int RunMigrate()
{
    if (!TryBuildEngine(out var engine, out var configError))
    {
        Console.Error.WriteLine(configError);
        return ExitError;
    }

    try
    {
        var applied = engine.Migrate();
        if (applied.Count == 0)
        {
            Console.WriteLine("No pending migrations. Database is up to date.");
        }
        else
        {
            Console.WriteLine($"Applied {applied.Count} migration(s):");
            foreach (var name in applied)
            {
                Console.WriteLine($"  applied  {name}");
            }
        }

        return ExitOk;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Migration failed: {ex.Message}");
        return ExitError;
    }
}

int RunStatus()
{
    if (!TryBuildEngine(out var engine, out var configError))
    {
        Console.Error.WriteLine(configError);
        return ExitError;
    }

    try
    {
        var status = engine.Status();

        Console.WriteLine($"Applied ({status.Applied.Count}):");
        if (status.Applied.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            foreach (var name in status.Applied)
            {
                Console.WriteLine($"  applied  {name}");
            }
        }

        Console.WriteLine($"Pending ({status.Pending.Count}):");
        if (status.Pending.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            foreach (var name in status.Pending)
            {
                Console.WriteLine($"  pending  {name}");
            }
        }

        return ExitOk;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not read migration status: {ex.Message}");
        return ExitError;
    }
}

// Builds the migration engine from environment configuration. Returns false and
// a clear, single-message error (no stack trace) when configuration is invalid.
bool TryBuildEngine(out MigrationEngine engine, out string? error)
{
    engine = null!;
    error = null;

    try
    {
        var options = WorkspaceDbOptions.FromEnvironment();
        options.Validate();

        var factory = new NpgsqlConnectionFactory(options);
        engine = new MigrationEngine(factory.ConnectionString);
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

// Phase 7 quickstart: from a clean database, import a sample GEDCOM into a
// tree and store an evidence screenshot — the runnable proof of the Phase 7
// exit criterion ("a clean machine can install, import a GEDCOM and add an
// evidence screenshot from documented commands"). Every step prints a progress
// line; the flow is non-interactive and safe to re-run (resolve-or-create the
// tree, deterministic UUIDs mean re-apply adds no duplicate persons).
async Task<int> RunQuickstartAsync(string[] argv)
{
    string? gedArg = null;
    string treeName = "Quickstart";
    string? screenshotArg = null;

    for (var i = 1; i < argv.Length; i++)
    {
        var arg = argv[i];
        switch (arg)
        {
            case "--ged":
                if (++i >= argv.Length) { Console.Error.WriteLine("--ged requires a path."); return ExitUsage; }
                gedArg = argv[i];
                break;
            case "--tree":
                if (++i >= argv.Length) { Console.Error.WriteLine("--tree requires a name."); return ExitUsage; }
                treeName = argv[i];
                break;
            case "--screenshot":
                if (++i >= argv.Length) { Console.Error.WriteLine("--screenshot requires a path."); return ExitUsage; }
                screenshotArg = argv[i];
                break;
            default:
                Console.Error.WriteLine($"Unknown quickstart argument: {arg}");
                PrintUsage();
                return ExitUsage;
        }
    }

    try
    {
        var options = WorkspaceDbOptions.FromEnvironment();
        options.Validate();
        var factory = new NpgsqlConnectionFactory(options);

        var workspaceDir = ResolveWorkspaceDirectory();
        var gedPath = gedArg is not null
            ? Path.GetFullPath(gedArg)
            : Path.Combine(workspaceDir, "tools", "gedcom", "tests", "fixtures", "phase0_baseline.ged");
        var toolPath = Path.Combine(workspaceDir, "tools", "gedcom", "gedcom_tool.py");

        if (!File.Exists(gedPath))
        {
            Console.Error.WriteLine($"GEDCOM file not found: {gedPath}");
            return ExitError;
        }

        var screenshotBytes = screenshotArg is not null
            ? await File.ReadAllBytesAsync(Path.GetFullPath(screenshotArg))
            : SyntheticPng();
        var screenshotName = screenshotArg is not null ? Path.GetFileName(screenshotArg) : "quickstart.png";

        // Step 1: ensure migrations are applied (idempotent; up.sh usually did this).
        Console.WriteLine("[1/6] Ensuring database migrations are applied...");
        var engine = new MigrationEngine(factory.ConnectionString);
        var applied = engine.Migrate();
        Console.WriteLine(applied.Count == 0
            ? "      Database already up to date."
            : $"      Applied {applied.Count} migration(s).");

        // Step 2: resolve-or-create the target tree by name.
        Console.WriteLine($"[2/6] Resolving tree \"{treeName}\"...");
        var resolver = new TreeResolver(factory);
        var treeRepo = new TreeRepository(factory);
        var resolution = await resolver.ResolveTreeAsync(treeName);
        Guid treeId;
        if (resolution.IsResolved)
        {
            treeId = resolution.TreeId;
            Console.WriteLine($"      Found existing tree {treeId}.");
        }
        else
        {
            var tree = await treeRepo.CreateTreeAsync(treeName, description: "Phase 7 quickstart tree");
            treeId = tree.TreeId;
            Console.WriteLine($"      Created tree {treeId}.");
        }

        // Step 3: stage the GEDCOM (parse -> staging tables).
        Console.WriteLine($"[3/6] Staging GEDCOM {gedPath}...");
        var staging = new GedcomStagingService(factory);
        var stageResult = await staging.StageAsync(new GedcomStageRequest
        {
            GedcomFilePath = gedPath,
            TreeId = treeId,
            TreeName = treeName,
            GedcomToolPath = toolPath,
        });
        Console.WriteLine($"      Staged batch {stageResult.BatchId}. Row counts:");
        foreach (var kv in stageResult.RowCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"        {kv.Key}: {kv.Value}");
        }

        // Step 4: apply the staged batch (real apply, no deletes).
        Console.WriteLine($"[4/6] Applying batch {stageResult.BatchId}...");
        var apply = new GedcomApplyService(factory);
        var applyResult = await apply.ApplyAsync(stageResult.BatchId, deleteMissing: false, dryRun: false);
        Console.WriteLine($"      Status: {applyResult.Status}. {applyResult.Note}");
        foreach (var change in applyResult.Changes)
        {
            Console.WriteLine($"        {change.EntityType} {change.ChangeType}: {change.RowCount}");
        }

        // Step 5: add an evidence record + attach the screenshot.
        Console.WriteLine("[5/6] Adding evidence record and attachment...");
        var records = new SourceRecordRepository(factory);
        var record = await records.AddRecordAsync(new SourceRecordInput(
            TreeId: treeId,
            Title: "Quickstart evidence",
            RecordType: "other",
            RecordText: "Evidence screenshot added by the Phase 7 quickstart flow."));
        var attachments = new AttachmentRepository(factory, AttachmentOptions.FromEnvironment());
        var attachment = await attachments.AddAttachmentAsync(
            record.SourceRecordId,
            screenshotBytes,
            fileName: screenshotName,
            caption: "Quickstart evidence screenshot",
            attachmentType: "image",
            sequenceNo: 0,
            sourceUrl: null);
        Console.WriteLine($"      Source record {record.SourceRecordId} (status={record.Status}).");
        Console.WriteLine($"      Attachment hash={attachment.ContentHash} bytes={attachment.ByteLength} mime={attachment.MimeType} deduplicated={attachment.Deduplicated}");

        // Step 6: final summary — count persons imported for this tree.
        var personCount = await CountPersonsAsync(factory, treeId);
        Console.WriteLine("[6/6] Quickstart complete.");
        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Tree:              {treeName} ({treeId})");
        Console.WriteLine($"  Persons imported:  {personCount}");
        Console.WriteLine($"  Evidence record:   {record.SourceRecordId}");
        Console.WriteLine($"  Attachment hash:   {attachment.ContentHash}");
        return ExitOk;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Quickstart failed: {ex.Message}");
        return ExitError;
    }
}

// Counts genealogy.person rows for a tree — the persons-imported metric.
async Task<long> CountPersonsAsync(NpgsqlConnectionFactory factory, Guid treeId)
{
    await using var connection = factory.Create();
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        "SELECT count(*) FROM genealogy.person WHERE tree_id = @tree_id;", connection);
    command.Parameters.Add(new NpgsqlParameter("tree_id", NpgsqlDbType.Uuid) { Value = treeId });
    return (long)(await command.ExecuteScalarAsync() ?? 0L);
}

// Walks up from the running assembly's base directory to the workspace root,
// identified by docker-compose.yml + scripts/up.sh living next to each other
// (folder name irrelevant). Works both standalone (the repo root itself) and
// nested (monorepo: a parent's Genealogy.Workspace/ subdirectory), mirroring
// MigrationEngine's discovery.
string ResolveWorkspaceDirectory()
{
    bool IsWorkspace(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "docker-compose.yml")) &&
        File.Exists(Path.Combine(path, "scripts", "up.sh"));

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        // Standalone / running from within the workspace (folder name irrelevant).
        if (IsWorkspace(dir.FullName))
        {
            return dir.FullName;
        }

        // Monorepo: a parent directory contains a Genealogy.Workspace/ subfolder.
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

// A minimal valid 1x1 PNG (8-byte signature + IHDR/IDAT/IEND) so the quickstart
// needs no external screenshot file. Sniffed as image/png by MimeSniffer.
byte[] SyntheticPng() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

void PrintUsage()
{
    Console.Error.WriteLine(
        """
        Genealogy.Workspace.Migrator — PostgreSQL migration runner

        Usage:
          migrate    Apply all pending SQL migrations, printing each applied script.
          status     Show applied and pending migrations without changing anything.
          quickstart Import a sample GEDCOM into a tree and store an evidence
                     screenshot, end to end (Phase 7 exit criterion).

        quickstart options:
          --ged <path>         GEDCOM file to import
                               (default: tools/gedcom/tests/fixtures/phase0_baseline.ged)
          --tree <name>        Target tree name, resolved-or-created (default: Quickstart)
          --screenshot <path>  Evidence image to attach
                               (default: a synthesized in-memory 1x1 PNG)

        Configuration (environment variables):
          GENEALOGY_DB_HOST      Database host      (default 127.0.0.1)
          GENEALOGY_DB_PORT      Database port      (default 5432)
          GENEALOGY_DB_DATABASE  Database name      (default genealogy_workspace)
          GENEALOGY_DB_USERNAME  Database user      (default genealogy)
          GENEALOGY_DB_PASSWORD  Database password  (required, no default)
        """);
}
