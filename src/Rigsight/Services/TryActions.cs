using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>
/// What What to try's buttons reach, set up once by the shell (like <see cref="AiGate"/>): pages, the agent, the saved
/// "Tried it" days and hidden PC health lines, and the few things a step can do. The tests give their own.
/// </summary>
public static class TryActions
{
    public static SettingsModel? Settings { get; set; }
    /// <summary>Opens a RigSight page by name, with what to show on it.</summary>
    public static Action<string, string?>? OpenPage { get; set; }
    /// <summary>Sends the agent a command.</summary>
    public static Action<string, string?>? SendAgent { get; set; }
    /// <summary>The agent is running with admin rights, so it can set Windows Memory Diagnostic for the next start.</summary>
    public static Func<bool> AgentIsAdmin { get; set; } = () => false;
    /// <summary>A standard account: Windows asks for an administrator for the memory test.</summary>
    public static Func<bool> StandardAccount { get; set; } = () => false;

    internal static AppCloser Closer { get; set; } = new();
    internal static Action<string> Launch { get; set; } = target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// <summary>A step was marked tried or a line hidden or shown again: Home reads its hidden lines again.</summary>
    public static event Action? Changed;

    // The blocks that can hide a line, held weakly (a page makes new ones as it reads again), told when one is hidden or shown.
    private static readonly List<WeakReference<Models.TryBlockView>> Blocks = [];

    internal static void Watch(Models.TryBlockView view)
    {
        lock (Blocks)
        {
            Blocks.RemoveAll(w => !w.TryGetTarget(out _));
            Blocks.Add(new WeakReference<Models.TryBlockView>(view));
        }
    }

    private static void HiddenChanged()
    {
        List<Models.TryBlockView> alive;
        lock (Blocks) alive = [.. Blocks.Select(w => w.TryGetTarget(out var v) ? v : null).OfType<Models.TryBlockView>()];
        foreach (var v in alive) v.Refresh();
        Changed?.Invoke();
    }

    private static string StepKey(string block, string step) => $"{block}|{step}";

    public static DateTime? TriedOn(string block, string step) =>
        Settings?.Current.Try.Tried is { } t && t.TryGetValue(StepKey(block, step), out var day) ? day : null;

    /// <summary>The first day any of a block's steps was tried, for "Not seen since you tried a step on…".</summary>
    public static DateTime? FirstTried(string block) =>
        Settings?.Current.Try.Tried?.Where(p => p.Key.StartsWith(block + "|", StringComparison.Ordinal)).Select(p => (DateTime?)p.Value).Min();

    public static void MarkTried(string block, string step)
    {
        var day = DateTime.Now.Date;
        Log.Write("try", $"Tried {StepKey(block, step)} on {day:yyyy-MM-dd}");
        Settings?.Update(s => (s.Try.Tried ??= [])[StepKey(block, step)] = day);
        Changed?.Invoke();
    }

    public static bool IsHidden(string homeKey) => Settings?.Current.Try.Hidden?.ContainsKey(homeKey) == true;

    /// <summary>"I know about this": the line leaves PC health until it gets worse (Home notes how it read the first time it hides it).</summary>
    public static void Hide(string homeKey)
    {
        Log.Write("try", $"Hid {homeKey} from PC health");
        Settings?.Update(s => (s.Try.Hidden ??= []).TryAdd(homeKey, ""));
        HiddenChanged();
    }

    /// <param name="quiet">Home itself bringing a line back because it got worse: it's redrawing already.</param>
    public static void ShowAgain(string homeKey, bool quiet = false)
    {
        Log.Write("try", quiet ? $"{homeKey} came back on PC health: it reads worse than when it was hidden" : $"Showed {homeKey} on PC health again");
        Settings?.Update(s => s.Try.Hidden?.Remove(homeKey));
        if (!quiet) HiddenChanged();
    }

    /// <summary>How a hidden line read when Home first hid it ("" until then); null when it isn't hidden.</summary>
    public static string? HiddenAs(string homeKey) => Settings?.Current.Try.Hidden is { } h && h.TryGetValue(homeKey, out var was) ? was : null;

    /// <summary>Notes how a hidden line read when Home first hid it, so "worse" has something to be worse than.</summary>
    public static void NoteHidden(string homeKey, string text)
    {
        if (Settings?.Current.Try.Hidden is not { } hidden || !hidden.TryGetValue(homeKey, out var was) || was == text) return;
        Settings.Update(s => { if (s.Try.Hidden is { } h) h[homeKey] = text; });
    }

    /// <summary>
    /// Whether a line reads worse than when it was hidden: the same kind of sentence with any of its figures higher (more
    /// shut-offs, more degrees, more rpm slower); a sentence of another shape says something new, so it shows too.
    /// </summary>
    public static bool Worse(string was, string now)
    {
        if (was.Length == 0 || was == now) return false;
        var a = Figures(was);
        var b = Figures(now);
        if (a.Count != b.Count || a.Count == 0) return true;
        return a.Zip(b).Any(p => p.Second > p.First);
    }

    private static List<double> Figures(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"\d+(?:[.,]\d+)?").Select(m => double.TryParse(m.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)];

    /// <summary>
    /// The apps with a window open now, by name, for the restart's question (Google Chrome, Discord).
    /// Windows' own pieces and RigSight itself aren't named.
    /// </summary>
    public static List<string> OpenApps()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "Rigsight", "LockApp" };
        int me = Environment.ProcessId, session = Process.GetCurrentProcess().SessionId;
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == me || p.SessionId != session || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || skip.Contains(p.ProcessName)) continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch (Exception) { }
                    string name = Core.Apps.AppCatalog.ResolveName(p.ProcessName + ".exe", path);
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
                catch (Exception) { }
            }
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatus; public int LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

    /// <summary>A laptop running on its battery now.</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.AcLine == 0 && s.BatteryFlag != 128;

    /// <summary>
    /// Windows Memory Diagnostic's own result after <paramref name="since"/>, from Windows' log: when, and whether it
    /// found errors (an error or warning entry); null when there is none yet.
    /// </summary>
    public static (DateTime At, bool Errors)? MemoryResult(DateTime since)
    {
        try
        {
            string when = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000Z", System.Globalization.CultureInfo.InvariantCulture);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and TimeCreated[@SystemTime>='{when}']]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var e = reader.ReadEvent();
            if (e?.TimeCreated is not { } at) return null;
            bool errors = e.Level is 1 or 2 or 3;
            Log.Write("try", $"Memory Diagnostic's result: event {e.Id}, level {e.Level} at {at:yyyy-MM-dd HH:mm} (expected one after {since:yyyy-MM-dd HH:mm}); read as {(errors ? "errors found" : "no errors")}");
            return (at, errors);
        }
        catch (Exception ex)
        {
            Log.Write("try", $"Reading Memory Diagnostic's result failed (expected Windows' System log): {ex.Message}");
            return null;
        }
    }

    /// <summary>Shell work (the Recycle Bin) on a thread of its own, as Storage does it.</summary>
    internal static Task<T> OnOwnThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Rigsight shell work" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
