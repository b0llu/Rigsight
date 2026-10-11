namespace Rigsight.Core.Data;

/// <summary>What exporting needs of the database beyond the pages' own reads (see <see cref="Export.DataExport"/>).</summary>
public sealed partial class RigsightDb
{
    /// <summary>
    /// A whole copy of the history in one file, as it is at this moment (what is still waiting to be folded into the
    /// main file included). The file must not exist yet.
    /// </summary>
    public void CopyTo(string path)
    {
        using var cmd = Cmd("VACUUM INTO $path", ("$path", path));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// How many rows a table holds in [from, to) by its time column, for saying how big an export will be before it
    /// is made; 0 for a table this history doesn't have.
    /// </summary>
    public long CountRows(string table, string timeColumn, long from, long to)
    {
        if (!HasTable(table)) return 0;
        using var cmd = Cmd($"SELECT count(*) FROM {table} WHERE {timeColumn} >= $from AND {timeColumn} < $to", ("$from", from), ("$to", to));
        return cmd.ExecuteScalar() is long n ? n : 0;
    }
}
