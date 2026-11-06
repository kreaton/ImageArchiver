using System.Text.RegularExpressions;

namespace ImageArchiver.Core;

/// <summary>A date found in a photo, in local time. Source is its catalog column name.</summary>
public sealed record DateCandidate(string Source, DateTime Value);

public sealed record ResolvedDate(DateTime Value, string Source, IReadOnlyList<DateCandidate> Ignored);

public static partial class PhotoDate
{
    public const string FileModifiedSource = "file_modified";
    public const string FileCreatedSource = "file_created";

    private const int MinimumYear = 1800;

    /// <summary>Dates that devices and tools write when they have no real date.</summary>
    private static readonly HashSet<DateOnly> PlaceholderDates =
    [
        new(1601, 1, 1), // Windows FILETIME zero
        new(1904, 1, 1), // Mac epoch
        new(1970, 1, 1), // Unix epoch
        new(1980, 1, 1), // FAT epoch
    ];

    /// <summary>
    /// Picks the smallest plausible date among the photo's metadata dates and its file dates.
    /// On a tie the earliest listed source wins, so metadata goes before the file dates.
    /// </summary>
    public static ResolvedDate Resolve(
        IEnumerable<DateCandidate> candidates, DateTime fileCreated, DateTime fileModified, DateTime now)
    {
        var metadata = candidates.ToList();
        var ignored = metadata.Where(c => !IsPlausible(c.Value, now)).ToList();

        var modified = new DateCandidate(FileModifiedSource, fileModified);
        var chosen = metadata.Append(modified).Append(new DateCandidate(FileCreatedSource, fileCreated))
            .Where(c => IsPlausible(c.Value, now))
            .OrderBy(c => c.Value) // Stable, which is what settles a tie.
            .FirstOrDefault() ?? modified;
        return new ResolvedDate(chosen.Value, chosen.Source, ignored);
    }

    /// <summary>A UTC date in this machine's time zone, with the daylight saving of that date.</summary>
    public static DateTime ToLocal(DateTime utc) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local),
            DateTimeKind.Unspecified);

    public static bool IsPlausible(DateTime value, DateTime now) =>
        value.Year >= MinimumYear
        && value <= now.AddDays(1)
        && !PlaceholderDates.Contains(DateOnly.FromDateTime(value));

    /// <summary>
    /// Parses the forms found in EXIF, IPTC and XMP: 2004:09:25 11:28:37, 20040925,
    /// 2004-09-25T11:28:37+02:00, 2004-09 and 2004. The time is kept as written; only a Z
    /// suffix marks it as UTC.
    /// </summary>
    public static bool TryParse(string? text, out DateTime value, out bool isUtc)
    {
        value = default;
        isUtc = false;
        var match = text is null ? Match.Empty : DatePattern().Match(text.Trim(' ', '\0', '\t', '\r', '\n'));
        if (!match.Success)
            return false;

        int Part(int group, int fallback) =>
            match.Groups[group].Success ? int.Parse(match.Groups[group].ValueSpan) : fallback;

        int year = Part(1, 0), month = Part(2, 1), day = Part(3, 1);
        int hour = Part(4, 0), minute = Part(5, 0), second = Part(6, 0);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
            return false;

        value = new DateTime(year, month, day, hour, minute, second);
        isUtc = match.Groups[7].ValueSpan is "Z" or "z";
        return true;
    }

    [GeneratedRegex(
        @"^(\d{4})(?:[-:/.]?(\d{2})(?:[-:/.]?(\d{2})(?:[T\s]+(\d{1,2})[:.]?(\d{2})(?:[:.]?(\d{2})(?:\.\d+)?)?\s*(Z|z|[+-]\d{2}:?\d{2})?)?)?)?$")]
    private static partial Regex DatePattern();

    /// <summary>Parses an EXIF offset such as +02:00.</summary>
    public static bool TryParseOffset(string? text, out TimeSpan offset)
    {
        offset = default;
        var match = text is null ? Match.Empty : OffsetPattern().Match(text.Trim(' ', '\0'));
        if (!match.Success)
            return false;
        offset = new TimeSpan(int.Parse(match.Groups[2].ValueSpan), int.Parse(match.Groups[3].ValueSpan), 0);
        if (match.Groups[1].ValueSpan is "-")
            offset = -offset;
        return true;
    }

    [GeneratedRegex(@"^([+-])(\d{2}):?(\d{2})$")]
    private static partial Regex OffsetPattern();
}
