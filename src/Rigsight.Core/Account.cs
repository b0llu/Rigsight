using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Rigsight.Core;

/// <summary>What the account this process runs under may do, as Windows tells every program.</summary>
public enum AccountKind
{
    /// <summary>Admin rights are on (an elevated admin, the built-in Administrator, or an admin with UAC off).</summary>
    Admin,
    /// <summary>An admin's account with the rights not on: one "Yes" on Windows' prompt turns them on, no password.</summary>
    AdminNotElevated,
    /// <summary>A standard account: admin rights would take someone else's password, so they're never asked for.</summary>
    Standard,
}

public static class Account
{
    private static AccountKind? _kind;

    /// <summary>
    /// This process's account. A test copy can be told what to be (RIGSIGHT_TEST_ACCOUNT=standard), to look at a
    /// standard account's Rigsight on a PC that only has admins.
    /// </summary>
    public static AccountKind Kind => _kind ??= Read();

    private static AccountKind Read()
    {
        if (RigsightPaths.IsTestInstance && string.Equals(Environment.GetEnvironmentVariable("RIGSIGHT_TEST_ACCOUNT"), "standard", StringComparison.OrdinalIgnoreCase))
            return AccountKind.Standard;
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return AccountKind.Admin;
        // Only a clear answer makes it a standard account. Should Windows not say, it stays as it was before this
        // was asked at all: an account that is offered the prompt.
        return Elevation(identity.Token) == Default ? AccountKind.Standard : AccountKind.AdminNotElevated;
    }

    /// <summary>
    /// This process runs under another account than the person signed in at the screen: started with an admin's
    /// password from a standard account's sign-in. Its own folders are then the admin's, not the person's.
    /// </summary>
    public static bool ElevatedAsSomeoneElse
    {
        get
        {
            try
            {
                string? user = SessionText(UserName);
                if (string.IsNullOrEmpty(user)) return false; // no one at this session's screen (a service, the system)
                // By SID, not by name: a Microsoft or work account's sign-in name isn't spelt the way its own
                // processes spell it. Any doubt means "the same person", which is what was assumed before this existed.
                var atScreen = (SecurityIdentifier)new NTAccount(SessionText(DomainName) ?? "", user).Translate(typeof(SecurityIdentifier));
                using var me = WindowsIdentity.GetCurrent();
                return me.User is { } mine && !atScreen.Equals(mine);
            }
            catch
            {
                return false;
            }
        }
    }

    private const int CurrentSession = -1, UserName = 5, DomainName = 7; // WTS_CURRENT_SESSION, WTSUserName, WTSDomainName

    private static string? SessionText(int what)
    {
        if (!WTSQuerySessionInformation(0, CurrentSession, what, out nint buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(nint server, int session, int infoClass, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    // TOKEN_ELEVATION_TYPE: 1 no other token goes with this one (a standard account, or UAC off), 2 the full token of
    // an admin, 3 the limited token of an admin, whose full one a "Yes" on the prompt gives.
    private const int TokenElevationType = 18, Default = 1;

    private static int Elevation(nint token)
    {
        try
        {
            return GetTokenInformation(token, TokenElevationType, out int type, sizeof(int), out _) ? type : 0;
        }
        catch (Exception ex)
        {
            Log.Error("agent", ex);
            return 0; // unknown
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);
}
