namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// The person header returned alongside their life events. Mirrors the
/// header result set of <c>ged.GetPersonLifeEvents</c>
/// (Database/Procedures/ged/GetPersonLifeEvents.sql).
/// </summary>
public sealed record PersonLifeEventsHeader(
    Guid PersonId,
    string? FullName,
    char? Sex,
    bool? IsLiving);

/// <summary>
/// One life event for a person (<c>genealogy.event</c>), enriched with its
/// place text and, for events that reference a related person (e.g. a
/// marriage partner), that person's display name. No DNA columns and no
/// citation join — the source proc does not join citations either. Mirrors
/// <c>ged.GetPersonLifeEvents</c>'s events result set.
/// </summary>
public sealed record PersonEvent(
    long EventId,
    string? ExternalEventKey,
    string EventType,
    string? EventValue,
    string? DateRaw,
    DateOnly? DateFrom,
    DateOnly? DateTo,
    short? YearFrom,
    short? YearTo,
    string? PlaceRaw,
    string? PlaceNormalized,
    Guid? FamilyId,
    Guid? RelatedPersonId,
    string? RelatedPersonName,
    bool IsDerived,
    string? Notes);

/// <summary>
/// The full result of <see cref="Repositories.PersonEventsRepository.GetLifeEventsAsync"/>:
/// the person header plus every life event, ordered chronologically (birth
/// year, then date, then a fixed event-type rank — see
/// <see cref="Repositories.PersonEventsRepository"/> for the exact rule).
/// </summary>
public sealed record PersonLifeEvents(
    PersonLifeEventsHeader Header,
    IReadOnlyList<PersonEvent> Events);
