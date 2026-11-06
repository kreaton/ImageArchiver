using System.Globalization;
using System.Security.Cryptography;

namespace ImageArchiver.Core;

/// <summary>Reported once while scanning (total not yet known) and then after every file.</summary>
public sealed record SourceProgress(
    bool Scanning, int Processed, int Total, string Summary, string? Detail = null, bool IsFailure = false);

public sealed record SourceFailure(string Path, string Reason);

/// <summary>Files with the same photo. The first is the one an archive run would store.</summary>
public sealed record DuplicateGroup(IReadOnlyList<string> Files);

public sealed record DuplicateReport(
    IReadOnlyList<DuplicateGroup> Groups, int FilesScanned, IReadOnlyList<SourceFailure> Failures, bool Cancelled)
{
    public int DuplicateCount => Groups.Sum(g => g.Files.Count - 1);
}

public sealed record DateUpdateResult(int Updated, int Unchanged, IReadOnlyList<SourceFailure> Failures, bool Cancelled);

/// <summary>Operations on the source directory alone, with no archive involved.</summary>
public static class SourceTools
{
    /// <summary>A difference this small can be the filesystem's own rounding (FAT/exFAT).</summary>
    private static readonly TimeSpan DateTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A dry run: reports which files an archive run with suppressed duplicates would leave out,
    /// by the same rules and in the same order. Nothing is written anywhere.
    /// </summary>
    public static DuplicateReport FindDuplicates(
        string sourceDirectory, IProgress<SourceProgress>? progress = null, CancellationToken cancellation = default)
    {
        var source = ResolveSource(sourceDirectory);
        var failures = new List<SourceFailure>();
        var index = new ArchiveIndex();
        var groups = new List<List<string>>();
        var groupOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;
        string Summary() => $"{duplicates} duplicates, {failures.Count} failed";

        progress?.Report(new SourceProgress(true, 0, 0, Summary()));
        var files = ScanInto(source, failures, cancellation);
        if (cancellation.IsCancellationRequested)
            return Report(0, true);

        var processed = 0;
        progress?.Report(new SourceProgress(false, 0, files.Count, Summary()));
        foreach (var file in files)
        {
            if (cancellation.IsCancellationRequested)
                return Report(processed, true);

            var relative = Path.GetRelativePath(source, file);
            string? detail = null;
            var failed = false;
            try
            {
                var data = File.ReadAllBytes(file);
                var metadata = ImageMetadata.Read(data);
                Archiver.ReadSidecar(file, metadata, []);

                var sha = Convert.ToHexStringLower(SHA256.HashData(data));
                var identity = new ContentIdentity(sha, data.Length);
                var origin = ContentIdentity.Parse(metadata["origin_sha256"], metadata["origin_size"]);
                var found = origin is null ? index.Find(identity) : index.Find(identity).Concat(index.Find(origin));

                if (found.FirstOrDefault() is { } kept)
                {
                    groupOf[kept.TargetPath].Add(relative);
                    duplicates++;
                    detail = $"duplicate {relative} (same as {kept.TargetPath})";
                }
                else
                {
                    index.Add(new ArchiveEntry(relative, sha, data.Length, relative, null, origin));
                    List<string> group = [relative];
                    groupOf[relative] = group;
                    groups.Add(group);
                }
            }
            catch (Exception ex)
            {
                failures.Add(new SourceFailure(relative, ex.Message));
                detail = $"error     {relative}: {ex.Message}";
                failed = true;
            }
            processed++;
            progress?.Report(new SourceProgress(false, processed, files.Count, Summary(), detail, failed));
        }
        return Report(processed, false);

        DuplicateReport Report(int scanned, bool cancelled) => new(
            groups.Where(g => g.Count > 1).Select(g => new DuplicateGroup(g)).ToList(), scanned, failures, cancelled);
    }

    /// <summary>
    /// Sets each source file's created and modified dates to its smallest detected date. The
    /// file contents aren't touched, but the previous dates are gone afterwards.
    /// </summary>
    public static DateUpdateResult UpdateSourceDates(
        string sourceDirectory, IProgress<SourceProgress>? progress = null, CancellationToken cancellation = default)
    {
        var source = ResolveSource(sourceDirectory);
        var failures = new List<SourceFailure>();
        int updated = 0, unchanged = 0;
        string Summary() => $"{updated} updated, {unchanged} unchanged, {failures.Count} failed";

        progress?.Report(new SourceProgress(true, 0, 0, Summary()));
        var files = ScanInto(source, failures, cancellation);
        if (cancellation.IsCancellationRequested)
            return new DateUpdateResult(0, 0, failures, true);

        var processed = 0;
        progress?.Report(new SourceProgress(false, 0, files.Count, Summary()));
        foreach (var file in files)
        {
            if (cancellation.IsCancellationRequested)
                return new DateUpdateResult(updated, unchanged, failures, true);

            var relative = Path.GetRelativePath(source, file);
            string? detail = null;
            var failed = false;
            try
            {
                var info = new FileInfo(file);
                DateTime created = info.CreationTime, modified = info.LastWriteTime;
                var metadata = ImageMetadata.Read(File.ReadAllBytes(file));
                var date = PhotoDate.Resolve(metadata.Dates, created, modified, DateTime.Now);

                if ((created - date.Value).Duration() < DateTolerance && (modified - date.Value).Duration() < DateTolerance)
                {
                    unchanged++;
                }
                else
                {
                    File.SetCreationTime(file, date.Value);
                    File.SetLastWriteTime(file, date.Value);
                    updated++;
                    detail = $"updated   {relative}: created {Text(created)}, modified {Text(modified)} -> {Text(date.Value)} ({date.Source})";
                }
            }
            catch (Exception ex)
            {
                failures.Add(new SourceFailure(relative, ex.Message));
                detail = $"error     {relative}: {ex.Message}";
                failed = true;
            }
            processed++;
            progress?.Report(new SourceProgress(false, processed, files.Count, Summary(), detail, failed));
        }
        return new DateUpdateResult(updated, unchanged, failures, false);
    }

    private static string ResolveSource(string sourceDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        return Directory.Exists(source) ? source : throw new ArchiveException($"Source directory does not exist: {source}");
    }

    private static List<string> ScanInto(string source, List<SourceFailure> failures, CancellationToken cancellation)
    {
        var (files, scanFailures) = SourceScanner.Scan(source, null, cancellation);
        failures.AddRange(scanFailures.Select(f => new SourceFailure(Path.GetRelativePath(source, f.Path), f.Reason)));
        return files;
    }

    private static string Text(DateTime date) => date.ToString(Catalog.DateFormat, CultureInfo.InvariantCulture);
}
