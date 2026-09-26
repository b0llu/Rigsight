using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rigsight.Core.Protocol;
using Rigsight.Core.Updates;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// A stand-in for GitHub on this machine: the latest-release JSON and the installer, over plain HTTP on a free local
/// port. (Debug builds read the feed address from RIGSIGHT_UPDATE_FEED.)
/// </summary>
internal sealed class FeedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public FeedServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string FeedUrl => $"http://127.0.0.1:{Port}/latest";
    public string Tag { get; set; } = "v99.0.0";
    public string Version => Tag.TrimStart('v');
    public byte[] Setup { get; set; } = RandomNumberGenerator.GetBytes(300_000);
    /// <summary>What a download actually gets (a damaged copy, say); the installer itself when null.</summary>
    public byte[]? Served { get; set; }
    public HttpStatusCode FeedStatus { get; set; } = HttpStatusCode.OK;
    public bool WithDigest { get; set; } = true;
    public int FeedDelayMs { get; set; }
    public int ChunkDelayMs { get; set; }
    public int FeedRequests;
    public int SetupRequests;

    public string AssetName => $"Rigsight-Setup-{Version}.exe";

    public string Json => $$"""
        {"tag_name":"{{Tag}}","html_url":"http://127.0.0.1:{{Port}}/page","assets":[
          {"name":"notes.txt","size":1,"browser_download_url":"x"},
          {"name":"{{AssetName}}","size":{{Setup.Length}},{{(WithDigest ? $"\"digest\":\"sha256:{Convert.ToHexStringLower(SHA256.HashData(Setup))}\"," : "")}}
           "browser_download_url":"http://127.0.0.1:{{Port}}/setup.exe"}]}
        """;

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var request = await reader.ReadLineAsync() ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            string path = request.Split(' ') is [_, var p, ..] ? p : "/";
            if (path == "/latest")
            {
                Interlocked.Increment(ref FeedRequests);
                if (FeedDelayMs > 0) await Task.Delay(FeedDelayMs);
                await WriteAsync(stream, FeedStatus, Encoding.UTF8.GetBytes(FeedStatus == HttpStatusCode.OK ? Json : "{}"), 0);
            }
            else if (path == "/setup.exe")
            {
                Interlocked.Increment(ref SetupRequests);
                await WriteAsync(stream, HttpStatusCode.OK, Served ?? Setup, ChunkDelayMs);
            }
            else await WriteAsync(stream, HttpStatusCode.NotFound, [], 0);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task WriteAsync(Stream stream, HttpStatusCode status, byte[] body, int chunkDelayMs)
    {
        var head = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        for (int i = 0; i < body.Length; i += 32 * 1024)
        {
            await stream.WriteAsync(body.AsMemory(i, Math.Min(32 * 1024, body.Length - i)));
            if (chunkDelayMs > 0) await Task.Delay(chunkDelayMs);
        }
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// Updating from inside the app, against <see cref="FeedServer"/>, never GitHub. Nothing here ever reaches the step
/// that starts an installer: every update view model is put back to "none" afterwards, so no timer can get there.
/// </summary>
[Collection("UI")]
public sealed class UpdateTests : IDisposable
{
    private readonly FeedServer _server = new();
    private readonly string? _feedBefore = Environment.GetEnvironmentVariable("RIGSIGHT_UPDATE_FEED");
    private readonly List<UpdateViewModel> _made = [];
    private bool _auto;
    private bool _agentCanInstall;

    public UpdateTests()
    {
        Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", _server.FeedUrl);
        if (Directory.Exists(UpdateStore.Folder)) Directory.Delete(UpdateStore.Folder, recursive: true);
        Assert.True(ReleaseFeed.Current < new Version(99, 0, 0));
    }

    public void Dispose()
    {
        Ui.Run(() => { foreach (var vm in _made) vm.State = UpdateState.None; });
        Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", _feedBefore);
        _server.Dispose();
        // Leave no "update found" behind for the other tests' app.
        try { Directory.Delete(UpdateStore.Folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private UpdateViewModel Make(AgentClient? client = null)
    {
        var vm = Ui.Run(() => new UpdateViewModel(client ?? new AgentClient(Ui.Dispatcher), () => _agentCanInstall, () => _auto));
        _made.Add(vm);
        return vm;
    }

    private static void Act(UpdateViewModel vm) => Kit.Wait(() => vm.ActionCommand.ExecuteAsync(null));

    private string SetupPath => Path.Combine(UpdateStore.Folder, _server.AssetName);

    [Fact]
    public void Before_checking()
    {
        var vm = Make();
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.None, vm.State);
            Assert.Equal("Rigsight checks for a new version once a day.", vm.StatusText);
            Assert.Equal("Check for updates", vm.ActionText);
            Assert.False(vm.ActionIsPrimary);
            Assert.False(vm.ShowLine || vm.ShowProgress || vm.ShowPrompt || vm.ShowFailed);
            Assert.False(vm.IsBusy);
            Assert.Equal("", vm.Version);
            Assert.True(vm.ActionCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Checking_finds_a_newer_version_and_offers_it()
    {
        var vm = Make();
        Act(vm);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Available, vm.State);
            Assert.Equal("99.0.0", vm.Version);
            Assert.Equal("Version 99.0.0 is available.", vm.StatusText);
            Assert.Equal("Download", vm.ActionText);
            Assert.True(vm.ActionIsPrimary);
            Assert.True(vm.ShowLine);
            Assert.Equal("Update available", vm.LineTitle);
            Assert.Equal("Rigsight 99.0.0 is out. Click to download it; you can keep using Rigsight meanwhile.", vm.LineTip);
        });
        // "Check for updates" only says what it found, even with automatic updates on.
        Assert.Equal(0, _server.SetupRequests);
        Assert.Equal("99.0.0", UpdateStore.ReadCheck()?.Latest?.Version.ToString(3));
    }

    [Fact]
    public void Downloading_then_asking_to_restart_then_later()
    {
        var vm = Make();
        Act(vm);
        Act(vm);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Ready, vm.State);
            Assert.True(vm.ShowPrompt);
            Assert.Equal("Restart Rigsight to finish updating to 99.0.0.", vm.PromptText);
            Assert.Equal("Version 99.0.0 is downloaded and ready to install.", vm.StatusText);
            Assert.Equal("Restart to update", vm.ActionText);
            Assert.Equal(100, vm.Progress, 6);
        });
        Assert.Equal(_server.Setup, File.ReadAllBytes(SetupPath));
        Assert.Empty(Directory.GetFiles(UpdateStore.Folder, "*.part"));

        _auto = _agentCanInstall = true;
        Ui.Run(() =>
        {
            Assert.Equal("Restart now to update to 99.0.0, or it installs by itself the next time you start your PC.", vm.PromptText);
            vm.LaterCommand.Execute(null);
            Assert.Equal(UpdateState.Waiting, vm.State);
            Assert.True(vm.ShowLine);
            Assert.Equal("Restart to update", vm.LineTitle);
            Assert.Equal("Rigsight 99.0.0 is downloaded. Click to restart and install it.", vm.LineTip);
            Assert.Equal("Restart to update", vm.ActionText);
        });
    }

    [Fact]
    public void A_downloaded_update_shows_the_moment_the_app_opens_without_going_online()
    {
        var first = Make();
        Act(first);
        Act(first); // found and downloaded (as the agent's updater would while the app was closed)
        int feed = _server.FeedRequests;
        var opened = Make();
        Kit.Wait(() => opened.ShowKnownAsync());
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Waiting, opened.State); // already asked today: the quiet line
            Assert.True(opened.ShowLine);
            Assert.Equal("Restart to update", opened.LineTitle);
        });
        Assert.Equal(feed, _server.FeedRequests);
    }

    [Fact]
    public void The_new_version_notification_asks_to_restart_even_after_later()
    {
        var first = Make();
        Act(first);
        Act(first);
        Ui.Run(() => first.LaterCommand.Execute(null));
        var opened = Make();
        Kit.Wait(() => opened.ShowKnownAsync(asked: true));
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Ready, opened.State);
            Assert.True(opened.ShowPrompt);
        });
    }

    [Fact]
    public void The_new_version_notification_downloads_one_not_downloaded_yet()
    {
        var first = Make();
        Act(first); // found, not downloaded (automatic updates off)
        Assert.Equal(0, _server.SetupRequests);
        var opened = Make();
        Kit.Wait(() => opened.ShowKnownAsync(asked: true));
        Assert.Equal(1, _server.SetupRequests);
        Assert.Equal(UpdateState.Ready, Ui.Run(() => opened.State));
    }

    [Fact]
    public void Nothing_known_shows_nothing()
    {
        var opened = Make();
        Kit.Wait(() => opened.ShowKnownAsync());
        Ui.Run(() => Assert.Equal(UpdateState.None, opened.State));
        Assert.Equal(0, _server.FeedRequests);
    }

    [Fact]
    public void The_restart_question_comes_once_a_day()
    {
        var first = Make();
        Act(first);
        Act(first);
        Assert.Equal(UpdateState.Ready, Ui.Run(() => first.State));
        // The app opened again the same day, with the installer already downloaded: just the quiet line.
        var second = Make();
        Kit.Wait(() => second.CheckIfDueAsync());
        Assert.Equal(UpdateState.Waiting, Ui.Run(() => second.State));
        Assert.Equal(1, _server.SetupRequests);
    }

    [Fact]
    public void Automatic_updates_download_by_themselves()
    {
        _auto = true;
        var vm = Make();
        Kit.Wait(() => vm.CheckIfDueAsync());
        Assert.Equal(UpdateState.Ready, Ui.Run(() => vm.State));
        Assert.Equal(1, _server.SetupRequests);
        // Once a day: a second check the same day asks nobody.
        Kit.Wait(() => vm.CheckIfDueAsync());
        Assert.Equal(1, _server.FeedRequests);
    }

    [Fact]
    public void The_daily_answer_is_shared_through_the_update_folder()
    {
        var a = Make();
        Kit.Wait(() => a.CheckIfDueAsync());
        var b = Make();
        Kit.Wait(() => b.CheckIfDueAsync());
        Assert.Equal(1, _server.FeedRequests);
        Assert.Equal(UpdateState.Available, Ui.Run(() => b.State));
    }

    [Fact]
    public void Turning_on_automatic_updates_fetches_the_waiting_one()
    {
        var vm = Make();
        Act(vm);
        Assert.Equal(UpdateState.Available, Ui.Run(() => vm.State));
        Ui.Run(vm.OnAutoUpdateChanged); // still off: nothing
        Ui.Pump(300);
        Assert.Equal(0, _server.SetupRequests);
        _auto = true;
        Ui.Run(vm.OnAutoUpdateChanged);
        Assert.True(Ui.WaitFor(() => vm.State == UpdateState.Ready, 15_000));
    }

    [Fact]
    public void A_damaged_download_fails_and_try_again_fetches_it_again()
    {
        var vm = Make();
        Act(vm);
        var damaged = (byte[])_server.Setup.Clone();
        damaged[1000] ^= 0xFF;
        _server.Served = damaged;
        Act(vm);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Failed, vm.State);
            Assert.Equal("The download was damaged.", vm.Error);
            Assert.True(vm.ShowFailed);
            Assert.Equal("The download was damaged. Try again, or get the latest version from the download page.", vm.FailedText);
            Assert.Equal("The download was damaged. You can also get it from the download page.", vm.StatusText);
            Assert.Equal("Try again", vm.ActionText);
            Assert.True(vm.ActionIsPrimary);
        });
        Assert.False(File.Exists(SetupPath));
        Assert.Empty(Directory.GetFiles(UpdateStore.Folder, "*.part"));
        // A failed update isn't checked again by itself.
        Kit.Wait(() => vm.CheckIfDueAsync());
        Assert.Equal(UpdateState.Failed, Ui.Run(() => vm.State));

        _server.Served = null;
        Act(vm);
        Assert.Equal(UpdateState.Ready, Ui.Run(() => vm.State));
        Assert.Equal(2, _server.SetupRequests);
    }

    [Fact]
    public void A_short_download_is_damaged_too()
    {
        var vm = Make();
        Act(vm);
        _server.Served = _server.Setup[..1000];
        Act(vm);
        Assert.Equal("The download was damaged.", Ui.Run(() => vm.Error));
    }

    [Fact]
    public void An_automatic_download_that_fails_quietly_goes_back_to_offering_it()
    {
        _auto = true;
        _server.Served = [1, 2, 3];
        var vm = Make();
        Kit.Wait(() => vm.CheckIfDueAsync());
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Available, vm.State);
            Assert.Equal("", vm.Error);
        });
    }

    [Fact]
    public void A_check_that_cannot_reach_the_server_says_so_only_when_asked()
    {
        _server.FeedStatus = HttpStatusCode.InternalServerError;
        var quiet = Make();
        Kit.Wait(() => quiet.CheckIfDueAsync());
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.None, quiet.State);
            Assert.Equal("Rigsight checks for a new version once a day.", quiet.StatusText);
            Assert.False(quiet.IsChecking);
        });
        // Not counted as today's check: the next one tries again.
        Kit.Wait(() => quiet.CheckIfDueAsync());
        Assert.Equal(2, _server.FeedRequests);

        var asked = Make();
        Act(asked);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.None, asked.State);
            Assert.Equal("Couldn't check for updates. Check your internet connection and try again.", asked.StatusText);
            Assert.Equal("Check for updates", asked.ActionText);
        });
    }

    [Fact]
    public void Offline_at_startup_uses_what_an_earlier_check_found()
    {
        var earlier = Make();
        Act(earlier); // saves today's answer
        _server.FeedStatus = HttpStatusCode.ServiceUnavailable;
        var vm = Make();
        Act(vm); // forced check: the server fails
        Assert.Equal(UpdateState.None, Ui.Run(() => vm.State));
        Kit.Wait(() => vm.CheckIfDueAsync()); // today's saved answer, no server needed
        Assert.Equal(UpdateState.Available, Ui.Run(() => vm.State));
    }

    [Fact]
    public void Up_to_date_says_so_and_tidies_old_installers()
    {
        _server.Tag = "v0.0.1";
        Directory.CreateDirectory(UpdateStore.Folder);
        var stale = Path.Combine(UpdateStore.Folder, "Rigsight-Setup-0.4.0.exe");
        File.WriteAllText(stale, "old");
        var vm = Make();
        Act(vm);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.None, vm.State);
            Assert.Equal("You have the latest version.", vm.StatusText);
            Assert.Equal("", vm.Version);
            Assert.False(vm.ShowLine);
        });
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_release_without_a_checksum_is_not_offered()
    {
        _server.WithDigest = false;
        var vm = Make();
        Act(vm);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.None, vm.State);
            Assert.Equal("You have the latest version.", vm.StatusText);
        });
    }

    [Fact]
    public void While_checking_the_button_waits()
    {
        _server.FeedDelayMs = 800;
        var vm = Make();
        var task = Ui.Run(() => vm.ActionCommand.ExecuteAsync(null));
        Assert.True(Ui.WaitFor(() => vm.IsChecking, 3000));
        Ui.Run(() =>
        {
            Assert.Equal("Checking for updates…", vm.StatusText);
            Assert.Equal("Checking…", vm.ActionText);
            Assert.True(vm.IsBusy);
            Assert.False(vm.ActionCommand.CanExecute(null));
        });
        Assert.True(Ui.WaitFor(() => task.IsCompleted, 10_000));
        Ui.Run(() =>
        {
            Assert.False(vm.IsChecking);
            Assert.True(vm.ActionCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Download_progress_is_shown()
    {
        _server.Setup = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        _server.ChunkDelayMs = 25;
        var vm = Make();
        Act(vm);
        var task = Ui.Run(() => vm.ActionCommand.ExecuteAsync(null));
        Assert.True(Ui.WaitFor(() => vm.State == UpdateState.Downloading && vm.Progress > 5, 15_000));
        Ui.Run(() =>
        {
            Assert.True(vm.ShowProgress);
            Assert.True(vm.IsBusy);
            Assert.Equal("Downloading 99.0.0", vm.ProgressTitle);
            Assert.Matches(@"^Downloading version 99\.0\.0… \d+%$", vm.StatusText);
            Assert.Matches(@"^\d+(\.\d)? of 3 MB", vm.ProgressText);
            Assert.Equal("Downloading…", vm.ActionText);
        });
        Assert.True(Ui.WaitFor(() => task.IsCompleted, 30_000));
        Assert.Equal(UpdateState.Ready, Ui.Run(() => vm.State));
    }

    [Fact]
    public void Installing_goes_through_the_agent()
    {
        using var link = new AgentLink();
        _agentCanInstall = true;
        var vm = Make(link.Client);
        Act(vm);
        Act(vm);
        Assert.Equal(UpdateState.Ready, Ui.Run(() => vm.State));
        Ui.Run(() => vm.InstallCommand.Execute(null));
        Assert.Equal(SetupPath, link.Sent("install-update").Arg);
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Installing, vm.State);
            Assert.True(vm.ShowProgress);
            Assert.Equal("Updating to 99.0.0…", vm.ProgressTitle);
            Assert.Equal("Installing the update…", vm.StatusText);
            Assert.Equal("Installing…", vm.ActionText);
            Assert.Equal("Rigsight closes and reopens by itself in a few seconds.", vm.ProgressText);
            Assert.False(vm.ActionCommand.CanExecute(null));
            // The agent started the installer: now we just wait for it to close the app.
            vm.OnAgentMessage(new AgentMessage { T = "update", UpdateStatus = "started" });
            Assert.Equal(UpdateState.Installing, vm.State);
        });
    }

    [Fact]
    public void Agent_messages_outside_an_install_are_ignored()
    {
        var vm = Make();
        Ui.Run(() =>
        {
            vm.OnAgentMessage(new AgentMessage { T = "update", UpdateStatus = "started" });
            vm.OnAgentMessage(new AgentMessage { T = "update", UpdateStatus = "failed" });
            Assert.Equal(UpdateState.None, vm.State);
        });
    }

    [Fact]
    public void The_agent_finding_an_update_shows_it()
    {
        // The agent's own check wrote today's answer to the shared folder.
        var agentSide = Make();
        Act(agentSide);
        var vm = Make();
        Ui.Run(() => vm.OnAgentMessage(new AgentMessage { T = "update", UpdateStatus = "found" }));
        Assert.True(Ui.WaitFor(() => vm.State == UpdateState.Available, 10_000));
    }

    [Fact]
    public void An_install_that_never_finished_is_reported()
    {
        var vm = Make();
        Act(vm);
        Act(vm);
        // Started more than five minutes ago, and this copy is still the old version.
        File.WriteAllText(Path.Combine(UpdateStore.Folder, "attempt.json"),
            System.Text.Json.JsonSerializer.Serialize(new InstallAttempt(new Version(99, 0, 0), DateTime.Now.AddMinutes(-10))));
        var next = Make();
        Kit.Wait(() => next.CheckIfDueAsync());
        Ui.Run(() =>
        {
            Assert.Equal(UpdateState.Failed, next.State);
            Assert.Equal("The update didn't install.", next.Error);
        });
    }

    [Fact]
    public void An_attempt_for_a_version_already_installed_is_forgotten()
    {
        UpdateStore.WriteAttempt(new Version(0, 0, 1));
        var vm = Make();
        Kit.Wait(() => vm.CheckIfDueAsync());
        Assert.Null(UpdateStore.ReadAttempt());
    }
}
