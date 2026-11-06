using ImageArchiver.Core;

namespace ImageArchiver.Tests;

public class PhotoDateTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0);
    private static readonly DateTime FileCreated = new(2026, 1, 2);
    private static readonly DateTime FileModified = new(2025, 3, 4);

    private static ResolvedDate Resolve(params DateCandidate[] candidates) =>
        PhotoDate.Resolve(candidates, FileCreated, FileModified, Now);

    [Fact]
    public void Oldest_metadata_date_wins()
    {
        var result = Resolve(
            new("exif_date_time_original", new DateTime(2010, 5, 1)),
            new("exif_date_time", new DateTime(2008, 2, 1)),
            new("xmp_modify_date", new DateTime(2020, 1, 1)));

        Assert.Equal(new DateTime(2008, 2, 1), result.Value);
        Assert.Equal("exif_date_time", result.Source);
    }

    [Fact]
    public void Tie_goes_to_the_first_listed_source()
    {
        var date = new DateTime(2010, 5, 1);
        Assert.Equal("exif_date_time_original", Resolve(new("exif_date_time_original", date), new("exif_date_time", date)).Source);
    }

    [Fact]
    public void Smallest_date_wins_even_by_a_second()
    {
        var result = Resolve(
            new("exif_date_time_original", new DateTime(2024, 6, 1, 0, 30, 5)),
            new("gps_date_time_local", new DateTime(2024, 6, 1, 0, 30, 0)));

        Assert.Equal("gps_date_time_local", result.Source);
    }

    [Fact]
    public void File_date_older_than_the_metadata_wins()
    {
        var result = PhotoDate.Resolve(
            [new DateCandidate("exif_date_time_original", new DateTime(2020, 1, 1))],
            new DateTime(2026, 1, 1), new DateTime(2019, 3, 4), Now);

        Assert.Equal(PhotoDate.FileModifiedSource, result.Source);
        Assert.Equal(new DateTime(2019, 3, 4), result.Value);
    }

    [Fact]
    public void Metadata_wins_a_tie_with_a_file_date()
    {
        var date = new DateTime(2020, 1, 1);
        var result = PhotoDate.Resolve([new DateCandidate("exif_date_time", date)], date, date, Now);

        Assert.Equal("exif_date_time", result.Source);
    }

    [Fact]
    public void Implausible_file_date_is_passed_over()
    {
        var result = PhotoDate.Resolve(
            [new DateCandidate("exif_date_time", new DateTime(2020, 1, 1))],
            new DateTime(2026, 1, 1), new DateTime(1980, 1, 1), Now);

        Assert.Equal("exif_date_time", result.Source);
    }

    [Fact]
    public void Utc_converts_with_the_daylight_saving_of_that_date()
    {
        var zone = TimeZoneInfo.Local;
        var winter = new DateTime(2024, 1, 15, 12, 0, 0);
        var summer = new DateTime(2024, 7, 15, 12, 0, 0);

        Assert.Equal(winter + zone.GetUtcOffset(DateTime.SpecifyKind(winter, DateTimeKind.Utc)), PhotoDate.ToLocal(winter));
        Assert.Equal(summer + zone.GetUtcOffset(DateTime.SpecifyKind(summer, DateTimeKind.Utc)), PhotoDate.ToLocal(summer));
    }

    [Theory]
    [InlineData(1970, 1, 1)]
    [InlineData(1980, 1, 1)]
    [InlineData(1904, 1, 1)]
    [InlineData(1601, 1, 1)]
    [InlineData(1799, 6, 1)]
    [InlineData(2030, 1, 1)]
    public void Implausible_dates_are_ignored_and_reported(int year, int month, int day)
    {
        var result = Resolve(
            new("exif_date_time", new DateTime(year, month, day)),
            new("exif_date_time_original", new DateTime(2010, 5, 1)));

        Assert.Equal(new DateTime(2010, 5, 1), result.Value);
        Assert.Equal("exif_date_time", Assert.Single(result.Ignored).Source);
    }

    [Fact]
    public void Old_scans_keep_their_historic_date()
    {
        Assert.Equal(1936, Resolve(new DateCandidate("xmp_date_created", new DateTime(1936, 1, 1))).Value.Year);
    }

    [Fact]
    public void Older_file_date_is_used_without_metadata()
    {
        var result = Resolve();
        Assert.Equal(FileModified, result.Value);
        Assert.Equal(PhotoDate.FileModifiedSource, result.Source);

        var created = PhotoDate.Resolve([], new DateTime(2001, 1, 2), FileModified, Now);
        Assert.Equal(PhotoDate.FileCreatedSource, created.Source);
    }

    [Theory]
    [InlineData("2004:09:25 11:28:37", 2004, 9, 25, 11, 28, 37, false)]
    [InlineData("2004-09-25T11:28:37+02:00", 2004, 9, 25, 11, 28, 37, false)]
    [InlineData("2004-09-25T11:28:37.25Z", 2004, 9, 25, 11, 28, 37, true)]
    [InlineData("2004-09-25T11:28", 2004, 9, 25, 11, 28, 0, false)]
    [InlineData("20040925", 2004, 9, 25, 0, 0, 0, false)]
    [InlineData("20040925 112837+0200", 2004, 9, 25, 11, 28, 37, false)]
    [InlineData("2004-09", 2004, 9, 1, 0, 0, 0, false)]
    [InlineData("1936", 1936, 1, 1, 0, 0, 0, false)]
    [InlineData("2004:09:25 11:28:37\0 ", 2004, 9, 25, 11, 28, 37, false)]
    public void Parses_metadata_date_forms(string text, int y, int mo, int d, int h, int mi, int s, bool utc)
    {
        Assert.True(PhotoDate.TryParse(text, out var value, out var isUtc));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), value);
        Assert.Equal(utc, isUtc);
    }

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("    :  :     :  :  ")]
    [InlineData("2004:13:01 00:00:00")]
    [InlineData("2004:02:30 00:00:00")]
    [InlineData("2004:09:25 24:00:00")]
    [InlineData("not a date")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_invalid_dates(string? text)
    {
        Assert.False(PhotoDate.TryParse(text, out _, out _));
    }
}

public class ImageMetadataTests
{
    [Fact]
    public void Reads_all_date_sources()
    {
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            DateTimeOriginal = "2010:05:01 10:00:00",
            DateTimeDigitized = "2010:05:02 10:00:00",
            DateTime = "2011:01:01 10:00:00",
            GpsDate = "2010:04:30",
            GpsTime = (8, 15, 30),
            XmpDateCreated = "2009-07-14",
        }.ToBytes());

        Assert.Null(metadata.ReadError);
        Assert.Equal("2010:05:01 10:00:00", metadata["exif_date_time_original"]);
        Assert.Equal("2010:05:02 10:00:00", metadata["exif_date_time_digitized"]);
        Assert.Equal("2011:01:01 10:00:00", metadata["exif_date_time"]);
        Assert.Equal("2010-04-30 08:15:30", metadata["gps_date_time_utc"]);
        Assert.Equal("2009-07-14", metadata["xmp_date_created"]);
        Assert.Equal(5, metadata.Dates.Count);
    }

    [Fact]
    public void Gps_date_converts_to_this_machines_time_zone()
    {
        var metadata = ImageMetadata.Read(new TestJpeg { GpsDate = "2024:05:31", GpsTime = (22, 30, 0) }.ToBytes());

        var expected = PhotoDate.ToLocal(new DateTime(2024, 5, 31, 22, 30, 0));
        Assert.Equal(expected, metadata.Dates.Single(d => d.Source == "gps_date_time_local").Value);
        Assert.Equal("2024-05-31 22:30:00", metadata["gps_date_time_utc"]);
        Assert.Equal(expected.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), metadata["gps_date_time_local"]);
    }

    [Fact]
    public void Photos_own_offset_is_preferred_for_the_gps_date()
    {
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            DateTimeOriginal = "2024:06:01 00:30:00",
            OffsetTimeOriginal = "+02:00",
            GpsDate = "2024:05:31",
            GpsTime = (22, 30, 0),
        }.ToBytes());

        Assert.Equal(new DateTime(2024, 6, 1, 0, 30, 0), metadata.Dates.Single(d => d.Source == "gps_date_time_local").Value);
    }

    [Fact]
    public void Gps_date_without_a_time_is_not_shifted()
    {
        var metadata = ImageMetadata.Read(new TestJpeg { GpsDate = "2024:05:31" }.ToBytes());

        Assert.Equal(new DateTime(2024, 5, 31), metadata.Dates.Single().Value);
    }

    [Fact]
    public void Utc_xmp_date_converts_to_local_time()
    {
        var metadata = ImageMetadata.Read(new TestJpeg { XmpDateCreated = "2024-05-31T22:30:00Z" }.ToBytes());

        Assert.Equal(PhotoDate.ToLocal(new DateTime(2024, 5, 31, 22, 30, 0)), metadata.Dates.Single().Value);
    }

    private const string OriginXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:archivejpg="http://ns.archivejpg/1.0/">
        <archivejpg:SourcePath>Ferie\Ærø &amp; Søby\å.jpg</archivejpg:SourcePath>
        <archivejpg:SourceRoot>C:\photos</archivejpg:SourceRoot>
        <archivejpg:OriginalSHA256>ab12</archivejpg:OriginalSHA256>
        <archivejpg:OriginalSize>42</archivejpg:OriginalSize>
        <archivejpg:ArchivedAt>2026-10-15T12:00:00+02:00</archivejpg:ArchivedAt>
        </rdf:Description></rdf:RDF></x:xmpmeta>
        """;

    [Fact]
    public void Reads_the_origin_embedded_as_xmp()
    {
        var metadata = ImageMetadata.Read(new TestJpeg { RawXmp = OriginXmp }.ToBytes());

        Assert.Equal("xmpembed", metadata["origin_method"]);
        Assert.Equal(@"Ferie\Ærø & Søby\å.jpg", metadata["origin_source_path"]);
        Assert.Equal(@"C:\photos", metadata["origin_source_root"]);
        Assert.Equal("ab12", metadata["origin_sha256"]);
        Assert.Equal("42", metadata["origin_size"]);
        Assert.Equal("2026-10-15T12:00:00+02:00", metadata["origin_archived_at"]);
    }

    [Fact]
    public void Reads_the_origin_from_a_sidecar_in_attribute_form()
    {
        var metadata = ImageMetadata.Read(new TestJpeg().ToBytes());
        metadata.ReadSidecar("""<rdf:Description archivejpg:SourcePath="a.jpg" archivejpg:OriginalSHA256="ab" archivejpg:OriginalSize="1"/>""");

        Assert.Equal("xmpsidecar", metadata["origin_method"]);
        Assert.Equal("a.jpg", metadata["origin_source_path"]);
        Assert.Null(metadata["origin_source_root"]);
    }

    [Fact]
    public void Sidecar_of_another_program_is_no_origin()
    {
        var metadata = ImageMetadata.Read(new TestJpeg().ToBytes());
        metadata.ReadSidecar("""<x:xmpmeta xmlns:x="adobe:ns:meta/"><dc:source>a.jpg</dc:source></x:xmpmeta>""");

        Assert.Null(metadata["origin_method"]);
    }

    [Theory]
    [InlineData(@"Holiday\a.jpg")]
    [InlineData(@"Ferie\Ærø\å.jpg")]
    public void Reads_the_origin_from_the_exif_user_comment(string path)
    {
        var text = $"archivejpg|source={path}|sha256=ab12|size=42";
        byte[] comment = System.Text.Ascii.IsValid(text)
            ? [.. "ASCII\0\0\0"u8, .. System.Text.Encoding.ASCII.GetBytes(text)]
            : [.. "UNICODE\0"u8, .. System.Text.Encoding.Unicode.GetBytes(text)];

        var metadata = ImageMetadata.Read(new TestJpeg { UserComment = comment }.ToBytes());

        Assert.Equal("exifembed", metadata["origin_method"]);
        Assert.Equal(path, metadata["origin_source_path"]);
        Assert.Equal("ab12", metadata["origin_sha256"]);
        Assert.Equal("42", metadata["origin_size"]);
    }

    [Fact]
    public void Someone_elses_user_comment_is_no_origin()
    {
        var metadata = ImageMetadata.Read(new TestJpeg { UserComment = [.. "ASCII\0\0\0A nice day"u8] }.ToBytes());

        Assert.Null(metadata["origin_method"]);
    }

    [Fact]
    public void All_methods_found_are_listed()
    {
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            RawXmp = OriginXmp,
            UserComment = [.. "ASCII\0\0\0archivejpg|source=other.jpg|sha256=cd|size=7"u8],
        }.ToBytes());
        metadata.ReadSidecar(OriginXmp);

        Assert.Equal("xmpembed;exifembed;xmpsidecar", metadata["origin_method"]);
        Assert.Equal("ab12", metadata["origin_sha256"]); // The first one found wins.
    }

    [Fact]
    public void Stray_bytes_before_the_xmp_packet_lose_nothing()
    {
        // The bytes real IIPImage files have there, which XmpCore takes for UTF-16.
        var metadata = ImageMetadata.Read(new TestJpeg
        {
            DateTimeOriginal = "2010:05:01 10:00:00",
            XmpDateCreated = "1953-07-14",
            XmpPrefix = [0x00, (byte)'q', 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        }.ToBytes());

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
        }.ToBytes());

        Assert.Equal("2010:05:01 10:00:00", metadata["exif_date_time_original"]);
        Assert.Single(metadata.Dates);
        Assert.Contains("XmpReader", metadata.ReadError);
    }

    [Fact]
    public void Values_do_not_depend_on_the_current_culture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("da-DK");
        try
        {
            var metadata = ImageMetadata.Read(new TestJpeg { FNumberTenths = 28 }.ToBytes());

            Assert.Equal("f/2.8", metadata["f_number"]);
            Assert.Equal("da-DK", System.Globalization.CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Garbage_reports_an_error_instead_of_throwing()
    {
        var metadata = ImageMetadata.Read([1, 2, 3, 4]);
        Assert.NotNull(metadata.ReadError);
        Assert.Empty(metadata.Dates);
    }
}
