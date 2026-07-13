namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// A genealogy tree (<c>genealogy.tree</c>). Trees are the top-level ownership
/// boundary: every person, family and event belongs to exactly one tree.
/// </summary>
public sealed record Tree(
    Guid TreeId,
    string Name,
    string? Description,
    Guid? RootPersonId,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
