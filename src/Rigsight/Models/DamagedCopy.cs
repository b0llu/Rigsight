namespace Rigsight.Models;

/// <summary>A damaged history file a repair kept (rigsight-damaged-&lt;date&gt;.db), for Settings → History.</summary>
public sealed record DamagedCopy(string Name, string Path, string Size);
