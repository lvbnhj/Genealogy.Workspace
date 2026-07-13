namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Descriptive fields for a person named in a source record. Deliberately
/// excludes <c>tree_id</c> (denormalized server-side from the parent record),
/// <c>status</c>/<c>accepted_person_id</c>/<c>confidence</c> (owned by the
/// link lifecycle — see <see cref="PersonLinkService"/>) and
/// <c>person_mention_id</c> (chosen by the repository on insert, or supplied
/// separately when correcting an existing mention).
/// </summary>
public sealed record PersonMentionInput(
    string? NameText,
    string? GivenName,
    string? Surname,
    string? Patronymic,
    char? Sex,
    string? Role,
    string? AgeText,
    short? EstimatedBirthYear,
    string? SocialStatus,
    string? RelationshipText);

/// <summary>Result of adding or correcting a person mention.</summary>
public sealed record PersonMentionResult(Guid PersonMentionId, Guid SourceRecordId);

/// <summary>
/// Fields for a place named in a source record. <see cref="PlaceText"/> is
/// required; <see cref="PlaceId"/> optionally links to the GLOBAL
/// <c>genealogy.place</c> table.
/// </summary>
public sealed record PlaceMentionInput(
    string PlaceText,
    string? PlaceType = null,
    string? NormalizedName = null,
    long? PlaceId = null);

/// <summary>Result of adding a place mention.</summary>
public sealed record PlaceMentionResult(Guid PlaceMentionId, Guid SourceRecordId);
