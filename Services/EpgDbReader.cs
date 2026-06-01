using Microsoft.Data.Sqlite;

namespace EDCBViewer.Services;

public sealed class EpgDbReader
{
    private readonly string _dbPath;

    public EpgDbReader(string dbPath) => _dbPath = dbPath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_dbPath) && File.Exists(_dbPath);

    public string? GetEventInfoText(int onid, int tsid, int sid, int eventId)
    {
        if (!IsConfigured) return null;
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT short_text, ext_text FROM events " +
                "WHERE onid=$o AND tsid=$t AND sid=$s AND event_id=$e LIMIT 1";
            cmd.Parameters.AddWithValue("$o", onid);
            cmd.Parameters.AddWithValue("$t", tsid);
            cmd.Parameters.AddWithValue("$s", sid);
            cmd.Parameters.AddWithValue("$e", eventId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var shortText = r.IsDBNull(0) ? "" : r.GetString(0);
            var extText   = r.IsDBNull(1) ? "" : r.GetString(1);
            if (string.IsNullOrEmpty(shortText) && string.IsNullOrEmpty(extText)) return null;
            return string.IsNullOrEmpty(extText)   ? shortText
                 : string.IsNullOrEmpty(shortText) ? extText
                 : shortText + "\n" + extText;
        }
        catch { return null; }
    }
}
