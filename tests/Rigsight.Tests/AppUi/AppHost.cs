using System.Windows;
using System.Windows.Controls;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The real Rigsight window, as App.OnStartup builds it, connected over a real pipe to a <see cref="FakeAgent"/>, on
/// two weeks of generated history in this run's data folder. Shown off-screen. Shared by the UI tests of a class.
/// </summary>
public sealed class AppHost : IDisposable
{
    public const string DashboardId = SharedData.DashboardId;

    public FakeAgent Agent { get; private set; }
    public AgentClient Client { get; private set; } = null!;
    public ShellViewModel Shell { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;

    /// <summary>Every page, the dashboard (with one of every tile) last.</summary>
    public static readonly string[] Pages =
        [.. ShellViewModel.BuiltInPages.Select(p => p.Key), "widgets", "overlay", "settings", "custom:" + DashboardId];

    public AppHost()
    {
        SharedData.EnsureSeeded();
        Agent = new FakeAgent(SharedData.ResetSettings());

        Ui.Run(() =>
        {
            ThemeManager.Apply(ThemeManager.Dark);
            Client = new AgentClient(Ui.Dispatcher);
            Shell = new ShellViewModel(Client);
            Window = new MainWindow(Shell)
            {
                Left = -32000, Top = -32000, Width = 1440, Height = 900,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            Application.Current.MainWindow = Window;
            Window.Show();
            Client.Start();
        });
        Assert.True(Ui.WaitFor(() => Shell.IsConnected && Shell.Live.Hardware.Count > 0, 15_000), "The app didn't connect to the fake agent");
        Agent.Tick();
        Agent.Procs();
        Ui.Pump(300);
    }

    /// <summary>A page with one of every tile the catalog offers.</summary>
    public static CustomPageConfig Dashboard()
    {
        var page = new CustomPageConfig { Id = DashboardId, Name = "Everything", Grid = CustomPageConfig.CurrentGrid };
        int x = 0, y = 0, rowH = 0;
        foreach (var kind in TileCatalog.Groups.SelectMany(g => g.Tiles))
        {
            if (x + kind.W > TileConfig.Columns) { x = 0; y += rowH; rowH = 0; }
            page.Tiles.Add(new TileConfig { Kind = kind.Kind, Sensor = kind.Sensor, X = x, Y = y, W = kind.W, H = kind.H });
            x += kind.W;
            rowH = Math.Max(rowH, kind.H);
        }
        return page;
    }

    /// <summary>Goes to a page and waits for its data (history pages load in the background).</summary>
    public void Show(string page, int settleMs = 400)
    {
        Ui.Run(() => Shell.CurrentPage = page);
        Agent.Tick(Environment.TickCount % 1000);
        Ui.Pump(settleMs);
    }

    /// <summary>
    /// Scrolls every scrolling area of the page to the end and back, in steps: lists build (and reuse) rows as they
    /// come into view, and pages that load more cards near the bottom do so.
    /// </summary>
    public void ScrollThrough()
    {
        var scrollers = Ui.Run(() => Visuals.Descendants<ScrollViewer>(Window.PageHost).Where(s => s.ScrollableHeight > 0).ToList());
        foreach (var sv in scrollers)
        {
            for (int step = 1; step <= 8; step++)
            {
                Ui.Run(() => sv.ScrollToVerticalOffset(sv.ScrollableHeight * step / 8));
                Ui.Pump(40);
            }
            Ui.Run(sv.ScrollToTop);
            Ui.Pump(40);
        }
    }

    /// <summary>Starts a new fake agent (after the old one "crashed") and waits for the app to reconnect to it.</summary>
    /// <param name="integratedGpu">The new agent's PC also has a processor's integrated graphics (two GPUs).</param>
    public void RestartAgent(Action? whileDown = null, bool integratedGpu = false)
    {
        var settings = Agent.Settings;
        Agent.Dispose();
        Assert.True(Ui.WaitFor(() => !Shell.IsConnected, 10_000), "The app didn't notice the agent went away");
        whileDown?.Invoke();
        Agent = new FakeAgent(settings, integratedGpu);
        Assert.True(Ui.WaitFor(() => Shell.IsConnected, 15_000), "The app didn't reconnect");
    }

    public void Dispose()
    {
        try
        {
            Ui.Run(() =>
            {
                Window.Close();
                Client.Dispose();
                ThemeManager.Apply(ThemeManager.Dark);
            });
            Ui.Pump(100);
        }
        finally
        {
            Agent.Dispose();
            Ui.TakeProblems();
        }
    }
}
