namespace Genealogy.Workspace.Data;

/// <summary>
/// Snapshot of the migration journal: scripts already applied and scripts still
/// pending, both ordered by their numeric filename prefix.
/// </summary>
public sealed record MigrationStatus(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending);
