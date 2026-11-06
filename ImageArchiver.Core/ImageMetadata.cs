using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Xmp;
using MetadataExtractor.IO;
using MxDirectory = MetadataExtractor.Directory;

namespace ImageArchiver.Core;

/// <summary>The metadata read from a JPEG or HEIF file: catalog column values and the candidate dates.</summary>
public sealed class ImageMetadata
{
    /// <summary>XMP properties holding dates, with their catalog columns.</summary>
    private static readonly (string Column, string Property)[] XmpDates =
    [
        ("xmp_date_time_original", "exif:DateTimeOriginal"),
        ("xmp_date_time_digitized", "exif:DateTimeDigitized"),
        ("xmp_create_date", "xmp:CreateDate"),
        ("xmp_date_created", "photoshop:DateCreated"),
        ("xmp_modify_date", "xmp:ModifyDate"),
        ("xmp_metadata_date", "xmp:MetadataDate"),
    ];

    /// <summary>Catalog columns filled from metadata, in order.</summary>
    public static readonly string[] Columns =
    [
        "exif_date_time_original", "exif_date_time_digitized", "exif_date_time", "exif_offset_time_original",
        "gps_date_time_utc", "gps_date_time_local", "iptc_date_created", "iptc_digital_creation_date",
        .. XmpDates.Select(d => d.Column),
        "make", "model", "lens_model", "software",
        "orientation", "width", "height",
        "exposure_time", "f_number", "iso", "focal_length", "flash",
        "artist", "copyright", "description",
        "gps_latitude", "gps_longitude", "gps_altitude",
        "origin_method", "origin_source_root", "origin_source_path", "origin_sha256", "origin_size", "origin_archived_at",
        "metadata_all",
    ];

    private static ReadOnlySpan<byte> XmpSignature => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>Longer values, mostly binary blobs, are left out of metadata_all.</summary>
    private const int MaxAllValueLength = 256;

    private readonly Dictionary<string, string> _values = [];
    private readonly List<DateCandidate> _dates = [];

    public IReadOnlyList<DateCandidate> Dates => _dates;

    /// <summary>Set when the file has no metadata that could be read.</summary>
    public string? ReadError { get; private init; }

    public string? this[string column] => _values.GetValueOrDefault(column);

    public static ImageMetadata Read(byte[] data)
    {
        // The library formats values such as "f/2.8" by the current culture, and a catalog
        // shouldn't depend on the machine that wrote it.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            ICollection<JpegSegment> segments = [];
            IReadOnlyList<MxDirectory> directories;
            var problems = new List<string>();
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                if (IsHeif(data))
                {
                    directories = ReadHeifDirectories(stream, data, problems);
                }
                else
                {
                    segments = JpegSegmentReader.ReadSegments(
                        new SequentialStreamReader(stream),
                        JpegMetadataReader.AllReaders.SelectMany(r => r.SegmentTypes).ToHashSet()).ToList();
                    directories = ReadDirectories(segments, problems);
                }
            }
            catch (Exception ex)
            {
                // A damaged file is still archived, by its file date. The parser can throw
                // more than its documented exceptions on garbage input.
                return new ImageMetadata { ReadError = ex.Message };
            }

            var metadata = new ImageMetadata { ReadError = problems.Count > 0 ? string.Join("; ", problems) : null };
            metadata.ReadExif(directories);
            metadata.ReadIptc(directories.OfType<IptcDirectory>());
            metadata.ReadXmp(directories.OfType<XmpDirectory>());
            metadata.ReadGps(directories.OfType<GpsDirectory>().FirstOrDefault());
            metadata.ReadOrigin(segments, directories);
            metadata.ReadAll(directories);
            return metadata;
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>By the content, as a photo's extension can be wrong: HEIF files start with an ftyp box.</summary>
    private static bool IsHeif(byte[] data) => data.Length >= 8 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8);

    /// <summary>
    /// Runs each reader on its own, because the library lets one reader's exception discard
    /// what all the others found: bad XMP would otherwise lose the EXIF dates.
    /// </summary>
    private static List<MxDirectory> ReadDirectories(ICollection<JpegSegment> segments, List<string> problems)
    {
        var directories = new List<MxDirectory>();
        foreach (var reader in JpegMetadataReader.AllReaders)
        {
            IReadOnlyList<MxDirectory> found = [];
            string? failure = null;
            try
            {
                found = JpegMetadataReader.ProcessJpegSegments([reader], segments);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            // Bad XMP either throws or gives a directory with nothing parsed, by what the bytes look like.
            if (reader is XmpReader && (failure is not null || found.OfType<XmpDirectory>().Any(d => d.XmpMeta is null)))
            {
                var recovered = ReadXmpFromPacketStart(segments);
                if (recovered.Count > 0)
                    (found, failure) = (recovered, null);
                else
                    failure ??= "the XMP packet cannot be parsed";
            }

            directories.AddRange(found);
            if (failure is not null)
                problems.Add($"{reader.GetType().Name}: {failure}");
        }
        return directories;
    }

    /// <summary>
    /// Some writers, such as IIPImage, put stray bytes before the XMP packet, which XmpCore
    /// takes for UTF-16. Parsing from the start of the packet itself gets around that.
    /// </summary>
    private static List<MxDirectory> ReadXmpFromPacketStart(ICollection<JpegSegment> segments)
    {
        var directories = new List<MxDirectory>();
        foreach (var segment in segments.Where(s => s.Type == JpegSegmentType.App1 && s.Bytes.AsSpan().StartsWith(XmpSignature)))
            if (ExtractXmpPacket(segment.Bytes) is { } directory)
                directories.Add(directory);
        return directories;
    }

    /// <summary>The same recovery for HEIF, where the reader gives a directory with nothing parsed.</summary>
    private static List<MxDirectory> ReadHeifDirectories(Stream stream, byte[] data, List<string> problems)
    {
        var directories = HeifMetadataReader.ReadMetadata(stream).ToList();
        if (directories.RemoveAll(d => d is XmpDirectory { XmpMeta: null }) > 0)
        {
            if (ExtractXmpPacket(data) is { } recovered)
                directories.Add(recovered);
            else
                problems.Add("XmpReader: the XMP packet cannot be parsed");
        }
        return directories;
    }

    /// <summary>Finds the XMP packet in the bytes, which can hold other data after it. Null if there is none that parses.</summary>
    private static XmpDirectory? ExtractXmpPacket(byte[] bytes)
    {
        var start = bytes.AsSpan().IndexOf("<?xpacket"u8);
        if (start < 0)
            start = bytes.AsSpan().IndexOf("<x:xmpmeta"u8);
        if (start < 0)
            return null;

        var close = bytes.AsSpan(start).IndexOf("</x:xmpmeta>"u8);
        var length = close < 0 ? bytes.Length - start : close + "</x:xmpmeta>"u8.Length;
        try
        {
            return new XmpReader().Extract(bytes, start, length) is { XmpMeta: not null } directory ? directory : null;
        }
        catch (Exception)
        {
            return null; // Really unreadable, which the caller reports.
        }
    }

    private void ReadExif(IReadOnlyList<MxDirectory> directories)
    {
        // Thumbnail and GPS directories share the base class but don't describe the photo itself.
        var exif = directories.OfType<ExifDirectoryBase>()
            .Where(d => d is not ExifThumbnailDirectory and not GpsDirectory)
            .ToList();

        string? Raw(int tag) => exif.Select(d => Clean(d.GetString(tag))).FirstOrDefault(v => v is not null);
        string? Described(int tag) =>
            exif.Where(d => d.ContainsTag(tag)).Select(d => Clean(d.GetDescription(tag))).FirstOrDefault(v => v is not null);

        AddDate("exif_date_time_original", Raw(ExifDirectoryBase.TagDateTimeOriginal));
        AddDate("exif_date_time_digitized", Raw(ExifDirectoryBase.TagDateTimeDigitized));
        AddDate("exif_date_time", Raw(ExifDirectoryBase.TagDateTime));
        Set("exif_offset_time_original", Raw(ExifDirectoryBase.TagTimeZoneOriginal));

        Set("make", Raw(ExifDirectoryBase.TagMake));
        Set("model", Raw(ExifDirectoryBase.TagModel));
        Set("lens_model", Raw(ExifDirectoryBase.TagLensModel));
        Set("software", Raw(ExifDirectoryBase.TagSoftware));
        Set("orientation", Described(ExifDirectoryBase.TagOrientation));
        Set("exposure_time", Described(ExifDirectoryBase.TagExposureTime));
        Set("f_number", Described(ExifDirectoryBase.TagFNumber));
        Set("iso", Raw(ExifDirectoryBase.TagIsoEquivalent));
        Set("focal_length", Described(ExifDirectoryBase.TagFocalLength));
        Set("flash", Described(ExifDirectoryBase.TagFlash));
        Set("artist", Raw(ExifDirectoryBase.TagArtist));
        Set("copyright", Raw(ExifDirectoryBase.TagCopyright));
        Set("description", Raw(ExifDirectoryBase.TagImageDescription));

        // The frame header holds the real pixel size; EXIF may describe an earlier version.
        var frame = directories.OfType<JpegDirectory>().FirstOrDefault();
        // A HEIF file also lists its thumbnail, so the photo is the largest image in it.
        var heif = directories.OfType<HeicImagePropertiesDirectory>()
            .Where(d => d.ContainsTag(HeicImagePropertiesDirectory.TagImageWidth))
            .MaxBy(d => d.TryGetInt32(HeicImagePropertiesDirectory.TagImageWidth, out var width) ? width : 0);
        Set("width", frame?.GetString(JpegDirectory.TagImageWidth) ?? heif?.GetString(HeicImagePropertiesDirectory.TagImageWidth) ?? Raw(ExifDirectoryBase.TagExifImageWidth));
        Set("height", frame?.GetString(JpegDirectory.TagImageHeight) ?? heif?.GetString(HeicImagePropertiesDirectory.TagImageHeight) ?? Raw(ExifDirectoryBase.TagExifImageHeight));
    }

    private void ReadIptc(IEnumerable<IptcDirectory> directories)
    {
        foreach (var iptc in directories)
        {
            AddDate("iptc_date_created", Join(iptc.GetString(IptcDirectory.TagDateCreated), iptc.GetString(IptcDirectory.TagTimeCreated)));
            AddDate("iptc_digital_creation_date", Join(iptc.GetString(IptcDirectory.TagDigitalDateCreated), iptc.GetString(IptcDirectory.TagDigitalTimeCreated)));
        }

        static string? Join(string? date, string? time) =>
            Clean(date) is { } d ? (Clean(time) is { } t ? $"{d} {t}" : d) : null;
    }

    private void ReadXmp(IEnumerable<XmpDirectory> directories)
    {
        foreach (var xmp in directories)
        {
            var properties = xmp.GetXmpProperties();
            foreach (var (column, property) in XmpDates)
                if (properties.TryGetValue(property, out var value))
                    AddDate(column, value);
        }
    }

    private void ReadGps(GpsDirectory? gps)
    {
        if (gps is null)
            return;

        if (gps.GetGeoLocation() is { } location)
        {
            Set("gps_latitude", location.Latitude.ToString("F6", CultureInfo.InvariantCulture));
            Set("gps_longitude", location.Longitude.ToString("F6", CultureInfo.InvariantCulture));
        }
        if (gps.TryGetRational(GpsDirectory.TagAltitude, out var altitude) && altitude.Denominator != 0)
        {
            var belowSeaLevel = gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var reference) && reference == 1;
            var metres = belowSeaLevel ? -altitude.ToDouble() : altitude.ToDouble();
            Set("gps_altitude", metres.ToString("0.##", CultureInfo.InvariantCulture));
        }

        if (!PhotoDate.TryParse(gps.GetString(GpsDirectory.TagDateStamp), out var utc, out _))
            return;
        var hasTime = false;
        if (gps.GetRationalArray(GpsDirectory.TagTimeStamp) is [var h, var m, var s]
            && h.Denominator != 0 && m.Denominator != 0 && s.Denominator != 0)
        {
            // Garbage stamps exist; keep the date alone rather than move it to another day.
            var seconds = h.ToDouble() * 3600 + m.ToDouble() * 60 + s.ToDouble();
            if (seconds is >= 0 and < 86400)
            {
                utc = utc.AddSeconds(Math.Floor(seconds));
                hasTime = true;
            }
        }

        // The photo's own offset is exact. Without one it is taken to be this machine's time
        // zone. A date without a time has no instant to convert, so it stays as it is.
        var local = !hasTime ? utc
            : PhotoDate.TryParseOffset(this["exif_offset_time_original"], out var offset) ? utc + offset
            : PhotoDate.ToLocal(utc);

        Set("gps_date_time_utc", utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Set("gps_date_time_local", local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        _dates.Add(new DateCandidate("gps_date_time_local", local));
    }

    /// <summary>
    /// Reads the origin archivejpg stored in a photo when it was archived with its embedding
    /// options, so that it isn't lost when such a photo is archived again.
    /// </summary>
    private void ReadOrigin(ICollection<JpegSegment> segments, IReadOnlyList<MxDirectory> directories)
    {
        foreach (var segment in segments.Where(s => s.Type == JpegSegmentType.App1 && s.Bytes.AsSpan().StartsWith(XmpSignature)))
            if (StoredOrigin.FromXmp(System.Text.Encoding.UTF8.GetString(segment.Bytes)) is { } origin)
            {
                AddOrigin("xmpembed", origin);
                break;
            }

        var comment = directories.OfType<ExifDirectoryBase>()
            .Where(d => d is not ExifThumbnailDirectory and not GpsDirectory)
            .Select(d => d.GetByteArray(ExifDirectoryBase.TagUserComment))
            .FirstOrDefault(bytes => bytes is not null);
        if (comment is not null && StoredOrigin.FromUserComment(comment) is { } fromComment)
            AddOrigin("exifembed", fromComment);
    }

    /// <summary>Adds the origin from the photo's archivejpg sidecar file, if the text is one.</summary>
    public void ReadSidecar(string xmp)
    {
        if (StoredOrigin.FromXmp(xmp) is { } origin)
            AddOrigin("xmpsidecar", origin);
    }

    /// <summary>Lists every method found; for the values, the first one found wins.</summary>
    private void AddOrigin(string method, StoredOrigin origin)
    {
        Set("origin_method", this["origin_method"] is { } methods ? $"{methods};{method}" : method);
        SetIfMissing("origin_source_root", origin.SourceRoot);
        SetIfMissing("origin_source_path", origin.SourcePath);
        SetIfMissing("origin_sha256", origin.Sha256);
        SetIfMissing("origin_size", origin.Size);
        SetIfMissing("origin_archived_at", origin.ArchivedAt);

        void SetIfMissing(string column, string? value)
        {
            if (this[column] is null)
                Set(column, value);
        }
    }

    private void ReadAll(IReadOnlyList<MxDirectory> directories)
    {
        var fields = new List<string>();
        foreach (var directory in directories)
        {
            if (directory is ExifThumbnailDirectory or not (ExifDirectoryBase or IptcDirectory))
                continue;
            foreach (var tag in directory.Tags)
                if (Clean(tag.Description) is { Length: <= MaxAllValueLength } value)
                    fields.Add($"{directory.Name}/{tag.Name}={value}");
        }
        foreach (var xmp in directories.OfType<XmpDirectory>())
            foreach (var (name, raw) in xmp.GetXmpProperties())
                if (Clean(raw) is { Length: <= MaxAllValueLength } value)
                    fields.Add($"XMP/{name}={value}");

        fields.Sort(StringComparer.Ordinal);
        Set("metadata_all", string.Join("; ", fields));
    }

    private void AddDate(string column, string? raw)
    {
        if (Clean(raw) is not { } text || _values.ContainsKey(column))
            return;
        Set(column, text);
        if (PhotoDate.TryParse(text, out var value, out var isUtc))
            _dates.Add(new DateCandidate(column, isUtc ? PhotoDate.ToLocal(value) : value));
    }

    private void Set(string column, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _values[column] = value;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim(' ', '\0', '\t', '\r', '\n');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
