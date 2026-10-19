using System.Windows.Markup;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// A page's fixed text in the person's language: <c>Text="{ctl:T Crashes}"</c>, or quoted where it has a comma:
/// <c>Text="{ctl:T 'Average, today'}"</c>. Read once as the page is built (a change of language starts the app again).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }

    public T(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Lang.T(Text);
}
