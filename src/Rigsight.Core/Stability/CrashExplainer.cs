namespace Rigsight.Core.Stability;

/// <summary>A crash described in plain language.</summary>
public sealed record CrashExplanation(string Title, string Reason, string Advice, string Culprit);

/// <summary>
/// Turns the cryptic parts of a crash record (module names, exception and bugcheck codes) into
/// "what happened" and "what you can try".
/// </summary>
public static class CrashExplainer
{
    private static readonly (string Prefix, string Culprit, string Reason, string Advice)[] Modules =
    [
        ("nvwgf2um", "NVIDIA driver", "The NVIDIA graphics driver failed while running {app}.", "Update the NVIDIA driver (a clean install helps). If the GPU is overclocked, try stock clocks."),
        ("nvd3dum", "NVIDIA driver", "The NVIDIA graphics driver failed while running {app}.", "Update the NVIDIA driver (a clean install helps). If the GPU is overclocked, try stock clocks."),
        ("nvoglv", "NVIDIA driver", "The NVIDIA OpenGL driver failed while running {app}.", "Update the NVIDIA driver."),
        ("nvlddmkm", "NVIDIA driver", "The NVIDIA kernel driver failed.", "Update the NVIDIA driver. Repeated failures can mean an unstable GPU overclock or overheating."),
        ("nvspcap", "GeForce Experience overlay", "The NVIDIA overlay (GeForce Experience / NVIDIA app) crashed inside {app}.", "Try turning off the NVIDIA in-game overlay."),
        ("amdxx", "AMD driver", "The AMD graphics driver failed while running {app}.", "Update the AMD Adrenalin driver."),
        ("atiumd", "AMD driver", "The AMD graphics driver failed while running {app}.", "Update the AMD Adrenalin driver."),
        ("amdxc", "AMD driver", "The AMD graphics driver failed while running {app}.", "Update the AMD Adrenalin driver."),
        ("igd", "Intel graphics driver", "The Intel graphics driver failed while running {app}.", "Update the Intel graphics driver."),
        ("d3d11", "DirectX", "DirectX 11 failed inside {app} — usually a graphics driver or game problem.", "Update the GPU driver and the game; verify the game files."),
        ("d3d12", "DirectX", "DirectX 12 failed inside {app} — usually a graphics driver or game problem.", "Update the GPU driver and the game; verify the game files."),
        ("dxgi", "DirectX", "The DirectX graphics layer failed inside {app}.", "Update the GPU driver; disable overlays if it keeps happening."),
        ("vulkan-1", "Vulkan", "The Vulkan graphics layer failed inside {app}.", "Update the GPU driver."),
        ("gameoverlayrenderer", "Steam overlay", "The Steam overlay crashed inside {app}.", "Try disabling the Steam overlay for this game."),
        ("discordhook", "Discord overlay", "The Discord overlay crashed inside {app}.", "Try turning off Discord's in-game overlay."),
        ("rtsshooks", "RivaTuner overlay", "The RivaTuner/MSI Afterburner overlay crashed inside {app}.", "Update or disable RivaTuner Statistics Server."),
        ("unityplayer", "Unity engine", "The Unity game engine inside {app} crashed — usually a bug in the game.", "Update the game and verify its files. Mods can cause this too."),
        ("gameassembly", "Game code", "{app} crashed in its own game code.", "Update the game and verify its files. Mods can cause this too."),
        ("ue4", "Unreal Engine", "The Unreal Engine inside {app} crashed.", "Update the game and verify its files."),
        ("coreclr", ".NET runtime", "{app} hit an unhandled .NET error.", "Update the app; reinstall it if it keeps happening."),
        ("clr.dll", ".NET runtime", "{app} hit an unhandled .NET error.", "Update the app; reinstall it if it keeps happening."),
    ];

    private static readonly Dictionary<string, (string Name, string Meaning)> ExceptionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c0000005"] = ("Access violation", "it tried to use memory it wasn't allowed to"),
        ["c0000409"] = ("Stack buffer overrun", "it detected corrupted memory and shut itself down"),
        ["c0000374"] = ("Heap corruption", "its memory got corrupted (often mods, overlays or a bug)"),
        ["c00000fd"] = ("Stack overflow", "it ran out of stack space (usually a bug)"),
        ["c000001d"] = ("Illegal instruction", "the CPU hit an invalid instruction — this can point to an unstable overclock or undervolt"),
        ["c0000096"] = ("Privileged instruction", "the CPU refused an instruction — possibly an unstable overclock"),
        ["e0434352"] = (".NET exception", "it threw an error it didn't handle"),
        ["80000003"] = ("Breakpoint", "it stopped itself on an internal check"),
        ["c0000142"] = ("Failed to start", "it couldn't initialize (often missing files or runtimes)"),
    };

    private static readonly Dictionary<uint, (string Name, string Reason, string Advice)> Bugchecks = new()
    {
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it couldn't recover from — usually CPU or RAM instability.", "Undo overclocks/undervolts (including Curve Optimizer and XMP/EXPO), update the BIOS, and check temperatures."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "A CPU core stopped responding — commonly an unstable CPU overclock or undervolt.", "Reset CPU tuning (Curve Optimizer / PBO) to default and update the BIOS."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver took too long to respond — often storage, network or chipset drivers.", "Update chipset, storage (NVMe/SATA) and network drivers, and the SSD firmware."),
        [0x116] = ("VIDEO_TDR_FAILURE", "The graphics driver stopped responding and couldn't be recovered.", "Update or clean-reinstall the GPU driver; undo GPU overclocks; check GPU temperatures."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics scheduler hit an error — usually the GPU driver.", "Update or clean-reinstall the GPU driver."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Windows found a memory management error — often faulty or unstable RAM.", "Disable XMP/EXPO to test, and run Windows Memory Diagnostic or MemTest86."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Invalid memory was accessed — bad RAM or a faulty driver.", "Test RAM (disable XMP/EXPO) and update drivers."),
        [0x4E] = ("PFN_LIST_CORRUPT", "Memory bookkeeping got corrupted — often RAM.", "Test your RAM and disable XMP/EXPO to check stability."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "A system service or driver crashed.", "Update Windows and your drivers (especially GPU and anti-cheat)."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A driver crashed.", "Update drivers; recently installed software is a common cause."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver accessed memory incorrectly.", "Update network, audio and GPU drivers."),
        [0xA] = ("IRQL_NOT_LESS_OR_EQUAL", "A driver or hardware accessed memory incorrectly.", "Update drivers; test RAM if it repeats."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver failed during sleep or wake.", "Update chipset and GPU drivers; try disabling fast startup."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A critical Windows process stopped.", "Run 'sfc /scannow' and check the drive's health."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Windows couldn't read data from the drive.", "Check the drive health (Storage page) and cables."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "The storage system hit an error.", "Check SSD health and update its firmware."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Windows detected corrupted data in the kernel — usually a driver.", "Update drivers and Windows."),
        [0x19] = ("BAD_POOL_HEADER", "Kernel memory got corrupted — usually a driver.", "Update drivers; test RAM if it repeats."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "A driver crashed.", "Update drivers."),
        [0xC2] = ("BAD_POOL_CALLER", "A driver misused memory.", "Update drivers."),
    };

    public static CrashExplanation Explain(CrashEvent e, string? appName = null)
    {
        string app = appName ?? (string.IsNullOrEmpty(e.AppExe) ? "an app" : Path.GetFileNameWithoutExtension(e.AppExe));
        switch (e.Kind)
        {
            case CrashKind.AppHang:
                return new($"{app} stopped responding",
                    $"{app} froze and was closed.",
                    "If it keeps happening, update the app and your drivers. Running low on memory or overheating can cause freezes too.",
                    "Not responding");

            case CrashKind.GpuDriverReset:
                return new("Graphics driver reset",
                    "The GPU took too long to respond, so Windows reset the graphics driver. The screen may have gone black for a moment.",
                    "Usually a driver problem, an unstable GPU overclock, or overheating. Update the driver and check GPU temperatures around this time.",
                    "GPU driver");

            case CrashKind.SystemCrash:
            {
                uint code = ParseHex(e.Code);
                if (Bugchecks.TryGetValue(code, out var b))
                    return new("Windows crashed (blue screen)", $"{b.Reason} ({b.Name}, {e.Code})", b.Advice, b.Name);
                return new("Windows crashed (blue screen)", $"Windows stopped with error {e.Code}.",
                    "Search the code online together with your recently installed drivers or software.", e.Code ?? "Bugcheck");
            }

            case CrashKind.UnexpectedShutdown:
                return e.DuringSleep
                    ? new("PC lost power while asleep",
                        "The PC was asleep (or waking up) when it went down, and no crash report was saved.",
                        "Common if power is switched off at the wall while the PC sleeps. Otherwise try updating the BIOS and chipset drivers, or turning off hybrid sleep.",
                        "Power while asleep")
                    : new("PC shut off unexpectedly",
                        "The PC turned off without shutting down, and no crash report was saved — a power cut, a held power button, or a hard freeze.",
                        "If it happened while gaming, check the temperatures just before, and consider the power supply.",
                        "Power / hard freeze");

            default:
            {
                if (WindowsPart(e) is { } part) return part;
                string module = e.Module ?? "";
                string moduleLower = module.ToLowerInvariant();
                ExceptionCodes.TryGetValue(e.Code ?? "", out var ex);
                string what = ex.Meaning is null ? "" : $" Windows says {ex.Meaning} ({ex.Name}).";

                foreach (var m in Modules)
                    if (moduleLower.StartsWith(m.Prefix, StringComparison.Ordinal))
                        return new($"{app} crashed", m.Reason.Replace("{app}", app) + what, m.Advice, m.Culprit);

                bool ownCode = !string.IsNullOrEmpty(module) && string.Equals(module, e.AppExe, StringComparison.OrdinalIgnoreCase);
                if (ownCode)
                    return new($"{app} crashed", $"{app} crashed in its own code.{what}",
                        "Usually a bug in the app or game. Update it, verify the game files, and remove mods if you use them.", "The app itself");

                if (moduleLower is "ntdll.dll" or "kernelbase.dll" or "ucrtbase.dll" or "kernel32.dll")
                    return new($"{app} crashed", $"{app} hit an error it couldn't recover from.{what}",
                        "Usually a bug in the app. Update it; if it only happens in one game, verify its files and disable overlays.", "The app itself");

                return new($"{app} crashed",
                    string.IsNullOrEmpty(module) ? $"{app} crashed.{what}" : $"{app} crashed inside {module}.{what}",
                    "Update the app and your drivers. If a specific add-on or overlay is named, try disabling it.",
                    string.IsNullOrEmpty(module) ? "Unknown" : module);
            }
        }
    }

    /// <summary>Windows' own parts crashing mean something different from an app crashing: say what the user saw.</summary>
    private static CrashExplanation? WindowsPart(CrashEvent e)
    {
        string what = ExceptionCodes.TryGetValue(e.Code ?? "", out var ex) ? $" Windows says {ex.Meaning} ({ex.Name})." : "";
        return e.AppExe.ToLowerInvariant() switch
        {
            "dwm.exe" => new("Windows' display crashed",
                "The part of Windows that draws everything on screen (Desktop Window Manager) crashed and restarted. The screen probably flickered or went black for a moment." + what,
                "This is almost always the graphics driver: update or clean-reinstall it, and undo any GPU overclock.", "Graphics driver"),
            "explorer.exe" => new("Taskbar and desktop restarted",
                "Windows Explorer, which runs the taskbar, desktop and file windows, crashed and restarted." + what,
                "Often a shell extension from another app (right-click menu add-ons, cloud drives). If it repeats, update or remove recently installed tools.", "Windows Explorer"),
            "svchost.exe" => new("A Windows service crashed",
                "One of Windows' background services crashed." + what,
                "Usually harmless and restarted automatically. Keep Windows updated; if it repeats, run 'sfc /scannow'.", "Windows service"),
            _ => null,
        };
    }

    private static uint ParseHex(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }
}
