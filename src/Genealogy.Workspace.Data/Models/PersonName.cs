namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// A single structured name row for a person (<c>genealogy.person_name</c>).
/// A person may have several — different scripts (Latin/Cyrillic), name types
/// (birth, married, alias, ...) — but at most one is flagged
/// <see cref="IsPrimary"/> per person.
/// </summary>
public sealed record PersonName(
    long PersonNameId,
    Guid TreeId,
    Guid PersonId,
    string ScriptCode,
    string NameType,
    string? Given,
    string? Surname,
    string FullName,
    string FullNameNormalized,
    bool IsPrimary,
    DateTimeOffset CreatedAt);
