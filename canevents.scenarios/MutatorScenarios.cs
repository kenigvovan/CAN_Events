using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// The speed mutator is a walkspeed modifier; it must not follow anyone out of the match.
// Two ways out: the end of the match (ClearSpeedMutator) and an early release (the stats snapshot restore).
[Trait("Category", "E2E")]
public class MutatorScenarios : EventsScenarioBase
{
    private const string SoloHg = "hungergames_solotest";

    private static float? SpeedModifier(ITestPlayer p)
    {
        foreach (var stat in p.Player.Entity.Stats)
            if (stat.Key == "walkspeed" && stat.Value.ValuesByKey.TryGetValue(EventBroadcaster.SpeedStatCode, out var mod))
                return mod.Value;
        return null;
    }

    [AtlasScenario]
    public async Task SpeedMutator_Should_LeaveWithNobody_Whether_ReleasedEarly_Or_AtTheEnd()
    {
        UseCountdown(1);
        UseMutator(SoloHg, EventMutators.Speed, "1.5");
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("mutator-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer early = await World.JoinPlayer("releasedfast");
        ITestPlayer a = await World.JoinPlayer("fasta");
        ITestPlayer b = await World.JoinPlayer("fastb");
        foreach (var p in new[] { early, a, b })
        {
            Assert.True(SpeedModifier(p) == null, "precondition: " + p.Player.PlayerName + " already has the speed modifier");
            var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(early, a, b);

        foreach (var p in new[] { early, a, b })
        {
            float? mod = SpeedModifier(p);
            Assert.True(mod != null && Math.Abs(mod.Value - 0.5f) < 0.01f,
                $"{p.Player.PlayerName}: speed modifier {mod?.ToString() ?? "missing"} in the match, expected 0.5 (x1.5)");
        }

        await UntilLanded(match, early, a, b);
        early.Player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Void, Type = EnumDamageType.Injury }, 1000f);
        await World.Until(() => !match.IsActive(early.Player.PlayerUID), "the first tribute to be eliminated", 200);
        CommandResult leave = await early.ExecuteCommand("/canspectate off");
        Assert.True(leave.Ok, "precondition: /canspectate off did not release the eliminated tribute: " + leave.Message);
        Assert.True(Manager.RunningEventsSnapshot().Contains(match), "precondition: the match must go on");
        Assert.True(SpeedModifier(early) == null, "the released tribute still runs with the match's speed modifier");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Ticks(5);
        foreach (var p in new[] { a, b })
            Assert.True(SpeedModifier(p) == null, p.Player.PlayerName + " keeps the speed modifier after the match");
    }
}
