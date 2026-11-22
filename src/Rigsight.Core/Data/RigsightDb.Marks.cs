namespace Rigsight.Core.Data;

// A problem the person marked from the tray's "Something just went wrong" (issue #15, approved mock-up v6, 2026-11-22):
// when, and what the look back over the 2 minutes before found, as the Timeline shows it. A table of its own: a mark is
// not a change, so it never shows where changes show.
public sealed partial class RigsightDb
{
    private void EnsureMarkSchema() => Exec("CREATE TABLE IF NOT EXISTS problem_mark(ts INTEGER PRIMARY KEY, text TEXT NOT NULL)");

    // The app reads a database an older agent made (no table yet): nothing marked then.
    private bool? _hasMarks;
    private bool HasMarks => _hasMarks ??= HasTable("problem_mark");

    /// <summary>A problem marked at <paramref name="ts"/> (Unix seconds); a second mark in the same second replaces the first.</summary>
    public void AddProblemMark(long ts, string text)
    {
        using var cmd = Cmd("INSERT OR REPLACE INTO problem_mark(ts, text) VALUES($t, $x)", ("$t", ts), ("$x", text));
        cmd.ExecuteNonQuery();
        _hasMarks = true;
    }

    /// <summary>The marks from <paramref name="from"/> to <paramref name="to"/> (Unix seconds, the end left out), oldest first.</summary>
    public List<(long Ts, string Text)> GetProblemMarks(long from, long to)
    {
        var list = new List<(long, string)>();
        if (!HasMarks) return list;
        using var cmd = Cmd("SELECT ts, text FROM problem_mark WHERE ts >= $f AND ts < $t ORDER BY ts", ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    private void PruneMarks(long before)
    {
        if (!HasMarks) return;
        using var cmd = Cmd("DELETE FROM problem_mark WHERE ts < $t", ("$t", before));
        cmd.ExecuteNonQuery();
    }
}
