namespace ImageArchiver.Core;

public enum CatalogFormat
{
    /// <summary>catalog.csv in the target root.</summary>
    Csv,

    /// <summary>catalog.db (SQLite) in the target root.</summary>
    Sqlite,
}

public enum DuplicateHandling
{
    /// <summary>Identical content is never stored twice, whatever its modified date.</summary>
    Suppress,

    /// <summary>
    /// Same rules as the Rust archivejpg: a photo is already archived when its SHA-256, size
    /// and the source's modified date (within 2 seconds) match. The same bytes with a different
    /// modified date count as a new photo and are stored under a new name.
    /// </summary>
    Keep,
}

public enum FileStatus
{
    /// <summary>Archived under its original name.</summary>
    Copied,

    /// <summary>Archived as name_uuid.ext, because a different file already had that name.</summary>
    Renamed,

    /// <summary>Already archived from the same relative source path, or of unknown origin.</summary>
    Skipped,

    /// <summary>Already archived from a different source path, so not copied again.</summary>
    Duplicate,

    /// <summary>Could not be processed; the reason is in the note.</summary>
    Failed,
}

public sealed record ArchiveOptions(string SourceDirectory, string TargetDirectory)
{
    public CatalogFormat CatalogFormat { get; init; } = CatalogFormat.Csv;
    public DuplicateHandling Duplicates { get; init; } = DuplicateHandling.Suppress;
}

/// <summary>What happened to one source file. Becomes one row in the catalog.</summary>
public sealed class FileRecord
{
    public required string RunId { get; init; }
    public required string ProcessedAt { get; init; }
    public required string SourceRoot { get; init; }
    public required string SourcePath { get; init; }
    public FileStatus Status { get; set; }
    public string? TargetPath { get; set; }
    public string? ExistingSourcePath { get; set; }
    public long? Size { get; set; }
    public string? FileCreated { get; set; }
    public string? FileModified { get; set; }
    public string? Sha256 { get; set; }
    public string? ArchiveDate { get; set; }
    public string? ArchiveDateSource { get; set; }
    public string? Note { get; set; }
    public ImageMetadata? Metadata { get; set; }
}

public sealed record ArchiveStats(int Copied = 0, int Renamed = 0, int Skipped = 0, int Duplicate = 0, int Failed = 0)
{
    public ArchiveStats Add(FileStatus status) => status switch
    {
        FileStatus.Copied => this with { Copied = Copied + 1 },
        FileStatus.Renamed => this with { Renamed = Renamed + 1 },
        FileStatus.Skipped => this with { Skipped = Skipped + 1 },
        FileStatus.Duplicate => this with { Duplicate = Duplicate + 1 },
        _ => this with { Failed = Failed + 1 },
    };
}

/// <summary>
/// Reported once while scanning (<see cref="Scanning"/> true, total not yet known) and then
/// after every file.
/// </summary>
public sealed record ArchiveProgress(bool Scanning, int Processed, int Total, FileRecord? Last, ArchiveStats Stats);

/// <summary>
/// Calls back directly on the archiving thread, unlike <see cref="Progress{T}"/>, which queues
/// every report on the captured context and has no way to skip any.
/// </summary>
public sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed record ArchiveResult(
    ArchiveStats Stats,
    bool Cancelled,
    string CatalogPath,
    IReadOnlyList<string> Warnings);

/// <summary>The run cannot start or continue, for example because the catalog is locked.</summary>
public sealed class ArchiveException(string message, Exception? inner = null) : Exception(message, inner);
