namespace Genealogy.Workspace.Data.Resolvers;

/// <summary>
/// One candidate returned by <see cref="PersonResolver.ResolvePersonAsync"/>
/// when a name matches more than one person and no single exact match breaks
/// the tie.
/// </summary>
public sealed record PersonCandidate(Guid PersonId, string? FullName);

/// <summary>
/// The result of <see cref="PersonResolver.ResolvePersonAsync"/>: exactly one
/// resolved person, a list of candidates for the caller to disambiguate, or a
/// not-found reason. A resolver never silently picks among several non-exact
/// substring matches — see <see cref="Candidates"/>.
/// </summary>
public sealed record PersonResolution(
    bool IsResolved,
    Guid PersonId,
    string? FullName,
    IReadOnlyList<PersonCandidate> Candidates,
    string? FailureReason)
{
    public static PersonResolution Resolved(Guid personId, string? fullName) =>
        new(IsResolved: true, PersonId: personId, FullName: fullName,
            Candidates: Array.Empty<PersonCandidate>(), FailureReason: null);

    public static PersonResolution MultiMatch(IReadOnlyList<PersonCandidate> candidates) =>
        new(IsResolved: false, PersonId: Guid.Empty, FullName: null,
            Candidates: candidates, FailureReason: "multiple matching persons");

    public static PersonResolution NotFound(string reason) =>
        new(IsResolved: false, PersonId: Guid.Empty, FullName: null,
            Candidates: Array.Empty<PersonCandidate>(), FailureReason: reason);
}
