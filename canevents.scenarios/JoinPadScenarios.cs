using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// The zone store and its edit flow (pick, reshape, team, delete), as commands and the admin panel use it.
[Trait("Category", "E2E")]
public class JoinPadScenarios : LiveScenarioBase
{
    [AtlasScenario]
    public async Task Pad_Should_Reshape_And_ChangeTeam_When_Picked()
    {
        ITestPlayer admin = await World.JoinPlayer("padeditor");
        var pads = Mod.JoinPads;
        BlockPos floor = World.Spawn.AddCopy(-60, -1, -60);
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, floor);
        string uid = admin.Player.PlayerUID;

        var (picked, pickMsg) = pads.EditPick(uid, null, pad.Id);
        Assert.True(picked, "pick by id failed: " + pickMsg);

        pads.SetCorner(uid, 0, floor.AddCopy(10, 0, 10));
        pads.SetCorner(uid, 1, floor.AddCopy(14, 0, 12));
        var (reshaped, reshapeMsg) = pads.EditReshape(uid);
        Assert.True(reshaped, "reshape failed: " + reshapeMsg);
        Assert.True(pad.MinX == floor.X + 10 && pad.MaxX == floor.X + 14 && pad.MaxZ == floor.Z + 12,
            $"box is [{pad.MinX}..{pad.MaxX}, {pad.MinZ}..{pad.MaxZ}] after reshape");

        var (teamed, _) = pads.EditTeam(uid, 1);
        Assert.True(teamed && pad.Team == 1, "team not changed");
        var (bad, _) = pads.EditTeam(uid, 7);
        Assert.False(bad, "a team the event doesn't have was accepted");
        await World.Ticks(1);
    }

    [AtlasScenario]
    public async Task SideZone_Should_NeedCorners_When_FromThePanel()
    {
        ITestPlayer admin = await World.JoinPlayer("padpanel");
        var (ok, msg) = Mod.JoinPads.AddZone(admin.Player.PlayerUID, JoinPads.SideAction, Battle, 0, 0,
            admin.Position, requireSelection: true);
        Assert.False(ok, "a side zone was made without corners: " + msg);

        var (wrongKind, _) = Mod.JoinPads.AddZone(admin.Player.PlayerUID, JoinPads.JoinAction, Battle, 0, 0,
            admin.Position, requireSelection: false);
        Assert.False(wrongKind, "a join zone was made for a queue event");
        Assert.True(TeamPalette.Parse("red") == 1 && TeamPalette.Parse("2") == 1 && TeamPalette.Parse("x") == -1,
            "team names parse wrong");
    }
}
