using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// T10: a player inside an open match when the server goes down gets their inventory and position back.
// Atlas restarts only with no test players joined, so the player leaves first (A_), then B_ restarts.
[Trait("Category", "E2E")]
[TestCaseOrderer("canevents.scenarios.Infrastructure.AlphabeticalOrderer", "canevents.scenarios")]
public class OpenMatchRestartScenarios : LiveScenarioBase
{
    private const string Name = "tdmrestart";
    private static BlockPos? home;
    private static bool wasInside;

    [AtlasScenario]
    public async Task A_PlayerInOpenMatch_Disconnects()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");

        ITestPlayer p = await JoinSurvivor(Name);
        await p.GiveItem(Own, 2);
        home = p.Position.Copy();

        var result = await p.ExecuteCommand("/canlive join " + OpenTdm + " red");
        Assert.True(result.Ok, "/canlive join failed: " + result.Message);
        await World.Until(() => match.IsActive(p.Player.PlayerUID), "the player to be in the open match", 200);
        Assert.True(CountOf(p.Player, Own) == 0, "precondition: own items not stashed");
        wasInside = true;

        await Disconnect(p);
    }

    [AtlasScenario(RestartWorld = true)]
    public async Task B_PlayerGetsInventoryAndHomeBack_When_ServerRestarted()
    {
        Assert.True(wasInside && home != null, "A_ did not leave a player inside an open match");

        ITestPlayer back = await World.JoinPlayer(Name);
        await World.Until(() => DistanceXZ(back.Position, home!) <= 1.5, "the player to be sent home", 600);
        Assert.True(CountOf(back.Player, Own) == 2, $"own items: {CountOf(back.Player, Own)}, expected 2");
        Assert.True(Settled(back.Player), "the snapshot was not settled after the restart");
        await World.Until(() => !ReturnPosMarker.TryRead(back.Player, World.Api, out _), "the return marker to clear", 200);
    }
}
