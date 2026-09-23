using System.Windows.Threading;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;

namespace Rigsight.Services;

/// <summary>
/// The app's copy of the settings. Changes are sent to the agent (the only process that writes
/// settings.json) after a short debounce; the agent's copy comes back whenever it changes.
/// </summary>
public sealed class SettingsModel
{
    private readonly AgentClient _client;
    private readonly DispatcherTimer _debounce;

    public SettingsModel(AgentClient client)
    {
        _client = client;
        Current = SettingsStore.Load();
        Units.Fahrenheit = Current.UseFahrenheit;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => Flush();
    }

    public RigsightSettings Current { get; private set; }

    /// <summary>Raised after settings changed, locally or from the agent.</summary>
    public event Action? Changed;

    // A change not yet delivered to the agent (debouncing, or no connection): keep it, send it when the
    // agent connects, and don't let the agent's older copy overwrite it meanwhile.
    private bool _dirty;

    public void Update(Action<RigsightSettings> change)
    {
        change(Current);
        Units.Fahrenheit = Current.UseFahrenheit;
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
        Changed?.Invoke();
    }

    public void Flush()
    {
        _debounce.Stop();
        if (!_dirty) return;
        if (_client.Send(new UiMessage { T = "settings", Settings = Current.Clone() })) _dirty = false;
    }

    /// <summary>The agent (re)connected: deliver a change made while it wasn't there.</summary>
    public void OnConnected()
    {
        if (_dirty) Flush();
    }

    public void ApplyFromAgent(RigsightSettings settings)
    {
        // A local change is about to be (or couldn't yet be) sent; the agent will echo it back afterwards.
        if (_debounce.IsEnabled || _dirty) return;
        // Most messages echo what we already have: nothing to refresh then.
        if (SettingsStore.Serialize(settings) == SettingsStore.Serialize(Current)) return;
        Current = settings;
        Units.Fahrenheit = settings.UseFahrenheit;
        Changed?.Invoke();
    }
}
