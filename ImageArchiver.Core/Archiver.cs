using System.Globalization;
using System.Security.Cryptography;

namespace ImageArchiver.Core;

/// <summary>
/// Copies .jpg and .jpeg files from a directory tree into target\yyyy-MM folders, by the smallest
/// date detected for each photo, and records every file in a catalog in the target root. The
/// copies get that date as their created and modified dates; their content is left identical.
/// Source files are never changed and nothing in the archive is overwritten.
/// </summary>
public sealed class Archiver
{
    private const string DateFormat = Catalog.DateFormat;

    /// <summary>Tolerance for filesystems with coarse timestamps (FAT/exFAT).</summary>
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);

    private readonly ArchiveOptions _options;
    private readonly string _source;
    private readonly string _target;
    private readonly string _runId = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    private readonly HashSet<string> _indexedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private ArchiveIndex _index = new();

    private Archiver(ArchiveOptions options)
    {
        _options = options;
        _source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SourceDirectory));
        _target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.TargetDirectory));
    }

    /// <summary>
    /// Runs synchronously; call it on a background thread from a UI. Per-file problems are
    /// recorded as failed rows. Throws <see cref="ArchiveException"/> if the run can't start,
    /// or if the catalog can no longer be written.
    /// </summary>
    public static ArchiveResult Run(
        ArchiveOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellation = default) =>
        new Archiver(options).Run(progress, cancellation);

    private ArchiveResult Run(IProgress<ArchiveProgress>? progress, CancellationToken cancellation)
    {
        if (!Directory.Exists(_source))
            throw new ArchiveException($"Source directory does not exist: {_source}");
        if (SourceScanner.IsSameOrUnder(_source, _target))
            throw new ArchiveException("The source directory cannot be the target directory or inside it.");

        try
        {
            Directory.CreateDirectory(_target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException($"Cannot create target directory {_target}: {ex.Message}", ex);
        }

        _index = Catalog.LoadIndex(_target, _warnings);
        using var catalog = Catalog.Open(_options.CatalogFormat, _target);
        var stats = new ArchiveStats();

        void Record(FileRecord record)
        {
            try
            {
                catalog.Write(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Later runs depend on the catalog, so don't carry on without it.
                throw new ArchiveException($"Cannot write to {catalog.Path}: {ex.Message}", ex);
            }
            stats = stats.Add(record.Status);
        }

        progress?.Report(new ArchiveProgress(true, 0, 0, null, stats));
        var (files, scanFailures) = SourceScanner.Scan(_source, _target, cancellation);
        foreach (var (path, reason) in scanFailures)
            Record(Failed(path, reason));
        if (cancellation.IsCancellationRequested)
            return new ArchiveResult(stats, true, catalog.Path, _warnings);

        var processed = 0;
        progress?.Report(new ArchiveProgress(false, 0, files.Count, null, stats));
        foreach (var file in files)
        {
            if (cancellation.IsCancellationRequested)
                return new ArchiveResult(stats, true, catalog.Path, _warnings);

            FileRecord record;
            try
            {
                record = Process(file);
            }
            catch (Exception ex)
            {
                // One bad file must not stop the rest of the run.
                record = Failed(file, ex.Message);
            }
            Record(record);
            progress?.Report(new ArchiveProgress(false, ++processed, files.Count, record, stats));
        }

        return new ArchiveResult(stats, false, catalog.Path, _warnings);
    }

    private FileRecord Process(string sourceFile)
    {
        var info = new FileInfo(sourceFile);
        var data = File.ReadAllBytes(sourceFile);
        var modified = info.LastWriteTime;
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var metadata = ImageMetadata.Read(data);
        var resolved = PhotoDate.Resolve(metadata.Dates, info.CreationTime, modified, DateTime.Now);
        var relative = Path.GetRelativePath(_source, sourceFile);

        var notes = new List<string>();
        ReadSidecar(sourceFile, metadata, notes);
        if (metadata.ReadError is { } error)
            notes.Add($"metadata not fully readable: {error}");
        notes.AddRange(resolved.Ignored.Select(d => $"ignored implausible {d.Source}"));

        var record = NewRecord(relative);
        record.Size = data.Length;
        record.FileCreated = info.CreationTime.ToString(DateFormat, CultureInfo.InvariantCulture);
        record.FileModified = info.LastWriteTime.ToString(DateFormat, CultureInfo.InvariantCulture);
        record.Sha256 = sha;
        record.ArchiveDate = resolved.Value.ToString(DateFormat, CultureInfo.InvariantCulture);
        record.ArchiveDateSource = resolved.Source;
        record.Metadata = metadata;

        var folder = Path.Combine(_target, resolved.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        IndexFolder(folder);

        var identity = new ContentIdentity(sha, data.Length);
        var origin = ContentIdentity.Parse(metadata["origin_sha256"], metadata["origin_size"]);

        if (FindArchived(identity, origin, modified) is { } archived)
        {
            record.TargetPath = archived.TargetPath;
            record.ExistingSourcePath = archived.SourcePath;
            if (!archived.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                notes.Add("different bytes, but the same original by the stored origin hash");
            if (archived.SourcePath is null)
            {
                record.Status = FileStatus.Skipped;
                notes.Add("origin of archived copy unknown");
            }
            else
            {
                record.Status = string.Equals(archived.SourcePath, relative, StringComparison.OrdinalIgnoreCase)
                    ? FileStatus.Skipped
                    : FileStatus.Duplicate;
            }
            record.Note = NoteText(notes);
            return record;
        }

        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, info.Name);
        record.Status = FileStatus.Copied;
        if (File.Exists(destination))
        {
            var name = $"{Path.GetFileNameWithoutExtension(info.Name)}_{Guid.NewGuid()}{Path.GetExtension(info.Name)}";
            destination = Path.Combine(folder, name);
            record.Status = FileStatus.Renamed;
        }

        WriteCopy(destination, data, resolved.Value);

        record.TargetPath = Path.GetRelativePath(_target, destination);
        record.Note = NoteText(notes);
        _index.Add(new ArchiveEntry(record.TargetPath, sha, data.Length, relative, modified, origin));
        return record;
    }

    /// <summary>The origin in an archivejpg sidecar next to the source goes into the catalog.</summary>
    internal static void ReadSidecar(string sourceFile, ImageMetadata metadata, List<string> notes)
    {
        var sidecar = sourceFile + ".xmp";
        if (!File.Exists(sidecar))
            return;
        try
        {
            metadata.ReadSidecar(File.ReadAllText(sidecar));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"sidecar not readable: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds an archived copy of this photo: the same content, or the same original by the
    /// origin stored with either file, as embedding changes a copy's bytes. When duplicates are
    /// kept the source's modified date has to match too, so the same bytes with another date
    /// count as a new photo. A file of unknown origin has no such date to compare, so the
    /// content alone decides.
    /// </summary>
    private ArchiveEntry? FindArchived(ContentIdentity identity, ContentIdentity? origin, DateTime modified)
    {
        var candidates = origin is null ? _index.Find(identity) : _index.Find(identity).Concat(_index.Find(origin));
        foreach (var entry in candidates)
        {
            // A file removed from the archive is archived again.
            if (!File.Exists(Path.Combine(_target, entry.TargetPath)))
                continue;
            if (_options.Duplicates == DuplicateHandling.Suppress
                || entry.SourceModified is not { } archived
                || (archived - modified).Duration() < ModifiedTolerance)
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Writes under a temporary name first, so an interrupted run can't leave a partial photo
    /// that would later be mistaken for an archived one.
    /// </summary>
    private static void WriteCopy(string destination, byte[] data, DateTime date)
    {
        var temporary = destination + ".partial";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.SetCreationTime(temporary, date);
            File.SetLastWriteTime(temporary, date);
            File.Move(temporary, destination, overwrite: false);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>
    /// The first time a date folder is visited, adds photos the catalog doesn't cover to the
    /// index, so that they are recognised even if the catalog was deleted.
    /// </summary>
    private void IndexFolder(string folder)
    {
        if (!_indexedFolders.Add(folder) || !Directory.Exists(folder))
            return;

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var relative = Path.GetRelativePath(_target, path);
            if (!IsJpeg(path) || _index.Contains(relative))
                continue;
            try
            {
                var data = File.ReadAllBytes(path);
                var metadata = ImageMetadata.Read(data);
                _index.Add(new ArchiveEntry(
                    relative, Convert.ToHexStringLower(SHA256.HashData(data)), data.Length, null, null,
                    ContentIdentity.Parse(metadata["origin_sha256"], metadata["origin_size"])));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warnings.Add($"Cannot read {path}: {ex.Message}");
            }
        }
    }

    private FileRecord NewRecord(string relative) => new()
    {
        RunId = _runId,
        ProcessedAt = DateTime.Now.ToString(DateFormat, CultureInfo.InvariantCulture),
        SourceRoot = _source,
        SourcePath = relative,
    };

    private FileRecord Failed(string path, string reason)
    {
        var record = NewRecord(Path.GetRelativePath(_source, path));
        record.Status = FileStatus.Failed;
        record.Note = reason;
        return record;
    }

    private static string? NoteText(List<string> notes) => notes.Count == 0 ? null : string.Join("; ", notes);

    public static bool IsJpeg(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
