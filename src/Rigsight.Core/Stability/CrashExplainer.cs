namespace Rigsight.Core.Stability;

/// <summary>
/// A crash described in plain language. <see cref="Culprit"/> is the short label the Crashes page shows in its own
/// column ("NVIDIA driver", "Froze"); <see cref="Cause"/> is that label only when it adds to the title, for a
/// sentence like "Game crashed at 3:04 PM: NVIDIA driver" (null where it would only repeat the title: "froze: froze").
/// </summary>
public sealed record CrashExplanation(string Title, string Reason, string Advice, string Culprit, string? Cause = null);

/// <summary>
/// Turns the cryptic parts of a crash record (module names, exception and bugcheck codes) into
/// "what happened" and "what you can try".
/// </summary>
public static class CrashExplainer
{
    private static readonly (string Prefix, string Culprit, string Reason, string Advice)[] Modules =
    [
        ("nvwgf2um", Lang.Key("NVIDIA driver"), Lang.Key("The NVIDIA graphics driver failed while running {0}."), Lang.Key("Update the NVIDIA driver (a clean install helps). If the GPU is overclocked, try stock clocks.")),
        ("nvd3dum", Lang.Key("NVIDIA driver"), Lang.Key("The NVIDIA graphics driver failed while running {0}."), Lang.Key("Update the NVIDIA driver (a clean install helps). If the GPU is overclocked, try stock clocks.")),
        ("nvoglv", Lang.Key("NVIDIA driver"), Lang.Key("The NVIDIA OpenGL driver failed while running {0}."), Lang.Key("Update the NVIDIA driver.")),
        ("nvlddmkm", Lang.Key("NVIDIA driver"), Lang.Key("The NVIDIA kernel driver failed."), Lang.Key("Update the NVIDIA driver. Repeated failures can mean an unstable GPU overclock or overheating.")),
        ("nvspcap", Lang.Key("GeForce Experience overlay"), Lang.Key("The NVIDIA overlay (GeForce Experience / NVIDIA app) crashed inside {0}."), Lang.Key("Try turning off the NVIDIA in-game overlay.")),
        ("amdkmdag", Lang.Key("AMD driver"), Lang.Key("The AMD kernel driver failed."), Lang.Key("Update the AMD Adrenalin driver (a clean install helps). Repeated failures can mean an unstable GPU overclock or overheating.")),
        ("atikm", Lang.Key("AMD driver"), Lang.Key("The AMD kernel driver failed."), Lang.Key("Update the AMD Adrenalin driver (a clean install helps). Repeated failures can mean an unstable GPU overclock or overheating.")),
        ("amdxx", Lang.Key("AMD driver"), Lang.Key("The AMD graphics driver failed while running {0}."), Lang.Key("Update the AMD Adrenalin driver.")),
        ("atiumd", Lang.Key("AMD driver"), Lang.Key("The AMD graphics driver failed while running {0}."), Lang.Key("Update the AMD Adrenalin driver.")),
        ("amdxc", Lang.Key("AMD driver"), Lang.Key("The AMD graphics driver failed while running {0}."), Lang.Key("Update the AMD Adrenalin driver.")),
        ("igd", Lang.Key("Intel graphics driver"), Lang.Key("The Intel graphics driver failed while running {0}."), Lang.Key("Update the Intel graphics driver.")),
        ("d3d11", Lang.Key("DirectX"), Lang.Key("DirectX 11 failed inside {0} — usually a graphics driver or game problem."), Lang.Key("Update the graphics driver and the game; verify the game files.")),
        ("d3d12", Lang.Key("DirectX"), Lang.Key("DirectX 12 failed inside {0} — usually a graphics driver or game problem."), Lang.Key("Update the graphics driver and the game; verify the game files.")),
        ("dxgi", Lang.Key("DirectX"), Lang.Key("The DirectX graphics layer failed inside {0}."), Lang.Key("Update the graphics driver; disable overlays if it keeps happening.")),
        ("vulkan-1", Lang.Key("Vulkan"), Lang.Key("The Vulkan graphics layer failed inside {0}."), Lang.Key("Update the graphics driver.")),
        ("gameoverlayrenderer", Lang.Key("Steam overlay"), Lang.Key("The Steam overlay crashed inside {0}."), Lang.Key("Try disabling the Steam overlay for this game.")),
        ("discordhook", Lang.Key("Discord overlay"), Lang.Key("The Discord overlay crashed inside {0}."), Lang.Key("Try turning off Discord's in-game overlay.")),
        ("rtsshooks", Lang.Key("RivaTuner overlay"), Lang.Key("The RivaTuner/MSI Afterburner overlay crashed inside {0}."), Lang.Key("Update or disable RivaTuner Statistics Server.")),
        ("unityplayer", Lang.Key("Unity engine"), Lang.Key("The Unity game engine inside {0} crashed — usually a bug in the game."), Lang.Key("Update the game and verify its files. Mods can cause this too.")),
        ("gameassembly", Lang.Key("Game code"), Lang.Key("{0} crashed in its own game code."), Lang.Key("Update the game and verify its files. Mods can cause this too.")),
        ("ue4", Lang.Key("Unreal Engine"), Lang.Key("The Unreal Engine inside {0} crashed."), Lang.Key("Update the game and verify its files.")),
        ("coreclr", Lang.Key(".NET runtime"), Lang.Key("{0} hit an unhandled .NET error."), Lang.Key("Update the app; reinstall it if it keeps happening.")),
        ("clr.dll", Lang.Key(".NET runtime"), Lang.Key("{0} hit an unhandled .NET error."), Lang.Key("Update the app; reinstall it if it keeps happening.")),
    ];

    private static readonly Dictionary<string, (string Name, string Meaning)> ExceptionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c0000005"] = (Lang.Key("Access violation"), Lang.Key("it tried to use memory it wasn't allowed to")),
        ["c0000409"] = (Lang.Key("Stack buffer overrun"), Lang.Key("it detected corrupted memory and shut itself down")),
        ["c0000374"] = (Lang.Key("Heap corruption"), Lang.Key("its memory got corrupted (often mods, overlays or a bug)")),
        ["c00000fd"] = (Lang.Key("Stack overflow"), Lang.Key("it ran out of stack space (usually a bug)")),
        ["c000001d"] = (Lang.Key("Illegal instruction"), Lang.Key("the CPU hit an invalid instruction — this can point to an unstable overclock or undervolt")),
        ["c0000096"] = (Lang.Key("Privileged instruction"), Lang.Key("the CPU refused an instruction — possibly an unstable overclock")),
        ["e0434352"] = (Lang.Key(".NET exception"), Lang.Key("it threw an error it didn't handle")),
        ["80000003"] = (Lang.Key("Breakpoint"), Lang.Key("it stopped itself on an internal check")),
        ["c0000142"] = (Lang.Key("Failed to start"), Lang.Key("it couldn't initialize (often missing files or runtimes)")),
    };

    private static readonly Dictionary<uint, (string Name, string Reason, string Advice)> Bugchecks = new()
    {
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", Lang.Key("The hardware reported an error it couldn't recover from — usually CPU or RAM instability."), Lang.Key("Undo overclocks/undervolts (including Curve Optimizer and XMP/EXPO), update the BIOS, and check temperatures.")),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", Lang.Key("A CPU core stopped responding — commonly an unstable CPU overclock or undervolt."), Lang.Key("Reset CPU tuning (Curve Optimizer / PBO) to default and update the BIOS.")),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", Lang.Key("A driver took too long to respond — often storage, network or chipset drivers."), Lang.Key("Update chipset, storage (NVMe/SATA) and network drivers, and the SSD firmware.")),
        [0x116] = ("VIDEO_TDR_FAILURE", Lang.Key("The graphics driver stopped responding and couldn't be recovered."), Lang.Key("Update or clean-reinstall the graphics driver; undo GPU overclocks; check GPU temperatures.")),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", Lang.Key("The graphics scheduler hit an error — usually the graphics driver."), Lang.Key("Update or clean-reinstall the graphics driver.")),
        [0x1A] = ("MEMORY_MANAGEMENT", Lang.Key("Windows found a memory management error — often faulty or unstable RAM."), Lang.Key("Disable XMP/EXPO to test, and run Windows Memory Diagnostic or MemTest86.")),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", Lang.Key("Invalid memory was accessed — bad RAM or a faulty driver."), Lang.Key("Test RAM (disable XMP/EXPO) and update drivers.")),
        [0x4E] = ("PFN_LIST_CORRUPT", Lang.Key("Memory bookkeeping got corrupted — often RAM."), Lang.Key("Test your RAM and disable XMP/EXPO to check stability.")),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", Lang.Key("A system service or driver crashed."), Lang.Key("Update Windows and your drivers (especially GPU and anti-cheat).")),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", Lang.Key("A driver crashed."), Lang.Key("Update drivers; recently installed software is a common cause.")),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", Lang.Key("A driver accessed memory incorrectly."), Lang.Key("Update network, audio and graphics drivers.")),
        [0xA] = ("IRQL_NOT_LESS_OR_EQUAL", Lang.Key("A driver or hardware accessed memory incorrectly."), Lang.Key("Update drivers; test RAM if it repeats.")),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", Lang.Key("A driver failed during sleep or wake."), Lang.Key("Update chipset and graphics drivers; try disabling fast startup.")),
        [0xEF] = ("CRITICAL_PROCESS_DIED", Lang.Key("A critical Windows process stopped."), Lang.Key("Run 'sfc /scannow' and check the drive's health.")),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", Lang.Key("Windows couldn't read data from the drive."), Lang.Key("Check the drive health (Storage page) and cables.")),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", Lang.Key("The storage system hit an error."), Lang.Key("Check SSD health and update its firmware.")),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", Lang.Key("Windows detected corrupted data in the kernel — usually a driver."), Lang.Key("Update drivers and Windows.")),
        [0x19] = ("BAD_POOL_HEADER", Lang.Key("Kernel memory got corrupted — usually a driver."), Lang.Key("Update drivers; test RAM if it repeats.")),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", Lang.Key("A driver crashed."), Lang.Key("Update drivers.")),
        [0xC2] = ("BAD_POOL_CALLER", Lang.Key("A driver misused memory."), Lang.Key("Update drivers.")),
    };

    /// <summary>
    /// What a file that something crashed in is known to be, and what helps (null: not one of the known ones). The
    /// culprit stays English, since it's also matched on: translate it where it's shown (<c>Lang.T(culprit)</c>).
    /// </summary>
    public static (string Culprit, string Advice)? Module(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        string lower = file.ToLowerInvariant();
        foreach (var m in Modules)
            if (lower.StartsWith(m.Prefix, StringComparison.Ordinal)) return (m.Culprit, Lang.T(m.Advice));
        return null;
    }

    /// <summary>What Windows says the error code means, as a sentence that follows another: " Windows says it tried … (Access violation)."</summary>
    private static string Says((string Name, string Meaning) ex) =>
        ex.Meaning is null ? "" : " " + Lang.T($"Windows says {Lang.T(ex.Meaning)} ({Lang.T(ex.Name)}).");

    public static CrashExplanation Explain(CrashEvent e, string? appName = null)
    {
        string app = appName ?? (string.IsNullOrEmpty(e.AppExe) ? Lang.T("an app") : Path.GetFileNameWithoutExtension(e.AppExe));
        // The culprit (and the cause, where it is one) stays English, since it's matched on elsewhere: it's marked here
        // and translated where it's shown.
        switch (e.Kind)
        {
            case CrashKind.AppHang:
                return new(Wording.Event(CrashKind.AppHang, app),
                    Lang.T($"{app} froze and was closed."),
                    Lang.T("If it keeps happening, update the app and your drivers. Running low on memory or overheating can cause freezes too."),
                    Lang.Key("Froze")); // no cause: the title says it all

            case CrashKind.GpuDriverReset:
                return new(Wording.Title(CrashKind.GpuDriverReset, ""),
                    Lang.T("The GPU took too long to respond, so Windows reset the graphics driver. The screen may have gone black for a moment."),
                    Lang.T("Usually a driver problem, an unstable GPU overclock, or overheating. Update the driver and check GPU temperatures around this time."),
                    Lang.Key("Graphics driver")); // no cause: "graphics driver reset: graphics driver" says nothing new

            case CrashKind.SystemCrash:
            {
                uint code = ParseHex(e.Code);
                if (Bugchecks.TryGetValue(code, out var b))
                    return new(Wording.Event(CrashKind.SystemCrash, ""), $"{Lang.T(b.Reason)} ({b.Name}, {e.Code})", Lang.T(b.Advice), b.Name, b.Name);
                return new(Wording.Event(CrashKind.SystemCrash, ""), Lang.T($"Windows stopped with error {e.Code}."),
                    Lang.T("Search the code online together with your recently installed drivers or software."), e.Code ?? Lang.Key("Bugcheck"), e.Code);
            }

            case CrashKind.UnexpectedShutdown:
                return e.Moment switch
                {
                    PowerMoment.Asleep => new(Lang.T("The PC shut off while asleep"),
                        Lang.T("The PC was asleep (or waking up) when it shut off, and no crash report was saved."),
                        Lang.T("Common if power is switched off at the wall while the PC sleeps. Otherwise try updating the BIOS and chipset drivers, or turning off hybrid sleep."),
                        Lang.Key("Asleep")), // no cause: the title says it
                    PowerMoment.ShuttingDown => new(Lang.T("The PC didn't finish shutting down"),
                        Lang.T("Windows was shutting down or restarting when the PC shut off, before it could record a clean finish. No crash report was saved."),
                        Lang.T("Usually harmless. If it keeps happening, try turning off Fast Startup, or update the BIOS and chipset drivers."),
                        Lang.Key("Shutdown")), // no cause: the title says it
                    _ => new(Wording.Event(CrashKind.UnexpectedShutdown, ""),
                        Lang.T("The PC turned off without shutting down, and no crash report was saved — a power cut, a held power button, or a hard freeze."),
                        Lang.T("If it happened while gaming, check the temperatures just before, and consider the power supply."),
                        Lang.Key("Power / hard freeze"), Lang.Key("a power cut or a hard freeze")),
                };

            default:
            {
                if (WindowsPart(e) is { } part) return part;
                // Windows writes "unknown" when it couldn't tell: no module, not one called that.
                string module = string.Equals(e.Module, "unknown", StringComparison.OrdinalIgnoreCase) ? "" : e.Module ?? "";
                string moduleLower = module.ToLowerInvariant();
                ExceptionCodes.TryGetValue(e.Code ?? "", out var ex);
                string what = Says(ex);

                foreach (var m in Modules)
                    if (moduleLower.StartsWith(m.Prefix, StringComparison.Ordinal))
                        return new(Lang.T($"{app} crashed"), Lang.F(m.Reason, app) + what, Lang.T(m.Advice), m.Culprit, m.Culprit);

                bool ownCode = !string.IsNullOrEmpty(module) && string.Equals(module, e.AppExe, StringComparison.OrdinalIgnoreCase);
                if (ownCode)
                    return new(Lang.T($"{app} crashed"), Lang.T($"{app} crashed in its own code.") + what,
                        Lang.T("Usually a bug in the app or game. Update it, verify the game files, and remove mods if you use them."), Lang.Key("The app itself"), Lang.Key("in its own code"));

                if (moduleLower is "ntdll.dll" or "kernelbase.dll" or "ucrtbase.dll" or "kernel32.dll")
                    return new(Lang.T($"{app} crashed"), Lang.T($"{app} hit an error it couldn't recover from.") + what,
                        Lang.T("Usually a bug in the app. Update it; if it only happens in one game, verify its files and disable overlays."), Lang.Key("The app itself"), Lang.Key("in its own code"));

                return new(Lang.T($"{app} crashed"),
                    (string.IsNullOrEmpty(module) ? Lang.T($"{app} crashed.") : Lang.T($"{app} crashed inside {module}.")) + what,
                    Lang.T("Update the app and your drivers. If a specific add-on or overlay is named, try disabling it."),
                    string.IsNullOrEmpty(module) ? Lang.Key("Unknown") : module, string.IsNullOrEmpty(module) ? null : module);
            }
        }
    }

    /// <summary>One of Windows' own parts, whose crash is said as what the user saw ("Windows' display crashed").</summary>
    public static bool IsWindowsPart(string exe) => exe.ToLowerInvariant() is "dwm.exe" or "explorer.exe" or "svchost.exe";

    /// <summary>Windows' own parts crashing mean something different from an app crashing: say what the user saw.</summary>
    private static CrashExplanation? WindowsPart(CrashEvent e)
    {
        string what = ExceptionCodes.TryGetValue(e.Code ?? "", out var ex) ? Says(ex) : "";
        return e.AppExe.ToLowerInvariant() switch
        {
            "dwm.exe" => new(Lang.T("Windows' display crashed"),
                Lang.T("The part of Windows that draws everything on screen (Desktop Window Manager) crashed and restarted. The screen probably flickered or went black for a moment.") + what,
                Lang.T("This is almost always the graphics driver. Update or clean-reinstall it, and undo any GPU overclock."), Lang.Key("Graphics driver")),
            "explorer.exe" => new(Lang.T("Taskbar and desktop restarted"),
                Lang.T("Windows Explorer, which runs the taskbar, desktop and file windows, crashed and restarted.") + what,
                Lang.T("Often a shell extension from another app (right-click menu add-ons, cloud drives). If it repeats, update or remove recently installed tools."), Lang.Key("Windows Explorer")),
            "svchost.exe" => new(Lang.T("A Windows service crashed"),
                Lang.T("One of Windows' background services crashed.") + what,
                Lang.T("Usually harmless and restarted automatically. Keep Windows updated; if it repeats, run 'sfc /scannow'."), Lang.Key("Windows service")),
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
