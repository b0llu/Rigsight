using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>What the crash and change readers make of event log text. The machine's own logs are only read in smoke tests.</summary>
public sealed class EventLogParsingTests
{
    // ---- When the PC actually went down (event 6008: date and time in the PC's own format) ----

    [Theory]
    [InlineData("en-US", "9/25/2026", "10:30:15 PM")]
    [InlineData("en-US", "‎9/‎25/‎2026", "‎10:30:15 PM")]
    [InlineData("en-US", " 9/25/2026 ", " 10:30:15 PM ")]
    [InlineData("en-GB", "25/09/2026", "22:30:15")]
    [InlineData("en-GB", "‏25‏/09‏/2026", "22:30:15")]
    [InlineData("de-DE", "25.09.2026", "22:30:15")]
    [InlineData("ja-JP", "2026/09/25", "22:30:15")]
    [InlineData("en-US", "2026-09-25", "22:30:15")]
    [InlineData("en-US", "25/09/2026", "22:30:15")] // a day-first date on a month-first PC
    [InlineData("de-DE", "09/25/2026", "22:30:15")] // a month-first date on a day-first PC
    [InlineData("fr-FR", "25/09/2026", "22:30:15")]
    public void Shutdown_times_are_read_in_the_PCs_format_and_common_others(string culture, string date, string time)
    {
        using var _ = new CultureScope(culture);
        var parsed = CrashLogReader.ParseShutdownTime(date, time);
        Assert.Equal(new DateTime(2026, 9, 25, 22, 30, 15), parsed);
        Assert.Equal(DateTimeKind.Local, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData("en-US", 5, 9)]
    [InlineData("en-GB", 9, 5)]
    public void An_ambiguous_date_follows_the_PCs_format(string culture, int month, int day)
    {
        using var _ = new CultureScope(culture);
        Assert.Equal(new DateTime(2026, month, day, 8, 0, 0), CrashLogReader.ParseShutdownTime("05/09/2026", "08:00:00"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("yesterday", "noon")]
    [InlineData("‎‎", "‎")]
    [InlineData("32/13/2026", "25:61:61")]
    public void Unreadable_shutdown_times_are_null(string? date, string? time)
    {
        using var _ = new CultureScope("en-US");
        Assert.Null(CrashLogReader.ParseShutdownTime(date, time));
    }

    // ---- Windows updates ----

    [Theory]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.437.1.0) - Current Channel (Broad)")]
    [InlineData("Windows Malicious Software Removal Tool x64 - v5.130 (KB890830)")]
    [InlineData("Update for Microsoft Defender Antivirus antimalware platform - KB4052623 (Version 4.18.25080.5)")]
    [InlineData("Update for Windows Security platform - KB5007651 (Version 10.0.29429.1000)")]
    [InlineData("9NBLGGH4NNS1-Microsoft.DesktopAppInstaller")]
    [InlineData("9WZDNCRFJ3TJ-Netflix")]
    [InlineData("SECURITY INTELLIGENCE UPDATE")]
    public void Routine_updates_are_left_out(string title) => Assert.True(ChangeLogReader.IsRoutine(title));

    [Theory]
    [InlineData("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5065426)")]
    [InlineData("NVIDIA - Display - 32.0.15.8097")]
    [InlineData("9 things-to do")]
    [InlineData("9NBLGGH4NNS-short")]
    [InlineData("")]
    public void Real_changes_are_kept(string title) => Assert.False(ChangeLogReader.IsRoutine(title));

    [Theory]
    [InlineData("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5065426)", "Windows update KB5065426")]
    [InlineData("2026-09 Security Update for Windows 10 Version 22H2 (KB5066791)", "Windows update KB5066791")]
    [InlineData("Cumulative Update for Windows 11 Insider Preview (10.0.26200.1000)", "Windows update")]
    [InlineData("2026-09 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB5064401)", ".NET update KB5064401")]
    [InlineData("2026-09 .NET 8.0.20 Security Update for x64 Client (KB5066133)", ".NET update KB5066133")]
    [InlineData("Microsoft .NET 9.0.9 Update", ".NET update")]
    [InlineData("LG Electronics Inc. Extension Driver Update (1.1.2026.6241)", "LG Electronics driver 1.1.2026.6241 (from Windows Update)")]
    [InlineData("NVIDIA - Display - 32.0.15.8097", "NVIDIA display driver 32.0.15.8097 (from Windows Update)")]
    [InlineData("Advanced Micro Devices, Inc. - System - 2.0.0.1", "AMD system driver 2.0.0.1 (from Windows Update)")]
    [InlineData("Intel Corporation - Extension - 10.29.0.1", "Intel extension driver 10.29.0.1 (from Windows Update)")]
    [InlineData("Realtek Semiconductor Corp. - MEDIA - 6.0.9600.1", "Realtek Semiconductor media driver 6.0.9600.1 (from Windows Update)")]
    [InlineData("Logitech - Keyboard - HID - 1.2.3", "Logitech keyboard driver 1.2.3 (from Windows Update)")]
    [InlineData("Feature update to Windows 11, version 25H2", "Feature update to Windows 11, version 25H2")]
    [InlineData("Foo - Bar", "Foo - Bar")]
    public void Updates_are_described_simply(string title, string expected) => Assert.Equal(expected, ChangeLogReader.Describe(title));

    [Fact]
    public void Long_titles_are_cut_to_seventy_characters()
    {
        var seventy = new string('x', 70);
        Assert.Equal(seventy, ChangeLogReader.Describe(seventy));
        var described = ChangeLogReader.Describe(new string('y', 90));
        Assert.Equal(new string('y', 67) + "…", described);
        Assert.Equal(68, described.Length);
    }

    [Theory]
    [InlineData("NVIDIA Corporation", "NVIDIA")]
    [InlineData("Realtek Semiconductor Corp.", "Realtek Semiconductor")]
    [InlineData("Advanced Micro Devices, Inc.", "AMD")]
    [InlineData("Advanced Micro Devices Inc.", "AMD")]
    [InlineData("Intel(R) Corporation", "Intel")]
    [InlineData("Intel Corporation", "Intel")]
    [InlineData("Logitech LLC", "Logitech")]
    [InlineData("Qualcomm Inc.", "Qualcomm")]
    [InlineData("  Samsung  ", "Samsung")]
    [InlineData("", "")]
    public void Provider_names_are_shortened(string provider, string expected) => Assert.Equal(expected, ChangeLogReader.Shorten(provider));

    private static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0);

    [Fact]
    public void A_driver_also_listed_by_Windows_Update_is_listed_once()
    {
        var merged = ChangeLogReader.Merge(
        [
            new(T0, ChangeKind.WindowsUpdate, "NVIDIA display driver 32.0.15.8097 (from Windows Update)"),
            new(T0.AddHours(1), ChangeKind.Driver, "NVIDIA graphics driver 32.0.15.8097"),
        ]);
        Assert.Equal([ChangeKind.Driver], merged.Select(c => c.Kind));
    }

    [Theory]
    [InlineData(-2.9, 1)]
    [InlineData(2.9, 1)]
    [InlineData(3.1, 2)]
    [InlineData(-3.1, 2)]
    [InlineData(48, 2)]
    public void Only_the_same_version_within_three_hours_counts_as_the_same(double hours, int expected)
    {
        var merged = ChangeLogReader.Merge(
        [
            new(T0, ChangeKind.Driver, "Realtek audio driver 6.0.9600.1"),
            new(T0.AddHours(hours), ChangeKind.WindowsUpdate, "Realtek media driver 6.0.9600.1 (from Windows Update)"),
        ]);
        Assert.Equal(expected, merged.Count);
    }

    [Fact]
    public void Other_versions_and_updates_without_one_are_kept_in_time_order()
    {
        var merged = ChangeLogReader.Merge(
        [
            new(T0.AddHours(2), ChangeKind.WindowsUpdate, "Windows update KB5065426"),
            new(T0.AddHours(1), ChangeKind.WindowsUpdate, "NVIDIA display driver 32.0.15.9000 (from Windows Update)"),
            new(T0, ChangeKind.Driver, "NVIDIA graphics driver 32.0.15.8097"),
            new(T0.AddMinutes(30), ChangeKind.Driver, "PawnIO sensor driver 2.0.1"),
        ]);
        Assert.Equal(4, merged.Count);
        Assert.True(merged.Select(c => c.Time).SequenceEqual(merged.Select(c => c.Time).Order()));
    }

    [Fact]
    public void Two_drivers_are_never_merged()
    {
        var merged = ChangeLogReader.Merge(
        [
            new(T0, ChangeKind.Driver, "Intel system driver 10.1.1.44"),
            new(T0, ChangeKind.Driver, "Intel system driver 10.1.1.44"),
        ]);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Nothing_merges_into_nothing() => Assert.Empty(ChangeLogReader.Merge([]));

    [Fact]
    public void A_change_reads_briefly()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("Windows update KB1 (5 Sep)", new SystemChange(new DateTime(2026, 9, 5, 10, 0, 0), ChangeKind.WindowsUpdate, "Windows update KB1").Short);
    }

    // ---- The real event logs: whatever is on this PC, reading never fails ----

    [Fact]
    public void Reading_this_PCs_crashes_does_not_throw()
    {
        var crashes = CrashLogReader.ReadSince(DateTime.Now.AddDays(-1));
        Assert.True(crashes.Select(c => c.Ts).SequenceEqual(crashes.Select(c => c.Ts).Order()));
        Assert.All(crashes, c => Assert.True(Enum.IsDefined(c.Kind)));
        Assert.All(crashes, c => Assert.False(string.IsNullOrWhiteSpace(CrashExplainer.Explain(c).Title)));
        Assert.Null(CrashLogReader.RecentAppCrash("rigsight-no-such-app.exe", TimeSpan.FromMinutes(5)));
        Assert.All(CrashLogReader.ReadDumps(DateTime.Now.AddDays(-1)), d => Assert.False(string.IsNullOrWhiteSpace(d.Path)));
    }

    [Fact]
    public void Reading_from_the_future_finds_nothing() => Assert.Empty(CrashLogReader.ReadSince(DateTime.Now.AddDays(1)));

    [Fact]
    public void Reading_this_PCs_changes_does_not_throw()
    {
        var changes = ChangeLogReader.Read(DateTime.Now.AddDays(-7), DateTime.Now);
        Assert.True(changes.Select(c => c.Time).SequenceEqual(changes.Select(c => c.Time).Order()));
        Assert.All(changes, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
        Assert.Empty(ChangeLogReader.Read(DateTime.Now, DateTime.Now.AddDays(-1)));
    }
}
