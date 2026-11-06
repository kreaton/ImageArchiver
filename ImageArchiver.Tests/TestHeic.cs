using System.Buffers.Binary;
using System.Text;

namespace ImageArchiver.Tests;

/// <summary>
/// Builds minimal HEIC files with the metadata of a <see cref="TestJpeg"/>, laid out as an iPhone
/// does: a photo, a thumbnail, and EXIF and XMP items. The image data isn't a decodable picture.
/// </summary>
public static class TestHeic
{
    private const int PhotoItem = 1, ThumbnailItem = 2, ExifItem = 3, XmpItem = 4;

    public static byte[] ToHeicBytes(this TestJpeg photo, int width = 4032, int height = 3024)
    {
        var tiff = photo.BuildExif();
        byte[]? exif = tiff.Length > 0 ? [0, 0, 0, 6, .. "Exif\0\0"u8, .. tiff] : null;
        byte[]? xmp = photo.XmpPacket() is { } packet ? [.. photo.XmpPrefix, .. Encoding.UTF8.GetBytes(packet)] : null;
        byte[] picture = [photo.Content, 0x34];

        var ftyp = Box("ftyp", [.. "heic"u8, 0, 0, 0, 0, .. "mif1"u8, .. "heic"u8]);

        // Offsets only change values, not sizes, so a first pass gives the size of what comes before the data.
        var dataStart = ftyp.Length + Meta(0, picture, exif, xmp, width, height).Length + 8;
        var meta = Meta(dataStart, picture, exif, xmp, width, height);
        return [.. ftyp, .. meta, .. Box("mdat", [.. picture, .. exif ?? [], .. xmp ?? []])];
    }

    private static byte[] Meta(int dataStart, byte[] picture, byte[]? exif, byte[]? xmp, int width, int height)
    {
        var offset = dataStart;
        var items = new List<(int Id, int Offset, int Length)> { (PhotoItem, offset, picture.Length), (ThumbnailItem, offset, picture.Length) };
        offset += picture.Length;
        if (exif is not null)
        {
            items.Add((ExifItem, offset, exif.Length));
            offset += exif.Length;
        }
        if (xmp is not null)
            items.Add((XmpItem, offset, xmp.Length));

        return FullBox("meta",
        [
            .. FullBox("hdlr", [0, 0, 0, 0, .. "pict"u8, .. new byte[12], 0]),
            .. FullBox("pitm", U16(PhotoItem)),
            .. FullBox("iloc",
            [
                0x44, 0x00, .. U16(items.Count),
                .. items.SelectMany(i => (byte[])[.. U16(i.Id), 0, 0, 0, 1, .. U32(i.Offset), .. U32(i.Length)]),
            ]),
            .. FullBox("iinf",
            [
                .. U16(items.Count),
                .. items.SelectMany(i => ItemInfo(i.Id)),
            ]),
            .. FullBox("iref",
            [
                .. Box("thmb", [.. U16(ThumbnailItem), .. U16(1), .. U16(PhotoItem)]),
                .. (exif is null ? [] : Box("cdsc", [.. U16(ExifItem), .. U16(1), .. U16(PhotoItem)])),
                .. (xmp is null ? [] : Box("cdsc", [.. U16(XmpItem), .. U16(1), .. U16(PhotoItem)])),
            ]),
            .. Box("iprp",
            [
                .. Box("ipco",
                [
                    .. FullBox("ispe", [.. U32(320), .. U32(240)]),
                    .. FullBox("ispe", [.. U32(width), .. U32(height)]),
                ]),
                .. FullBox("ipma",
                [
                    .. U32(2),
                    .. U16(ThumbnailItem), 1, 1,
                    .. U16(PhotoItem), 1, 2,
                ]),
            ]),
        ]);
    }

    private static byte[] ItemInfo(int id)
    {
        byte[] type = id switch
        {
            ExifItem => [.. "Exif"u8, 0],
            XmpItem => [.. "mime"u8, 0, .. "application/rdf+xml"u8, 0, 0],
            _ => [.. "hvc1"u8, 0],
        };
        return FullBox("infe", [.. U16(id), 0, 0, .. type], version: 2);
    }

    private static byte[] Box(string type, byte[] content) =>
        [.. U32(content.Length + 8), .. Encoding.ASCII.GetBytes(type), .. content];

    private static byte[] FullBox(string type, byte[] content, byte version = 0) =>
        Box(type, [version, 0, 0, 0, .. content]);

    private static byte[] U16(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    private static byte[] U32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }
}
