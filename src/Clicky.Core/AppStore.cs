using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Clicky.Core;

public sealed class AppStore
{
    private readonly string path;
    public AppStore(string? directory = null)
    {
        var root = directory ?? AppPaths.Root;
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "clicky.db");
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current > 1)
            throw new InvalidOperationException("This database was created by a newer HeyBuddy. Install that version; your data has not been changed.");
        if (current < 1)
        {
            if (new FileInfo(path).Length > 0)
            {
                using var backup = new SqliteConnection($"Data Source={path}.before-v1.bak");
                backup.Open();
                connection.BackupDatabase(backup);
            }
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS history(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, kind TEXT NOT NULL,
                role TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_history_session ON history(session_id,created_at);
                CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY,prompt TEXT NOT NULL,status TEXT NOT NULL,
                created_at TEXT NOT NULL,updated_at TEXT NOT NULL,actions INTEGER NOT NULL,result TEXT NOT NULL,parent_id TEXT);
                CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY,run_id TEXT NOT NULL,kind TEXT NOT NULL,
                detail TEXT NOT NULL,created_at TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        Execute("UPDATE runs SET status='Paused',result='Interrupted by shutdown. Review the task before continuing.' WHERE status IN ('Running','Queued','AwaitingApproval')");
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public void AddMessage(string session, string kind, string role, string text) => Execute(
        "INSERT INTO history VALUES($id,$session,$kind,$role,$text,$date)", ("$id", Guid.NewGuid().ToString("N")), ("$session", session), ("$kind", kind), ("$role", role), ("$text", text), ("$date", DateTimeOffset.UtcNow.ToString("O")));
    public IReadOnlyList<HistoryEntry> GetHistory(string? query = null, string? session = null, int limit = 250)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM history WHERE ($q='' OR instr(lower(text),lower($q))>0) AND ($s='' OR session_id=$s) ORDER BY created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$q", query ?? "");
        command.Parameters.AddWithValue("$s", session ?? "");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var rows = new List<HistoryEntry>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        return rows;
    }
    public void SaveRun(AgentRun run) => Execute("""
        INSERT INTO runs VALUES($id,$prompt,$status,$created,$updated,$actions,$result,$parent)
        ON CONFLICT(id) DO UPDATE SET status=$status,updated_at=$updated,actions=$actions,result=$result;
        """, ("$id", run.Id), ("$prompt", run.Prompt), ("$status", run.Status.ToString()), ("$created", run.CreatedAt.ToString("O")), ("$updated", run.UpdatedAt.ToString("O")), ("$actions", run.Actions), ("$result", run.Result), ("$parent", run.ParentId));
    public IReadOnlyList<AgentRun> GetRuns()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM runs ORDER BY created_at DESC LIMIT 200";
        using var reader = command.ExecuteReader();
        var rows = new List<AgentRun>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), Enum.Parse<RunStatus>(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetInt32(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }
    public void AddEvent(string run, string kind, string detail) => Execute("INSERT INTO events(run_id,kind,detail,created_at) VALUES($run,$kind,$detail,$date)", ("$run", run), ("$kind", kind), ("$detail", detail), ("$date", DateTimeOffset.UtcNow.ToString("O")));
    public void DeleteHistory() => Execute("DELETE FROM history");
    public void PruneHistory(int days)
    {
        if (days > 0)
            Execute("DELETE FROM history WHERE created_at < $date", ("$date", DateTimeOffset.UtcNow.AddDays(-days).ToString("O")));
    }
    public void Backup(string destination)
    {
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }
}
