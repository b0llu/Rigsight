using System.Windows;

namespace Rigsight.Controls;

/// <summary>
/// Holds an object for bindings that can't find it by walking up the tree (1.23.0): a recycled row of All sensors is out of
/// the page for a moment while it changes kind, and its triggers' "the SensorsView above me" bindings logged errors each
/// time (seen at the 1.23.0 gate). Put in a page's resources; the page sets <see cref="Data"/> to itself.
/// </summary>
public sealed class Proxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(Proxy));

    public object? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    protected override Freezable CreateInstanceCore() => new Proxy();
}
