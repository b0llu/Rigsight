namespace Rigsight.Core.Settings;

/// <summary>
/// What to try (1.21.0): the steps someone said they tried, and the PC health lines they said they know about.
/// </summary>
public sealed class TrySettings
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>"Tried it", by block and step ("shutoff-power|p1"), with the day it was pressed: kept for good, never "Fixed".</summary>
    public Dictionary<string, DateTime>? Tried { get; set; }

    /// <summary>
    /// "I know about this": PC health lines by key, each with the line as it read when hidden (empty until Home first sees
    /// it). The line comes back by itself when it gets worse: any of its figures higher than when it was hidden.
    /// </summary>
    public Dictionary<string, string>? Hidden { get; set; }
}
