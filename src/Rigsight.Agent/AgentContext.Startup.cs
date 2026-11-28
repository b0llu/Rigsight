using Rigsight.Core;
using Rigsight.Core.Startup;

namespace Rigsight.Agent;

// Every start of the PC, kept in RigSight's own history (1.22.0, the Start-up page): Windows' sign-in logs hold only about
// a week (Shell-Core, 1 MB) to a month (Winlogon), so each start is copied a few minutes after it. Read two minutes after the
// agent starts (Windows is still launching the apps that start with Windows at sign-in), again every few minutes until this
// start is complete (those apps all launched; up to 45 minutes after sign-in), then once a day (a PC woken from hibernation
// keeps the agent running). None of it needs admin rights. Sampler thread, from the minute's checks.
internal sealed partial class AgentContext
{
    private long _startsDueAt;

    /// <summary>How often the starts are read again while this start isn't complete, and once it is.</summary>
    private const long StartsSoonMs = 5 * 60_000, StartsDailyMs = 24 * 3600_000L, StartsFirstMs = 2 * 60_000;

    /// <summary>Sampler thread, each minute: reads and keeps Windows' records of the PC's starts when they're due.</summary>
    private void MaybeRecordStarts()
    {
        long now = Environment.TickCount64;
        if (_startsDueAt == 0) { _startsDueAt = now + StartsFirstMs; return; }
        if (now < _startsDueAt) return;
        _startsDueAt = now + StartsDailyMs;
        if (StartupLogReader.Record(_db, _isAdmin, DateTime.Now) is not null) _startsDueAt = now + StartsSoonMs;
    }
}
