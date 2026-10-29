using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.Bots;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// R18 in docs/testing/03-risks.md. Atlas only restarts a host with no test players joined, so the
// player leaves first and bots keep the match alive; the restart then kills a match that never finished.
// A_ must run before B_: the orderer sorts by method name.
[Trait("Category", "E2E")]
[TestCaseOrderer("canevents.scenarios.Infrastructure.AlphabeticalOrderer", "canevents.scenarios")]
public class RestartScenarios : EventsScenarioBase
{
    private const string BotsHg = "hungergames_bots";
    private const string Own = "game:flint";
    private const string Name = "restarter";

    private static BlockPos? home;
    private static bool matchAliveAtRestart;

    private static double DistanceXZ(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    [AtlasScenario]
    public async Task A_PlayerLeaves_While_BotsKeepTheMatchGoing()
    {
        UseCountdown(1);
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("restart-" + area, centre, 25, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 6);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer p = await World.JoinPlayer(Name);
        await p.GiveItem(Own, 2);
        BlockPos start = p.Position.Copy();
        home = start;

        var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(BotsHg), p.Player);
        Assert.True(ok, "could not queue: " + msg);
        var match = await StartQueuedMatch(p);
        Assert.True(match.ParticipantsBefore.Any(BotIdentity.IsBot), "the match was not filled with bots");
        await World.Until(() => DistanceXZ(p.Position, start) > 50, "the player to be teleported to the arena", 600);
        Assert.True(CountOf(p.Player, Own) == 0, "precondition: the inventory was not swapped for the match");

        await Disconnect(p);
        Assert.True(Manager.RunningEventsSnapshot().Contains(match) && match.EventState == EventState.Running,
            "the match ended when the player left; the restart would not interrupt a running match");
        matchAliveAtRestart = true;
    }

    [AtlasScenario(RestartWorld = true)]
    public async Task B_PlayerGetsHomeAndInventoryBack_When_ServerRestartedMidMatch()
    {
        Assert.True(matchAliveAtRestart && home != null,
            "A_ did not leave a running match behind (it failed, or ran after this one)");
        BlockPos start = home!;
        Assert.True(Manager.RunningEventsSnapshot().Count == 0, "a match came back after the restart");

        ITestPlayer back = await World.JoinPlayer(Name);
        await World.Until(() => DistanceXZ(back.Position, start) <= 1.5, "the player to be sent home on rejoin", 600);

        Assert.True(CountOf(back.Player, Own) == 2, $"own items: {Own} x{CountOf(back.Player, Own)}, expected 2");
        Assert.True(back.Player.GetModdata(PlayerInventoryBackup.ModDataKey) == null, "the inventory backup was not settled");
        await World.Until(() => !ReturnPosMarker.TryRead(back.Player, World.Api, out _), "the return marker to be cleared", 200);

        int bots = World.Api.World.LoadedEntities.Values.Count(e => e is EntityArenaBot && e.Alive);
        Assert.True(bots == 0, $"{bots} arena bot(s) of the interrupted match are alive after the restart");
    }
}
