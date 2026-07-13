namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// The full result of previewing a staged GEDCOM import batch against the
/// production tree. Mirrors the three result sets of the SQL Server source
/// procedure <c>ged.GetGedcomImportPreview</c>
/// (Database/Procedures/ged/GetGedcomImportPreview.sql): the batch header, the
/// per-entity change counts, and a capped sample of person-level changes.
/// </summary>
public sealed record GedcomImportPreviewResult(
    GedcomImportPreviewBatch Batch,
    IReadOnlyList<GedcomImportPreviewChange> Changes,
    IReadOnlyList<GedcomImportPreviewPersonChange> SamplePersonChanges);

/// <summary>
/// One row of the change summary: how many rows of a given entity fall into a
/// given change type. Only positive counts are reported. Mirrors source result
/// set 2 (source lines 41-368).
/// </summary>
public sealed record GedcomImportPreviewChange(
    string EntityType,
    string ChangeType,
    long RowCount);

/// <summary>
/// The batch header. Mirrors source result set 1 (source lines 20-39). The
/// SQL Server <c>RootTreePersonId</c> column maps to the workspace column
/// <c>root_person_id</c>.
/// </summary>
public sealed record GedcomImportPreviewBatch(
    Guid ImportBatchId,
    Guid TreeId,
    string? TreeName,
    string SourceFilePath,
    string? SourceFileHash,
    string? RootExternalId,
    Guid? RootPersonId,
    int? PersonCount,
    int? FamilyCount,
    int? EventCount,
    int? PlaceCount,
    int? ScopeInvalidCount,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PreviewedAt,
    DateTimeOffset? AppliedAt,
    string? Notes);

/// <summary>
/// One sampled person-level change (ADD or UPDATE) with staged versus current
/// values side by side. Mirrors source result set 3 (source lines 370-390).
/// </summary>
public sealed record GedcomImportPreviewPersonChange(
    string EntityType,
    string ChangeType,
    Guid TreePersonId,
    string? ExternalId,
    string? PrimaryDisplayName,
    string? CurrentPrimaryDisplayName,
    char? Sex,
    char? CurrentSex);
