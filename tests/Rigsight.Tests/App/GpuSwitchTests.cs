using Rigsight.Tests.AppUi;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// A PC with a processor's integrated graphics listed before its card: the card is the main GPU, and the Temperatures
/// page's GPU card switches between the two.
/// </summary>
[Collection("UI")]
public sealed class GpuSwitchTests
{
    private const string Card = "NVIDIA GeForce RTX 3080 Ti";

    [Fact]
    public void The_card_is_the_main_gpu_and_shown_first()
    {
        var (_, live) = Kit.Greeted(hello: Fixtures.HelloWithIntegratedGpu());
        Ui.Run(() =>
        {
            Assert.Equal([Card, Fixtures.IntegratedGpuName], live.Gpus.Select(g => g.Name));
            Assert.Equal(Card, live.GpuName);
            Assert.Same(live.Gpus[0], live.SelectedGpu);
            Assert.True(live.HasSeveralGpus);
            Assert.Equal("GPU 1 of 2", live.GpuPositionText);
            // The main readings (sidebar, Home, history) are the card's too.
            Assert.Same(live.Gpus[0].Temp, live.GpuTemp);
            Assert.True(live.Gpus[1].Integrated);
        });
    }

    [Fact]
    public void The_arrows_go_round_the_gpus()
    {
        var (_, live) = Kit.Greeted(hello: Fixtures.HelloWithIntegratedGpu());
        Ui.Run(() =>
        {
            live.NextGpuCommand.Execute(null);
            Assert.Equal(Fixtures.IntegratedGpuName, live.SelectedGpu?.Name);
            Assert.Equal("GPU 2 of 2", live.GpuPositionText);
            live.NextGpuCommand.Execute(null);
            Assert.Equal(Card, live.SelectedGpu?.Name);
            live.PreviousGpuCommand.Execute(null);
            Assert.Equal(Fixtures.IntegratedGpuName, live.SelectedGpu?.Name);
            // Only the card shown changes: the main GPU stays the card.
            Assert.Equal(Card, live.GpuName);
        });
    }

    [Fact]
    public void Each_gpu_shows_its_own_readings()
    {
        var (_, live) = Kit.Greeted(hello: Fixtures.HelloWithIntegratedGpu());
        Ui.Run(() =>
        {
            live.ApplyTick(Fixtures.TickWithIntegratedGpu());
            var igpu = live.Gpus[1];
            Assert.Equal(44, igpu.Temp?.Value);
            Assert.Equal(54, igpu.MemJunction?.Value);
            Assert.Null(igpu.HotSpot);
            Assert.Equal("0.5 / 2.0 GB", igpu.VramText);
            Assert.Equal(25, igpu.VramPercent, 3);
            Assert.NotEqual(igpu.Temp?.Value, live.Gpus[0].Temp?.Value);
        });
    }

    [Fact]
    public void The_chosen_gpu_stays_chosen_when_the_sensor_list_is_sent_again()
    {
        var (_, live) = Kit.Greeted(hello: Fixtures.HelloWithIntegratedGpu());
        Ui.Run(() =>
        {
            live.NextGpuCommand.Execute(null);
            live.LoadHello(Fixtures.HelloWithIntegratedGpu()); // the agent restarted
            Assert.Equal(Fixtures.IntegratedGpuName, live.SelectedGpu?.Name);
        });
    }

    [Fact]
    public void One_gpu_has_no_arrows()
    {
        var (_, live) = Kit.Greeted(hello: Fixtures.Hello());
        Ui.Run(() =>
        {
            Assert.Single(live.Gpus);
            Assert.False(live.HasSeveralGpus);
            live.NextGpuCommand.Execute(null);
            Assert.Same(live.Gpus[0], live.SelectedGpu);
        });
    }
}

/// <summary>The Temperatures page on a PC with two GPUs: the arrows show, switch the card, and nothing fails to bind.</summary>
[Collection("UI")]
public sealed class GpuSwitchPageTests(AppHost host) : IClassFixture<AppHost>
{
    [Fact]
    public void The_temperatures_page_switches_between_gpus()
    {
        try
        {
            host.RestartAgent(integratedGpu: true);
            Assert.True(Ui.WaitFor(() => host.Shell.Live.Gpus.Count == 2, 10_000), "the app didn't get the second GPU");
            host.Show("temperatures", 600);
            Ui.TakeProblems();
            for (int i = 0; i < 3; i++)
            {
                Ui.Run(() => host.Shell.Live.NextGpuCommand.Execute(null));
                host.Agent.Tick(i);
                Ui.Pump(200);
            }
            Ui.AssertNoProblems("switching GPUs on Temperatures");
            var arrows = Ui.Run(() => Visuals.Descendants<System.Windows.Controls.Button>(host.Window.PageHost)
                .Count(b => System.Windows.Automation.AutomationProperties.GetName(b) is "Next GPU" or "Previous GPU" && b.IsVisible));
            Assert.Equal(2, arrows);
            Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(TestEnvironment.DataDir, "screens", "temperatures-two-gpus.png")));
        }
        finally
        {
            host.RestartAgent();
            Assert.True(Ui.WaitFor(() => host.Shell.Live.Gpus.Count == 1, 10_000));
        }
    }
}
