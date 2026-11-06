using System.Text;

namespace ImageArchiver.Core;

/// <summary>
/// The origin archivejpg stored with a photo: as XMP in the JPEG (--xmpembed), in the EXIF
/// UserComment (--exifembed) or in a sidecar file (--xmpsidecar).
/// </summary>
internal sealed record StoredOrigin(string? SourceRoot, string SourcePath, string Sha256, string Size, string? ArchivedAt)
{
    private const string CommentPrefix = "archivejpg|";
    private const int CommentHeaderLength = 8;

    public static StoredOrigin? FromXmp(string xmp)
    {
        var path = XmpField(xmp, "SourcePath");
        var sha = XmpField(xmp, "OriginalSHA256");
        var size = XmpField(xmp, "OriginalSize");
        return path is null || sha is null || size is null
            ? null
            : new StoredOrigin(XmpField(xmp, "SourceRoot"), path, sha, size, XmpField(xmp, "ArchivedAt"));
    }

    /// <summary>The value is an 8 byte encoding header, then archivejpg|source=…|sha256=…|size=….</summary>
    public static StoredOrigin? FromUserComment(byte[] value)
    {
        if (value.Length <= CommentHeaderLength)
            return null;

        var body = value.AsSpan(CommentHeaderLength);
        // Its byte order is the file's, so a wrong guess shows as a missing prefix.
        string[] candidates = value.AsSpan(0, CommentHeaderLength).SequenceEqual("UNICODE\0"u8)
            ? [Encoding.Unicode.GetString(body), Encoding.BigEndianUnicode.GetString(body)]
            : [Encoding.UTF8.GetString(body)];
        var text = candidates.FirstOrDefault(c => c.StartsWith(CommentPrefix, StringComparison.Ordinal));
        if (text is null)
            return null;

        string? path = null, sha = null, size = null;
        foreach (var part in text[CommentPrefix.Length..].TrimEnd('\0').Split('|'))
        {
            var (name, field) = part.IndexOf('=') is var i and >= 0 ? (part[..i], part[(i + 1)..]) : (part, "");
            switch (name)
            {
                case "source": path = field; break;
                case "sha256": sha = field; break;
                case "size": size = field; break;
            }
        }
        return path is null || sha is null || size is null ? null : new StoredOrigin(null, path, sha, size, null);
    }

    /// <summary>Reads archivejpg:name in element or attribute form, as other tools may rewrite it.</summary>
    private static string? XmpField(string xmp, string name)
    {
        var open = $"<archivejpg:{name}>";
        var start = xmp.LastIndexOf(open, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += open.Length;
            var end = xmp.IndexOf('<', start);
            return end < 0 ? null : Unescape(xmp[start..end]);
        }

        var attribute = $"archivejpg:{name}=\"";
        start = xmp.LastIndexOf(attribute, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += attribute.Length;
        var quote = xmp.IndexOf('"', start);
        return quote < 0 ? null : Unescape(xmp[start..quote]);
    }

    private static string Unescape(string value) => value
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
}
