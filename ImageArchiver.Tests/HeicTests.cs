using ImageArchiver.Core;

namespace ImageArchiver.Tests;

public sealed class HeicTests : IDisposable
{
    private static readonly DateTime Modified = new(2015, 6, 15, 12, 0, 0);
    private static readonly TestJpeg Photo = new() { DateTimeOriginal = "2010:05:01 10:00:00" };

    private readonly string _root = Directory.CreateTempSubdirectory("imagearchiver-test").FullName;
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Add(string relative, byte[] bytes)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTime(path, Modified);
        return path;
    }

    [Theory]
    [InlineData("a.heic")]
    [InlineData("a.HEIC")]
    [InlineData("a.heif")]
    public void Archives_by_the_exif_date_and_keeps_the_bytes(string name)
    {
        var bytes = Photo.ToHeicBytes();
        Add(name, bytes);

        var result = Archiver.Run(new ArchiveOptions(Source, Target));

        Assert.Equal(1, result.Stats.Copied);
        var copy = Path.Combine(Target, "2010-05", name);
        Assert.Equal(bytes, File.ReadAllBytes(copy));
        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetLastWriteTime(copy));
    }

    [Fact]
    public void Reads_the_same_fields_as_from_a_jpeg()
    {
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            DateTimeOriginal = "2010:05:01 10:00:00",
            OffsetTimeOriginal = "+02:00",
            GpsDate = "2010:05:01",
            GpsTime = (8, 0, 0),
            XmpDateCreated = "2009-01-02T03:04:05",
        }.ToHeicBytes());

        Assert.Null(metadata.ReadError);
        Assert.Equal("2010:05:01 10:00:00", metadata["exif_date_time_original"]);
        Assert.Equal("+02:00", metadata["exif_offset_time_original"]);
        Assert.Equal("2010-05-01 10:00:00", metadata["gps_date_time_local"]);
        Assert.Equal("2009-01-02T03:04:05", metadata["xmp_date_created"]);
        Assert.Contains(metadata.Dates, d => d.Source == "xmp_date_created");
    }

    [Fact]
    public void Size_is_the_photo_rather_than_the_thumbnail()
    {
        var metadata = ImageMetadata.Read(Photo.ToHeicBytes(4032, 3024));

        Assert.Equal("4032", metadata["width"]);
        Assert.Equal("3024", metadata["height"]);
    }

    [Fact]
    public void Identical_files_are_duplicates()
    {
        Add(@"a\one.heic", Photo.ToHeicBytes());
        Add(@"b\two.heic", Photo.ToHeicBytes());

        Assert.Equal(1, Archiver.Run(new ArchiveOptions(Source, Target)).Stats.Duplicate);
        Assert.Single(SourceTools.FindDuplicates(Source).Groups);
    }

    [Fact]
    public void A_heic_and_a_jpeg_of_one_photo_are_both_kept()
    {
        Add("a.heic", Photo.ToHeicBytes());
        Add("a.jpg", Photo.ToBytes());

        var result = Archiver.Run(new ArchiveOptions(Source, Target));

        Assert.Equal(2, result.Stats.Copied);
        Assert.True(File.Exists(Path.Combine(Target, "2010-05", "a.heic")));
        Assert.True(File.Exists(Path.Combine(Target, "2010-05", "a.jpg")));
    }

    [Fact]
    public void Already_archived_is_recognised_on_a_second_run_and_without_the_catalog()
    {
        Add("a.heic", Photo.ToHeicBytes());
        Archiver.Run(new ArchiveOptions(Source, Target));

        Assert.Equal(1, Archiver.Run(new ArchiveOptions(Source, Target)).Stats.Skipped);

        File.Delete(Path.Combine(Target, "catalog.csv"));
        var result = Archiver.Run(new ArchiveOptions(Source, Target));
        Assert.Equal(0, result.Stats.Copied + result.Stats.Renamed);
    }

    [Fact]
    public void Update_sets_the_source_dates()
    {
        var path = Add("a.heic", Photo.ToHeicBytes());

        Assert.Equal(1, SourceTools.UpdateSourceDates(Source).Updated);

        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetCreationTime(path));
        Assert.Equal(new DateTime(2010, 5, 1, 10, 0, 0), File.GetLastWriteTime(path));
    }

    [Fact]
    public void The_content_decides_the_format_rather_than_the_extension()
    {
        Add("really-heic.jpg", Photo.ToHeicBytes());
        Add("really-jpeg.heic", Photo.ToBytes());

        var result = Archiver.Run(new ArchiveOptions(Source, Target));

        Assert.Equal(2, result.Stats.Copied);
        Assert.True(File.Exists(Path.Combine(Target, "2010-05", "really-heic.jpg")));
        Assert.True(File.Exists(Path.Combine(Target, "2010-05", "really-jpeg.heic")));
    }

    [Fact]
    public void Bad_xmp_keeps_the_exif_date()
    {
        var metadata = ImageMetadata.Read((Photo with { RawXmp = "<x:xmpmeta this is not xml" }).ToHeicBytes());

        Assert.Equal("2010:05:01 10:00:00", metadata["exif_date_time_original"]);
    }

    [Fact]
    public void Stray_bytes_before_the_xmp_packet_lose_nothing()
    {
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            DateTimeOriginal = "2010:05:01 10:00:00",
            XmpDateCreated = "1953-07-14",
            XmpPrefix = [0x00, (byte)'q', 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        }.ToHeicBytes());

        Assert.Null(metadata.ReadError);
        Assert.Equal("2010:05:01 10:00:00", metadata["exif_date_time_original"]);
        Assert.Equal("1953-07-14", metadata["xmp_date_created"]);
    }

    [Fact]
    public void Unreadable_xmp_is_reported_and_exif_dates_survive()
    {
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            DateTimeOriginal = "2010:05:01 10:00:00",
            RawXmp = "not xml at all",
            XmpPrefix = [0x00, (byte)'q', 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        }.ToHeicBytes());

        Assert.Equal("2010:05:01 10:00:00", metadata["exif_date_time_original"]);
        Assert.Single(metadata.Dates);
        Assert.NotNull(metadata.ReadError);
    }

    [Fact]
    public void A_damaged_file_is_archived_by_its_file_date()
    {
        Add("broken.heic", [0, 0, 0, 24, .. "ftypheic"u8, 1, 2, 3]);

        var result = Archiver.Run(new ArchiveOptions(Source, Target));

        Assert.Equal(1, result.Stats.Copied);
        Assert.True(File.Exists(Path.Combine(Target, "2015-06", "broken.heic")));
    }
}
