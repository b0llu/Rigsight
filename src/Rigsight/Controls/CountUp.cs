using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// A figure that counts from what it said to what it says now ("34h 45m" to "241h 10m", "53 GB" to "512 GB"), in a
/// quarter of a second, as mock-up ReK33EVbdKbsoarY7jwYXQ's tiles do when the period changes (issue #11). Only when
/// the two read alike (the same words around the same kinds of figure); "947 GB" to "1.1 TB", or "None" to "2", just
/// changes. At once with Windows' animations off. Set <c>ctl:CountUp.Text</c> in place of <c>Text</c>.
/// </summary>
public static partial class CountUp
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(CountUp), new PropertyMetadata(null, (d, e) => Changed((TextBlock)d, e.OldValue as string, e.NewValue as string)));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    /// <summary>A figure in the text: a time ("12h 45m", "38m", "45s", in seconds) or a plain number, and how many decimals it shows.</summary>
    internal readonly record struct Figure(bool IsTime, double Value, int Decimals);

    [GeneratedRegex(@"(?<h>\d+)h(?: (?<hm>\d{1,2})m)?|(?<m>\d+)m|(?<s>\d+)s|(?<n>\d+(?:\.\d+)?)")]
    private static partial Regex Figures();

    /// <summary>The words around the figures, and the figures; null with none.</summary>
    internal static (string[] Words, Figure[] Figures)? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var words = new List<string>();
        var figures = new List<Figure>();
        int at = 0;
        foreach (Match m in Figures().Matches(text))
        {
            words.Add(text[at..m.Index]);
            at = m.Index + m.Length;
            if (m.Groups["h"].Success) figures.Add(new(true, int.Parse(m.Groups["h"].Value) * 3600 + (m.Groups["hm"].Success ? int.Parse(m.Groups["hm"].Value) * 60 : 0), 0));
            else if (m.Groups["m"].Success) figures.Add(new(true, int.Parse(m.Groups["m"].Value) * 60, 0));
            else if (m.Groups["s"].Success) figures.Add(new(true, int.Parse(m.Groups["s"].Value), 0));
            else
            {
                string n = m.Groups["n"].Value;
                int dot = n.IndexOf('.');
                figures.Add(new(false, double.Parse(n, CultureInfo.InvariantCulture), dot < 0 ? 0 : n.Length - dot - 1));
            }
        }
        words.Add(text[at..]);
        return figures.Count == 0 ? null : ([.. words], [.. figures]);
    }

    /// <summary>The text on its way from <paramref name="from"/> to <paramref name="to"/>, <paramref name="t"/> of the way (eased); null when they don't read alike.</summary>
    internal static string? Between(string? from, string? to, double t)
    {
        if (Parse(from) is not var (wordsA, figsA) || Parse(to) is not var (wordsB, figsB)) return null;
        if (figsA.Length != figsB.Length || !wordsA.SequenceEqual(wordsB)) return null;
        for (int i = 0; i < figsA.Length; i++)
            if (figsA[i].IsTime != figsB[i].IsTime) return null;
        var sb = new System.Text.StringBuilder(wordsB[0]);
        for (int i = 0; i < figsB.Length; i++)
        {
            double v = figsA[i].Value + (figsB[i].Value - figsA[i].Value) * t;
            sb.Append(figsB[i].IsTime ? Units.Duration(v >= 60 ? Math.Round(v / 60) * 60 : Math.Round(v)) : v.ToString("F" + figsB[i].Decimals, CultureInfo.InvariantCulture));
            sb.Append(wordsB[i + 1]);
        }
        return sb.ToString();
    }

    private sealed class Run
    {
        public required string From, To;
        public readonly Stopwatch Clock = new();
        /// <summary>Frames still to pass before it starts counting (see <see cref="Frames.Settle"/>).</summary>
        public int Wait = Frames.Settle;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Run> Runs = new();

    private static void Changed(TextBlock block, string? old, string? now)
    {
        string shown = block.Text;
        Runs.Remove(block);
        // The first figure, a figure that reads differently, or no motion wanted: it just changes.
        if (!SystemParameters.ClientAreaAnimation || !block.IsLoaded || string.IsNullOrEmpty(shown) || now is null || shown == now
            || Between(shown, now, 0) is null)
        {
            block.Text = now ?? "";
            return;
        }
        Runs.Add(block, new Run { From = shown, To = now });
        EnsureTicking();
    }

    private static bool _ticking;
    private static void EnsureTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += Tick;
    }

    private static void Tick(object? sender, EventArgs e)
    {
        bool any = false;
        foreach (var (block, run) in Runs.ToList())
        {
            if (run.Wait > 0)
            {
                if (--run.Wait == 0) run.Clock.Start();
                any = true;
                continue;
            }
            double p = Math.Min(1, run.Clock.Elapsed / ChartTween.Duration);
            block.Text = p >= 1 ? run.To : Between(run.From, run.To, ChartTween.Ease(p)) ?? run.To;
            if (p >= 1) Runs.Remove(block); else any = true;
        }
        if (any) return;
        CompositionTarget.Rendering -= Tick;
        _ticking = false;
    }
}

/// <summary>
/// A bar (a ProgressBar's value) that slides from its width to the new one when the period changes, as the mock-up's
/// Where the time went bars do (issue #11). A row built again (the list is made anew with each report) slides from
/// where the row of the same <see cref="KeyProperty"/> was; a new one from nothing.
/// </summary>
public static class SmoothBar
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached("Key", typeof(string), typeof(SmoothBar), new PropertyMetadata(null));
    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(SmoothBar), new PropertyMetadata(0.0, (d, e) => Changed((ProgressBar)d, (double)e.NewValue)));
    public static double GetValue(DependencyObject d) => (double)d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, double value) => d.SetValue(ValueProperty, value);

    /// <summary>Where each keyed bar was last drawn to.</summary>
    private static readonly Dictionary<string, double> Last = [];

    private static void Changed(ProgressBar bar, double to, bool waited = false)
    {
        string? key = GetKey(bar);
        // The key is a binding of its own and may come after the value: a row built again would slide from nothing.
        if (key is null && !waited)
        {
            bar.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () => Changed(bar, GetValue(bar), waited: true));
            return;
        }
        double from = key is not null && Last.TryGetValue(key, out var was) ? was : bar.Value;
        if (key is not null) Last[key] = to;
        if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - to) < 1e-6)
        {
            bar.BeginAnimation(ProgressBar.ValueProperty, null);
            bar.Value = to;
            return;
        }
        // Held where it was until the new figures are laid out and shown, then the mock-up's ease: quick, then slowing
        // right down as it lands (started at once, the layout's frame used it all up).
        bar.BeginAnimation(ProgressBar.ValueProperty, new DoubleAnimation(from, from, TimeSpan.Zero), HandoffBehavior.SnapshotAndReplace);
        Frames.After(() => bar.BeginAnimation(ProgressBar.ValueProperty, new DoubleAnimation(from, to, ChartTween.Duration)
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace));
    }
}
