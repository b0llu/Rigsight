using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>Where the overlay goes: nine anchors, offsets as a share of the screen, hanging off edges, and old corners.</summary>
public class OverlayPlacementTests
{
    private const double W = 2560, H = 1440, Gap = 16, Ow = 300, Oh = 120;

    [Theory]
    [InlineData(0, 16, 16)]
    [InlineData(1, 1130, 16)]
    [InlineData(2, 2244, 16)]
    [InlineData(3, 16, 660)]
    [InlineData(4, 1130, 660)]
    [InlineData(5, 2244, 660)]
    [InlineData(6, 16, 1304)]
    [InlineData(7, 1130, 1304)]
    [InlineData(8, 2244, 1304)]
    public void Each_anchor_without_an_offset_sits_the_gap_in_or_centred(int anchor, double x, double y) =>
        Assert.Equal((x, y), OverlayPlacement.Place(anchor, 0, 0, W, H, Ow, Oh, Gap));

    [Fact]
    public void An_offset_is_a_share_of_the_screen_so_it_holds_at_any_resolution()
    {
        Assert.Equal((16 + 256, 16 + 72), OverlayPlacement.Place(0, 0.1, 0.05, W, H, Ow, Oh, Gap));
        Assert.Equal((16 + 192, 16 + 54), OverlayPlacement.Place(0, 0.1, 0.05, 1920, 1080, Ow, Oh, Gap));
        // From the right: moving left is a negative offset.
        Assert.Equal((2244 - 256, 16), OverlayPlacement.Place(2, -0.1, 0, W, H, Ow, Oh, Gap));
    }

    [Fact]
    public void It_can_hang_off_any_edge_but_a_sliver_stays_on_screen()
    {
        var (x, y) = OverlayPlacement.Place(0, -0.05, -0.03, W, H, Ow, Oh, Gap);
        Assert.True(x < 0 && y < 0);
        Assert.Equal((OverlayPlacement.MinVisible - Ow, OverlayPlacement.MinVisible - Oh), OverlayPlacement.Place(0, -1, -1, W, H, Ow, Oh, Gap));
        Assert.Equal((W - OverlayPlacement.MinVisible, H - OverlayPlacement.MinVisible), OverlayPlacement.Place(8, 1, 1, W, H, Ow, Oh, Gap));
    }

    [Theory]
    [InlineData(16, 16, 0, 0, 0)]           // top-left corner
    [InlineData(1130, 660, 4, 0, 0)]        // dead centre
    [InlineData(2244, 1304, 8, 0, 0)]       // bottom-right corner
    [InlineData(400, 16, 0, 0.1500, 0)]     // along the top, still in the left third
    [InlineData(1000, 900, 4, -0.0508, 0.1667)] // middle third both ways
    [InlineData(-100, 700, 3, -0.0453, 0.0278)] // hanging off the left edge
    [InlineData(2400, -50, 2, 0.0609, -0.0458)] // off the top right
    public void A_spot_is_hung_from_the_third_its_middle_is_in(double x, double y, int anchor, double offsetX, double offsetY)
    {
        var spot = OverlayPlacement.FromPosition(x, y, W, H, Ow, Oh, Gap);
        Assert.Equal((anchor, offsetX, offsetY), spot);
        // And it goes back to the same place.
        var (px, py) = OverlayPlacement.Place(spot.Anchor, spot.OffsetX, spot.OffsetY, W, H, Ow, Oh, Gap);
        Assert.InRange(px, x - 1, x + 1);
        Assert.InRange(py, y - 1, y + 1);
    }

    [Fact]
    public void A_right_hand_spot_grows_to_the_left_when_the_overlay_gets_wider()
    {
        var (anchor, ox, oy) = OverlayPlacement.FromPosition(2000, 16, W, H, Ow, Oh, Gap);
        var (narrow, _) = OverlayPlacement.Place(anchor, ox, oy, W, H, Ow, Oh, Gap);
        var (wide, _) = OverlayPlacement.Place(anchor, ox, oy, W, H, Ow + 100, Oh, Gap);
        Assert.Equal(narrow + Ow, wide + Ow + 100, 6); // the right edge stays put
    }

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 0)]
    [InlineData(OverlayCorner.TopRight, 2)]
    [InlineData(OverlayCorner.BottomLeft, 6)]
    [InlineData(OverlayCorner.BottomRight, 8)]
    public void The_old_corners_are_their_anchors(OverlayCorner corner, int anchor) =>
        Assert.Equal(anchor, OverlayPlacement.FromCorner(corner));

    [Theory]
    [InlineData("\"Corner\": \"BottomRight\"", 8, 0, 0)]
    [InlineData("\"Corner\": \"TopRight\"", 2, 0, 0)]
    [InlineData("\"Corner\": \"BottomRight\", \"Anchor\": 4, \"OffsetX\": 0.2, \"OffsetY\": -0.1", 4, 0.2, -0.1)]
    [InlineData("\"Anchor\": 42, \"OffsetX\": 7, \"OffsetY\": -9", 8, 1, -1)]
    [InlineData("\"Anchor\": -3", 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    public void Settings_keep_the_old_corner_and_stay_in_range(string overlay, int anchor, double offsetX, double offsetY)
    {
        var s = SettingsStore.Deserialize($$"""{ "SettingsVersion": 8, "Overlay": { {{overlay}} } }""");
        Assert.Equal((anchor, offsetX, offsetY), (OverlayPlacement.AnchorOf(s.Overlay), s.Overlay.OffsetX, s.Overlay.OffsetY));
    }
}
