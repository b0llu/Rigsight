using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Settings › Ask RigSight › Your API key, the "Just paste" box (2026-10-27): what it says about what was pasted, and
/// that a key it can't place is never sent anywhere until the person picks the service.
/// </summary>
public sealed class JustPasteViewModelTests
{
    private static AiSettingsViewModel Make()
    {
        var settings = Kit.OfflineSettings(new RigsightSettings());
        return new AiSettingsViewModel(settings, new AskViewModel(settings));
    }

    [Theory]
    [InlineData("sk-or-v1-3f9cabc", "That's an OpenRouter key.", "OpenRouter")]
    [InlineData("sk-ant-usr-1eB-3g8", "That's an Anthropic key.", "Anthropic")]
    [InlineData("AIzaSyD-example", "That's a Google Gemini key.", "Google Gemini")]
    [InlineData("http://192.168.1.20:8000", "That's a server address.", "Custom")]
    public void The_box_says_what_was_pasted(string pasted, string line, string service)
    {
        var vm = Make();
        vm.ApiKey = pasted;
        Assert.Equal(line, vm.DetectedLine);
        Assert.Equal(service, vm.DetectedService);
    }

    /// <summary>A DeepSeek key starts "sk-" like an old OpenAI key: the box asks, and Connect waits for the pick.</summary>
    [Fact]
    public void A_plain_sk_key_is_asked_about_and_sent_nowhere_until_picked()
    {
        var vm = Make();
        vm.ApiKey = "sk-a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
        Assert.Null(vm.DetectedService);
        Assert.StartsWith("Keys from DeepSeek", vm.DetectedLine);
        Assert.True(vm.ShowServicePick);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        vm.PickedService = "DeepSeek";
        Assert.Equal("DeepSeek", vm.DetectedService);
        Assert.False(vm.ShowServicePick);
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void A_guess_can_be_corrected()
    {
        var vm = Make();
        vm.ApiKey = "sk-proj-abc";
        Assert.True(vm.ShowNotRightLink);
        vm.ChooseServiceCommand.Execute(null);
        Assert.True(vm.ShowServicePick);
        vm.PickedService = "DeepSeek";
        Assert.Equal("DeepSeek", vm.DetectedService);
        Assert.False(vm.ShowServicePick);
    }

    [Theory]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_048_576, "1M")]
    [InlineData(200_000, "200k")]
    [InlineData(128_000, "128k")]
    public void Context_is_written_as_the_services_write_it(int tokens, string text) =>
        Assert.Equal(text, AiSettingsViewModel.ContextText(tokens));
}
