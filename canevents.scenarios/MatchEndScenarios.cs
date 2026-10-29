using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.Bots;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// A match that never ends keeps its players "in a match" (R24d) and its arena occupied.
[Trait("Category", "E2E")]
public class MatchEndScenarios : EventsScenarioBase
{
    [AtlasScenario]
    public async Task AbandonedDuel_Should_Finish_When_BothPlayersLeave()
    {
        UseCountdown(1);
        int area = NewArea();
        var place = AddArena("abandoned-" + area, AreaCentre(area), 12, PlaceTags.Arena1x1, 2);

        ITestPlayer a = await World.JoinPlayer("abandona");
        ITestPlayer b = await World.JoinPlayer("abandonb");
        var duel = Manager.FindEventByCode("1x1");
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(a, b);

        await Disconnect(a);
        await Disconnect(b);
        await World.Until(() => !Manager.RunningEventsSnapshot().Contains(match), "the abandoned duel to finish", 600);
        Assert.True(!place.Occupied, "the arena is still marked occupied after the abandoned duel");
    }

    // After the last human leaves, bots carry on (see RestartScenarios); the clock must still end it.
    [AtlasScenario]
    public async Task BotOnlyMatch_Should_StillEndOnTime_When_TheLastHumanLeft()
    {
        const string BotsHg = "hungergames_bots";
        const float TimeLimit = 15f;
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(BotsHg, attrs =>
        {
            attrs["timeLimitSeconds"] = TimeLimit;
            attrs["startFreezeSeconds"] = 0;
            attrs["gracePeriodSeconds"] = 0;
            attrs["sponsorEverySeconds"] = 0;
            attrs["feastAtSeconds"] = 0;
        });
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("botsonly-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 6);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer p = await World.JoinPlayer("lasthuman");
        var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(BotsHg), p.Player);
        Assert.True(ok, "could not queue: " + msg);
        var match = await StartQueuedMatch(p);
        Assert.True(match.ParticipantsBefore.Any(BotIdentity.IsBot), "the match was not filled with bots");

        await Disconnect(p);
        Assert.True(Manager.RunningEventsSnapshot().Contains(match), "precondition: the bots should keep the match going");

        // Bots may also finish it early by fighting; either way it must not outlive the limit by much.
        await World.Until(() => !Manager.RunningEventsSnapshot().Contains(match),
            $"the bot-only match to end by its {TimeLimit} s limit", 2400);
        Assert.True(match.ElapsedSeconds <= TimeLimit + 3f, $"the bot-only match ran {match.ElapsedSeconds:0.#} s");
        Assert.True(!place.Occupied, "the arena is still marked occupied after the bot-only match");
    }
}
