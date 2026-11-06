using ImageArchiver.Core;

namespace ImageArchiver.Tests;

public sealed class ArchiverTests : IDisposable
{
    private static readonly DateTime Modified = new(2015, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TestJpeg Photo = new() { DateTimeOriginal = "2010:05:01 10:00:00" };

    private readonly string _root = Directory.CreateTempSubdirectory("imagearchiver-test").FullName;
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AddSource(string relative, TestJpeg photo, DateTime? modified = null) =>
        AddSource(relative, photo.ToBytes(), modified);

    private string AddSource(string relative, byte[] data, DateTime? modified = null)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, modified ?? Modified);
        return path;
    }

    private ArchiveResult Run(
        CatalogFormat format = CatalogFormat.Csv,
        DuplicateHandling duplicates = DuplicateHandling.Suppress,
        CancellationToken cancellation = default) =>
        Archiver.Run(new ArchiveOptions(Source, Target) { CatalogFormat = format, Duplicates = duplicates }, null, cancellation);

    private List<Dictionary<string, string>> CsvRows()
    {
        using var reader = new StreamReader(Path.Combine(Target, Catalog.CsvFileName));
        var rows = CsvCatalog.ReadRows(reader).ToList();
        return rows.Skip(1)
            .Select(row => rows[0].Zip(row).ToDictionary(pair => pair.First, pair => pair.Second))
            .ToList();
    }

    private string[] ArchivedFiles() =>
        Directory.GetFiles(Target, "*.jp*g", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Target, f))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Copies_into_year_month_folder_unchanged_and_dated_by_the_smallest_date()
    {
        var source = AddSource(@"sub\deep\a.jpg", Photo);
        var sourceCreated = File.GetCreationTimeUtc(source);

        var result = Run();

        Assert.Equal(1, result.Stats.Copied);
        var copy = Path.Combine(Target, "2010-05", "a.jpg");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetLastWriteTime(copy));
        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetCreationTime(copy));
        Assert.Equal(Modified, File.GetLastWriteTimeUtc(source));
        Assert.Equal(sourceCreated, File.GetCreationTimeUtc(source));
    }

    [Fact]
    public void Folder_and_file_dates_come_from_the_gps_date_in_local_time_when_smallest()
    {
        AddSource("a.jpg", Photo with { GpsDate = "2009:12:24", GpsTime = (1, 2, 3) });

        Run();

        var local = PhotoDate.ToLocal(new DateTime(2009, 12, 24, 1, 2, 3));
        var row = Assert.Single(CsvRows());
        Assert.Equal(@"2009-12\a.jpg", row["target_path"]);
        Assert.Equal("gps_date_time_local", row["archive_date_source"]);
        Assert.Equal(local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), row["archive_date"]);
        Assert.Equal("2009-12-24 01:02:03", row["gps_date_time_utc"]);
        Assert.Equal("2010:05:01 10:00:00", row["exif_date_time_original"]);
        Assert.Equal(local, File.GetLastWriteTime(Path.Combine(Target, row["target_path"])));
    }

    [Fact]
    public void File_date_older_than_the_metadata_decides_the_folder()
    {
        AddSource("a.jpg", Photo, new DateTime(2005, 3, 15, 12, 0, 0, DateTimeKind.Utc));

        Run();

        var row = Assert.Single(CsvRows());
        Assert.Equal(PhotoDate.FileModifiedSource, row["archive_date_source"]);
        Assert.Equal(@"2005-03\a.jpg", row["target_path"]);
    }

    [Fact]
    public void Origin_in_a_sidecar_next_to_the_source_goes_into_the_catalog()
    {
        var source = AddSource("a.jpg", Photo);
        File.WriteAllText(source + ".xmp", """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:archivejpg="http://ns.archivejpg/1.0/">
            <archivejpg:SourcePath>Ærø\a.jpg</archivejpg:SourcePath>
            <archivejpg:OriginalSHA256>ab12</archivejpg:OriginalSHA256>
            <archivejpg:OriginalSize>42</archivejpg:OriginalSize>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            """);

        Run();

        var row = Assert.Single(CsvRows());
        Assert.Equal("xmpsidecar", row["origin_method"]);
        Assert.Equal(@"Ærø\a.jpg", row["origin_source_path"]);
        Assert.Equal("ab12", row["origin_sha256"]);
        Assert.Empty(Directory.GetFiles(Target, "*.xmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Csv_catalog_from_an_earlier_version_gets_the_new_columns()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(
            Path.Combine(Target, Catalog.CsvFileName),
            "run_id,status,source_path,target_path,size,file_modified,sha256,retired\r\n" +
            "1,copied,\"old, one.jpg\",2001-01\\old.jpg,5,2001-01-01 00:00:00,abc,kept\r\n");
        AddSource("a.jpg", Photo);

        Run();

        var rows = CsvRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal("old, one.jpg", rows[0]["source_path"]);
        Assert.Equal("kept", rows[0]["retired"]);
        Assert.Equal("", rows[0]["origin_method"]);
        Assert.Equal("a.jpg", rows[1]["source_path"]);
        Assert.Equal(@"2010-05\a.jpg", rows[1]["target_path"]);
    }

    [Fact]
    public void Sqlite_catalog_from_an_earlier_version_gets_the_new_columns()
    {
        Directory.CreateDirectory(Target);
        var path = Path.Combine(Target, Catalog.SqliteFileName);
        var connectionString = $"Data Source={path};Pooling=False";
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE files (id INTEGER PRIMARY KEY, status TEXT, source_path TEXT, target_path TEXT, size INTEGER, file_modified TEXT, sha256 TEXT)";
            create.ExecuteNonQuery();
        }
        AddSource("a.jpg", Photo);

        Assert.Equal(1, Run(CatalogFormat.Sqlite).Stats.Copied);

        using var check = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT archive_date_source FROM files";
        Assert.Equal("exif_date_time_original", query.ExecuteScalar());
    }

    [Fact]
    public void Both_extensions_in_any_case_are_archived_and_others_ignored()
    {
        AddSource("a.JPG", Photo with { Content = 1 });
        AddSource("b.jpeg", Photo with { Content = 2 });
        AddSource("c.png", Photo with { Content = 3 });

        Assert.Equal(2, Run().Stats.Copied);
    }

    [Fact]
    public void File_without_metadata_is_archived_by_its_file_date()
    {
        AddSource("broken.jpg", [1, 2, 3, 4]);

        var result = Run();

        Assert.Equal(1, result.Stats.Copied);
        var row = Assert.Single(CsvRows());
        Assert.Equal(PhotoDate.FileModifiedSource, row["archive_date_source"]);
        Assert.StartsWith(Modified.ToLocalTime().ToString("yyyy-MM"), row["target_path"]);
        Assert.Contains("metadata not fully readable", row["note"]);
    }

    [Fact]
    public void Different_photo_with_the_same_name_is_renamed_not_overwritten()
    {
        AddSource(@"x\a.jpg", Photo with { Content = 1 });
        AddSource(@"y\a.jpg", Photo with { Content = 2 });

        var result = Run();

        Assert.Equal(1, result.Stats.Copied);
        Assert.Equal(1, result.Stats.Renamed);
        Assert.Equal(2, ArchivedFiles().Length);
        Assert.Matches(@"^2010-05\\a_[0-9a-f-]{36}\.jpg$", ArchivedFiles()[1]);
    }

    [Theory]
    [InlineData(CatalogFormat.Csv, DuplicateHandling.Suppress)]
    [InlineData(CatalogFormat.Sqlite, DuplicateHandling.Suppress)]
    [InlineData(CatalogFormat.Csv, DuplicateHandling.Keep)]
    [InlineData(CatalogFormat.Sqlite, DuplicateHandling.Keep)]
    public void Second_run_skips_what_is_already_archived(CatalogFormat format, DuplicateHandling duplicates)
    {
        AddSource("a.jpg", Photo);

        Assert.Equal(1, Run(format, duplicates).Stats.Copied);
        var second = Run(format, duplicates);

        Assert.Equal(new ArchiveStats(Skipped: 1), second.Stats);
        Assert.Single(ArchivedFiles());
    }

    /// <summary>A photo as archivejpg --xmpembed leaves it: other bytes, with the original's identity inside.</summary>
    private static TestJpeg EmbeddedCopyOf(TestJpeg original, string? sha256 = null, long? size = null)
    {
        var bytes = original.ToBytes();
        return original with
        {
            RawXmp = $"""
                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:archivejpg="http://ns.archivejpg/1.0/">
                <archivejpg:SourcePath>orig\a.jpg</archivejpg:SourcePath>
                <archivejpg:OriginalSHA256>{sha256 ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}</archivejpg:OriginalSHA256>
                <archivejpg:OriginalSize>{size ?? bytes.Length}</archivejpg:OriginalSize>
                </rdf:Description></rdf:RDF></x:xmpmeta>
                """,
        };
    }

    [Fact]
    public void Untouched_original_after_its_embedded_copy_is_a_duplicate()
    {
        AddSource(@"a\copy.jpg", EmbeddedCopyOf(Photo));
        AddSource(@"b\original.jpg", Photo);

        var result = Run();

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), result.Stats);
        Assert.Equal([@"2010-05\copy.jpg"], ArchivedFiles());
        var duplicate = CsvRows().Single(r => r["status"] == "duplicate");
        Assert.Equal(@"a\copy.jpg", duplicate["existing_source_path"]);
        Assert.Contains("stored origin", duplicate["note"]);
    }

    [Fact]
    public void Embedded_copy_after_its_untouched_original_is_a_duplicate()
    {
        AddSource(@"a\original.jpg", Photo);
        AddSource(@"b\copy.jpg", EmbeddedCopyOf(Photo));

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), Run().Stats);
        Assert.Equal([@"2010-05\original.jpg"], ArchivedFiles());
    }

    [Fact]
    public void Two_different_embedded_copies_of_one_original_are_duplicates()
    {
        var original = EmbeddedCopyOf(Photo);
        AddSource(@"a\one.jpg", original);
        AddSource(@"b\two.jpg", original with { Content = 0x55 });

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), Run().Stats);
    }

    [Fact]
    public void Origin_in_a_sidecar_matches_too()
    {
        var plain = Photo with { Content = 0x77 };
        AddSource(@"a\original.jpg", plain);
        var copy = AddSource(@"b\changed.jpg", Photo with { Content = 0x78 });
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(plain.ToBytes()));
        File.WriteAllText(copy + ".xmp", $"""<rdf:Description archivejpg:SourcePath="a.jpg" archivejpg:OriginalSHA256="{hash}" archivejpg:OriginalSize="{plain.ToBytes().Length}"/>""");

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), Run().Stats);
    }

    [Theory]
    [InlineData(CatalogFormat.Csv)]
    [InlineData(CatalogFormat.Sqlite)]
    public void Origin_is_remembered_across_runs(CatalogFormat format)
    {
        AddSource(@"a\copy.jpg", EmbeddedCopyOf(Photo));
        Run(format);
        AddSource(@"b\original.jpg", Photo);

        Assert.Equal(new ArchiveStats(Skipped: 1, Duplicate: 1), Run(format).Stats);
        Assert.Single(ArchivedFiles());
    }

    [Fact]
    public void Origin_is_read_from_the_archived_file_without_a_catalog()
    {
        AddSource(@"a\copy.jpg", EmbeddedCopyOf(Photo));
        Run();
        File.Delete(Path.Combine(Target, Catalog.CsvFileName));
        Directory.Delete(Path.Combine(Source, "a"), recursive: true);
        AddSource(@"b\original.jpg", Photo);

        Assert.Equal(new ArchiveStats(Skipped: 1), Run().Stats);
        Assert.Single(ArchivedFiles());
    }

    [Theory]
    [InlineData("ab12", null)]                                                                 // Not a hash.
    [InlineData("zz12345678901234567890123456789012345678901234567890123456789012", null)]     // Not hex.
    [InlineData(null, 1L)]                                                                     // The right hash, the wrong size.
    public void Origin_that_does_not_really_identify_the_original_does_not_match(string? hash, long? size)
    {
        AddSource(@"a\original.jpg", Photo);
        AddSource(@"b\copy.jpg", EmbeddedCopyOf(Photo, hash, size));

        Assert.Equal(new ArchiveStats(Copied: 2), Run().Stats);
    }

    [Fact]
    public void Keeping_duplicates_still_compares_the_date_for_origin_matches()
    {
        AddSource(@"a\original.jpg", Photo);
        AddSource(@"b\same-date.jpg", EmbeddedCopyOf(Photo));
        AddSource(@"c\other-date.jpg", EmbeddedCopyOf(Photo), Modified.AddDays(3));

        var result = Run(duplicates: DuplicateHandling.Keep);

        Assert.Equal(1, result.Stats.Duplicate);
        Assert.Equal(2, ArchivedFiles().Length);
    }

    [Fact]
    public void Suppressing_is_the_default()
    {
        Assert.Equal(DuplicateHandling.Suppress, new ArchiveOptions("a", "b").Duplicates);
    }

    [Fact]
    public void Keeping_duplicates_compares_the_source_date_across_runs()
    {
        AddSource(@"x\a.jpg", Photo);
        Run(duplicates: DuplicateHandling.Keep);
        AddSource(@"y\a.jpg", Photo, Modified.AddDays(3));

        var second = Run(duplicates: DuplicateHandling.Keep);

        Assert.Equal(new ArchiveStats(Renamed: 1, Skipped: 1), second.Stats);
    }

    [Fact]
    public void Keeping_duplicates_skips_a_file_of_unknown_origin_by_its_content()
    {
        AddSource("a.jpg", Photo);
        Run(duplicates: DuplicateHandling.Keep);
        File.Delete(Path.Combine(Target, Catalog.CsvFileName));

        Assert.Equal(new ArchiveStats(Skipped: 1), Run(duplicates: DuplicateHandling.Keep).Stats);
    }

    [Fact]
    public void Switching_catalog_format_still_recognises_earlier_files()
    {
        AddSource(@"x\a.jpg", Photo);
        Run(CatalogFormat.Csv);
        AddSource(@"y\b.jpg", Photo);

        var second = Run(CatalogFormat.Sqlite);

        Assert.Equal(new ArchiveStats(Skipped: 1, Duplicate: 1), second.Stats);
    }

    [Fact]
    public void Same_bytes_and_date_elsewhere_is_a_duplicate_and_not_copied()
    {
        AddSource(@"x\a.jpg", Photo);
        AddSource(@"y\a-copy.jpg", Photo);

        var result = Run();

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), result.Stats);
        Assert.Single(ArchivedFiles());
        var duplicate = CsvRows().Single(r => r["status"] == "duplicate");
        Assert.Equal(@"x\a.jpg", duplicate["existing_source_path"]);
        Assert.Equal(@"2010-05\a.jpg", duplicate["target_path"]);
    }

    [Fact]
    public void Keeping_duplicates_treats_same_bytes_with_another_date_as_a_new_photo()
    {
        AddSource(@"x\a.jpg", Photo);
        AddSource(@"y\a.jpg", Photo, Modified.AddDays(3));

        var result = Run(duplicates: DuplicateHandling.Keep);

        Assert.Equal(new ArchiveStats(Copied: 1, Renamed: 1), result.Stats);
        Assert.Equal(2, ArchivedFiles().Length);
    }

    [Fact]
    public void Suppressing_stores_identical_content_once_whatever_its_date()
    {
        AddSource(@"x\a.jpg", Photo);
        AddSource(@"y\a.jpg", Photo, Modified.AddDays(3));

        var result = Run(duplicates: DuplicateHandling.Suppress);

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), result.Stats);
        Assert.Single(ArchivedFiles());
    }

    [Fact]
    public void Modified_dates_within_two_seconds_count_as_the_same()
    {
        AddSource(@"x\a.jpg", Photo);
        AddSource(@"y\b.jpg", Photo, Modified.AddSeconds(1));

        Assert.Equal(new ArchiveStats(Copied: 1, Duplicate: 1), Run(duplicates: DuplicateHandling.Keep).Stats);
    }

    [Fact]
    public void File_removed_from_the_archive_is_archived_again()
    {
        AddSource("a.jpg", Photo);
        Run();
        File.Delete(Path.Combine(Target, "2010-05", "a.jpg"));

        Assert.Equal(1, Run().Stats.Copied);
    }

    [Fact]
    public void Without_a_catalog_files_in_the_folder_are_skipped_with_unknown_origin()
    {
        AddSource("a.jpg", Photo);
        Run();
        File.Delete(Path.Combine(Target, Catalog.CsvFileName));

        var second = Run();

        Assert.Equal(new ArchiveStats(Skipped: 1), second.Stats);
        Assert.Contains("origin", Assert.Single(CsvRows())["note"]);
    }

    [Fact]
    public void Archive_inside_the_source_is_left_out_of_the_scan()
    {
        AddSource("a.jpg", Photo);
        var options = new ArchiveOptions(Source, Path.Combine(Source, "archive"));

        Assert.Equal(1, Archiver.Run(options).Stats.Copied);
        Assert.Equal(new ArchiveStats(Skipped: 1), Archiver.Run(options).Stats);
    }

    [Fact]
    public void Source_inside_target_or_missing_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(Target, "sub"));
        Assert.Throws<ArchiveException>(() => Archiver.Run(new ArchiveOptions(Path.Combine(Target, "sub"), Target)));
        Assert.Throws<ArchiveException>(() => Archiver.Run(new ArchiveOptions(Target, Target)));
        Assert.Throws<ArchiveException>(() => Archiver.Run(new ArchiveOptions(Source, Target)));
    }

    [Fact]
    public void Locked_csv_catalog_stops_the_run_before_copying()
    {
        AddSource("a.jpg", Photo);
        Directory.CreateDirectory(Target);
        using var locked = new FileStream(Path.Combine(Target, Catalog.CsvFileName), FileMode.Create, FileAccess.Write, FileShare.Read);

        var ex = Assert.Throws<ArchiveException>(() => Run());

        Assert.Contains("open in another program", ex.Message);
        Assert.Empty(ArchivedFiles());
    }

    [Fact]
    public void Names_needing_quoting_survive_the_csv_round_trip()
    {
        const string name = "Ærø, Søby å.jpg";
        AddSource(name, Photo);

        Run();
        Assert.Equal(new ArchiveStats(Skipped: 1), Run().Stats);

        Assert.Equal(name, CsvRows()[0]["source_path"]);
        Assert.Equal(name, CsvRows()[1]["source_path"]);
    }

    [Fact]
    public void Csv_reader_handles_quotes_and_line_breaks_in_values()
    {
        var rows = CsvCatalog.ReadRows(new StringReader("a,\"b,\"\"c\"\"\r\nd\",\r\ne,f")).ToList();

        Assert.Equal(["a", "b,\"c\"\r\nd", ""], rows[0]);
        Assert.Equal(["e", "f"], rows[1]);
    }

    [Fact]
    public void Csv_starts_with_one_bom_and_one_header_across_runs()
    {
        AddSource("a.jpg", Photo);
        Run();
        Run();

        var bytes = File.ReadAllBytes(Path.Combine(Target, Catalog.CsvFileName));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(1, System.Text.Encoding.UTF8.GetString(bytes).Split("run_id,").Length - 1);
    }

    [Fact]
    public void Cancelling_stops_and_says_so()
    {
        AddSource("a.jpg", Photo);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = Run(cancellation: cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(ArchivedFiles());
    }

    [Fact]
    public void Progress_counts_up_to_the_total()
    {
        AddSource("a.jpg", Photo with { Content = 1 });
        AddSource("b.jpg", Photo with { Content = 2 });
        var reports = new List<ArchiveProgress>();

        Archiver.Run(new ArchiveOptions(Source, Target), new DirectProgress<ArchiveProgress>(reports.Add));

        Assert.True(reports[0].Scanning);
        Assert.Equal(2, reports[^1].Processed);
        Assert.Equal(2, reports[^1].Total);
    }
}
