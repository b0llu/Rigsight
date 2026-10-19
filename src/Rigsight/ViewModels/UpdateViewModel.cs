using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Updates;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public enum UpdateState
{
    /// <summary>Up to date, or not checked yet.</summary>
    None,
    Available,
    Downloading,
    /// <summary>Downloaded: asking whether to restart now.</summary>
    Ready,
    /// <summary>Downloaded, and the user chose "Later".</summary>
    Waiting,
    Installing,
    Failed,
}

/// <summary>
/// Updates from inside the app. The first time the app opens each day it asks GitHub for the latest release
/// (the agent's background updater does the same while the app is closed; they share the answer and the
/// download in %LocalAppData%\Rigsight\updates). A newer version downloads by itself when automatic updates
/// are on (otherwise a quiet line in the sidebar offers it); the installer is checked against GitHub's SHA-256,
/// then the sidebar asks to restart. "Later" leaves a line, the question comes back once a day, and with
/// automatic updates the agent installs it the next time Windows starts. The agent runs the installer with its
/// admin rights, so there's no UAC prompt; without the agent, Windows asks once.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    /// <summary>Where to get the installer by hand when updating in the app keeps failing.</summary>
    public const string DownloadPage = "https://rigsight.app/#install";

    private static string PromptedFile => Path.Combine(UpdateStore.Folder, "prompted");

    private readonly AgentClient _client;
    private readonly Func<bool> _agentCanInstall;
    private readonly Func<bool> _autoUpdate;
    private readonly Func<bool> _checksOn;
    // A standard account can't install an update itself. Either the PC keeps Rigsight up to date for it (nothing to
    // show, nothing to press), or someone with an admin's password has to (said so, with the one button for it).
    private readonly Func<bool> _pcUpdates, _needsAdmin;
    private Release? _release;
    private DateTime _checkedDay;
    private DispatcherTimer? _agentTimeout;
    private bool _checked;
    private string? _checkError;

    /// <param name="checksOn">Whether RigSight may look for a new version by itself (off: only when "Check for updates" is pressed).</param>
    public UpdateViewModel(AgentClient client, Func<bool> agentCanInstall, Func<bool> autoUpdate, Func<bool>? pcUpdates = null, Func<bool>? needsAdmin = null,
        Func<bool>? checksOn = null)
    {
        _client = client;
        _agentCanInstall = agentCanInstall;
        _autoUpdate = autoUpdate;
        _checksOn = checksOn ?? (() => true);
        _pcUpdates = pcUpdates ?? (() => false);
        _needsAdmin = needsAdmin ?? (() => false);
    }

    /// <summary>The agent said what kind of account this is: what is shown about updates may differ.</summary>
    public void AccountChanged() => OnPropertyChanged(string.Empty);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLine), nameof(ShowProgress), nameof(ShowPrompt), nameof(ShowFailed), nameof(LineTitle), nameof(LineIcon),
        nameof(LineTip), nameof(ProgressTitle), nameof(PromptText), nameof(StatusText), nameof(ActionText), nameof(IsBusy), nameof(ActionIsPrimary),
        nameof(AnswerText), nameof(AnswerIcon))]
    [NotifyCanExecuteChangedFor(nameof(ActionCommand))]
    private UpdateState _state;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailedText), nameof(StatusText))]
    private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ActionText), nameof(IsBusy), nameof(AnswerText), nameof(AnswerIcon))]
    [NotifyCanExecuteChangedFor(nameof(ActionCommand))]
    private bool _isChecking;

    public string Version => _release?.Version.ToString(3) ?? "";
    public bool IsBusy => IsChecking || State is UpdateState.Downloading or UpdateState.Installing;

    // ── Sidebar: one quiet line, a progress card, the restart prompt, or what went wrong ──

    public bool ShowLine => !_pcUpdates() && State is UpdateState.Available or UpdateState.Waiting;
    public bool ShowProgress => !_pcUpdates() && State is UpdateState.Downloading or UpdateState.Installing;
    public bool ShowPrompt => !_pcUpdates() && State == UpdateState.Ready;
    public bool ShowFailed => !_pcUpdates() && State == UpdateState.Failed;

    public string PromptTitle => _needsAdmin() ? Lang.T("Update needs an admin") : Lang.T("Update ready");
    // "Update" in the sidebar card: "Update now" wrapped onto two lines in half its width. Settings has room: "Update now".
    public string InstallText => _needsAdmin() ? Lang.T("Install") : Lang.T("Update");

    public string LineTitle => State == UpdateState.Waiting ? Lang.T("Update ready") : Lang.T("Update available");
    public string LineIcon => State == UpdateState.Waiting ? "" : "";

    public string LineTip => State == UpdateState.Waiting
        ? Lang.T($"RigSight {Version} is downloaded. Click to install it: RigSight closes and opens again by itself.")
        : Lang.T($"RigSight {Version} is out. Click to download it; you can keep using RigSight meanwhile.");

    public string ProgressTitle => State == UpdateState.Installing ? Lang.T($"Updating to {Version}…") : Lang.T($"Downloading {Version}");

    public string PromptText => _needsAdmin() ? Lang.T($"Version {Version} is out. Someone with an admin password can install it.")
        : _autoUpdate() && _agentCanInstall()
        ? Lang.T($"Update now to get {Version}, or it installs by itself the next time you start your PC.")
        : Lang.T($"Update now to get {Version}.");

    public string FailedText => Lang.T($"{Error} Try again, or get the latest version from the download page.");

    // ── Settings: the version, what's going on, and the one button that fits ──

    public string StatusText => State switch
    {
        _ when IsChecking => Lang.T("Checking for updates…"),
        UpdateState.Available or UpdateState.Ready or UpdateState.Waiting when _pcUpdates() => Lang.T($"Version {Version} is out. It installs the next time RigSight starts, or now."),
        UpdateState.Installing when _pcUpdates() => Lang.T("Updating: RigSight closes and reopens by itself in a minute."),
        _ when _pcUpdates() => Lang.T("This PC keeps RigSight up to date by itself."),
        UpdateState.Available or UpdateState.Ready or UpdateState.Waiting when _needsAdmin() => Lang.T($"Version {Version} is out. Installing it needs an admin's password."),
        UpdateState.Available => Lang.T($"Version {Version} is available."),
        UpdateState.Downloading => Lang.T($"Downloading version {Version}… {Progress:0}%"),
        UpdateState.Ready or UpdateState.Waiting => Lang.T($"Version {Version} is downloaded and ready to install."),
        UpdateState.Installing => Lang.T("Installing the update…"),
        UpdateState.Failed => Lang.T($"{Error} You can also get it from the download page."),
        _ when _checkError is not null => _checkError,
        _ when _checked => Lang.T("You have the latest version."),
        _ => Lang.T("RigSight checks for a new version once a day."),
    };

    /// <summary>The button is the next step of an update (not just "Check for updates"): it stands out.</summary>
    public bool ActionIsPrimary => State is UpdateState.Available or UpdateState.Ready or UpdateState.Waiting or UpdateState.Failed;

    public const string CheckIcon = "\uE72C";

    /// <summary>
    /// What the Settings menu's update line reads once it has been pressed, in place of "Check for updates": the
    /// answer in two or three words, or the next step when there is one (Download, Restart to update).
    /// </summary>
    public string AnswerText => State switch
    {
        _ when IsChecking => Lang.T("Checking…"),
        UpdateState.None when _checkError is not null => Lang.T("Couldn't check"),
        UpdateState.None when _checked => Lang.T("Up to date"),
        _ => ActionText,
    };

    /// <summary>The icon beside <see cref="AnswerText"/>: a tick when up to date, a warning when the check couldn't be made.</summary>
    public string AnswerIcon => State switch
    {
        _ when IsChecking => CheckIcon,
        UpdateState.None when _checkError is not null => "\uE7BA",
        UpdateState.None when _checked => "\uE73E",
        _ => CheckIcon,
    };

    public string ActionText => State switch
    {
        _ when IsChecking => Lang.T("Checking…"),
        UpdateState.Available when _pcUpdates() => Lang.T("Update now"),
        UpdateState.Available => Lang.T("Download"),
        UpdateState.Downloading => Lang.T("Downloading…"),
        UpdateState.Ready or UpdateState.Waiting => Lang.T("Update now"),
        UpdateState.Installing => Lang.T("Installing…"),
        UpdateState.Failed => Lang.T("Try again"),
        _ => Lang.T("Check for updates"),
    };

    partial void OnProgressChanged(double value)
    {
        if (State == UpdateState.Downloading) OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>The Settings button and the sidebar line: whatever comes next.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Action()
    {
        switch (State)
        {
            case UpdateState.None: await CheckNowAsync(); break;
            // On a PC that updates Rigsight itself there is nothing for this account to download: it asks for the update.
            case UpdateState.Available when _pcUpdates(): Install(); break;
            case UpdateState.Available: await DownloadAsync(automatic: false); break;
            case UpdateState.Ready or UpdateState.Waiting: Install(); break;
            case UpdateState.Failed: await RetryAsync(); break;
        }
    }

    private bool CanAct() => !IsBusy;

    [RelayCommand]
    private static void OpenDownloadPage()
    {
        try { Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("update", ex); }
    }

    // ── Checking ──

    /// <summary>Once a day: at startup, and again if the app is still open on a new day.</summary>
    public async Task CheckIfDueAsync()
    {
        // Automatic updates off: no look at GitHub by itself, only when "Check for updates" is pressed.
        if (!_checksOn()) return;
        if (_checkedDay == DateTime.Today || IsBusy || State == UpdateState.Failed) return;
        _checkedDay = DateTime.Today;
        await CheckAsync(force: false, quiet: true);
    }

    private async Task CheckNowAsync() => await CheckAsync(force: true, quiet: false);

    /// <summary>
    /// What's already known, at once and without going online: a newer version the agent (or an earlier run) found, and
    /// whether its installer is downloaded. At startup, so a waiting update shows from the first moment; and from the
    /// agent's "new version" notification (<paramref name="asked"/>), which was clicked to update: the restart question
    /// comes even if "Later" was chosen today, and one not downloaded yet starts downloading.
    /// </summary>
    public async Task ShowKnownAsync(bool asked = false)
    {
        if (IsBusy || State == UpdateState.Failed) return;
        await ApplyAsync(UpdateStore.ReadCheck()?.Latest, autoDownload: asked);
        if (!asked) return;
        if (State == UpdateState.Available) await DownloadAsync(automatic: false); // asked for, even with automatic updates off
        if (State == UpdateState.Waiting) State = UpdateState.Ready;
    }

    private async Task CheckAsync(bool force, bool quiet)
    {
        IsChecking = true;
        Release? latest;
        try
        {
            latest = await Task.Run(() => UpdateStore.LatestAsync(force));
            _checked = true;
            _checkError = null;
        }
        catch (Exception ex)
        {
            // Offline or GitHub unreachable. At startup, say nothing and use what an earlier check found.
            Log.Write("update", $"Check failed: {ex.Message}");
            _checkedDay = default;
            IsChecking = false;
            if (quiet) await ApplyAsync(UpdateStore.ReadCheck()?.Latest);
            else
            {
                // Only Settings says so: nothing is wrong with the copy you have.
                _checkError = Lang.T("Couldn't check for updates. Check your internet connection and try again.");
                RefreshTexts();
            }
            return;
        }
        IsChecking = false;
        // GitHub has been asked today, whoever asked: the day's own check, due a few seconds after the window opens,
        // doesn't come after a "Check for updates" pressed before it and start the download that press only offered.
        _checkedDay = DateTime.Today;
        // "Check for updates" only says what it found; downloading is the next click.
        if (!quiet) _offered = latest?.Version;
        await ApplyAsync(latest, autoDownload: quiet);
    }

    /// <summary>
    /// The version "Check for updates" found and offered with a Download button. It's the user's to start from then
    /// on: nothing that comes later by itself (the day's check, the agent finding the same version) downloads it.
    /// </summary>
    private Version? _offered;

    /// <summary>The agent found (or downloaded) a new version while the app is open.</summary>
    public void OnAgentMessage(AgentMessage msg)
    {
        if (msg.UpdateStatus == "found")
        {
            if (!IsBusy && State != UpdateState.Failed) _ = ApplyAsync(UpdateStore.ReadCheck()?.Latest);
            return;
        }

        // The answer to "install-update".
        if (State != UpdateState.Installing || _release is not { } release) return;
        _agentTimeout?.Stop();
        if (msg.UpdateStatus == "started") WaitForInstaller();
        else _ = RunSetupAsync(UpdateStore.SetupPath(release), release);
    }

    /// <summary>Automatic updates were just turned on: fetch one that's waiting.</summary>
    public void OnAutoUpdateChanged()
    {
        OnPropertyChanged(nameof(PromptText));
        if (_autoUpdate() && State == UpdateState.Available) _ = DownloadAsync(automatic: true);
        else if (_checksOn()) _ = CheckIfDueAsync(); // turned back on: today's look, which was skipped
    }

    /// <summary>
    /// Shows what the latest release means for this copy, and tidies up installers that are no longer needed.
    /// With automatic updates on, a newer version starts downloading unless <paramref name="autoDownload"/> is false.
    /// </summary>
    private async Task ApplyAsync(Release? latest, bool autoDownload = true)
    {
        bool newer = ReleaseFeed.IsNewer(latest);
        var (downloaded, attempt) = await Task.Run(async () =>
        {
            UpdateStore.Tidy(newer ? latest : null);
            var attempt = UpdateStore.ReadAttempt();
            if (attempt is not null && attempt.Version <= ReleaseFeed.Current) { UpdateStore.ClearAttempt(); attempt = null; }
            return (newer && await ReleaseFeed.MatchesAsync(UpdateStore.SetupPath(latest!), latest!), attempt);
        });

        _release = newer ? latest : null;
        OnPropertyChanged(nameof(Version));
        if (!newer)
        {
            State = UpdateState.None;
            RefreshTexts();
            return;
        }

        // It was installed a while ago and this copy is still older: the installer didn't finish.
        if (downloaded && attempt is not null && attempt.Version == latest!.Version && DateTime.Now - attempt.At > TimeSpan.FromMinutes(5))
            Fail(Lang.T("The update didn't install."));
        else if (downloaded) AskOrWait();
        else if (autoDownload && _autoUpdate() && latest!.Version != _offered) await DownloadAsync(automatic: true);
        else State = UpdateState.Available;
        RefreshTexts();
    }

    /// <summary>Downloaded: ask about restarting, at most once a day ("Later" leaves the quiet line until tomorrow).</summary>
    private void AskOrWait()
    {
        string today = $"{DateTime.Today:yyyy-MM-dd} {Version}";
        string? asked = null;
        try { if (File.Exists(PromptedFile)) asked = File.ReadAllText(PromptedFile); } catch { }
        if (asked == today)
        {
            State = UpdateState.Waiting;
            return;
        }
        try { File.WriteAllText(PromptedFile, today); } catch { }
        State = UpdateState.Ready;
    }

    // ── Downloading ──

    private async Task DownloadAsync(bool automatic)
    {
        if (_release is not { } release) return;
        string setup = UpdateStore.SetupPath(release);
        if (await ReleaseFeed.MatchesAsync(setup, release))
        {
            AskOrWait();
            return;
        }

        State = UpdateState.Downloading;
        Progress = 0;
        ProgressText = Lang.T("Connecting…");
        var speed = new SpeedMeter();
        long received = 0;
        void Show(long done)
        {
            Progress = release.Size > 0 ? done * 100.0 / release.Size : 0;
            double mb = done / 1048576.0;
            ProgressText = Lang.T($"{mb.ToString(mb < 10 ? "0.0" : "0")} of {release.Size / 1048576.0:0} MB{speed.Text(done)}");
        }
        var progress = new Progress<long>(done =>
        {
            received = done;
            Show(done);
        });
        // While no data arrives the speed falls towards zero instead of showing the last one.
        var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        idle.Tick += (_, _) => { if (received > 0) Show(received); };
        idle.Start();
        try
        {
            // Off the UI thread: the app stays fully usable. A download that gets stuck starts again by itself (twice at most).
            await Task.Run(() => UpdateStore.DownloadAsync(release, progress));
            AskOrWait();
        }
        catch (Exception ex)
        {
            Log.Write("update", $"Download failed: {ex.Message}");
            // A background download that fails just goes back to offering it; one the user asked for says why.
            if (automatic) State = UpdateState.Available;
            else Fail(ex switch
            {
                InvalidDataException => Lang.T("The download was damaged."),
                TimeoutException => Lang.T("The download got stuck. Check your internet connection."),
                HttpRequestException { StatusCode: null } => Lang.T("Couldn't connect. Check your internet connection."),
                _ => Lang.T("The download stopped. Check your internet connection."),
            });
        }
        finally
        {
            idle.Stop();
        }
    }

    private async Task RetryAsync()
    {
        if (_release is null) { await CheckNowAsync(); return; }
        UpdateStore.ClearAttempt();
        if (await ReleaseFeed.MatchesAsync(UpdateStore.SetupPath(_release), _release)) Install();
        else await DownloadAsync(automatic: false);
    }

    // ── Installing ──

    // ── The screen over the window while an update someone asked for installs ──

    /// <summary>
    /// "Updating Rigsight" over the whole window, from the moment someone presses Update now until the installer closes
    /// the window (or it didn't install). Never for an update that installs by itself at the next start: nobody is
    /// looking then. Its bar moves only at moments the app can see: the download checked, the installer started, and
    /// then over the usual time an install takes, short of the end, since the installer closes the window to replace it.
    /// </summary>
    [ObservableProperty] private bool _showOverlay;
    [ObservableProperty] private double _overlayFill;
    [ObservableProperty] private bool _overlayFailed;

    /// <summary>Under "The update didn't install": what is still there, and what to do.</summary>
    public string ReleaseFeedCurrentText => Lang.T($"RigSight is still on {ReleaseFeed.Current.ToString(3)}, with nothing lost. Try again, or get the new version from the download page.");

    /// <summary>"0.24.0 → 0.25.0" under the screen's title.</summary>
    public string OverlayVersions => $"{ReleaseFeed.Current.ToString(3)} → {Version}";

    /// <summary>Where the bar stops while the installer works: the end is the window closing, never drawn.</summary>
    internal const double OverlayLast = 92;
    private DispatcherTimer? _overlayTimer;

    private void Fill(double to)
    {
        if (to > OverlayFill) OverlayFill = to;
    }

    /// <summary>The installer is running: the last part of the bar fills over about the time an install takes.</summary>
    private void FillWhileInstalling()
    {
        if (!ShowOverlay) return;
        Fill(66);
        _overlayTimer?.Stop();
        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _overlayTimer.Tick += (_, _) =>
        {
            if (!ShowOverlay || OverlayFailed) { _overlayTimer?.Stop(); return; }
            OverlayFill += (OverlayLast - OverlayFill) * 0.025;
        };
        _overlayTimer.Start();
    }

    /// <summary>"Close" on the screen after an update that didn't install: back to the app, as it was.</summary>
    [RelayCommand]
    private void CloseOverlay()
    {
        _overlayTimer?.Stop();
        ShowOverlay = false;
        OverlayFailed = false;
        OverlayFill = 0;
    }

    /// <summary>"Try again" on the screen: the same as the sidebar's.</summary>
    [RelayCommand]
    private Task RetryFromOverlay()
    {
        OverlayFailed = false;
        OverlayFill = 0;
        return RetryAsync();
    }

    /// <summary>"Update now" in the prompt, the "Update ready" line, or Settings.</summary>
    [RelayCommand]
    private void Install()
    {
        if (_release is not { } release) return;
        // Someone pressed it: the screen goes over the window, the download already checked when it was saved.
        ShowOverlay = true;
        OverlayFailed = false;
        OnPropertyChanged(nameof(OverlayVersions));
        Fill(18);
        State = UpdateState.Installing;
        Progress = 100;
        ProgressText = Lang.T("RigSight closes and reopens by itself in a few seconds.");
        if (_pcUpdates())
        {
            // The PC's own updater does it: told it may close this window (and that it is to come back), then asked to run.
            try
            {
                UpdateStore.AskNow();
                if (AgentTask.Schtasks(out _, "/Run", "/TN", RigsightPaths.UpdaterTaskName) != 0) throw new InvalidOperationException("the updater couldn't be asked to run");
                // The PC's updater downloads the installer itself first: longer than an install that is already here.
                WaitForInstaller(minutes: 10);
            }
            catch (Exception ex)
            {
                Log.Error("update", ex);
                Fail(Lang.T("The update couldn't be started."));
            }
            return;
        }
        string setup = UpdateStore.SetupPath(release);

        if (_agentCanInstall())
        {
            // The agent answers with an "update" message; if it never does, run the installer ourselves.
            _client.SendCommand("install-update", setup);
            Fill(34);
            _agentTimeout?.Stop();
            _agentTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _agentTimeout.Tick += (_, _) => { _agentTimeout.Stop(); if (State == UpdateState.Installing) _ = RunSetupAsync(setup, release); };
            _agentTimeout.Start();
        }
        else
        {
            _ = RunSetupAsync(setup, release);
        }
    }

    /// <summary>"Later": keep the download, and leave a quiet "Restart to update" line in the sidebar.</summary>
    [RelayCommand]
    private void Later() => State = UpdateState.Waiting;

    /// <summary>Without the agent: start the installer ourselves (Windows asks for admin rights once).</summary>
    private async Task RunSetupAsync(string setup, Release release)
    {
        if (!await ReleaseFeed.MatchesAsync(setup, release))
        {
            Fail(Lang.T("The download was damaged."));
            return;
        }
        try
        {
            UpdateStore.WriteAttempt(release.Version);
            // On a standard account setup is started as the person and asks for the admin's password itself: started
            // already elevated, it wouldn't know who to open Rigsight for afterwards, and would open it as that admin.
            Fill(34);
            Process.Start(new ProcessStartInfo(setup, ReleaseFeed.SetupArguments) { UseShellExecute = true, Verb = _needsAdmin() ? "" : "runas" })?.Dispose();
            WaitForInstaller();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            UpdateStore.ClearAttempt();
            CloseOverlay();
            State = UpdateState.Waiting; // the admin prompt was declined: ready whenever they are
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            UpdateStore.ClearAttempt();
            Fail(Lang.T("The installer couldn't start."));
        }
    }

    /// <summary>
    /// The installer is running silently. The window stays up saying so until the installer closes it (then opens
    /// the new version), so there's never a moment where Rigsight has simply vanished. Still here after two minutes:
    /// the installer didn't get that far.
    /// </summary>
    private void WaitForInstaller(double minutes = 2)
    {
        FillWhileInstalling();
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            if (State == UpdateState.Installing) Fail(Lang.T("The update didn't install."));
        };
        watchdog.Start();
    }

    /// <summary>Download speed over the last few seconds, as " · 4.1 MB/s" (nothing until there's a second of data).</summary>
    private sealed class SpeedMeter
    {
        private readonly Queue<(long Ms, long Bytes)> _samples = new();

        public string Text(long bytes)
        {
            long now = Environment.TickCount64;
            // Started again after getting stuck: measure afresh.
            if (_samples.Count > 0 && bytes < _samples.Last().Bytes) _samples.Clear();
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().Ms > 3000) _samples.Dequeue();
            var (ms, from) = _samples.Peek();
            if (now - ms < 1000) return "";
            double perSec = (bytes - from) * 1000.0 / (now - ms);
            return perSec >= 1048576 ? $" · {perSec / 1048576:0.0} MB/s" : $" · {perSec / 1024:0} KB/s";
        }
    }

    private void Fail(string reason)
    {
        Error = reason;
        State = UpdateState.Failed;
        // The screen, if it is up, says so in place, with nothing lost: the window was never closed.
        if (ShowOverlay) OverlayFailed = true;
    }

    private void RefreshTexts()
    {
        foreach (var name in new[] { nameof(LineTip), nameof(ProgressTitle), nameof(PromptText), nameof(StatusText), nameof(AnswerText), nameof(AnswerIcon) })
            OnPropertyChanged(name);
    }
}
