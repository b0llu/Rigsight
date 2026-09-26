using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rigsight.Services;

namespace Rigsight.Tests.Support;

/// <summary>
/// One WPF UI thread for the whole test run, with an Application holding Rigsight's theme (as App.xaml sets it up), so
/// the app's real windows and views can be built and shown in tests. Everything WPF runs through <see cref="Run"/>.
/// Unhandled dispatcher exceptions and binding errors are collected, never shown.
/// </summary>
public static class Ui
{
    private static readonly Lazy<Dispatcher> Thread = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentQueue<string> Bindings = new();
    private static readonly ConcurrentQueue<Exception> Crashes = new();

    public static Dispatcher Dispatcher => Thread.Value;

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Dark };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Rigsight;component/Themes/Dark.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Rigsight;component/Themes/Theme.xaml") });
                ThemeManager.Apply(ThemeManager.Dark);
                app.DispatcherUnhandledException += (_, e) =>
                {
                    Crashes.Enqueue(e.Exception);
                    e.Handled = true;
                };

                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(new Collector());
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            ready.Set();
            if (error is null) Dispatcher.Run();
        })
        { IsBackground = true, Name = "Test UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return dispatcher!;
    }

    public static void Run(Action action) => Dispatcher.Invoke(action);

    public static T Run<T>(Func<T> func) => Dispatcher.Invoke(func);

    /// <summary>Runs async UI work to completion (its continuations stay on the UI thread).</summary>
    public static void Run(Func<Task> func, int timeoutMs = 60_000)
    {
        var task = Dispatcher.Invoke(func);
        if (!task.Wait(timeoutMs)) throw new TimeoutException($"UI work didn't finish in {timeoutMs} ms");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the UI thread process everything queued (layout, rendering, bindings, posted work) for a while.</summary>
    public static void Pump(int ms = 50)
    {
        var until = Stopwatch.StartNew();
        do
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (ms > 0) System.Threading.Thread.Sleep(Math.Min(15, ms));
        } while (until.ElapsedMilliseconds < ms);
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Waits (pumping) until <paramref name="condition"/>, evaluated on the UI thread, holds.</summary>
    public static bool WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Dispatcher.Invoke(condition)) return true;
            Pump(25);
        }
        return Dispatcher.Invoke(condition);
    }

    /// <summary>Binding errors and UI-thread exceptions since the last call (then cleared).</summary>
    public static (List<string> Bindings, List<Exception> Crashes) TakeProblems()
    {
        var b = new List<string>();
        var c = new List<Exception>();
        while (Bindings.TryDequeue(out var s)) b.Add(s);
        while (Crashes.TryDequeue(out var e)) c.Add(e);
        return (b, c);
    }

    /// <summary>Fails with every binding error and UI-thread exception since the last call.</summary>
    public static void AssertNoProblems(string where)
    {
        var (bindings, crashes) = TakeProblems();
        var lines = bindings.Select(b => "binding: " + b).Concat(crashes.Select(c => "exception: " + c)).ToList();
        Assert.True(lines.Count == 0, $"{where}:\n" + string.Join("\n", lines.Distinct().Take(40)));
    }

    /// <summary>Renders an element as it looks now (PNG), for failure evidence or visual review.</summary>
    public static void SavePng(FrameworkElement element, string path)
    {
        int w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        png.Save(file);
    }

    private sealed class Collector : TraceListener
    {
        private string _pending = "";

        public override void Write(string? message) => _pending += message;

        public override void WriteLine(string? message)
        {
            Bindings.Enqueue(_pending + message);
            _pending = "";
        }
    }
}

/// <summary>
/// Tests that use the shared UI thread, or change something the whole process shares (Units.Fahrenheit, environment
/// variables, the update store's folder), run here, one at a time: binding errors are collected globally, and a switch
/// flipped by one test must not show up in another running meanwhile.
/// </summary>
[CollectionDefinition("UI", DisableParallelization = true)]
public sealed class UiCollection;
