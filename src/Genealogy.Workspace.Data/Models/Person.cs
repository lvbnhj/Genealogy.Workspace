namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// A person within a specific tree (<c>genealogy.person</c>). Never valid
/// outside the tree it was loaded for — callers must always scope by
/// <see cref="TreeId"/>.
/// </summary>
public sealed record Person(
    Guid PersonId,
    Guid TreeId,
    string? ExternalId,
    char? Sex,
    bool? IsLiving,
    string? PrimaryDisplayName,
    string? SurnameNormalized,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    PersonName? PrimaryName);
