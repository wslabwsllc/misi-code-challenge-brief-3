using System.Security.Cryptography;
using EventPermits.Domain;

namespace EventPermits.Application;

/// <summary>
/// Upload checks (FR-15, A-14): extension, declared MIME and magic bytes must agree; size within the limit.
/// Signature checks establish the format; they are not antivirus scanning.
/// </summary>
public static class FileSignatures
{
    public sealed record Kind(string Extension, string ContentType, byte[] Magic);

    public static readonly Kind Pdf = new(".pdf", "application/pdf", "%PDF-"u8.ToArray());
    public static readonly Kind Png = new(".png", "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    public static readonly Kind Jpeg = new(".jpg", "image/jpeg", [0xFF, 0xD8, 0xFF]);

    public static readonly IReadOnlyList<Kind> All = [Pdf, Png, Jpeg];
    public static readonly IReadOnlyList<Kind> ImagesOnly = [Png, Jpeg];

    public sealed record Checked(string SafeName, string ContentType, string Sha256, long Size);

    public static Checked Validate(string? fileName, string? declaredContentType, ReadOnlySpan<byte> content, long maxBytes, IReadOnlyList<Kind> allowed)
    {
        if (content.Length == 0) throw Reject("The file is empty.");
        if (content.Length > maxBytes) throw Reject($"The file is larger than {maxBytes / (1024 * 1024)} MiB ({maxBytes:N0} bytes).");
        var name = SafeFileName(fileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".jpeg") ext = ".jpg";
        var kind = allowed.FirstOrDefault(k => k.Extension == ext)
                   ?? throw Reject($"Only {string.Join(", ", allowed.Select(k => k.Extension.TrimStart('.').ToUpperInvariant()))} files are accepted.");
        var declared = (declaredContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (declared == "image/jpg") declared = "image/jpeg";
        if (declared.Length > 0 && declared != "application/octet-stream" && declared != kind.ContentType)
            throw Reject($"The declared type {declared} does not match the {ext} extension.");
        if (!content.StartsWith(kind.Magic))
            throw Reject($"The file content is not a valid {ext.TrimStart('.').ToUpperInvariant()} file.");
        return new Checked(name, kind.ContentType, Convert.ToHexStringLower(SHA256.HashData(content)), content.Length);
    }

    /// <summary>Strips paths and control characters; keeps a readable display name only.</summary>
    public static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (name.Length == 0 || name.Trim('.').Length == 0) name = "file";
        if (name.Length > Limits.MaxFileNameLength)
        {
            var ext = Path.GetExtension(name);
            name = name[..(Limits.MaxFileNameLength - ext.Length)] + ext;
        }
        return name;
    }

    private static BusinessRuleException Reject(string message) => new(ErrorCodes.AttachmentRejected, message);
}
