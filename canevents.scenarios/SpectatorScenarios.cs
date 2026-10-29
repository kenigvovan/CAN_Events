using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// An eliminated tribute watches in Spectator mode; the match end must give them their own mode back.
[Trait("Category", "E2E")]
public class SpectatorScenarios : EventsScenarioBase
{
    private const string SoloHg = "hungergames_solotest";

    private static double DistanceXZ(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    [AtlasScenario]
    public async Task EliminatedSpectator_Should_GetTheirOwnModeBack_When_MatchEnds()
    {
        UseCountdown(1);
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("spectate-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer fallen = await World.JoinPlayer("fallentribute");
        ITestPlayer a = await World.JoinPlayer("survivora");
        ITestPlayer b = await World.JoinPlayer("survivorb");
        var data = fallen.Player.WorldData;
        var modeBefore = data.CurrentGameMode;
        bool freeMoveBefore = data.FreeMove, noClipBefore = data.NoClip;
        BlockPos home = fallen.Position.Copy();

        foreach (var p in new[] { fallen, a, b })
        {
            var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(fallen, a, b);
        await UntilLanded(match, fallen, a, b);

        var entity = fallen.Player.Entity;
        var health = entity.GetBehavior<Vintagestory.GameContent.EntityBehaviorHealth>();
        float hpBefore = health?.Health ?? -1f;
        bool hit = entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Void, Type = EnumDamageType.Injury }, 1000f);
        // Seen failing with no death in the log at all: say which step stopped it.
        await World.Until(() => data.CurrentGameMode == EnumGameMode.Spectator,
            $"the fallen tribute to be seated as a spectator (hit accepted: {hit}, hp {hpBefore} -> {health?.Health}, "
            + $"alive: {fallen.Player.Entity?.Alive}, mode: {data.CurrentGameMode}, active: {match.IsActive(fallen.Player.PlayerUID)}, "
            + $"state: {match.EventState})", 300);
        Assert.True(Manager.RunningEventsSnapshot().Contains(match), "precondition: the match must go on with the two others");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => data.CurrentGameMode == modeBefore, "the game mode to be restored", 300);

        Assert.True(data.FreeMove == freeMoveBefore && data.NoClip == noClipBefore,
            $"after the match: freeMove={data.FreeMove} noClip={data.NoClip}, was {freeMoveBefore}/{noClipBefore}");
        Assert.True(fallen.Player.GetModdata(SpectatorManager.ModDataKey) == null, "the saved game mode was left in ModData");
        await World.Until(() => DistanceXZ(fallen.Position, home) <= 1.5, "the spectator to be sent home", 600);
    }
}
