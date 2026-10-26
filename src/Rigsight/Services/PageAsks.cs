using Rigsight.Core;
using Rigsight.Core.Ai;

namespace Rigsight.Services;

/// <summary>
/// The question of the AI button in each page's title row (approved mock-up board 5, 2026-10-26): what most people want
/// from that page, about this PC. A page whose records it reads may be one the plan doesn't include; the answer then says
/// what it could see and names the page (see AskViewModel.LockedFor). Read once: a language change restarts the app.
/// </summary>
public static class PageAsks
{
    private static AiQuestion Q(string text, string page) => new(text, page);

    public static AiQuestion Home { get; } = Q(Lang.T("How is my PC doing today, and is anything worth a look?"), AiPages.Home);
    public static AiQuestion Apps { get; } = Q(Lang.T("Which of my apps use the most time, CPU or memory, and is any worth closing or swapping?"), AiPages.Apps);
    public static AiQuestion Games { get; } = Q(Lang.T("Which of my games runs worst on this PC, and what would help it most?"), AiPages.Games);
    public static AiQuestion Processes { get; } = Q(Lang.T("Is anything running right now slowing my PC down or using more than usual?"), AiPages.Processes);
    public static AiQuestion Crashes { get; } = Q(Lang.T("Looking at all my crashes and shut-offs, is there a pattern, and what should I try first?"), AiPages.Crashes);
    public static AiQuestion Timeline { get; } = Q(Lang.T("Did any recent driver, Windows update or setting change make my PC worse?"), AiPages.Timeline);
    public static AiQuestion Temperatures { get; } = Q(Lang.T("Are my temperatures normal for my parts, and should I do anything about them?"), AiPages.Temperatures);
    public static AiQuestion Fans { get; } = Q(Lang.T("Are my fans keeping up, and would a different fan curve be quieter or cooler?"), AiPages.Fans);
    public static AiQuestion Memory { get; } = Q(Lang.T("Is my memory enough for how I use this PC, or would more help?"), AiPages.Memory);
    public static AiQuestion Storage { get; } = Q(Lang.T("What is filling my drives, and what is safe to free up?"), AiPages.Storage);
    public static AiQuestion Network { get; } = Q(Lang.T("Is my connection stable, and which apps use the most internet?"), AiPages.Network);
    public static AiQuestion Sensors { get; } = Q(Lang.T("Are any of my sensor readings unusual or worth keeping an eye on?"), AiPages.Sensors);
    public static AiQuestion Widgets { get; } = Q(Lang.T("From how my PC runs (its heat, games and memory), which widgets should I keep on screen, and why?"), AiPages.Widgets);
    public static AiQuestion Overlay { get; } = Q(Lang.T("For the games I play, which readings should my in-game overlay show?"), AiPages.Overlay);
    public static AiQuestion Taskbar { get; } = Q(Lang.T("Which readings are most worth watching in my taskbar, given how my PC runs?"), AiPages.Taskbar);
    public static AiQuestion Dashboards { get; } = Q(Lang.T("What tiles would make a useful dashboard for how I use this PC?"), AiPages.Dashboards);
}
