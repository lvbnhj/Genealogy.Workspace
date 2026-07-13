namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// One scored candidate produced by
/// <see cref="PersonLinkService.SuggestLinksAsync"/>, already persisted as a
/// <c>research.person_link_candidate</c> row.
/// </summary>
public sealed record PersonLinkCandidateResult(
    Guid PersonLinkCandidateId,
    Guid PersonId,
    string? FullName,
    decimal Score,
    string Explanation);

/// <summary>Result of <see cref="PersonLinkService.AcceptLinkAsync"/>.</summary>
public sealed record AcceptLinkResult(Guid PersonMentionId, Guid PersonId, string Status);

/// <summary>Result of <see cref="PersonLinkService.RejectLinkAsync"/>.</summary>
public sealed record RejectLinkResult(Guid PersonLinkCandidateId, string Status);
