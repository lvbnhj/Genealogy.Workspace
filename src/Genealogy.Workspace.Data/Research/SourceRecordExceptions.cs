namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Thrown when an operation needs an existing <c>research.source_record</c>
/// row (e.g. to denormalize its <c>tree_id</c> onto a new mention) and no row
/// with the given id exists.
/// </summary>
public sealed class SourceRecordNotFoundException : Exception
{
    public SourceRecordNotFoundException(Guid sourceRecordId)
        : base($"Source record {sourceRecordId} was not found.")
    {
        SourceRecordId = sourceRecordId;
    }

    public Guid SourceRecordId { get; }
}

/// <summary>
/// Thrown when an operation needs an existing
/// <c>research.record_person_mention</c> row and no row with the given id
/// exists (e.g. correcting a mention that was never created, or scoring links
/// for an unknown mention).
/// </summary>
public sealed class PersonMentionNotFoundException : Exception
{
    public PersonMentionNotFoundException(Guid personMentionId)
        : base($"Person mention {personMentionId} was not found.")
    {
        PersonMentionId = personMentionId;
    }

    public Guid PersonMentionId { get; }
}

/// <summary>
/// Thrown when accepting or rejecting a link needs an existing
/// <c>research.person_link_candidate</c> row and no row with the given id
/// exists.
/// </summary>
public sealed class PersonLinkCandidateNotFoundException : Exception
{
    public PersonLinkCandidateNotFoundException(Guid personLinkCandidateId)
        : base($"Person link candidate {personLinkCandidateId} was not found.")
    {
        PersonLinkCandidateId = personLinkCandidateId;
    }

    public Guid PersonLinkCandidateId { get; }
}
