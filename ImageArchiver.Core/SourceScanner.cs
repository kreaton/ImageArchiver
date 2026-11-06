namespace ImageArchiver.Core;

/// <summary>Finds the JPEG files under a source directory, in a stable order.</summary>
internal static class SourceScanner
{
    public static (List<string> Files, List<(string Path, string Reason)> Failures) Scan(
        string source, string? excluded, CancellationToken cancellation)
    {
        var files = new List<string>();
        var failures = new List<(string, string)>();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(source));

        while (pending.Count > 0)
        {
            if (cancellation.IsCancellationRequested)
                break;

            var directory = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", options)
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add((directory.FullName, ex.Message));
                continue;
            }

            // A stack visits the last pushed first, so push in reverse to keep name order.
            foreach (var entry in Enumerable.Reverse(entries))
            {
                if (entry.LinkTarget is not null) // Links can form cycles and lead outside the source.
                    continue;
                if (entry is DirectoryInfo subdirectory
                    && (excluded is null || !IsSameOrUnder(subdirectory.FullName, excluded))
                    && !IsProtected(subdirectory))
                    pending.Push(subdirectory);
            }
            files.AddRange(entries.Where(e => e is FileInfo && e.LinkTarget is null && Archiver.IsJpeg(e.Name)).Select(e => e.FullName));
        }
        return (files, failures);
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        var parent = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hidden system folders, such as the recycle bin at the root of a drive.</summary>
    private static bool IsProtected(DirectoryInfo directory) =>
        directory.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System);
}
