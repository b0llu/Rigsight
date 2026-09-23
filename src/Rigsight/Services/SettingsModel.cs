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

    public void Update(Action<RigsightSettings> change)
    {
        change(Current);
        Units.Fahrenheit = Current.UseFahrenheit;
        _debounce.Stop();
        _debounce.Start();
        Changed?.Invoke();
    }

    public void Flush()
    {
        _debounce.Stop();
        _client.Send(new UiMessage { T = "settings", Settings = Current.Clone() });
    }

    public void ApplyFromAgent(RigsightSettings settings)
    {
        // A local change is about to be sent; the agent will echo it back afterwards.
        if (_debounce.IsEnabled) return;
        Current = settings;
        Units.Fahrenheit = settings.UseFahrenheit;
        Changed?.Invoke();
    }
}
