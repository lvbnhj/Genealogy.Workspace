namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Thrown when <see cref="TreeRepository.CreateTreeAsync"/> would violate the
/// unique tree name constraint (<c>genealogy.tree.name</c> is UNIQUE).
/// </summary>
public sealed class DuplicateTreeNameException : Exception
{
    public DuplicateTreeNameException(string name)
        : base($"A tree named '{name}' already exists. Tree names must be unique.")
    {
        Name = name;
    }

    public string Name { get; }
}

/// <summary>
/// Thrown when <see cref="TreeRepository.CreateTreeAsync"/> would create a
/// second default tree. The schema enforces at most one default tree via a
/// partial unique index on <c>genealogy.tree.is_default</c>.
/// </summary>
public sealed class DefaultTreeConflictException : Exception
{
    public DefaultTreeConflictException()
        : base("Another tree is already marked as the default tree. Only one default tree is allowed at a time.")
    {
    }
}
