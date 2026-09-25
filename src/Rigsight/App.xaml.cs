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
        if (RigsightPaths.IsTestInstance)
        {
            LogBindingErrors();
            ReportHeapOnRequest();
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
    private static string InstanceSuffix => RigsightPaths.InstanceSuffix;

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

    /// <summary>A test copy logs every binding WPF can't resolve (normally silent), so the tests can fail on them.</summary>
    private static void LogBindingErrors()
    {
        System.Diagnostics.PresentationTraceSources.Refresh();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new BindingErrorLog());
        source.Switch.Level = System.Diagnostics.SourceLevels.Warning;
    }

    /// <summary>
    /// A test copy, when signalled, collects all garbage and logs the memory still in use ("[heap] N bytes"): the tests'
    /// leak check compares that after each round of pages, which the collector's own timing would otherwise blur.
    /// </summary>
    private void ReportHeapOnRequest()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Rigsight.App.Heap" + InstanceSuffix);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.Invoke(() =>
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    Log.Write("heap", $"{GC.GetTotalMemory(forceFullCollection: false)} bytes");
                }, DispatcherPriority.ApplicationIdle);
        })
        { IsBackground = true, Name = "HeapReport" };
        thread.Start();
    }

    private sealed class BindingErrorLog : System.Diagnostics.TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) => Log.Write("binding", message ?? "");
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
        if (RigsightPaths.IsTestInstance) return; // the tests read the log; no dialog to click away

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
