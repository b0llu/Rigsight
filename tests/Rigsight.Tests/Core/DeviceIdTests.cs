using Rigsight.Core.Ai;

namespace Rigsight.Tests.Core;

/// <summary>
/// The id the relay knows a PC by is per Windows account since 1.0.0: with one id for the whole PC, only the first
/// account to ask was let in (each account has its own secret), and every other one was told its sign-in changed.
/// </summary>
public sealed class DeviceIdTests
{
    [Fact]
    public void Each_account_on_one_PC_is_its_own_and_stays_the_same()
    {
        const string machine = "9b2c6f3e-1111-2222-3333-444455556666";
        string admin = Relay.IdFor("rigsight:", machine, "S-1-5-21-1-2-3-1001");
        string child = Relay.IdFor("rigsight:", machine, "S-1-5-21-1-2-3-1002");
        Assert.NotEqual(admin, child);
        Assert.Equal(admin, Relay.IdFor("rigsight:", machine, "S-1-5-21-1-2-3-1001"));
        Assert.Matches("^[0-9a-f]{64}$", admin);
        // No account readable: the PC alone, as before.
        Assert.NotEqual(admin, Relay.IdFor("rigsight:", machine, ""));
    }

    [Fact]
    public void This_PCs_id_is_made_from_this_account()
    {
        Assert.Matches("^[0-9a-f]{64}$", Relay.DeviceId);
        Assert.Equal(Relay.DeviceId, Relay.DeviceId);
    }
}
