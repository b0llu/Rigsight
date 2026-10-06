using System.Windows;
using System.Windows.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Controls;

/// <summary>The chat's two kinds of line: what the user typed, and Riggy's answer.</summary>
public sealed class AskMessageTemplates : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Riggy { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item is AskMessage { IsUser: true } ? User : Riggy;
}
