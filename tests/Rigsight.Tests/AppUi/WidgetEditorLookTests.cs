using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.AppUi;

/// <summary>The widget editor in the real window, as a picture to look at.</summary>
[Collection("UI")]
public sealed class WidgetEditorLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    [Fact]
    public void The_editor_as_it_looks()
    {
        host.Show("widgets");
        Ui.Run(() =>
        {
            var cfg = WidgetsViewModel.NewWidget("In use", [WidgetTiles.New(WidgetTiles.Kinds.First(k => k.Id == WidgetTiles.NowPlaying))]);
            host.Shell.Settings.Update(s => s.Widgets.Add(cfg));
            host.Shell.Widgets.Edit(cfg.Id);
        });
        Ui.Pump(800);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "widget-editor.png")));
    }
}
