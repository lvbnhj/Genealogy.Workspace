namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// A parent, enriched with birth/death year and place. Used by
/// <see cref="RichFamilyContext.Parents"/>. Mirrors result set 2 of
/// <c>ged.GetPersonFamilyContext</c> (Database/Procedures/ged/GetPersonFamilyContext.sql).
/// </summary>
public sealed record RichFamilyMember(
    Guid PersonId,
    string? PrimaryDisplayName,
    char? Sex,
    bool? IsLiving,
    short? BirthYear,
    string? BirthPlace,
    short? DeathYear,
    string? DeathPlace);

/// <summary>
/// A sibling — another child sharing at least one parent with the requested
/// person. Mirrors result set 3 of <c>ged.GetPersonFamilyContext</c>.
/// </summary>
public sealed record RichSibling(
    Guid PersonId,
    string? PrimaryDisplayName,
    char? Sex,
    bool? IsLiving,
    short? BirthYear,
    string? BirthPlace,
    short? DeathYear);

/// <summary>
/// A marriage, from the requested person's side: the spouse (enriched with
/// birth/death year and place) plus the marriage's own date/year/place.
/// Mirrors result set 4 of <c>ged.GetPersonFamilyContext</c>. Derived from
/// <c>genealogy.family</c> since there is no dedicated spouse-edge table
/// (same derivation as <c>Repositories.FamilyContextRepository</c>'s
/// <c>Spouse</c>, but enriched to parity with the source).
/// </summary>
public sealed record RichMarriage(
    Guid FamilyId,
    Guid SpousePersonId,
    string? SpouseName,
    char? SpouseSex,
    bool? SpouseIsLiving,
    short? SpouseBirthYear,
    string? SpouseBirthPlace,
    short? SpouseDeathYear,
    string? MarriageDateRaw,
    short? MarriageYear,
    string? MarriagePlaceRaw);

/// <summary>
/// A child, enriched with birth/death year+place and the identity of the
/// *other* parent of that child (who may not be the requested person's
/// current or only spouse — covers half-siblings from different unions).
/// Mirrors result set 5 of <c>ged.GetPersonFamilyContext</c>.
/// </summary>
public sealed record RichChild(
    Guid PersonId,
    string? PrimaryDisplayName,
    char? Sex,
    bool? IsLiving,
    short? BirthYear,
    string? BirthDateRaw,
    string? BirthPlace,
    short? DeathYear,
    string? OtherParentName,
    char? OtherParentSex);

/// <summary>
/// The full family context of a person, at parity with the SQL Server proc
/// <c>ged.GetPersonFamilyContext</c>'s five result sets: (1) the person's own
/// life events, (2) parents, (3) siblings, (4) marriages, (5) children — each
/// enriched with birth/death year and place where the source provides it.
/// This is a separate, richer type from <see cref="FamilyContext"/> (Phase 2)
/// rather than an in-place extension, so Phase 2's
/// <c>FamilyContextRepository</c>/<c>FamilyContext</c> callers and tests are
/// unaffected. Every list is empty, never null, when a person has no
/// relatives/events of that kind.
/// </summary>
public sealed record RichFamilyContext(
    Guid TreeId,
    Guid PersonId,
    string? PrimaryDisplayName,
    char? Sex,
    bool? IsLiving,
    IReadOnlyList<PersonEvent> LifeEvents,
    IReadOnlyList<RichFamilyMember> Parents,
    IReadOnlyList<RichSibling> Siblings,
    IReadOnlyList<RichMarriage> Marriages,
    IReadOnlyList<RichChild> Children);
