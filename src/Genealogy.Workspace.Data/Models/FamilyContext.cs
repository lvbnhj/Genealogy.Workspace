namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// A parent or child of a person, as seen through <c>genealogy.parent_child</c>.
/// <see cref="RelationType"/> is the free-text relation label from that edge
/// (e.g. <c>BIO</c>), oriented from parent to child regardless of which side
/// this row represents.
/// </summary>
public sealed record FamilyMember(
    Guid PersonId,
    string? PrimaryDisplayName,
    string? SurnameNormalized,
    char? Sex,
    bool? IsLiving,
    string RelationType);

/// <summary>
/// A spouse of a person, derived from <c>genealogy.family</c> (there is no
/// dedicated spouse-edge table). <see cref="PersonId"/> is the *other* spouse
/// in the family, never the person the query was made for.
/// </summary>
public sealed record Spouse(
    Guid PersonId,
    string? PrimaryDisplayName,
    string? SurnameNormalized,
    char? Sex,
    bool? IsLiving,
    Guid FamilyId,
    short? MarriageYear,
    string? MarriagePlaceRaw,
    string? MarriageDateRaw);

/// <summary>
/// The immediate family context of a person within one tree: parents,
/// children and spouses. Lists are always empty, never null, when a person
/// has no relatives of that kind.
/// </summary>
public sealed record FamilyContext(
    Guid TreeId,
    Guid PersonId,
    IReadOnlyList<FamilyMember> Parents,
    IReadOnlyList<FamilyMember> Children,
    IReadOnlyList<Spouse> Spouses);
