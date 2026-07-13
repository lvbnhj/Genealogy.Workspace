namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Outcome of <see cref="AttachmentRepository.AddAttachmentAsync"/>: the new
/// link row, the content row it points at (freshly inserted or reused), and the
/// server-computed hash/length/MIME. <see cref="Deduplicated"/> is true when the
/// bytes already existed and the content row was reused rather than re-inserted.
/// </summary>
public sealed record AddAttachmentResult(
    long SourceRecordAttachmentId,
    long AttachmentContentId,
    string ContentHash,
    long ByteLength,
    string MimeType,
    bool Deduplicated);

/// <summary>
/// A single attachment's bytes plus its metadata, returned by
/// <see cref="AttachmentRepository.GetAttachmentAsync"/>. <see cref="Content"/>
/// is byte-identical to what was stored.
/// </summary>
public sealed record AttachmentBytes(
    long SourceRecordAttachmentId,
    long AttachmentContentId,
    byte[] Content,
    string? FileName,
    string? Caption,
    string MimeType,
    long ByteLength,
    string ContentHash,
    DateTimeOffset CreatedAt);

/// <summary>
/// Metadata for one attachment link on a source record (no bytes), returned by
/// <see cref="AttachmentRepository.ListAttachmentsAsync"/>.
/// </summary>
public sealed record AttachmentInfo(
    long SourceRecordAttachmentId,
    long AttachmentContentId,
    string? FileName,
    string? Caption,
    int SequenceNo,
    string AttachmentType,
    string? SourceUrl,
    string MimeType,
    long ByteLength,
    string ContentHash,
    DateTimeOffset CreatedAt);
