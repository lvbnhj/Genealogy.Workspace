namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Everything needed to insert a new <c>research.source_record</c> row.
/// <see cref="TreeId"/>, <see cref="Title"/> and <see cref="RecordType"/> are
/// required; every other field is optional metadata/citation detail. When
/// <see cref="Status"/> is null the row gets the schema default (<c>inbox</c>).
/// </summary>
public sealed record SourceRecordInput(
    Guid TreeId,
    string Title,
    string RecordType,
    string? RecordText = null,
    string? Transcription = null,
    string? RecordDateText = null,
    DateOnly? RecordDateFrom = null,
    DateOnly? RecordDateTo = null,
    short? RecordYearFrom = null,
    short? RecordYearTo = null,
    string? PlaceText = null,
    string? ChurchText = null,
    string? ArchiveName = null,
    string? Fond = null,
    string? Opis = null,
    string? Sprava = null,
    string? Page = null,
    string? CitationText = null,
    string? SourceUrl = null,
    string? Status = null);

/// <summary>Result of <see cref="SourceRecordRepository.AddRecordAsync"/>.</summary>
public sealed record SourceRecordCreated(Guid SourceRecordId, string Status, DateTimeOffset CreatedAt);

/// <summary>Result of <see cref="SourceRecordRepository.UpdateRecordAsync"/>.</summary>
public sealed record SourceRecordUpdated(Guid SourceRecordId, string Status, DateTimeOffset UpdatedAt);

/// <summary>One denormalized search keyword attached to a source record.</summary>
public sealed record SourceRecordKeywordEntry(string Keyword, string KeywordType);

/// <summary>
/// A person mentioned in a source record, as surfaced by
/// <see cref="SourceRecordRepository.GetRecordAsync"/>. Mirrors
/// <c>research.record_person_mention</c> minus the denormalized
/// <c>tree_id</c> (already known from the parent record).
/// </summary>
public sealed record PersonMentionEntry(
    Guid PersonMentionId,
    string? NameText,
    string? GivenName,
    string? Surname,
    string? Patronymic,
    char? Sex,
    string? Role,
    string? AgeText,
    short? EstimatedBirthYear,
    string? SocialStatus,
    string? RelationshipText,
    string Status,
    Guid? AcceptedPersonId,
    decimal? Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// A place mentioned in a source record, as surfaced by
/// <see cref="SourceRecordRepository.GetRecordAsync"/>.
/// </summary>
public sealed record PlaceMentionEntry(
    Guid PlaceMentionId,
    string PlaceText,
    string? PlaceType,
    string? NormalizedName,
    long? PlaceId);

/// <summary>
/// A person-link candidate belonging to one of the record's mentions, as
/// surfaced by <see cref="SourceRecordRepository.GetRecordAsync"/>.
/// </summary>
public sealed record LinkCandidateEntry(
    Guid PersonLinkCandidateId,
    Guid PersonMentionId,
    Guid PersonId,
    decimal Score,
    string? Explanation,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

/// <summary>
/// The full Evidence Inbox view of one source record: its own fields plus
/// keywords, person mentions, place mentions and link candidates. Binary
/// attachments are intentionally NOT included here — Task 2's attachment list
/// service (<c>AttachmentRepository</c>) is the source of truth for those; see
/// <see cref="AttachmentsNote"/>.
/// </summary>
public sealed record SourceRecordDetail(
    Guid SourceRecordId,
    Guid TreeId,
    string Title,
    string RecordType,
    string? RecordText,
    string? Transcription,
    string? RecordDateText,
    DateOnly? RecordDateFrom,
    DateOnly? RecordDateTo,
    short? RecordYearFrom,
    short? RecordYearTo,
    string? PlaceText,
    string? ChurchText,
    string? ArchiveName,
    string? Fond,
    string? Opis,
    string? Sprava,
    string? Page,
    string? CitationText,
    string? SourceUrl,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<SourceRecordKeywordEntry> Keywords,
    IReadOnlyList<PersonMentionEntry> PersonMentions,
    IReadOnlyList<PlaceMentionEntry> PlaceMentions,
    IReadOnlyList<LinkCandidateEntry> LinkCandidates)
{
    /// <summary>
    /// Constant note surfaced to callers (e.g. the MCP tool layer) so they
    /// know to fetch attachments separately rather than expecting them here.
    /// </summary>
    public string AttachmentsNote { get; init; } =
        "Binary attachments are not included in this view. List them separately via the attachment list service (AttachmentRepository).";
}
