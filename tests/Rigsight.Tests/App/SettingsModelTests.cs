using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// The app's copy of the settings against a (fake) agent over the real pipe: changes go out once, debounced, as the
/// whole settings; the agent's copy comes back and only real changes are applied.
/// </summary>
[Collection("UI")]
public sealed class SettingsModelTests
{
    /// <summary>A settings model wired to its agent the way the shell wires it.</summary>
    private sealed class Wired : IDisposable
    {
        public FakeAgent? Agent { get; private set; }
        public AgentClient Client { get; }
        public SettingsModel Settings { get; }
        public List<string> Themes { get; } = [];

        private readonly RigsightSettings _file;

        public Wired(bool connect = true)
        {
            // The agent starts with what's in the file, as the real one does.
            _file = SharedData.ResetSettings();
            (Client, Settings) = Ui.Run(() =>
            {
                var client = new AgentClient(Ui.Dispatcher);
                var settings = new SettingsModel(client);
                client.MessageReceived += m =>
                {
                    if (m.Settings is not null) settings.ApplyFromAgent(m.Settings);
                    if (m.T == "hello") settings.OnConnected();
                };
                settings.Changed += () => Themes.Add(settings.Current.Theme);
                return (client, settings);
            });
            if (connect) Connect();
        }

        public void Connect()
        {
            Agent ??= new FakeAgent(_file.Clone());
            Ui.Run(() => Client.Start());
            Assert.True(Ui.WaitFor(() => Client.IsConnected && Agent.ClientCount > 0), "didn't connect");
            Ui.Pump(100); // the hello
        }

        public void DropAgent()
        {
            Agent?.Dispose();
            Agent = null;
        }

        public List<UiMessage> Sent => [.. Agent!.Received.Where(m => m.T == "settings")];

        public void Dispose()
        {
            Ui.Run(Client.Dispose);
            Agent?.Dispose();
            Ui.Run(() => Units.Fahrenheit = false);
        }
    }

    [Fact]
    public void Changes_go_to_the_agent_once_after_a_short_pause_as_the_whole_settings()
    {
        using var w = new Wired();
        Ui.Run(() =>
        {
            w.Settings.Update(s => s.Theme = ThemeManager.Light);
            w.Settings.Update(s => s.Alerts.CpuLimit = 91);
            w.Settings.Update(s => s.AppNames["x.exe"] = "Ex");
        });
        Assert.Empty(w.Sent); // still waiting for more changes
        Assert.True(Ui.WaitFor(() => w.Sent.Count == 1, 3000));
        Ui.Pump(500);
        var sent = Assert.Single(w.Sent).Settings!;
        Assert.Equal(ThemeManager.Light, sent.Theme);
        Assert.Equal(91, sent.Alerts.CpuLimit);
        Assert.Equal("Ex", sent.AppNames["x.exe"]);
        // Everything else too, not just what changed.
        Assert.True(sent.StartupConfigured);
        Assert.Equal(Enum.GetValues<WidgetStyle>().Length, sent.Widgets.Count);
        Assert.Contains(sent.CustomPages, p => p.Id == SharedData.DashboardId);
    }

    [Fact]
    public void Each_change_is_announced_straight_away()
    {
        using var w = new Wired(connect: false);
        Ui.Run(() =>
        {
            w.Settings.Update(s => s.Theme = ThemeManager.Light);
            Assert.Equal([ThemeManager.Light], w.Themes);
            w.Settings.Update(s => s.UseFahrenheit = true);
            Assert.True(Units.Fahrenheit); // formatting follows at once
            Assert.Equal(2, w.Themes.Count);
        });
    }

    [Fact]
    public void Flush_sends_now_and_only_when_something_changed()
    {
        using var w = new Wired();
        Ui.Run(() => w.Settings.Flush());
        Ui.Pump(200);
        Assert.Empty(w.Sent);
        Ui.Run(() =>
        {
            w.Settings.Update(s => s.LiveRefreshMs = 2000);
            w.Settings.Flush();
        });
        Assert.True(Ui.WaitFor(() => w.Sent.Count == 1, 2000));
        Kit.AfterDebounce(); // the debounce was cancelled by the flush: nothing more goes out
        Assert.Single(w.Sent);
    }

    [Fact]
    public void The_agents_echo_of_our_own_change_changes_nothing()
    {
        using var w = new Wired();
        Ui.Run(() => w.Settings.Update(s => s.Theme = ThemeManager.Light));
        Assert.True(Ui.WaitFor(() => w.Sent.Count == 1, 3000));
        Ui.Pump(300); // the echo
        Assert.Equal([ThemeManager.Light], w.Themes);
        Assert.Equal(ThemeManager.Light, Ui.Run(() => w.Settings.Current.Theme));
    }

    [Fact]
    public void A_change_from_the_agent_is_applied_and_announced()
    {
        using var w = new Wired();
        var fromAgent = w.Agent!.Settings.Clone();
        fromAgent.UseFahrenheit = true;
        fromAgent.Theme = ThemeManager.Light;
        w.Agent!.Broadcast(new AgentMessage { T = "settings", Settings = fromAgent });
        Assert.True(Ui.WaitFor(() => w.Settings.Current.Theme == ThemeManager.Light));
        Assert.Equal([ThemeManager.Light], w.Themes);
        Assert.True(Ui.Run(() => Units.Fahrenheit));
        // The same again (every app gets every broadcast): nothing to do.
        w.Agent.Broadcast(new AgentMessage { T = "settings", Settings = fromAgent });
        Ui.Pump(200);
        Assert.Single(w.Themes);
    }

    [Fact]
    public void The_agents_copy_is_ignored_while_a_change_waits_to_be_sent()
    {
        using var w = new Wired();
        Ui.Run(() =>
        {
            w.Settings.Update(s => s.Theme = ThemeManager.Light);
            var older = w.Agent!.Settings.Clone();
            older.LiveRefreshMs = 5000;
            w.Settings.ApplyFromAgent(older);
            Assert.Equal(ThemeManager.Light, w.Settings.Current.Theme);
            Assert.Equal(1000, w.Settings.Current.LiveRefreshMs);
        });
    }

    [Fact]
    public void Changes_made_without_an_agent_wait_and_go_out_when_it_connects()
    {
        using var w = new Wired(connect: false);
        Ui.Run(() => w.Settings.Update(s => s.Alerts.GpuLimit = 77));
        Kit.AfterDebounce();
        Ui.Run(() =>
        {
            // Still ours: an agent's copy (from an earlier connection, say) doesn't overwrite it.
            w.Settings.ApplyFromAgent(new RigsightSettings());
            Assert.Equal(77, w.Settings.Current.Alerts.GpuLimit);
        });
        w.Connect();
        Assert.True(Ui.WaitFor(() => w.Sent.Count == 1, 3000));
        Assert.Equal(77, w.Sent[0].Settings!.Alerts.GpuLimit);
        Ui.Pump(300);
        Assert.Equal(77, Ui.Run(() => w.Settings.Current.Alerts.GpuLimit));
        Assert.Equal(77, w.Agent!.Settings.Alerts.GpuLimit);
    }

    [Fact]
    public void Once_sent_the_agents_copies_apply_again()
    {
        using var w = new Wired();
        Ui.Run(() => w.Settings.Update(s => s.LiveRefreshMs = 2000));
        Assert.True(Ui.WaitFor(() => w.Sent.Count == 1, 3000));
        Ui.Pump(200);
        var fromAgent = w.Agent!.Settings.Clone();
        fromAgent.LiveRefreshMs = 3000;
        w.Agent.Broadcast(new AgentMessage { T = "settings", Settings = fromAgent });
        Assert.True(Ui.WaitFor(() => w.Settings.Current.LiveRefreshMs == 3000));
    }

    [Fact]
    public void Update_while_the_agent_is_gone_keeps_the_change_dirty_until_it_is_back()
    {
        using var w = new Wired();
        w.DropAgent();
        Assert.True(Ui.WaitFor(() => !w.Client.IsConnected));
        Ui.Run(() => w.Settings.Update(s => s.Alerts.CardSeconds = 12));
        Kit.AfterDebounce();
        var agent = new FakeAgent(SharedData.ResetSettings());
        try
        {
            Assert.True(Ui.WaitFor(() => agent.Received.Any(m => m.T == "settings"), 10_000));
            Assert.Equal(12, agent.Received.First(m => m.T == "settings").Settings!.Alerts.CardSeconds);
        }
        finally
        {
            agent.Dispose();
        }
    }
}
