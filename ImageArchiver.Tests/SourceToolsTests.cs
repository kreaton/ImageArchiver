using System.Security.Cryptography;
using ImageArchiver.Core;

namespace ImageArchiver.Tests;

public sealed class SourceToolsTests : IDisposable
{
    private static readonly DateTime Modified = new(2015, 6, 15, 12, 0, 0);
    private static readonly TestJpeg Photo = new() { DateTimeOriginal = "2010:05:01 10:00:00" };

    private readonly string _root = Directory.CreateTempSubdirectory("imagearchiver-test").FullName;
    private string Source => Path.Combine(_root, "source");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Add(string relative, TestJpeg photo)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, photo.ToBytes());
        File.SetLastWriteTime(path, Modified);
        return path;
    }

    private static TestJpeg EmbeddedCopyOf(TestJpeg original)
    {
        var bytes = original.ToBytes();
        return original with
        {
            RawXmp = $"""
                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:archivejpg="http://ns.archivejpg/1.0/">
                <archivejpg:SourcePath>orig\a.jpg</archivejpg:SourcePath>
                <archivejpg:OriginalSHA256>{Convert.ToHexStringLower(SHA256.HashData(bytes))}</archivejpg:OriginalSHA256>
                <archivejpg:OriginalSize>{bytes.Length}</archivejpg:OriginalSize>
                </rdf:Description></rdf:RDF></x:xmpmeta>
                """,
        };
    }

    /// <summary>Directories by name only: Windows updates their modified date lazily.</summary>
    private static string[] Snapshot(string root) =>
        new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(i => i is FileInfo f ? $"{f.FullName}|{f.Length}|{f.CreationTimeUtc:O}|{f.LastWriteTimeUtc:O}" : i.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Dry_run_groups_duplicates_with_the_kept_file_first()
    {
        Add(@"a\one.jpg", Photo);
        Add(@"b\same.jpg", Photo);
        Add(@"c\copy.jpg", EmbeddedCopyOf(Photo));
        Add(@"d\other.jpg", Photo with { Content = 0x55 });

        var report = SourceTools.FindDuplicates(Source);

        var group = Assert.Single(report.Groups);
        Assert.Equal([@"a\one.jpg", @"b\same.jpg", @"c\copy.jpg"], group.Files);
        Assert.Equal(2, report.DuplicateCount);
        Assert.Equal(4, report.FilesScanned);
        Assert.False(report.Cancelled);
    }

    [Fact]
    public void Dry_run_ignores_modified_dates()
    {
        Add(@"a\one.jpg", Photo);
        File.SetLastWriteTime(Add(@"b\two.jpg", Photo), Modified.AddDays(9));

        Assert.Single(SourceTools.FindDuplicates(Source).Groups);
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        Add(@"a\one.jpg", Photo);
        Add(@"b\two.jpg", Photo);
        var before = Snapshot(_root);

        SourceTools.FindDuplicates(Source);

        Assert.Equal(before, Snapshot(_root));
    }

    [Fact]
    public void Dry_run_predicts_what_an_archive_run_leaves_out()
    {
        Add(@"a\one.jpg", Photo);
        Add(@"b\same.jpg", Photo);
        Add(@"c\copy.jpg", EmbeddedCopyOf(Photo));
        Add(@"d\other.jpg", Photo with { Content = 0x55 });
        Add(@"e\other-copy.jpg", EmbeddedCopyOf(Photo with { Content = 0x55 }));

        var report = SourceTools.FindDuplicates(Source);
        var archived = Archiver.Run(new ArchiveOptions(Source, Path.Combine(_root, "target")));

        Assert.Equal(archived.Stats.Duplicate, report.DuplicateCount);
    }

    [Fact]
    public void Dry_run_without_duplicates_reports_no_groups()
    {
        Add("a.jpg", Photo with { Content = 1 });
        Add("b.jpg", Photo with { Content = 2 });

        Assert.Empty(SourceTools.FindDuplicates(Source).Groups);
    }

    [Fact]
    public void Update_sets_both_source_dates_to_the_smallest_date_and_leaves_the_bytes()
    {
        var path = Add("a.jpg", Photo);
        var bytes = File.ReadAllBytes(path);

        var result = SourceTools.UpdateSourceDates(Source);

        Assert.Equal(1, result.Updated);
        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetCreationTime(path));
        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetLastWriteTime(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Update_converts_gps_to_local_time()
    {
        var path = Add("a.jpg", new TestJpeg { GpsDate = "2009:12:24", GpsTime = (1, 2, 3) });

        SourceTools.UpdateSourceDates(Source);

        Assert.Equal(PhotoDate.ToLocal(new DateTime(2009, 12, 24, 1, 2, 3)), File.GetLastWriteTime(path));
    }

    [Fact]
    public void Update_uses_the_older_file_date_without_metadata()
    {
        var path = Add("a.jpg", new TestJpeg());

        SourceTools.UpdateSourceDates(Source);

        Assert.Equal(Modified, File.GetCreationTime(path));
        Assert.Equal(Modified, File.GetLastWriteTime(path));
    }

    [Fact]
    public void Update_a_second_time_changes_nothing()
    {
        Add("a.jpg", Photo);
        SourceTools.UpdateSourceDates(Source);

        var second = SourceTools.UpdateSourceDates(Source);

        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public void Update_reports_the_old_and_new_dates()
    {
        Add("a.jpg", Photo);
        var details = new List<string>();

        SourceTools.UpdateSourceDates(Source, new DirectProgress<SourceProgress>(p =>
        {
            if (p.Detail is { } detail)
                details.Add(detail);
        }));

        var line = Assert.Single(details);
        Assert.Contains("modified 2015-06-15 12:00:00", line);
        Assert.Contains("-> 2010-05-01 10:00:00", line);
    }

    [Fact]
    public void Archive_after_update_stores_the_same_date()
    {
        Add("a.jpg", Photo);
        SourceTools.UpdateSourceDates(Source);

        var target = Path.Combine(_root, "target");
        Archiver.Run(new ArchiveOptions(Source, target));

        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetLastWriteTime(Path.Combine(target, "2010-05", "a.jpg")));
    }

    [Fact]
    public void Missing_source_is_refused()
    {
        Assert.Throws<ArchiveException>(() => SourceTools.FindDuplicates(Source));
        Assert.Throws<ArchiveException>(() => SourceTools.UpdateSourceDates(Source));
    }

    [Fact]
    public void Cancelling_stops_both()
    {
        var path = Add("a.jpg", Photo);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(SourceTools.FindDuplicates(Source, null, cancellation.Token).Cancelled);
        Assert.True(SourceTools.UpdateSourceDates(Source, null, cancellation.Token).Cancelled);
        Assert.Equal(Modified, File.GetLastWriteTime(path));
    }
}
