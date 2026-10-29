using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.scenarios;

// R28 in docs/testing/03-risks.md: the storm shrinks on schedule and hurts only outside.
[Trait("Category", "E2E")]
public class HungerGamesZoneScenarios : EventsScenarioBase
{
    private const string SoloHg = "hungergames_solotest";
    private const float Radius = 12f;

    // Short schedule on the event template for this scenario: flat at full radius until 20 s, then
    // down to half by 40 s, 1 hp/s outside from the start. No freeze (it snaps players back) and no truce.
    private void UseQuickStorm() => UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
    {
        attrs["startFreezeSeconds"] = 0;
        attrs["gracePeriodSeconds"] = 0;
        attrs["sponsorEverySeconds"] = 0;
        attrs["feastAtSeconds"] = 0;
        attrs["zonePhases"] = new JArray(
            new JObject { ["endsAtSeconds"] = 20, ["radius"] = 100, ["outsideDps"] = 1 },
            new JObject { ["endsAtSeconds"] = 40, ["radius"] = 50, ["outsideDps"] = 1 });
    });

    private static void Heal(ITestPlayer p)
    {
        var health = p.Player.Entity.GetBehavior<EntityBehaviorHealth>();
        health.Health = health.MaxHealth;
    }

    // HP lost between two moments of match time.
    private async Task<float> HpLostBetween(ITestPlayer p, RunningEvent match, float from, float to)
    {
        var health = p.Player.Entity.GetBehavior<EntityBehaviorHealth>();
        await World.Until(() => match.ElapsedSeconds >= from, $"match time {from}s", 2400);
        float before = health.Health;
        await World.Until(() => match.ElapsedSeconds >= to, $"match time {to}s", 2400);
        return before - health.Health;
    }

    [AtlasScenario]
    public async Task Storm_Should_HurtOnlyOutside_And_Shrink_OnSchedule()
    {
        UseCountdown(1);
        UseQuickStorm();
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("storm-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, Radius, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer p = await World.JoinPlayer("stormwalker");
        var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
        Assert.True(ok, "could not queue: " + msg);
        var match = await StartQueuedMatch(p);

        // 10.5 blocks out: inside the full radius (12), outside the radius at 32 s (12 → 6 over 20..40 s ≈ 8.4).
        BlockPos nearEdge = await SurfaceAt(centre.X + 10, centre.Z);
        BlockPos farOut = await SurfaceAt(centre.X + 20, centre.Z);

        await p.TeleportTo(nearEdge);
        Heal(p);
        float insideLoss = await HpLostBetween(p, match, 2f, 5f);
        Assert.True(insideLoss <= 0.2f, $"lost {insideLoss:0.##} hp inside the zone (2..5 s)");

        await p.TeleportTo(farOut);
        Heal(p);
        float outsideLoss = await HpLostBetween(p, match, 7f, 10f);
        Assert.True(outsideLoss >= 1.5f && outsideLoss <= 4.5f,
            $"lost {outsideLoss:0.##} hp in 3 s outside the zone, expected about 3 (1 hp/s)");

        await p.TeleportTo(nearEdge);
        Heal(p);
        float earlyLoss = await HpLostBetween(p, match, 12f, 15f);
        Assert.True(earlyLoss <= 0.2f, $"lost {earlyLoss:0.##} hp near the edge before the shrink (12..15 s)");

        Heal(p);
        float shrunkLoss = await HpLostBetween(p, match, 32f, 35f);
        Assert.True(shrunkLoss >= 1.5f,
            $"lost only {shrunkLoss:0.##} hp near the edge at 32..35 s: the zone did not shrink past it");

        await p.ExecuteCommand("/eventadmin stop");
    }

    private static double DistanceXZ(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // startFreezeSeconds: tributes are held on their pedestals. FreezeTick snaps back a move of 1..4 blocks
    // per 250 ms, but takes anything farther as the pedestal teleport landing and re-anchors there,
    // so a long hop (a teleport, a fast mount) leaves the pedestal for good.
    [AtlasScenario]
    public async Task StartFreeze_Should_HoldTributesOnTheirPedestal_UntilItEnds()
    {
        const float FreezeSeconds = 8f;
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
        {
            attrs["startFreezeSeconds"] = FreezeSeconds;
            attrs["gracePeriodSeconds"] = 0;
            attrs["sponsorEverySeconds"] = 0;
            attrs["feastAtSeconds"] = 0;
        });
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("freeze-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, Radius, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer p = await World.JoinPlayer("frozenone");
        BlockPos home = p.Position.Copy();
        var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
        Assert.True(ok, "could not queue: " + msg);
        var match = await StartQueuedMatch(p);

        // Let the pedestal teleport land and the freeze tick take its anchor.
        await World.Until(() => DistanceXZ(p.Position, home) > 50, "the player to land on the pedestal", 600);
        await World.Until(() => match.ElapsedSeconds >= 1.5f, "the freeze anchor", 600);
        BlockPos pedestal = p.Position.Copy();

        await p.TeleportTo(pedestal.AddCopy(3, 0, 0));
        await World.Until(() => DistanceXZ(p.Position, pedestal) <= 1.5, "a 3-block step to be snapped back", 120);

        await p.TeleportTo(pedestal.AddCopy(6, 0, 0));
        float hopAt = match.ElapsedSeconds;
        await World.Until(() => match.ElapsedSeconds >= hopAt + 1.5f, "1.5 s after a 6-block hop", 600);
        Assert.True(match.ElapsedSeconds < FreezeSeconds, "precondition: the freeze ended before the hop was judged");
        Assert.True(DistanceXZ(p.Position, pedestal) <= 1.5,
            $"a 6-block hop during the freeze stayed {DistanceXZ(p.Position, pedestal):0.#} blocks off the pedestal");

        await World.Until(() => match.ElapsedSeconds >= FreezeSeconds + 1.5f, "the freeze to end", 1200);
        BlockPos free = p.Position.Copy();
        await p.TeleportTo(free.AddCopy(3, 0, 0));
        await World.Ticks(40);
        Assert.True(DistanceXZ(p.Position, free) >= 2.5, "the player was pulled back after the freeze had ended");

        await p.ExecuteCommand("/eventadmin stop");
    }

    // Two tributes, nobody dies, the clock runs out: OnBeforeFinish picks most kills, then damage dealt.
    private async Task<MatchResult> PlayToTimeUp(string nameA, string nameB, Action<ITestPlayer, ITestPlayer> during)
    {
        const float TimeLimit = 12f;
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
        {
            attrs["timeLimitSeconds"] = TimeLimit;
            attrs["startFreezeSeconds"] = 0;
            attrs["gracePeriodSeconds"] = 0;
            attrs["sponsorEverySeconds"] = 0;
            attrs["feastAtSeconds"] = 0;
        });
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("timeup-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, Radius, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer a = await World.JoinPlayer(nameA);
        ITestPlayer b = await World.JoinPlayer(nameB);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }

        MatchResult? result = null;
        RunningEvent? match = null;
        Action<RunningEvent, MatchResult> onFinished = (re, r) => { if (re == match) result = r; };
        Manager.OnMatchFinished += onFinished;
        AddCleanup(() => Manager.OnMatchFinished -= onFinished);

        match = await StartQueuedMatch(a, b);
        // On the surface, so nobody suffocates into an early last-man-standing finish.
        await a.TeleportTo(await SurfaceAt(centre.X + 4, centre.Z));
        await b.TeleportTo(await SurfaceAt(centre.X - 4, centre.Z));
        during(a, b);

        await World.Until(() => result != null, "the match to run out of time", 2400);
        Assert.True(match.ElapsedSeconds >= TimeLimit - 0.5f,
            $"the match ended at {match.ElapsedSeconds:0.#} s, before the {TimeLimit} s limit - not a time-up");
        return result!;
    }

    [AtlasScenario]
    public async Task TimeUp_Should_GoToTheTributeWhoDealtMoreDamage_When_KillsAreEven()
    {
        string attackerUid = "";
        var result = await PlayToTimeUp("timeupattacker", "timeuppassive", (a, b) =>
        {
            attackerUid = a.Player.PlayerUID;
            b.Player.Entity.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Player,
                SourceEntity = a.Player.Entity,
                Type = EnumDamageType.BluntAttack
            }, 3f);
        });
        Assert.True(result.Winners.Count == 1 && result.Won(attackerUid),
            $"time-up winners: [{string.Join(", ", result.Winners)}], expected only the tribute who dealt damage");
    }

    [AtlasScenario]
    public async Task TimeUp_Should_BeADraw_When_NobodyDidAnything()
    {
        var result = await PlayToTimeUp("timeupidlea", "timeupidleb", (_, _) => { });
        Assert.True(result.Winners.Count == 0, $"time-up with no kills and no damage gave winners: [{string.Join(", ", result.Winners)}]");
    }

    // Measured in the same tick as the hit, so storm or suffocation can't blur it.
    private static float HitAndMeasure(ITestPlayer victim, DamageSource source, float amount)
    {
        Heal(victim);
        var health = victim.Player.Entity.GetBehavior<EntityBehaviorHealth>();
        float before = health.Health;
        victim.Player.Entity.ReceiveDamage(source, amount);
        return before - health.Health;
    }

    // gracePeriodSeconds: no damage between tributes at first; storm and falls still apply.
    [AtlasScenario]
    public async Task Truce_Should_BlockTributeHits_ButNotOtherDamage_UntilItEnds()
    {
        const float GraceSeconds = 10f;
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
        {
            attrs["gracePeriodSeconds"] = GraceSeconds;
            attrs["startFreezeSeconds"] = 0;
            attrs["sponsorEverySeconds"] = 0;
            attrs["feastAtSeconds"] = 0;
        });
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("truce-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, Radius, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer attacker = await World.JoinPlayer("truceattacker");
        ITestPlayer victim = await World.JoinPlayer("trucevictim");
        foreach (var p in new[] { attacker, victim })
        {
            var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(attacker, victim);
        Assert.True(match.ElapsedSeconds < GraceSeconds - 2, "precondition: the truce is almost over already");

        var hit = new DamageSource
        {
            Source = EnumDamageSource.Player,
            SourceEntity = attacker.Player.Entity,
            Type = EnumDamageType.BluntAttack
        };
        var fall = new DamageSource { Source = EnumDamageSource.Fall, Type = EnumDamageType.Gravity };

        float duringHit = HitAndMeasure(victim, hit, 3f);
        float duringFall = HitAndMeasure(victim, fall, 3f);
        Assert.True(duringHit <= 0.01f, $"a hit between tributes did {duringHit:0.##} damage during the truce");
        Assert.True(duringFall >= 2.9f, $"fall damage did only {duringFall:0.##} during the truce, expected 3");

        await World.Until(() => match.ElapsedSeconds >= GraceSeconds + 2, "the truce to end", 1200);
        float afterHit = HitAndMeasure(victim, hit, 3f);
        Assert.True(afterHit >= 2.9f, $"a hit between tributes did only {afterHit:0.##} damage after the truce, expected 3");

        await attacker.ExecuteCommand("/eventadmin stop");
    }
}
