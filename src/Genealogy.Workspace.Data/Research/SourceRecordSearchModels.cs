namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Evidence Inbox search filters (plan §8 "MVP search filters"). Every filter
/// besides <see cref="TreeId"/> is optional; an absent filter never restricts
/// the result set. Free-text/name/place filters are ILIKE substring matches.
/// </summary>
public sealed record ResearchSearchQuery(
    Guid TreeId,
    string? Status = null,
    string? RecordType = null,
    string? FreeText = null,
    string? Surname = null,
    string? GivenName = null,
    string? Place = null,
    string? ArchiveName = null,
    string? Fond = null,
    string? Opis = null,
    string? Sprava = null,
    string? Page = null,
    short? YearFrom = null,
    short? YearTo = null,
    string? Keyword = null,
    string? Role = null,
    bool? Linked = null,
    int TopN = 50);

/// <summary>One source record as returned by a search.</summary>
public sealed record SourceRecordSearchItem(
    Guid SourceRecordId,
    string Title,
    string RecordType,
    string Status,
    short? RecordYearFrom,
    short? RecordYearTo,
    string? PlaceText,
    string? ChurchText,
    string? ArchiveName,
    string? Fond,
    string? Opis,
    string? Sprava,
    string? Page,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Search result page: the matching records (already limited to <c>TopN</c>)
/// plus the total number of records matching the filters before that limit.
/// </summary>
public sealed record SourceRecordSearchResults(
    IReadOnlyList<SourceRecordSearchItem> Records,
    int TotalCount);
