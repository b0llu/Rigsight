using Rigsight.Agent.Tracking;

namespace Rigsight.Tests.Agent;

/// <summary>What each process of an app is, on the Memory page: roles read from command lines, titles, order.</summary>
public class ProcessLabelsTests
{
    private static string? Role(string exe, string? commandLine) =>
        ProcessLabels.Role(exe, commandLine, service => $"[{service}]");

    // ── Chrome, Edge and Electron ──

    [Theory]
    [InlineData("chrome.exe", "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --type=renderer --lang=en-US --renderer-client-id=7", "Tab")]
    [InlineData("msedge.exe", "\"msedge.exe\" --type=renderer --js-flags=--expose-gc", "Tab")]
    [InlineData("brave.exe", "brave.exe --type=renderer", "Tab")]
    [InlineData("zen.exe", "zen.exe --type=renderer", "Tab")]
    [InlineData("discord.exe", "\"Discord.exe\" --type=renderer --autoplay-policy=no-user-gesture-required", "Window")]
    [InlineData("code.exe", "Code.exe --type=renderer --enable-sandbox", "Window")]
    [InlineData("chrome.exe", "chrome.exe --type=renderer --extension-process --renderer-client-id=5", "Extension")]
    [InlineData("code.exe", "Code.exe --type=renderer --extension-process", "Extension")]
    [InlineData("chrome.exe", "chrome.exe --type=gpu-process --gpu-preferences=UAAAAAAAAADgAAA", "Graphics")]
    [InlineData("spotify.exe", "Spotify.exe --type=gpu-process", "Graphics")]
    [InlineData("chrome.exe", "chrome.exe --type=crashpad-handler \"--user-data-dir=C:\\Users\\me\\AppData\\Local\\Google\\Chrome\\User Data\"", "Crash reporter")]
    [InlineData("chrome.exe", "chrome.exe --type=utility --utility-sub-type=network.mojom.NetworkService --lang=en-US", "Network")]
    [InlineData("msedge.exe", "msedge.exe --type=utility --utility-sub-type=storage.mojom.StorageService", "Storage")]
    [InlineData("chrome.exe", "chrome.exe --type=utility --utility-sub-type=audio.mojom.AudioService", "Audio")]
    [InlineData("chrome.exe", "chrome.exe --type=utility --utility-sub-type=video_capture.mojom.VideoCaptureService", "Camera")]
    [InlineData("chrome.exe", "chrome.exe --type=utility --utility-sub-type=data_decoder.mojom.DataDecoderService", "Helper")]
    [InlineData("chrome.exe", "chrome.exe --type=utility", "Helper")]
    [InlineData("chrome.exe", "chrome.exe --type=utility --utility-sub-type=", "Helper")]
    [InlineData("chrome.exe", "chrome.exe --type=broker", "Helper")]
    [InlineData("chrome.exe", "chrome.exe --type=ppapi", "Helper")]
    public void Chromium_process_types(string exe, string commandLine, string expected) =>
        Assert.Equal(expected, Role(exe, commandLine));

    [Theory]
    [InlineData("chrome.exe --type=\"renderer\"", "Tab")]
    [InlineData("chrome.exe \"--type=gpu-process\" --x", "Graphics")]
    [InlineData("chrome.exe --type=utility \"--utility-sub-type=network.mojom.NetworkService\"", "Network")]
    [InlineData("chrome.exe --type=renderer", "Tab")]                 // value at the very end
    public void Quoted_and_trailing_values(string commandLine, string expected) =>
        Assert.Equal(expected, Role("chrome.exe", commandLine));

    [Theory]
    [InlineData("chrome.exe --type=")]
    [InlineData("chrome.exe --type= --x")]
    [InlineData("chrome.exe --type=\"\"")]
    public void An_empty_type_is_no_role(string commandLine) => Assert.Null(Role("chrome.exe", commandLine));

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"")]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --restore-last-session")]
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\Discord\\app-1.0.9185\\Discord.exe\" --processStart Discord.exe")]
    [InlineData("")]
    public void The_main_process_has_no_role(string commandLine) => Assert.Null(Role("chrome.exe", commandLine));

    [Fact]
    public void An_unreadable_command_line_has_no_role() => Assert.Null(Role("chrome.exe", null));

    [Fact]
    public void A_utility_sub_type_elsewhere_doesnt_make_a_type() =>
        Assert.Null(Role("chrome.exe", "chrome.exe --utility-sub-type=network.mojom.NetworkService"));

    // ── Firefox ──

    [Theory]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -contentproc -childID 1 -isForBrowser -prefsLen 29060 -parentBuildID 20240213 -appDir \"C:\\Program Files\\Mozilla Firefox\\browser\" {2c4e} 11760 tab", "Tab")]
    [InlineData("firefox.exe -contentproc {a} 7072 gpu", "Graphics")]
    [InlineData("firefox.exe -contentproc {a} 7072 socket", "Network")]
    [InlineData("firefox.exe -contentproc {a} 7072 rdd", "Media")]
    [InlineData("firefox.exe -contentproc {a} 7072 utility", "Helper")]
    [InlineData("firefox.exe -contentproc {a} 7072 forkserver", "Helper")]
    [InlineData("firefox.exe -contentproc {a} 7072 tab   ", "Tab")]
    [InlineData("zen.exe -contentproc {a} 7072 tab", "Tab")]
    public void Firefox_content_processes(string commandLine, string expected) =>
        Assert.Equal(expected, Role("firefox.exe", commandLine));

    [Theory]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\"")]
    [InlineData("firefox.exe -contentprocess")]  // not the flag (and not at a word start either)
    [InlineData("firefox.exe-contentproc tab")]
    public void Firefox_main_process(string commandLine)
    {
        var role = Role("firefox.exe", commandLine);
        Assert.True(role is null or "Helper", role); // at worst a helper, never a tab
        Assert.NotEqual("Tab", role);
    }

    // ── Service hosts ──

    [Theory]
    [InlineData("C:\\WINDOWS\\system32\\svchost.exe -k netsvcs -p -s Schedule", "[Schedule]")]
    [InlineData("C:\\WINDOWS\\system32\\svchost.exe -k LocalSystemNetworkRestricted -p -s NcbService", "[NcbService]")]
    [InlineData("svchost.exe -k UnistackSvcGroup -s CDPUserSvc", "[CDPUserSvc]")]
    [InlineData("C:\\WINDOWS\\system32\\svchost.exe -k DcomLaunch -p", "Services (DcomLaunch)")]
    [InlineData("C:\\WINDOWS\\System32\\svchost.exe -k LocalServiceNoNetworkFirewall -p", "Services (LocalServiceNoNetworkFirewall)")]
    [InlineData("svchost.exe -k \"netsvcs\"", "Services (netsvcs)")]
    [InlineData("svchost.exe -s \"Schedule\"", "[Schedule]")]
    public void Service_hosts(string commandLine, string expected) =>
        Assert.Equal(expected, Role("svchost.exe", commandLine));

    [Theory]
    [InlineData("C:\\WINDOWS\\system32\\svchost.exe")]
    [InlineData("svchost.exe -p")]
    public void A_service_host_without_its_group_has_no_role(string commandLine) =>
        Assert.Null(Role("svchost.exe", commandLine));

    [Fact]
    public void Service_names_are_only_looked_up_for_service_hosts() =>
        Assert.Null(Role("other.exe", "other.exe -k netsvcs -s Schedule"));

    [Fact]
    public void Service_host_case_doesnt_matter() =>
        Assert.Equal("Services (netsvcs)", Role("SvcHost.EXE", "svchost.exe -k netsvcs"));

    [Fact]
    [Trait("Category", "Machine")]
    public void A_real_service_gets_its_display_name_from_Windows()
    {
        // The real lookup: Task Scheduler is on every Windows PC.
        var labels = new ProcessLabels { CommandLine = _ => "C:\\WINDOWS\\system32\\svchost.exe -k netsvcs -p -s Schedule" };
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("svchost.exe", "Windows service", [new ProcessUsage(1, 1, 0, 10)], ref titles);
        // "Task Scheduler" in English: read from inside a Windows file, in the PC's language.
        var label = Assert.Single(list).Label;
        Assert.NotEqual("Schedule", label);
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.DoesNotContain("@", label);
    }

    [Fact]
    public void An_unknown_service_keeps_its_short_name()
    {
        var labels = new ProcessLabels { CommandLine = _ => "svchost.exe -k netsvcs -s NoSuchServiceRigsightTest" };
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("svchost.exe", "Windows service", [new ProcessUsage(1, 1, 0, 10)], ref titles);
        Assert.Equal("NoSuchServiceRigsightTest", Assert.Single(list).Label);
    }

    // ── Describe ──

    private static ProcessLabels With(Dictionary<int, string?> commandLines, List<int>? asked = null) => new()
    {
        CommandLine = pid =>
        {
            asked?.Add(pid);
            return commandLines.GetValueOrDefault(pid);
        },
    };

    [Fact]
    public void In_a_browser_the_process_without_a_role_is_the_main_one()
    {
        var labels = With(new()
        {
            [10] = "chrome.exe",
            [11] = "chrome.exe --type=renderer",
            [12] = "chrome.exe --type=gpu-process",
            [13] = "chrome.exe --type=utility --utility-sub-type=network.mojom.NetworkService",
        });
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("chrome.exe", "Google Chrome",
            [new(10, 1, 1, 300), new(11, 1, 2, 200), new(12, 1, 3, 150), new(13, 1, 0, 20)], ref titles);
        Assert.Equal(["Main process", "Tab", "Graphics", "Network"], list.Select(p => p.Label));
    }

    [Fact]
    public void A_single_process_app_is_named_after_the_app()
    {
        var labels = With(new() { [10] = "notepad.exe C:\\notes.txt", [11] = "notepad.exe" });
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("notepad.exe", "Notepad", [new(10, 1, 0, 30), new(11, 1, 0, 20)], ref titles);
        Assert.All(list, p => Assert.Equal("Notepad", p.Label));
    }

    [Fact]
    public void Unreadable_command_lines_fall_back_to_the_app_or_main_process()
    {
        var labels = With(new() { [11] = "discord.exe --type=renderer" }); // 10 and 12 can't be read
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("discord.exe", "Discord", [new(10, 1, 0, 300), new(11, 1, 0, 200), new(12, 1, 0, 100)], ref titles);
        Assert.Equal(["Main process", "Window", "Main process"], list.Select(p => p.Label));
    }

    [Fact]
    public void A_service_host_without_a_readable_command_line_is_just_a_service()
    {
        var labels = With(new() { [10] = "svchost.exe -k netsvcs" });
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("svchost.exe", "Windows service", [new(10, 1, 0, 50), new(11, 1, 0, 40)], ref titles);
        Assert.Equal(["Services (netsvcs)", "Windows service"], list.Select(p => p.Label));
    }

    [Fact]
    public void A_window_title_wins_over_everything()
    {
        var labels = With(new() { [10] = "chrome.exe", [11] = "chrome.exe --type=renderer" });
        Dictionary<int, string>? titles = new() { [10] = "Rigsight - GitHub - Google Chrome", [11] = "Some tab" };
        var list = labels.Describe("chrome.exe", "Google Chrome", [new(10, 1, 0, 300), new(11, 1, 0, 200)], ref titles);
        Assert.Equal(["Rigsight - GitHub - Google Chrome", "Some tab"], list.Select(p => p.Label));
    }

    [Fact]
    public void Biggest_first_with_rounded_numbers()
    {
        var labels = With([]);
        Dictionary<int, string>? titles = [];
        var list = labels.Describe("app.exe", "App",
            [new(1, 1, 0.04, 10.04), new(2, 1, 12.345, 500.06), new(3, 1, 0, 0), new(4, 1, 3.33, 90.96)], ref titles);
        Assert.Equal([2, 4, 1, 3], list.Select(p => p.Pid));
        Assert.Equal([500.1, 91.0, 10.0, 0], list.Select(p => p.MemMB));
        Assert.Equal(12.3, list[0].Cpu);
        Assert.Equal(0, list[2].Cpu);
    }

    [Fact]
    public void No_processes_no_labels()
    {
        Dictionary<int, string>? titles = [];
        Assert.Empty(With([]).Describe("app.exe", "App", [], ref titles));
    }

    [Fact]
    public void Window_titles_are_read_once_when_first_needed()
    {
        // Null asks Describe to read the real window titles (read-only), shared by the apps described after.
        var labels = With([]);
        Dictionary<int, string>? titles = null;
        labels.Describe("a.exe", "A", [new(1, 1, 0, 1)], ref titles);
        Assert.NotNull(titles);
        var first = titles;
        labels.Describe("b.exe", "B", [new(2, 1, 0, 1)], ref titles);
        Assert.Same(first, titles);
    }

    [Fact]
    public void Each_process_command_line_is_read_once()
    {
        var asked = new List<int>();
        var labels = With(new() { [10] = "chrome.exe", [11] = "chrome.exe --type=renderer" }, asked);
        Dictionary<int, string>? titles = [];
        for (int i = 0; i < 5; i++)
            labels.Describe("chrome.exe", "Chrome", [new(10, 100, 0, 1), new(11, 100, 0, 1)], ref titles);
        Assert.Equal([10, 11], asked);
    }

    [Fact]
    public void A_reused_process_id_is_read_again()
    {
        // Windows reuses ids: the same id with a new start time is a different process.
        var asked = new List<int>();
        var lines = new Dictionary<int, string?> { [10] = "chrome.exe --type=renderer" };
        var labels = With(lines, asked);
        Dictionary<int, string>? titles = [];
        Assert.Equal("Tab", labels.Describe("chrome.exe", "Chrome", [new(10, 100, 0, 1)], ref titles).Single().Label);
        lines[10] = "chrome.exe --type=gpu-process";
        Assert.Equal("Tab", labels.Describe("chrome.exe", "Chrome", [new(10, 100, 0, 1)], ref titles).Single().Label);
        Assert.Equal("Graphics", labels.Describe("chrome.exe", "Chrome", [new(10, 200, 0, 1)], ref titles).Single().Label);
        Assert.Equal([10, 10], asked);
    }

    [Fact]
    public void An_unreadable_command_line_is_remembered_too()
    {
        var asked = new List<int>();
        var labels = With([], asked);
        Dictionary<int, string>? titles = [];
        labels.Describe("app.exe", "App", [new(10, 1, 0, 1)], ref titles);
        labels.Describe("app.exe", "App", [new(10, 1, 0, 1)], ref titles);
        Assert.Single(asked);
    }

    [Fact]
    public void The_role_cache_is_bounded()
    {
        var asked = new List<int>();
        var labels = With([], asked);
        Dictionary<int, string>? titles = [];
        var many = Enumerable.Range(1, 4001).Select(pid => new ProcessUsage(pid, 1, 0, 1)).ToList();
        labels.Describe("app.exe", "App", many, ref titles);
        Assert.Equal(4001, asked.Count);

        // Over 4,000 remembered: the cache starts again, so pid 1 is read afresh.
        labels.Describe("app.exe", "App", [new(1, 1, 0, 1)], ref titles);
        Assert.Equal(4002, asked.Count);
        labels.Describe("app.exe", "App", [new(1, 1, 0, 1)], ref titles);
        Assert.Equal(4002, asked.Count);
    }

    [Fact]
    public void Up_to_the_limit_the_cache_is_kept()
    {
        var asked = new List<int>();
        var labels = With([], asked);
        Dictionary<int, string>? titles = [];
        labels.Describe("app.exe", "App", [.. Enumerable.Range(1, 4000).Select(pid => new ProcessUsage(pid, 1, 0, 1))], ref titles);
        labels.Describe("app.exe", "App", [new(1, 1, 0, 1)], ref titles);
        Assert.Equal(4000, asked.Count);
    }
}
