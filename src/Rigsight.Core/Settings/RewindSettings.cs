namespace Rigsight.Core.Settings;

/// <summary>
/// Rewind: a look back at a month, three months, six months or a year, as cards to watch in the app and share as
/// pictures. Every kind is made and every topic goes in; what people still choose is whether a finished one is
/// announced with a notification, and the pictures' shape in Share.
/// Removed 2026-11-13 at the user's word ("only thing people will be able to do is watch a rewind and share it"): which
/// kinds are made, the topics, the games and apps never shown, full screen on or off, and Share's colours.
/// </summary>
public sealed class RewindSettings
{
    /// <summary>A notification when one is finished while RigSight is closed (the agent's, in the Notifications style).</summary>
    public bool Notify { get; set; } = true;

    /// <summary>The pictures' shape in Share: "story", "post" or "wide".</summary>
    public string ShareShape { get; set; } = "story";
}
