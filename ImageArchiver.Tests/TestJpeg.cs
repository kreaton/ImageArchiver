using System.Text;

namespace ImageArchiver.Tests;

/// <summary>
/// Builds minimal JPEGs with chosen metadata. The image data isn't a decodable picture, which
/// nothing here needs; <see cref="Content"/> only makes files differ.
/// </summary>
public sealed record TestJpeg
{
    public string? DateTimeOriginal { get; init; }
    public string? DateTimeDigitized { get; init; }
    public string? DateTime { get; init; }
    public string? OffsetTimeOriginal { get; init; }
    public string? GpsDate { get; init; }
    public (int H, int M, int S)? GpsTime { get; init; }
    public string? XmpDateCreated { get; init; }
    public byte Content { get; init; } = 0x12;

    /// <summary>The raw EXIF UserComment value, with its 8 byte encoding header.</summary>
    public byte[]? UserComment { get; init; }

    /// <summary>The f-number in tenths, so 28 is f/2.8.</summary>
    public int? FNumberTenths { get; init; }

    /// <summary>Bytes before the XMP packet, as IIPImage writes them.</summary>
    public byte[] XmpPrefix { get; init; } = [];

    /// <summary>Replaces the XMP packet, to test what can't be parsed at all.</summary>
    public string? RawXmp { get; init; }

    private const ushort Ascii = 2, Long = 4, Rational = 5, Undefined = 7;

    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

    public byte[] ToBytes()
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);
        var exif = BuildExif();
        if (exif.Length > 0)
            WriteSegment(jpeg, [.. "Exif\0\0"u8, .. exif]);
        if (XmpDateCreated is not null || RawXmp is not null)
        {
            var packet = RawXmp ?? $"""
                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" photoshop:DateCreated="{XmpDateCreated}"/>
                </rdf:RDF></x:xmpmeta>
                """;
            WriteSegment(jpeg, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. XmpPrefix, .. Encoding.UTF8.GetBytes(packet)]);
        }
        jpeg.Write([0xFF, 0xDA, 0x00, 0x02, Content, 0x34, 0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static void WriteSegment(Stream jpeg, byte[] payload)
    {
        var length = payload.Length + 2;
        jpeg.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        jpeg.Write(payload);
    }

    private byte[] BuildExif()
    {
        var sub = new List<Entry>();
        AddText(sub, 0x9003, DateTimeOriginal);
        AddText(sub, 0x9004, DateTimeDigitized);
        AddText(sub, 0x9011, OffsetTimeOriginal);
        if (UserComment is { } comment)
            sub.Add(new Entry(0x9286, Undefined, (uint)comment.Length, comment));
        if (FNumberTenths is { } tenths)
            sub.Add(new Entry(0x829D, Rational, 1, [.. BitConverter.GetBytes(tenths), .. BitConverter.GetBytes(10)]));

        var gps = new List<Entry>();
        if (GpsTime is var (h, m, s))
            gps.Add(new Entry(0x0007, Rational, 3, [.. Fraction(h), .. Fraction(m), .. Fraction(s)]));
        AddText(gps, 0x001D, GpsDate);

        var root = new List<Entry>();
        AddText(root, 0x0132, DateTime);
        if (sub.Count == 0 && gps.Count == 0 && root.Count == 0)
            return [];

        // Pointers have a fixed size, so the layout is known before their values are.
        if (sub.Count > 0)
            root.Add(new Entry(0x8769, Long, 1, new byte[4]));
        if (gps.Count > 0)
            root.Add(new Entry(0x8825, Long, 1, new byte[4]));

        var offset = 8 + Ifd(root, 0).Length;
        var subOffset = offset;
        if (sub.Count > 0)
            offset += Ifd(sub, 0).Length;
        var gpsOffset = offset;

        root = root.Select(e => e.Tag switch
        {
            0x8769 => e with { Data = BitConverter.GetBytes(subOffset) },
            0x8825 => e with { Data = BitConverter.GetBytes(gpsOffset) },
            _ => e,
        }).ToList();

        using var tiff = new MemoryStream();
        tiff.Write([(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0]);
        tiff.Write(Ifd(root, 8));
        if (sub.Count > 0)
            tiff.Write(Ifd(sub, subOffset));
        if (gps.Count > 0)
            tiff.Write(Ifd(gps, gpsOffset));
        return tiff.ToArray();
    }

    private static void AddText(List<Entry> entries, ushort tag, string? text)
    {
        if (text is not null)
            entries.Add(new Entry(tag, Ascii, (uint)text.Length + 1, [.. Encoding.ASCII.GetBytes(text), 0]));
    }

    private static byte[] Fraction(int value) => [.. BitConverter.GetBytes(value), .. BitConverter.GetBytes(1)];

    private static byte[] Ifd(List<Entry> entries, int offset)
    {
        using var directory = new MemoryStream();
        using var overflow = new MemoryStream();
        var overflowStart = offset + 2 + 12 * entries.Count + 4;

        directory.Write(BitConverter.GetBytes((ushort)entries.Count));
        foreach (var entry in entries.OrderBy(e => e.Tag))
        {
            directory.Write(BitConverter.GetBytes(entry.Tag));
            directory.Write(BitConverter.GetBytes(entry.Type));
            directory.Write(BitConverter.GetBytes(entry.Count));
            if (entry.Data.Length <= 4)
            {
                directory.Write(entry.Data);
                directory.Write(new byte[4 - entry.Data.Length]);
            }
            else
            {
                directory.Write(BitConverter.GetBytes(overflowStart + (int)overflow.Length));
                overflow.Write(entry.Data);
            }
        }
        directory.Write(new byte[4]); // No next IFD.
        overflow.Position = 0;
        overflow.CopyTo(directory);
        return directory.ToArray();
    }
}
