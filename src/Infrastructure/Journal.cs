using Microsoft.Data.Sqlite;
using Pome.Core;

namespace Pome.Infrastructure;

public sealed class SqliteJournal : IOperationJournal
{
    private readonly string connectionString;
    public SqliteJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY, kind TEXT NOT NULL, stage TEXT NOT NULL, detail TEXT NOT NULL, updated TEXT NOT NULL, terminal INTEGER NOT NULL); PRAGMA user_version=1;";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public void Write(JournalEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO operations VALUES($id,$kind,$stage,$detail,$updated,$terminal) ON CONFLICT(id) DO UPDATE SET stage=$stage,detail=$detail,updated=$updated,terminal=$terminal;";
        command.Parameters.AddWithValue("$id", entry.Id.ToString()); command.Parameters.AddWithValue("$kind", entry.Kind);
        command.Parameters.AddWithValue("$stage", entry.Stage); command.Parameters.AddWithValue("$detail", Redaction.Clean(entry.Detail));
        command.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$terminal", entry.Terminal ? 1 : 0);
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<JournalEntry> ReadAll()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,kind,stage,detail,updated,terminal FROM operations ORDER BY updated DESC";
        using var reader = command.ExecuteReader(); var result = new List<JournalEntry>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt32(5) != 0));
        return result;
    }
}

public static class Redaction
{
    public static string Clean(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(serial(number)?|token|password|secret)\s*[:=]\s*[^\s,;]+", "$1=[redacted]");
    }
}
