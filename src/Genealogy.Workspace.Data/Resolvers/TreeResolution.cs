namespace Genealogy.Workspace.Data.Resolvers;

/// <summary>
/// The result of <see cref="TreeResolver.ResolveTreeAsync"/>: either exactly
/// one resolved tree, or an explicit reason why no single tree could be
/// chosen. Per the Phase 5 tree-scoping decision
/// (docs/POSTGRESQL_GENEALOGY_WORKSPACE_PLAN.md §10), a name matching
/// multiple/zero trees, or no default tree when none was specified, is always
/// surfaced through <see cref="FailureReason"/> — never silently picked.
/// </summary>
public sealed record TreeResolution(
    bool IsResolved,
    Guid TreeId,
    string Name,
    bool IsDefault,
    string? FailureReason)
{
    public static TreeResolution Resolved(Guid treeId, string name, bool isDefault) =>
        new(IsResolved: true, TreeId: treeId, Name: name, IsDefault: isDefault, FailureReason: null);

    /// <summary>
    /// Builds a non-resolution. <paramref name="reason"/> is one of:
    /// <c>"no default tree"</c>, <c>"tree '{value}' not found"</c>, or
    /// <c>"multiple trees named '{value}'"</c>.
    /// </summary>
    public static TreeResolution NotResolved(string reason) =>
        new(IsResolved: false, TreeId: Guid.Empty, Name: string.Empty, IsDefault: false, FailureReason: reason);
}
