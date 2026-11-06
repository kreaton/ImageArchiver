namespace ImageArchiver.Core;

/// <summary>What identifies a photo's content: its SHA-256 hash and size.</summary>
public sealed record ContentIdentity(string Sha256, long Size)
{
    /// <summary>
    /// The identity of a stored origin, or null if it isn't a real hash and size. Origins are
    /// data found in files, so anything else must not be able to match a photo.
    /// </summary>
    public static ContentIdentity? Parse(string? sha256, string? size) =>
        sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigit) && long.TryParse(size, out var bytes) && bytes >= 0
            ? new ContentIdentity(sha256.ToLowerInvariant(), bytes)
            : null;
}

/// <summary>
/// An archived file. SourcePath and SourceModified are null when it was found without a catalog
/// row. The modified date is the source's, as the archived copy carries the archive date.
/// Origin is the identity of the original, when the file had one stored by archivejpg.
/// </summary>
public sealed record ArchiveEntry(
    string TargetPath, string Sha256, long Size, string? SourcePath, DateTime? SourceModified, ContentIdentity? Origin = null)
{
    public bool Matches(ContentIdentity identity) =>
        (string.Equals(Sha256, identity.Sha256, StringComparison.OrdinalIgnoreCase) && Size == identity.Size)
        || (Origin is { } origin && origin == identity);
}

/// <summary>The archived files, keyed by target path and by content hash.</summary>
public sealed class ArchiveIndex
{
    private readonly Dictionary<string, ArchiveEntry> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ArchiveEntry>> _byHash = new(StringComparer.OrdinalIgnoreCase);

    public void Add(ArchiveEntry entry)
    {
        if (_byTarget.Remove(entry.TargetPath, out var old))
            foreach (var hash in Hashes(old))
                _byHash[hash].Remove(old);

        _byTarget[entry.TargetPath] = entry;
        foreach (var hash in Hashes(entry))
        {
            if (!_byHash.TryGetValue(hash, out var list))
                _byHash[hash] = list = [];
            list.Add(entry);
        }
    }

    public bool Contains(string targetPath) => _byTarget.ContainsKey(targetPath);

    /// <summary>Archived files that are this content, or were made from it, in the order archived.</summary>
    public IEnumerable<ArchiveEntry> Find(ContentIdentity identity) =>
        _byHash.TryGetValue(identity.Sha256, out var list) ? list.Where(e => e.Matches(identity)) : [];

    /// <summary>A file with an unchanged origin has the same hash twice; it is only listed once.</summary>
    private static IEnumerable<string> Hashes(ArchiveEntry entry) =>
        entry.Origin is { } origin && !origin.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)
            ? [entry.Sha256, origin.Sha256]
            : [entry.Sha256];
}
