using System.Text;
using Microsoft.Data.Sqlite;

namespace ImageArchiver.Core;

/// <summary>The catalog in the target root: one row per processed file, growing across runs.</summary>
public interface ICatalog : IDisposable
{
    string Path { get; }

    /// <summary>Rows are saved immediately, so an interrupted run still leaves an accurate record.</summary>
    void Write(FileRecord record);
}

public static class Catalog
{
    public const string CsvFileName = "catalog.csv";
    public const string SqliteFileName = "catalog.db";

    /// <summary>The form of all dates the program itself writes to the catalog.</summary>
    internal const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>All columns in catalog order. The file columns come first, then the metadata.</summary>
    internal static readonly (string Name, string SqlType, Func<FileRecord, string?> Value)[] Columns =
    [
        ("run_id", "TEXT", r => r.RunId),
        ("processed_at", "TEXT", r => r.ProcessedAt),
        ("status", "TEXT", r => StatusText(r.Status)),
        ("source_root", "TEXT", r => r.SourceRoot),
        ("source_path", "TEXT", r => r.SourcePath),
        ("target_path", "TEXT", r => r.TargetPath),
        ("existing_source_path", "TEXT", r => r.ExistingSourcePath),
        ("size", "INTEGER", r => r.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("file_created", "TEXT", r => r.FileCreated),
        ("file_modified", "TEXT", r => r.FileModified),
        ("sha256", "TEXT", r => r.Sha256),
        ("archive_date", "TEXT", r => r.ArchiveDate),
        ("archive_date_source", "TEXT", r => r.ArchiveDateSource),
        ("note", "TEXT", r => r.Note),
        .. ImageMetadata.Columns.Select(name => (
            name,
            name is "gps_latitude" or "gps_longitude" or "gps_altitude" ? "REAL" : name is "width" or "height" ? "INTEGER" : "TEXT",
            (Func<FileRecord, string?>)(r => r.Metadata?[name]))),
    ];

    public static string StatusText(FileStatus status) => status.ToString().ToLowerInvariant();

    public static ICatalog Open(CatalogFormat format, string targetRoot) => format switch
    {
        CatalogFormat.Sqlite => new SqliteCatalog(System.IO.Path.Combine(targetRoot, SqliteFileName)),
        _ => new CsvCatalog(System.IO.Path.Combine(targetRoot, CsvFileName)),
    };

    /// <summary>
    /// Loads what earlier runs archived, from both catalog kinds, so that switching format
    /// between runs doesn't lose track of files. An unreadable catalog only causes a warning:
    /// the files it lists are then still recognised by their hash, with origin unknown.
    /// </summary>
    public static ArchiveIndex LoadIndex(string targetRoot, List<string> warnings)
    {
        var index = new ArchiveIndex();
        Load(System.IO.Path.Combine(targetRoot, CsvFileName), CsvCatalog.Load);
        Load(System.IO.Path.Combine(targetRoot, SqliteFileName), SqliteCatalog.Load);
        return index;

        void Load(string path, Action<string, ArchiveIndex> loader)
        {
            if (!File.Exists(path))
                return;
            try
            {
                loader(path, index);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
            {
                warnings.Add($"Cannot read {path}: {ex.Message}");
            }
        }
    }

    internal static void AddRow(
        ArchiveIndex index, string? status, string? target, string? source, string? sha, string? size, string? modified,
        string? originSha, string? originSize)
    {
        if (status is not ("copied" or "renamed")
            || string.IsNullOrEmpty(target) || string.IsNullOrEmpty(sha) || !long.TryParse(size, out var bytes))
            return;
        DateTime? sourceModified = DateTime.TryParseExact(
            modified, DateFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        index.Add(new ArchiveEntry(target, sha, bytes, source, sourceModified, ContentIdentity.Parse(originSha, originSize)));
    }
}

public sealed class CsvCatalog : ICatalog
{
    private readonly StreamWriter _writer;

    public string Path { get; }

    public CsvCatalog(string path)
    {
        Path = path;
        try
        {
            UpgradeColumns(path);

            // Other programs may read it meanwhile. The BOM, written only to a new file, makes
            // Excel show non-ASCII names correctly.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (stream.Length == 0)
                WriteLine(Catalog.Columns.Select(c => c.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException($"Cannot open {path}; is it open in another program? {ex.Message}", ex);
        }
    }

    public void Write(FileRecord record) => WriteLine(Catalog.Columns.Select(c => c.Value(record)));

    private void WriteLine(IEnumerable<string?> values)
    {
        _writer.Write(string.Join(',', values.Select(Escape)));
        _writer.Write("\r\n");
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// Rewrites a catalog from an earlier version to the current columns, as appended rows
    /// would otherwise end up under the wrong headers. Columns unknown to this version are kept.
    /// </summary>
    private static void UpgradeColumns(string path)
    {
        if (!File.Exists(path))
            return;

        var temporary = path + ".upgrade";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            using var rows = ReadRows(reader).GetEnumerator();
            if (!rows.MoveNext())
                return;

            var current = Catalog.Columns.Select(c => c.Name).ToArray();
            var header = rows.Current;
            if (header.SequenceEqual(current))
                return;

            var columns = current.Concat(header.Except(current)).ToArray();
            var sources = columns.Select(name => Array.IndexOf(header, name)).ToArray();
            using var writer = new StreamWriter(temporary, append: false, encoding);
            writer.Write(string.Join(',', columns.Select(Escape)) + "\r\n");
            while (rows.MoveNext())
            {
                var row = rows.Current;
                writer.Write(string.Join(',', sources.Select(i => Escape(i >= 0 && i < row.Length ? row[i] : null))) + "\r\n");
            }
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string Escape(string? value) =>
        value is null ? "" :
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" :
        value;

    public static void Load(string path, ArchiveIndex index)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var rows = ReadRows(reader).GetEnumerator();
        if (!rows.MoveNext())
            return;

        var header = rows.Current;
        int Column(string name)
        {
            var i = Array.IndexOf(header, name);
            return i >= 0 ? i : throw new InvalidDataException($"missing column {name}");
        }
        int status = Column("status"), target = Column("target_path"), source = Column("source_path"),
            sha = Column("sha256"), size = Column("size"), modified = Column("file_modified");
        var needed = new[] { status, target, source, sha, size, modified }.Max();

        // A catalog from an earlier version may not have these yet.
        int originSha = Array.IndexOf(header, "origin_sha256"), originSize = Array.IndexOf(header, "origin_size");

        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (row.Length > needed) // An interrupted run can leave a partial last row.
                Catalog.AddRow(index, row[status], row[target], row[source], row[sha], row[size], row[modified],
                    originSha >= 0 && originSha < row.Length ? row[originSha] : null,
                    originSize >= 0 && originSize < row.Length ? row[originSize] : null);
        }
    }

    /// <summary>Reads RFC 4180 rows, where quoted values may hold commas and line breaks.</summary>
    internal static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (inQuotes)
            {
                if (c != '"')
                    field.Append((char)c);
                else if (reader.Peek() == '"')
                    field.Append((char)reader.Read());
                else
                    inQuotes = false;
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                    reader.Read();
                fields.Add(field.ToString());
                field.Clear();
                yield return fields.ToArray();
                fields.Clear();
            }
            else
            {
                field.Append((char)c);
            }
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }
}

public sealed class SqliteCatalog : ICatalog
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;

    public string Path { get; }

    public SqliteCatalog(string path)
    {
        Path = path;
        try
        {
            _connection = Connect(path, SqliteOpenMode.ReadWriteCreate);
            _connection.Open();

            var columns = string.Join(", ", Catalog.Columns.Select(c => $"{c.Name} {c.SqlType}"));
            using (var create = _connection.CreateCommand())
            {
                // The default rollback journal rather than WAL, which doesn't work on network drives.
                create.CommandText = $"""
                    PRAGMA synchronous = NORMAL;
                    CREATE TABLE IF NOT EXISTS files (id INTEGER PRIMARY KEY, {columns});
                    CREATE INDEX IF NOT EXISTS files_sha256 ON files (sha256);
                    CREATE INDEX IF NOT EXISTS files_source_path ON files (source_path);
                    """;
                create.ExecuteNonQuery();
            }
            AddMissingColumns();

            _insert = _connection.CreateCommand();
            var names = string.Join(", ", Catalog.Columns.Select(c => c.Name));
            var parameters = string.Join(", ", Catalog.Columns.Select(c => "$" + c.Name));
            _insert.CommandText = $"INSERT INTO files ({names}) VALUES ({parameters})";
            foreach (var column in Catalog.Columns)
                _insert.Parameters.Add(new SqliteParameter("$" + column.Name, DBNull.Value));
        }
        catch (SqliteException ex)
        {
            throw new ArchiveException($"Cannot open {path}; is it open in another program? {ex.Message}", ex);
        }
    }

    /// <summary>Brings a catalog from an earlier version up to the current columns.</summary>
    private void AddMissingColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = "SELECT name FROM pragma_table_info('files')";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(0));
        }

        foreach (var column in Catalog.Columns.Where(c => !existing.Contains(c.Name)))
        {
            using var add = _connection.CreateCommand();
            add.CommandText = $"ALTER TABLE files ADD COLUMN {column.Name} {column.SqlType}";
            add.ExecuteNonQuery();
        }
    }

    public void Write(FileRecord record)
    {
        foreach (var column in Catalog.Columns)
            _insert.Parameters["$" + column.Name].Value = (object?)column.Value(record) ?? DBNull.Value;
        _insert.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _insert.Dispose();
        _connection.Dispose();
    }

    public static void Load(string path, ArchiveIndex index)
    {
        using var connection = Connect(path, SqliteOpenMode.ReadOnly);
        connection.Open();

        // SELECT *, because a catalog from an earlier version may lack the origin columns, and
        // loading happens before the catalog being written is upgraded.
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT * FROM files WHERE status IN ('copied', 'renamed') ORDER BY id";
        using var reader = query.ExecuteReader();
        var ordinals = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);
        string? Value(string column) =>
            ordinals.TryGetValue(column, out var i) && !reader.IsDBNull(i) ? reader.GetString(i) : null;

        while (reader.Read())
            Catalog.AddRow(index, Value("status"), Value("target_path"), Value("source_path"), Value("sha256"),
                Value("size"), Value("file_modified"), Value("origin_sha256"), Value("origin_size"));
    }

    // No pooling, so that the file is released as soon as the catalog is disposed.
    private static SqliteConnection Connect(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
}
