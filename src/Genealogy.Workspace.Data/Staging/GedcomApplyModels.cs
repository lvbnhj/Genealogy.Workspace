namespace Genealogy.Workspace.Data.Staging;

/// <summary>
/// Result of <see cref="GedcomApplyService.ApplyAsync"/>. Shaped so the caller
/// cannot tell a dry run from a real apply just by looking at
/// <see cref="Changes"/> — <see cref="DryRun"/> and <see cref="Note"/> make the
/// distinction explicit, mirroring the source MCP apply tool's dry-run
/// behaviour.
/// </summary>
public sealed record GedcomApplyResult(
    bool DryRun,
    string Status,
    IReadOnlyList<GedcomApplyChange> Changes,
    string Note);

/// <summary>
/// One row of the per-entity change summary. In dry-run mode these are
/// projected from <see cref="GedcomImportPreviewChange"/>; in a real apply
/// they come straight from <c>genealogy.apply_gedcom_import</c> (migration
/// 0008).
/// </summary>
public sealed record GedcomApplyChange(
    string EntityType,
    string ChangeType,
    long RowCount);
