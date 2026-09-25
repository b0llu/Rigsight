using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

public sealed class CrashExplainerTests
{
    private static CrashExplanation Crash(string exe, string? module = null, string? code = null, string? name = null) =>
        CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.AppCrash, AppExe = exe, Module = module, Code = code }, name);

    // ---- Where it crashed ----

    [Theory]
    [InlineData("nvwgf2umx.dll", "NVIDIA driver", "The NVIDIA graphics driver failed while running game.")]
    [InlineData("nvd3dumx.dll", "NVIDIA driver", "The NVIDIA graphics driver failed while running game.")]
    [InlineData("nvoglv64.dll", "NVIDIA driver", "The NVIDIA OpenGL driver failed while running game.")]
    [InlineData("nvlddmkm.sys", "NVIDIA driver", "The NVIDIA kernel driver failed.")]
    [InlineData("nvspcap64.dll", "GeForce Experience overlay", "The NVIDIA overlay (GeForce Experience / NVIDIA app) crashed inside game.")]
    [InlineData("amdxx64.dll", "AMD driver", "The AMD graphics driver failed while running game.")]
    [InlineData("atiumd64.dll", "AMD driver", "The AMD graphics driver failed while running game.")]
    [InlineData("amdxc64.dll", "AMD driver", "The AMD graphics driver failed while running game.")]
    [InlineData("igdumdim64.dll", "Intel graphics driver", "The Intel graphics driver failed while running game.")]
    [InlineData("d3d11.dll", "DirectX", "DirectX 11 failed inside game — usually a graphics driver or game problem.")]
    [InlineData("d3d12core.dll", "DirectX", "DirectX 12 failed inside game — usually a graphics driver or game problem.")]
    [InlineData("dxgi.dll", "DirectX", "The DirectX graphics layer failed inside game.")]
    [InlineData("vulkan-1.dll", "Vulkan", "The Vulkan graphics layer failed inside game.")]
    [InlineData("gameoverlayrenderer64.dll", "Steam overlay", "The Steam overlay crashed inside game.")]
    [InlineData("DiscordHook64.dll", "Discord overlay", "The Discord overlay crashed inside game.")]
    [InlineData("RTSSHooks64.dll", "RivaTuner overlay", "The RivaTuner/MSI Afterburner overlay crashed inside game.")]
    [InlineData("UnityPlayer.dll", "Unity engine", "The Unity game engine inside game crashed — usually a bug in the game.")]
    [InlineData("GameAssembly.dll", "Game code", "game crashed in its own game code.")]
    [InlineData("UE4-Win64-Shipping.exe", "Unreal Engine", "The Unreal Engine inside game crashed.")]
    [InlineData("coreclr.dll", ".NET runtime", "game hit an unhandled .NET error.")]
    [InlineData("clr.dll", ".NET runtime", "game hit an unhandled .NET error.")]
    public void Known_modules_name_the_culprit(string module, string culprit, string reason)
    {
        foreach (var m in new[] { module, module.ToUpperInvariant(), module.ToLowerInvariant() })
        {
            var e = Crash("game.exe", m);
            Assert.Equal("game crashed", e.Title);
            Assert.Equal(culprit, e.Culprit);
            Assert.Equal(reason, e.Reason);
            Assert.False(string.IsNullOrWhiteSpace(e.Advice));
            Assert.DoesNotContain("{app}", e.Reason + e.Advice);
        }
    }

    [Theory]
    [InlineData("c0000005", "Access violation", "it tried to use memory it wasn't allowed to")]
    [InlineData("c0000409", "Stack buffer overrun", "it detected corrupted memory and shut itself down")]
    [InlineData("c0000374", "Heap corruption", "its memory got corrupted (often mods, overlays or a bug)")]
    [InlineData("c00000fd", "Stack overflow", "it ran out of stack space (usually a bug)")]
    [InlineData("c000001d", "Illegal instruction", "the CPU hit an invalid instruction — this can point to an unstable overclock or undervolt")]
    [InlineData("c0000096", "Privileged instruction", "the CPU refused an instruction — possibly an unstable overclock")]
    [InlineData("e0434352", ".NET exception", "it threw an error it didn't handle")]
    [InlineData("80000003", "Breakpoint", "it stopped itself on an internal check")]
    [InlineData("c0000142", "Failed to start", "it couldn't initialize (often missing files or runtimes)")]
    public void Exception_codes_are_explained(string code, string name, string meaning)
    {
        string says = $" Windows says {meaning} ({name}).";
        Assert.Equal("The NVIDIA graphics driver failed while running game." + says, Crash("game.exe", "nvwgf2umx.dll", code).Reason);
        Assert.Equal("game crashed in its own code." + says, Crash("game.exe", "game.exe", code).Reason);
        Assert.Equal("game hit an error it couldn't recover from." + says, Crash("game.exe", "ntdll.dll", code).Reason);
        Assert.Equal("game crashed inside foo.dll." + says, Crash("game.exe", "foo.dll", code).Reason);
        Assert.Equal("game crashed." + says, Crash("game.exe", null, code).Reason);
        Assert.Equal("game crashed." + says, Crash("game.exe", null, code.ToUpperInvariant()).Reason);
        Assert.Equal("Windows' display crashed", Crash("dwm.exe", null, code).Title);
        Assert.EndsWith(says, Crash("dwm.exe", null, code).Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("0xc0000005")]
    public void Unknown_codes_add_nothing(string? code) =>
        Assert.Equal("game crashed inside foo.dll.", Crash("game.exe", "foo.dll", code).Reason);

    [Theory]
    [InlineData("game.exe")]
    [InlineData("GAME.EXE")]
    [InlineData("Game.exe")]
    public void A_crash_in_the_apps_own_exe_blames_the_app(string module)
    {
        var e = Crash("game.exe", module);
        Assert.Equal("The app itself", e.Culprit);
        Assert.Equal("game crashed in its own code.", e.Reason);
    }

    [Theory]
    [InlineData("ntdll.dll")]
    [InlineData("KERNELBASE.dll")]
    [InlineData("ucrtbase.dll")]
    [InlineData("kernel32.dll")]
    public void A_crash_in_Windows_basic_libraries_blames_the_app(string module)
    {
        var e = Crash("game.exe", module);
        Assert.Equal("The app itself", e.Culprit);
        Assert.Equal("game hit an error it couldn't recover from.", e.Reason);
    }

    [Fact]
    public void An_unknown_module_is_named()
    {
        var e = Crash("game.exe", "SomeMod.dll");
        Assert.Equal("SomeMod.dll", e.Culprit);
        Assert.Equal("game crashed inside SomeMod.dll.", e.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_module_is_unknown(string? module)
    {
        var e = Crash("game.exe", module);
        Assert.Equal("Unknown", e.Culprit);
        Assert.Equal("game crashed.", e.Reason);
    }

    [Fact]
    public void Modules_are_matched_by_their_start_only()
    {
        Assert.Equal("mynvwgf2um.dll", Crash("game.exe", "mynvwgf2um.dll").Culprit);
        Assert.Equal("clrjit.dll", Crash("game.exe", "clrjit.dll").Culprit);
    }

    // ---- The app's name ----

    [Fact]
    public void The_app_is_named_by_its_exe_without_the_extension() =>
        Assert.Equal("eldenring crashed", Crash("eldenring.exe").Title);

    [Fact]
    public void A_given_name_is_used_everywhere()
    {
        var e = Crash("eldenring.exe", "UnityPlayer.dll", name: "Elden Ring");
        Assert.Equal("Elden Ring crashed", e.Title);
        Assert.Contains("inside Elden Ring crashed", e.Reason);
    }

    [Fact]
    public void An_app_without_an_exe_is_an_app()
    {
        Assert.Equal("an app crashed", Crash("").Title);
        Assert.Equal("an app stopped responding", CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.AppHang }).Title);
    }

    // ---- Windows' own parts ----

    [Theory]
    [InlineData("dwm.exe", "Windows' display crashed", "Graphics driver")]
    [InlineData("DWM.EXE", "Windows' display crashed", "Graphics driver")]
    [InlineData("explorer.exe", "Taskbar and desktop restarted", "Windows Explorer")]
    [InlineData("Explorer.EXE", "Taskbar and desktop restarted", "Windows Explorer")]
    [InlineData("svchost.exe", "A Windows service crashed", "Windows service")]
    public void Windows_parts_crashing_say_what_the_user_saw(string exe, string title, string culprit)
    {
        // Whatever module it was in: the user saw the screen flicker or the taskbar restart, not "dwm crashed in nvwgf2um".
        foreach (var module in new[] { null, "nvwgf2umx.dll", "ntdll.dll", exe })
        {
            var e = Crash(exe, module);
            Assert.Equal(title, e.Title);
            Assert.Equal(culprit, e.Culprit);
        }
    }

    // ---- Other kinds ----

    [Fact]
    public void A_hang_is_explained()
    {
        var e = CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.AppHang, AppExe = "chrome.exe" }, "Chrome");
        Assert.Equal("Chrome stopped responding", e.Title);
        Assert.Equal("Chrome froze and was closed.", e.Reason);
        Assert.Equal("Not responding", e.Culprit);
    }

    [Fact]
    public void A_hang_of_a_Windows_part_is_still_a_hang() =>
        Assert.Equal("explorer stopped responding", CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.AppHang, AppExe = "explorer.exe" }).Title);

    [Fact]
    public void A_driver_reset_is_explained()
    {
        var e = CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.GpuDriverReset, Module = "nvlddmkm" });
        Assert.Equal("Graphics driver reset", e.Title);
        Assert.Equal("GPU driver", e.Culprit);
    }

    [Theory]
    [InlineData(false, "PC shut off unexpectedly", "Power / hard freeze")]
    [InlineData(true, "PC lost power while asleep", "Power while asleep")]
    public void An_unexpected_shutdown_is_explained(bool asleep, string title, string culprit)
    {
        var e = CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.UnexpectedShutdown, DuringSleep = asleep });
        Assert.Equal((title, culprit), (e.Title, e.Culprit));
    }

    [Theory]
    [InlineData(0x124u, "WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(0x101u, "CLOCK_WATCHDOG_TIMEOUT")]
    [InlineData(0x133u, "DPC_WATCHDOG_VIOLATION")]
    [InlineData(0x116u, "VIDEO_TDR_FAILURE")]
    [InlineData(0x119u, "VIDEO_SCHEDULER_INTERNAL_ERROR")]
    [InlineData(0x1Au, "MEMORY_MANAGEMENT")]
    [InlineData(0x50u, "PAGE_FAULT_IN_NONPAGED_AREA")]
    [InlineData(0x4Eu, "PFN_LIST_CORRUPT")]
    [InlineData(0x3Bu, "SYSTEM_SERVICE_EXCEPTION")]
    [InlineData(0x7Eu, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED")]
    [InlineData(0xD1u, "DRIVER_IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0xAu, "IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0x9Fu, "DRIVER_POWER_STATE_FAILURE")]
    [InlineData(0xEFu, "CRITICAL_PROCESS_DIED")]
    [InlineData(0x7Au, "KERNEL_DATA_INPAGE_ERROR")]
    [InlineData(0x154u, "UNEXPECTED_STORE_EXCEPTION")]
    [InlineData(0x139u, "KERNEL_SECURITY_CHECK_FAILURE")]
    [InlineData(0x19u, "BAD_POOL_HEADER")]
    [InlineData(0x1Eu, "KMODE_EXCEPTION_NOT_HANDLED")]
    [InlineData(0xC2u, "BAD_POOL_CALLER")]
    public void Blue_screens_are_explained_by_their_code(uint bugcheck, string name)
    {
        // As the crash reader writes it ("0x124"), and the other ways a code can be written.
        foreach (var code in new[] { $"0x{bugcheck:X}", $"0x{bugcheck:x8}", $"0X{bugcheck:X}", $"{bugcheck:X}" })
        {
            var e = CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.SystemCrash, Code = code });
            Assert.Equal("Windows crashed (blue screen)", e.Title);
            Assert.Equal(name, e.Culprit);
            Assert.EndsWith($"({name}, {code})", e.Reason);
            Assert.False(string.IsNullOrWhiteSpace(e.Advice));
        }
    }

    [Theory]
    [InlineData("0xDEAD")]
    [InlineData("0x0")]
    [InlineData("zz")]
    [InlineData("0xFFFFFFFFFF")]
    public void Unknown_blue_screens_show_their_code(string code)
    {
        var e = CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.SystemCrash, Code = code });
        Assert.Equal($"Windows stopped with error {code}.", e.Reason);
        Assert.Equal(code, e.Culprit);
    }

    [Fact]
    public void A_blue_screen_without_a_code_still_explains()
    {
        var e = CrashExplainer.Explain(new CrashEvent { Kind = CrashKind.SystemCrash });
        Assert.Equal("Bugcheck", e.Culprit);
        Assert.Equal("Windows crashed (blue screen)", e.Title);
    }

    public static TheoryData<CrashKind> Kinds => [.. Enum.GetValues<CrashKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_is_explained_even_with_nothing_known(CrashKind kind)
    {
        var e = CrashExplainer.Explain(new CrashEvent { Kind = kind });
        Assert.False(string.IsNullOrWhiteSpace(e.Title));
        Assert.False(string.IsNullOrWhiteSpace(e.Reason));
        Assert.False(string.IsNullOrWhiteSpace(e.Advice));
        Assert.False(string.IsNullOrWhiteSpace(e.Culprit));
    }

    [Fact]
    public void A_crash_time_is_its_unix_time()
    {
        var e = new CrashEvent { Ts = 1_790_000_000 };
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).LocalDateTime, e.Time);
    }
}
