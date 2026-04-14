using Microsoft.Data.Sqlite;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class ConversionLog : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public record Entry(
        int     Id,
        string  SourcePath,
        string  OutputPath,
        string  Category,
        string  ConvertedAt,
        string  Status,
        string? Checksum);

    public ConversionLog()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PhotoOrganizer");
        Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "history.db")}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversions (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path      TEXT NOT NULL,
                output_path      TEXT NOT NULL,
                category         TEXT NOT NULL,
                converted_at     TEXT NOT NULL,
                status           TEXT NOT NULL,
                source_checksum  TEXT
            )
            """;
        cmd.ExecuteNonQuery();

        // Migration: add source_checksum column to pre-existing databases
        MigrateAddChecksum();
    }

    private void MigrateAddChecksum()
    {
        // Check whether the column already exists via PRAGMA table_info
        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(conversions)";
        using var r = pragma.ExecuteReader();
        while (r.Read())
            if (r.GetString(1) == "source_checksum") return;  // already present

        // Add it — existing rows get NULL, which is fine
        using var alter = _conn.CreateCommand();
        alter.CommandText = "ALTER TABLE conversions ADD COLUMN source_checksum TEXT";
        alter.ExecuteNonQuery();

        // Index for O(1) checksum lookups
        using var idx = _conn.CreateCommand();
        idx.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_conversions_checksum
            ON conversions(source_checksum)
            WHERE source_checksum IS NOT NULL
            """;
        idx.ExecuteNonQuery();
    }

    public void Log(string sourcePath, string outputPath, FileCategory category, ResultStatus status,
        string? checksum = null)
    {
        try
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO conversions (source_path, output_path, category, converted_at, status, source_checksum)
                    VALUES ($src, $out, $cat, $ts, $st, $cs)
                    """;
                cmd.Parameters.AddWithValue("$src", sourcePath);
                cmd.Parameters.AddWithValue("$out", outputPath);
                cmd.Parameters.AddWithValue("$cat", category.ToString());
                cmd.Parameters.AddWithValue("$ts",  DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$st",  status.ToString());
                cmd.Parameters.AddWithValue("$cs",  (object?)checksum ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* non-fatal */ }
    }

    public List<Entry> GetRecent(int limit = 1000)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, source_path, output_path, category, converted_at, status, source_checksum
                FROM conversions ORDER BY id DESC LIMIT {limit}
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<Entry>();
            while (r.Read())
                list.Add(new Entry(r.GetInt32(0), r.GetString(1), r.GetString(2),
                                   r.GetString(3), r.GetString(4), r.GetString(5),
                                   r.IsDBNull(6) ? null : r.GetString(6)));
            return list;
        }
    }

    public int GetTotalCount()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM conversions";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public List<Entry> GetPage(int offset, int pageSize, string? statusFilter = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            if (statusFilter != null)
            {
                cmd.CommandText = $"""
                    SELECT id, source_path, output_path, category, converted_at, status, source_checksum
                    FROM conversions WHERE status = $st ORDER BY id DESC LIMIT {pageSize} OFFSET {offset}
                    """;
                cmd.Parameters.AddWithValue("$st", statusFilter);
            }
            else
            {
                cmd.CommandText = $"""
                    SELECT id, source_path, output_path, category, converted_at, status, source_checksum
                    FROM conversions ORDER BY id DESC LIMIT {pageSize} OFFSET {offset}
                    """;
            }
            using var r = cmd.ExecuteReader();
            var list = new List<Entry>();
            while (r.Read())
                list.Add(new Entry(r.GetInt32(0), r.GetString(1), r.GetString(2),
                                   r.GetString(3), r.GetString(4), r.GetString(5),
                                   r.IsDBNull(6) ? null : r.GetString(6)));
            return list;
        }
    }

    /// <summary>
    /// Returns the MD5 checksums of all source files that were previously processed successfully.
    /// Only records that have a stored checksum are included (NULL entries are skipped).
    /// </summary>
    public HashSet<string> GetProcessedSourceChecksums()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT source_checksum FROM conversions
                WHERE status IN ('Success', 'DateInferred') AND source_checksum IS NOT NULL
                """;
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    /// <summary>Returns all source paths that were previously processed successfully.</summary>
    public HashSet<string> GetProcessedSourcePaths()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT source_path FROM conversions WHERE status IN ('Success', 'DateInferred')";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void Purge()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM conversions";
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
