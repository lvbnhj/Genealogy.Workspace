namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Input to <see cref="GedcomStagingService.StageAsync"/>: the GEDCOM file to
/// parse, the target tree, and the python invocation. Optional fields that
/// need filesystem access to resolve a default (the tool path, the output
/// directory) are left null here and resolved inside <c>StageAsync</c> rather
/// than in a static initializer, so constructing a request never touches disk.
/// </summary>
public sealed record GedcomStageRequest
{
    /// <summary>Path to the source GEDCOM file. Required.</summary>
    public required string GedcomFilePath { get; init; }

    /// <summary>
    /// Target <c>genealogy.tree.tree_id</c>. Required: the exporter's
    /// <c>--tree-id</c> flag has no default (<c>gedcom_tool.py</c>,
    /// <c>export-staging-tsv</c> sub-parser).
    /// </summary>
    public required Guid TreeId { get; init; }

    /// <summary>Optional human-readable tree name recorded in the manifest (<c>--tree-name</c>).</summary>
    public string? TreeName { get; init; }

    /// <summary>Optional root-person GEDCOM xref or unambiguous name substring (<c>--root</c>).</summary>
    public string? RootXref { get; init; }

    /// <summary>Optional free-text note stored on the batch header and passed through to the exporter (<c>--notes</c>).</summary>
    public string? Notes { get; init; }

    /// <summary>Use legacy xref-only UUID derivation instead of tree-scoped keys (<c>--legacy-ids</c>).</summary>
    public bool LegacyIds { get; init; }

    /// <summary>Optional fixed import batch id (<c>--batch-id</c>); the exporter generates one when omitted.</summary>
    public Guid? BatchId { get; init; }

    /// <summary>Python interpreter to invoke. Defaults to <c>python3</c>.</summary>
    public string PythonExecutable { get; init; } = "python3";

    /// <summary>
    /// Path to <c>gedcom_tool.py</c>. When null, <see cref="GedcomStagingService"/>
    /// resolves it by walking up from the running assembly's base directory to
    /// the checked-in <c>Genealogy.Workspace/tools/gedcom/gedcom_tool.py</c>.
    /// </summary>
    public string? GedcomToolPath { get; init; }

    /// <summary>
    /// Directory the exporter writes its 11 TSVs and <c>staging_manifest.json</c>
    /// into. When null, <see cref="GedcomStagingService"/> creates a fresh
    /// temp directory.
    /// </summary>
    public string? OutputDirectory { get; init; }
}

/// <summary>
/// One row of <see cref="GedcomStagingService.ListPendingAsync"/>: a pending
/// import batch decorated with its tree name, resolved root display name, and
/// two staging-quality signals (name-parse issues, date-parse warnings).
/// Mirrors <c>ged.ListPendingGedcomImports</c>
/// (Database/Procedures/ged/ListPendingGedcomImports.sql) minus the duplicate
/// candidate counts, which belong to Phase 4 (<c>gedcom_import_duplicate_candidate</c>
/// was deliberately not ported in migration 0004).
/// </summary>
public sealed record GedcomPendingImport(
    Guid ImportBatchId,
    Guid TreeId,
    string TreeName,
    string? RootExternalId,
    Guid? RootPersonId,
    string? RootName,
    string SourceFilePath,
    string? SourceFileHash,
    int? PersonCount,
    int? FamilyCount,
    int? EventCount,
    int? PlaceCount,
    int? ScopeInvalidCount,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PreviewedAt,
    DateTimeOffset? AppliedAt,
    DateTimeOffset? CancelledAt,
    string? Notes,
    long NameIssueCount,
    long DateWarningCount);

/// <summary>
/// Outcome of <see cref="GedcomStagingService.CancelAsync"/>: the batch header
/// after the cancellation (or after a no-op re-cancel). Mirrors the result set
/// of <c>ged.CancelGedcomImport</c> (Database/Procedures/ged/CancelGedcomImport.sql).
/// </summary>
public sealed record GedcomCancelResult(
    Guid ImportBatchId,
    Guid TreeId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PreviewedAt,
    DateTimeOffset? AppliedAt,
    DateTimeOffset? CancelledAt,
    string? Notes);

/// <summary>
/// Thrown by <see cref="GedcomStagingService.CancelAsync"/> when the batch
/// exists but has already reached <c>APPLIED</c>, mirroring the source
/// procedure's <c>THROW 53042, 'Cannot cancel an already applied GEDCOM
/// import batch.', 1</c> (Database/Procedures/ged/CancelGedcomImport.sql).
/// Applied batches are never rolled back by cancellation.
/// </summary>
public sealed class GedcomImportBatchAlreadyAppliedException : Exception
{
    public GedcomImportBatchAlreadyAppliedException(Guid importBatchId)
        : base($"Cannot cancel an already applied GEDCOM import batch: {importBatchId}.")
    {
        ImportBatchId = importBatchId;
    }

    public Guid ImportBatchId { get; }
}
