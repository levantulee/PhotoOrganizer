using Microsoft.Data.Sqlite;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Core;

public class ConversionLog : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public record Entry(
        int    Id,
        string SourcePath,
        string OutputPath,
        string Category,
        string ConvertedAt,
        string Status);

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
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path  TEXT NOT NULL,
                output_path  TEXT NOT NULL,
                category     TEXT NOT NULL,
                converted_at TEXT NOT NULL,
                status       TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public void Log(string sourcePath, string outputPath, FileCategory category, ResultStatus status)
    {
        try
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO conversions (source_path, output_path, category, converted_at, status)
                    VALUES ($src, $out, $cat, $ts, $st)
                    """;
                cmd.Parameters.AddWithValue("$src", sourcePath);
                cmd.Parameters.AddWithValue("$out", outputPath);
                cmd.Parameters.AddWithValue("$cat", category.ToString());
                cmd.Parameters.AddWithValue("$ts",  DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$st",  status.ToString());
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
                SELECT id, source_path, output_path, category, converted_at, status
                FROM conversions ORDER BY id DESC LIMIT {limit}
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<Entry>();
            while (r.Read())
                list.Add(new Entry(r.GetInt32(0), r.GetString(1), r.GetString(2),
                                   r.GetString(3), r.GetString(4), r.GetString(5)));
            return list;
        }
    }

    /// <summary>Returns all source paths that were previously processed successfully.</summary>
    public HashSet<string> GetProcessedSourcePaths()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT source_path FROM conversions WHERE status = 'Success'";
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
