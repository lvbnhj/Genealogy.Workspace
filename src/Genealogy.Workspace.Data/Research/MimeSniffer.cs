namespace Genealogy.Workspace.Data.Research;

/// <summary>
/// Magic-byte content-type detection and allowlist for attachments. This is the
/// authoritative gate: the server IGNORES any client-declared content type or
/// file extension and derives the MIME type solely from the leading bytes. Only
/// the formats enumerated here are permitted (images and PDF); anything whose
/// signature is not recognized is rejected. Content is never executed or
/// interpreted — it is only classified.
/// </summary>
public static class MimeSniffer
{
    public const string ImagePng = "image/png";
    public const string ImageJpeg = "image/jpeg";
    public const string ImageGif = "image/gif";
    public const string ImageWebp = "image/webp";
    public const string ImageTiff = "image/tiff";
    public const string ApplicationPdf = "application/pdf";

    /// <summary>
    /// Attempts to classify <paramref name="bytes"/> by its magic bytes.
    /// Returns true and sets <paramref name="mimeType"/> only for an allowed
    /// format; returns false (with an empty <paramref name="mimeType"/>) for any
    /// unknown or disallowed content, including too-short input.
    /// </summary>
    public static bool TrySniff(byte[] bytes, out string mimeType)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            mimeType = ImagePng;
            return true;
        }

        // JPEG: FF D8 FF
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF))
        {
            mimeType = ImageJpeg;
            return true;
        }

        // GIF: 47 49 46 38 ("GIF8")
        if (StartsWith(bytes, 0x47, 0x49, 0x46, 0x38))
        {
            mimeType = ImageGif;
            return true;
        }

        // WEBP: "RIFF" (52 49 46 46) at 0 AND "WEBP" (57 45 42 50) at 8
        if (StartsWith(bytes, 0x52, 0x49, 0x46, 0x46) &&
            MatchesAt(bytes, 8, 0x57, 0x45, 0x42, 0x50))
        {
            mimeType = ImageWebp;
            return true;
        }

        // TIFF: little-endian "II*\0" (49 49 2A 00) or big-endian "MM\0*" (4D 4D 00 2A)
        if (StartsWith(bytes, 0x49, 0x49, 0x2A, 0x00) ||
            StartsWith(bytes, 0x4D, 0x4D, 0x00, 0x2A))
        {
            mimeType = ImageTiff;
            return true;
        }

        // PDF: 25 50 44 46 ("%PDF")
        if (StartsWith(bytes, 0x25, 0x50, 0x44, 0x46))
        {
            mimeType = ApplicationPdf;
            return true;
        }

        mimeType = string.Empty;
        return false;
    }

    private static bool StartsWith(byte[] bytes, params byte[] signature) =>
        MatchesAt(bytes, 0, signature);

    private static bool MatchesAt(byte[] bytes, int offset, params byte[] signature)
    {
        if (bytes.Length < offset + signature.Length)
        {
            return false;
        }

        for (var i = 0; i < signature.Length; i++)
        {
            if (bytes[offset + i] != signature[i])
            {
                return false;
            }
        }

        return true;
    }
}
