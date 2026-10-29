using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// scoreboardVisibility decides who gets the live table; the rest get an empty one (NetworkHandler.SendScoreboard*).
// Only the table: elimination announcements and the HUD alive counter still reach everyone.
[Trait("Category", "E2E")]
public class HungerGamesScoreboardScenarios : EventsScenarioBase
{
    private const string SoloHg = "hungergames_solotest";

    [AtlasTheory]
    [InlineData("all", true, true, true)]
    [InlineData("eliminated", false, true, true)]
    [InlineData("spectators", false, false, true)]
    [InlineData("none", false, false, false)]
    public async Task Scoreboard_Should_ReachOnlyWhoTheSettingAllows(string visibility, bool active, bool eliminated, bool outsider)
    {
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
        {
            attrs["scoreboardVisibility"] = visibility;
            attrs["startFreezeSeconds"] = 0;
            attrs["gracePeriodSeconds"] = 0;
        });
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("board-" + visibility, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer fallen = await World.JoinPlayer("out" + visibility);
        ITestPlayer a = await World.JoinPlayer("ina" + visibility);
        ITestPlayer b = await World.JoinPlayer("inb" + visibility);
        foreach (var p in new[] { fallen, a, b })
        {
            var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(fallen, a, b);
        await UntilLanded(match, fallen, a, b);
        fallen.Player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Void, Type = EnumDamageType.Injury }, 1000f);
        await World.Until(() => !match.IsActive(fallen.Player.PlayerUID), "one tribute to be eliminated", 200);
        Assert.True(match.IsActive(a.Player.PlayerUID), "precondition: the other tributes must still be in");

        var scorer = match.Scorer;
        Assert.True(scorer != null, "the match has no scorer");
        string outsiderUid = "not-in-this-match";
        Assert.True(scorer!.ShowsScoreboardTo(a.Player.PlayerUID) == active, $"'{visibility}': active tribute sees the table = {!active}");
        Assert.True(scorer.ShowsScoreboardTo(fallen.Player.PlayerUID) == eliminated, $"'{visibility}': eliminated tribute sees the table = {!eliminated}");
        Assert.True(scorer.ShowsScoreboardTo(outsiderUid) == outsider, $"'{visibility}': non-participant sees the table = {!outsider}");

        await a.ExecuteCommand("/eventadmin stop");
    }
}
