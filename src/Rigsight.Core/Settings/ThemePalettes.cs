namespace Rigsight.Core.Settings;

/// <summary>
/// The colours a widget on the "Like RigSight" theme takes from each app theme (its card, line, text and parts), copied
/// from the app's palette files (src/Rigsight/Themes, by their file name) so the agent, which has no WPF, draws the same.
/// A test checks this table against those files, so the two can't drift.
/// </summary>
public static class ThemePalettes
{
    /// <summary>Colours as #RRGGBB: the card, its line, text, muted and faint text, the second surface (a track), CPU, GPU, the theme's own accent.</summary>
    public sealed record Palette(string Surface, string Stroke, string Text, string Muted, string Faint, string Surface2, string Cpu, string Gpu, string Accent);

    /// <summary>By palette file name ("Coal" for Themes/Coal.xaml, see <see cref="ThemeCatalog.Theme.File"/>).</summary>
    public static readonly IReadOnlyDictionary<string, Palette> ByFile = new Dictionary<string, Palette>
    {
        ["Blush"] = new("#FFFAFB", "#EAD8DE", "#2B1A21", "#6A4F5A", "#745963", "#F5E7EB", "#2058D0", "#0C6882", "#2B1A21"),
        ["Bright"] = new("#FFFFFF", "#767676", "#000000", "#333333", "#474747", "#F2F2F2", "#18429C", "#094F63", "#000000"),
        ["Carbon"] = new("#1A1A1A", "#2A2A2A", "#E6E6E6", "#A8A8A8", "#989898", "#232323", "#6E99FF", "#22D3EE", "#FFFFFF"),
        ["Cloud"] = new("#FFFFFF", "#E3E8F0", "#111827", "#4B5563", "#5B6474", "#F0F3F8", "#225CDA", "#0D6D88", "#111827"),
        ["Coal"] = new("#1A1817", "#2A2623", "#EDEAE6", "#ADA79F", "#9A948C", "#24211F", "#6E99FF", "#22D3EE", "#FFFFFF"),
        ["Contrast"] = new("#000000", "#8A8A8A", "#FFFFFF", "#DADADA", "#C4C4C4", "#141414", "#8FB1FF", "#22D3EE", "#FFFFFF"),
        ["Dark"] = new("#0A0A0A", "#1F1F1F", "#EDEDED", "#A3A3A3", "#8C8C8C", "#161616", "#5B8CFF", "#22D3EE", "#FFFFFF"),
        ["Dim"] = new("#303236", "#3E4046", "#F2F3F5", "#B8BCC3", "#B2B6BD", "#393B40", "#96B5FF", "#22D3EE", "#FFFFFF"),
        ["Ember"] = new("#1D140E", "#32241A", "#FFF6EE", "#CDB7A4", "#A79281", "#2A1E15", "#6E99FF", "#22D3EE", "#FB923C"),
        ["Forest"] = new("#0E1B15", "#1A3025", "#EFFAF4", "#A6C2B3", "#89A496", "#16291F", "#6E99FF", "#22D3EE", "#4ADE80"),
        ["Graphite"] = new("#1B1E23", "#2A2E36", "#E8EBF0", "#A9B0BC", "#979EAA", "#24282F", "#6E99FF", "#22D3EE", "#FFFFFF"),
        ["Grey"] = new("#262626", "#353535", "#FFFFFF", "#ADADAD", "#A6A6A6", "#303030", "#7FA6FF", "#22D3EE", "#FFFFFF"),
        ["Indigo"] = new("#141632", "#232750", "#EEEFFF", "#B4B8E2", "#989CCC", "#1E2146", "#6E99FF", "#22D3EE", "#818CF8"),
        ["Ink"] = new("#111722", "#1E2634", "#E6EBF2", "#A3ADBD", "#8994A6", "#19202D", "#6E99FF", "#22D3EE", "#FFFFFF"),
        ["Lavender"] = new("#FCFBFF", "#DED9EF", "#1F1A33", "#554D70", "#625A7E", "#ECE8F7", "#2058D0", "#0C6882", "#1F1A33"),
        ["Light"] = new("#FFFFFF", "#E3E3E3", "#000000", "#555555", "#666666", "#F3F3F3", "#225CDA", "#0D6D88", "#000000"),
        ["Lime"] = new("#131A0F", "#222E1B", "#F1F8EA", "#B6C7A6", "#9AAE88", "#1C2616", "#6E99FF", "#22D3EE", "#A3E635"),
        ["Midnight"] = new("#10162B", "#1E2747", "#EEF2FF", "#AAB4D4", "#8E99BA", "#19213D", "#6E99FF", "#22D3EE", "#7AA2FF"),
        ["Mist"] = new("#FAFBFD", "#DBE1EA", "#18202B", "#526072", "#5A6575", "#E9EDF3", "#215BD7", "#0D6D88", "#18202B"),
        ["Mocha"] = new("#1F1712", "#33271F", "#F7EDE4", "#CBB7A6", "#B09B8A", "#2B201A", "#6E99FF", "#22D3EE", "#E3B778"),
        ["Neon"] = new("#150F2B", "#261C48", "#F3EEFF", "#BEB2E0", "#A194C9", "#20173D", "#6E99FF", "#22D3EE", "#F472D0"),
        ["Ocean"] = new("#0A212B", "#163544", "#E6F8FC", "#A2C6D2", "#86ADBA", "#112D39", "#779FFF", "#22D3EE", "#2DD4BF"),
        ["Paper"] = new("#FCFAF6", "#E3DDD2", "#2A2520", "#5C544B", "#685F55", "#EEEAE2", "#2159D4", "#0D6A84", "#2A2520"),
        ["Plum"] = new("#1B1128", "#2D1E41", "#F5EEFF", "#C3B3DD", "#A595C2", "#271938", "#6E99FF", "#22D3EE", "#C084FC"),
        ["Rose"] = new("#231117", "#3A1D26", "#FFEFF2", "#DCB2BC", "#C0959F", "#321820", "#6E99FF", "#22D3EE", "#FB7185"),
        ["Sage"] = new("#F9FBF9", "#D9E1D9", "#17211A", "#506052", "#576659", "#E6ECE6", "#2159D4", "#0D6B86", "#17211A"),
        ["Sand"] = new("#FBF7F0", "#E0D5C2", "#2E2417", "#625542", "#685A48", "#ECE3D4", "#1F54C7", "#0C657E", "#2E2417"),
        ["Slate"] = new("#212731", "#313846", "#E9EDF3", "#AEB7C5", "#A1AAB8", "#2A313D", "#84A8FF", "#22D3EE", "#FFFFFF"),
        ["Soft"] = new("#FBFBFB", "#E1E1E1", "#1C1C1C", "#585858", "#646464", "#EDEDED", "#215BD7", "#0D6B86", "#1C1C1C"),
        ["Stone"] = new("#282624", "#383532", "#F0EDE9", "#B9B3AC", "#ABA59D", "#32302D", "#7FA5FF", "#22D3EE", "#FFFFFF"),
    };
}
