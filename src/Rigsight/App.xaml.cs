using System.IO;
using System.Windows;
using System.Windows.Threading;
using Rigsight.Core;
using Rigsight.Services;
using Rigsight.ViewModels;

namespace Rigsight;

/// <summary>
/// The Rigsight window. It runs only while open (closing it exits completely); all background
/// work is done by Rigsight.Agent, which this app connects to.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Rigsight.App";
    private const string ShowEventName = @"Local\Rigsight.App.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AgentClient? _client;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        _client = new AgentClient(Dispatcher);
        var shell = new ShellViewModel(_client);
        ThemeManager.Apply(shell.Settings.Current.Theme);
        var window = new MainWindow(shell);
        MainWindow = window;
        window.Show();
        _client.Start();
    }

    /// <summary>
    /// One window per data folder: a copy pointed at other data (RIGSIGHT_DATA_DIR, for testing) runs alongside the
    /// normal one instead of bringing it to the front.
    /// </summary>
    private static string InstanceSuffix =>
        Environment.GetEnvironmentVariable("RIGSIGHT_DATA_DIR") is { Length: > 0 } dir
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..12]
            : "";

    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(true, MutexName + InstanceSuffix, out bool created);
        if (!created)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName + InstanceSuffix);
                ev.Set();
            }
            catch { }
            return false;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + InstanceSuffix);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
        })
        { IsBackground = true, Name = "ShowSignal" };
        thread.Start();
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _client?.Dispose();
        base.OnExit(e);
    }

    private bool _showingError;
    private readonly HashSet<string> _shownErrors = [];

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("app", e.Exception);
        e.Handled = true;

        // One dialog at a time, and each distinct error only once per run: an error that repeats
        // (say, on every frame while scrolling) must not bury the screen in dialogs. The log has them all.
        if (_showingError || !_shownErrors.Add(e.Exception.Message)) return;
        _showingError = true;
        try
        {
            MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to rigsight.log in {RigsightPaths.DataDir}.",
                "Rigsight", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }
}
