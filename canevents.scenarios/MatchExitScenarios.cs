using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.scenarios;

// What a player has and where they stand once the match is over.
[Trait("Category", "E2E")]
public class MatchExitScenarios : EventsScenarioBase
{
    private const string Duel = "1x1";

    private async Task<(ITestPlayer, ITestPlayer)> GivenDuelPlayers(string nameA, string nameB)
    {
        UseCountdown(1);
        int area = NewArea();
        AddArena("exit-" + area, AreaCentre(area), 12, PlaceTags.Arena1x1, 2);

        ITestPlayer a = await World.JoinPlayer(nameA);
        ITestPlayer b = await World.JoinPlayer(nameB);
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        return (a, b);
    }

    private static double DistanceXZ(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // R23, baseline: back where they stood when the match took them, and the return marker is gone.
    [AtlasScenario]
    public async Task Player_Should_ReturnHome_When_MatchEnds()
    {
        var (a, b) = await GivenDuelPlayers("homea", "homeb");
        BlockPos home = a.Position.Copy();

        await StartQueuedMatch(a, b);
        await World.Until(() => DistanceXZ(a.Position, home) > 50, "the player to be teleported to the arena", 600);
        Assert.True(ReturnPosMarker.TryRead(a.Player, World.Api, out _), "no return marker while on the arena");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => DistanceXZ(a.Position, home) <= 1.5, "the player to be sent home", 600);

        BlockPos at = a.Position;
        Assert.True(at.dimension == home.dimension && Math.Abs(at.Y - home.Y) <= 2,
            $"back at {at} instead of {home}");
        await World.Until(() => !ReturnPosMarker.TryRead(a.Player, World.Api, out _), "the return marker to be cleared", 200);
    }

    // R03: left mid-match, the match ended without them, they come back - home, own inventory, nothing pending.
    [AtlasScenario]
    public async Task Player_Should_GetInventoryAndHomeBack_When_RejoiningAfterLeavingMidMatch()
    {
        const string Own = "game:flint";
        var (a, b) = await GivenDuelPlayers("leavera", "leaverb");
        await a.GiveItem(Own, 2);
        BlockPos home = a.Position.Copy();
        string uid = a.Player.PlayerUID;

        var match = await StartQueuedMatch(a, b);
        await World.Until(() => DistanceXZ(a.Position, home) > 50, "the player to be teleported to the arena", 600);
        Assert.True(CountOf(a.Player, Own) == 0, "precondition: the inventory was not swapped for the kit");

        await Disconnect(a);
        if (Manager.RunningEventsSnapshot().Contains(match)) await b.ExecuteCommand("/eventadmin stop");
        await World.Until(() => !Manager.RunningEventsSnapshot().Any(re => re.IsParticipant(uid)), "the match to end", 600);

        ITestPlayer back = await Rejoin("leavera");
        await World.Until(() => DistanceXZ(back.Position, home) <= 1.5, "the player to be sent home on rejoin", 600);

        Assert.True(CountOf(back.Player, Own) == 2, $"own items: {Own} x{CountOf(back.Player, Own)}, expected 2");
        foreach (var kit in new[] { "game:spear-generic-steel", "game:bow-long", "game:blade-blackguard-iron" })
            Assert.True(CountOf(back.Player, kit) == 0, "the match kit is still in the inventory: " + kit);
        Assert.True(back.Player.GetModdata(PlayerInventoryBackup.ModDataKey) == null, "the inventory backup was not settled");
        await World.Until(() => !ReturnPosMarker.TryRead(back.Player, World.Api, out _), "the return marker to be cleared", 200);
    }

    // R19: the match feeds and heals everyone; afterwards they get their own HP, saturation and nutrients back.
    [AtlasScenario]
    public async Task HealthAndHunger_Should_BeRestored_When_MatchEnds()
    {
        var (a, b) = await GivenDuelPlayers("statsa", "statsb");
        var health = a.Player.Entity.GetBehavior<EntityBehaviorHealth>();
        var hunger = a.Player.Entity.GetBehavior<EntityBehaviorHunger>();
        Assert.True(health != null && hunger != null, "player has no health or hunger behavior");

        health.Health = 7f;
        hunger.Saturation = 400f;
        hunger.FruitLevel = 100f;
        hunger.VegetableLevel = 200f;
        hunger.ProteinLevel = 300f;
        hunger.GrainLevel = 400f;
        hunger.DairyLevel = 500f;
        hunger.UpdateNutrientHealthBoost();
        float hpBefore = health.Health;

        await StartQueuedMatch(a, b);
        // Precondition: the match really changed them (Feed and heal on start).
        Assert.True(hunger.Saturation > 450f, "precondition: the match did not feed the player");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Ticks(10);

        // Hunger keeps ticking down meanwhile, hence the margins.
        Assert.True(Math.Abs(health.Health - hpBefore) <= 0.5f, $"HP {health.Health}, was {hpBefore}");
        Assert.True(Math.Abs(hunger.Saturation - 400f) <= 15f, $"saturation {hunger.Saturation}, was 400");
        var nutrients = new (string name, float now, float was)[]
        {
            ("fruit", hunger.FruitLevel, 100f), ("vegetable", hunger.VegetableLevel, 200f),
            ("protein", hunger.ProteinLevel, 300f), ("grain", hunger.GrainLevel, 400f),
            ("dairy", hunger.DairyLevel, 500f)
        };
        foreach (var (name, now, was) in nutrients)
            Assert.True(Math.Abs(now - was) <= 5f, $"{name} {now}, was {was}");
    }
}
