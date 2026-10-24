using Rigsight.Core;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// Text people can select and copy like in any app: drag over it, double-click a word, Ctrl+C (the person's call,
/// 2026-10-24: "make text in the app copyable"). Set <c>ctl:Select.Text="True"</c> on a window and every plain TextBlock
/// in it can be selected; "False" on an element takes its part out.
/// <para>
/// A TextBlock has everything a read-only TextBox selects with except WPF's own text editor, which TextBox switches on
/// and TextBlock never does. This switches it on, through WPF's internal TextEditor (the same code TextBox has used
/// since 2006), only on the TextBlock pressed and only until focus leaves it: the 1,850 others stay plain, so lists and
/// pages cost nothing more. If a .NET update ever changed those internals, text simply stays unselectable (logged
/// once), never a crash; SelectTests fails that day.
/// </para>
/// Text inside anything pressed for another reason stays plain, so pressing it still does that: buttons, toggles,
/// list and tab items, menus, scroll bars, links, anything with a hand or drag cursor, a press another handler took.
/// </summary>
public static class Select
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(bool), typeof(Select),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetText(DependencyObject d) => (bool)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, bool value) => d.SetValue(TextProperty, value);

    // WPF's internal text editor, found once.
    private static readonly Type? EditorType = typeof(FrameworkElement).Assembly.GetType("System.Windows.Documents.TextEditor")
                                               ?? typeof(TextBox).Assembly.GetType("System.Windows.Documents.TextEditor");
    private static readonly Type? ContainerType = typeof(TextBox).Assembly.GetType("System.Windows.Documents.ITextContainer");
    private static readonly PropertyInfo? BlockContainer = typeof(TextBlock).GetProperty("TextContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? ContainerView = ContainerType?.GetProperty("TextView");
    private static readonly PropertyInfo? EditorReadOnly = EditorType?.GetProperty("IsReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? EditorView = EditorType?.GetProperty("TextView", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? EditorSelection = EditorType?.GetProperty("Selection", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? EditorDetach = EditorType?.GetMethod("OnDetach", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
    private static readonly ConstructorInfo? EditorNew = EditorType is null || ContainerType is null ? null
        : EditorType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [ContainerType, typeof(FrameworkElement), typeof(bool)]);
    private static readonly MethodInfo? Register = EditorType?.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic,
        [typeof(Type), typeof(bool), typeof(bool), typeof(bool)]);

    /// <summary>Every internal piece is there: text can be selected on this .NET.</summary>
    internal static bool Works { get; } = Init();

    private static readonly DependencyProperty EditorProperty = DependencyProperty.RegisterAttached("Editor", typeof(object), typeof(Select));

    private static bool Init()
    {
        if (BlockContainer is null || ContainerView is null || EditorReadOnly is null || EditorView is null || EditorSelection is null
            || EditorDetach is null || EditorNew is null || Register is null)
        {
            Log.Write("ui", "Selectable text: WPF's text editor isn't where it was (a .NET update?); text stays unselectable");
            return false;
        }
        try
        {
            // The editor's own handlers (mouse, keys, Copy, Select all, the right-click menu), for every TextBlock: each
            // does nothing on a TextBlock with no editor, which is all of them but the one being selected.
            Register.Invoke(null, [typeof(TextBlock), true, true, true]);
            EventManager.RegisterClassHandler(typeof(TextBlock), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPress));
            EventManager.RegisterClassHandler(typeof(TextBlock), Mouse.QueryCursorEvent, new QueryCursorEventHandler(OnCursor));
            EventManager.RegisterClassHandler(typeof(TextBlock), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLeft));
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or MemberAccessException)
        {
            Log.Write("ui", $"Selectable text: WPF's text editor couldn't be switched on ({ex.Message}); text stays unselectable");
            return false;
        }
    }

    /// <summary>Called once at start, so the class handlers are in before any TextBlock is pressed.</summary>
    public static void Start() => _ = Works;

    /// <summary>Can be selected: in a part marked for it, not empty, and not inside something pressed for another reason.</summary>
    internal static bool Selectable(TextBlock tb)
    {
        if (!Works || !GetText(tb) || !tb.IsEnabled) return false;
        for (DependencyObject? d = tb; d is not null; d = Parent(d))
        {
            if (d is ButtonBase or Selector or ListBoxItem or ComboBoxItem or TabItem or MenuItem or Thumb or ScrollBar or TextBoxBase or Calendar or ToolTip) return false;
            // A hand (it opens), or a move or resize cursor (it drags): pressed for that, not to select.
            if (d != tb && d is FrameworkElement { Cursor: { } c } && c != Cursors.Arrow && c != Cursors.IBeam) return false;
            if (d is FrameworkElement fe && fe.ReadLocalValue(TextProperty) is false) return false;
            if (d is Window or Popup) break;
        }
        return tb.Cursor is null || tb.Cursor == Cursors.IBeam;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static void OnCursor(object sender, QueryCursorEventArgs e)
    {
        if (e.Handled || sender is not TextBlock tb || tb.GetValue(EditorProperty) is not null) return;
        if (Selectable(tb)) { e.Cursor = Cursors.IBeam; e.Handled = true; }
    }

    private static void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        // A link inside the text opens as before: no editor over it.
        if (e.OriginalSource is Hyperlink || (e.OriginalSource as DependencyObject is { } o && LogicalTreeHelper.GetParent(o) is Hyperlink))
        {
            Detach(tb);
            return;
        }
        if (tb.GetValue(EditorProperty) is null && Selectable(tb)) Attach(tb);
    }

    // Focus gone elsewhere (not to the text's own right-click menu): its selection is cleared and the editor removed.
    private static void OnLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBlock tb || !ReferenceEquals(e.OldFocus, tb) || e.NewFocus is ContextMenu or MenuItem) return;
        Detach(tb);
    }

    /// <summary>The editor on <paramref name="tb"/>: it can be selected, and takes keyboard focus for Ctrl+C.</summary>
    internal static bool Attach(TextBlock tb)
    {
        if (!Works || tb.GetValue(EditorProperty) is not null) return Works;
        try
        {
            var container = BlockContainer!.GetValue(tb);
            var editor = EditorNew!.Invoke([container, tb, false]);
            EditorReadOnly!.SetValue(editor, true);
            EditorView!.SetValue(editor, ContainerView!.GetValue(container));
            tb.SetValue(EditorProperty, editor);
            tb.Focusable = true;
            tb.FocusVisualStyle = null;
            KeyboardNavigation.SetIsTabStop(tb, false);
            // The selection in the accent, as TextBoxes have it.
            tb.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            Log.Write("ui", $"Selectable text: couldn't be switched on for one text ({ex.InnerException?.Message ?? ex.Message})");
            return false;
        }
    }

    internal static void Detach(TextBlock tb)
    {
        if (tb.GetValue(EditorProperty) is not { } editor) return;
        try
        {
            if (EditorSelection!.GetValue(editor) is TextRange selection) selection.Select(tb.ContentStart, tb.ContentStart);
            EditorDetach!.Invoke(editor, null);
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            Log.Write("ui", $"Selectable text: the editor couldn't be removed cleanly ({ex.InnerException?.Message ?? ex.Message})");
        }
        tb.ClearValue(EditorProperty);
        tb.ClearValue(UIElement.FocusableProperty);
        tb.ClearValue(FrameworkElement.FocusVisualStyleProperty);
        tb.ClearValue(KeyboardNavigation.IsTabStopProperty);
        tb.ClearValue(TextBoxBase.SelectionBrushProperty);
    }

    /// <summary>What is selected in <paramref name="tb"/> (the tests).</summary>
    internal static string Selected(TextBlock tb) =>
        tb.GetValue(EditorProperty) is { } editor && EditorSelection!.GetValue(editor) is TextRange range ? range.Text : "";

    /// <summary>Selects all of <paramref name="tb"/>'s text, as Ctrl+A in it does (the tests).</summary>
    internal static void SelectAll(TextBlock tb)
    {
        if (tb.GetValue(EditorProperty) is { } editor && EditorSelection!.GetValue(editor) is TextRange range) range.Select(tb.ContentStart, tb.ContentEnd);
    }
}
