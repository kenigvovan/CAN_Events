using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// R24 in docs/testing/03-risks.md: one player, one match at a time.
[Trait("Category", "E2E")]
public class QueueScenarios : EventsScenarioBase
{
    // Assembles with a single entrant, so it forms in the same pass as anything else.
    private const string SoloHg = "hungergames_solotest";
    private const string Duel = "1x1";

    private int MatchesOf(string uid) => Manager.RunningEventsSnapshot().Count(re => re.IsParticipant(uid));

    // R24a. No arena is needed: the match exists from assembly on (ready check, then arena search).
    [AtlasScenario]
    public async Task Player_Should_EnterOneMatch_When_QueuedForTwo()
    {
        ITestPlayer both = await World.JoinPlayer("queuedtwice");
        ITestPlayer other = await World.JoinPlayer("duelpartner");
        var hg = Manager.FindEventByCode(SoloHg);
        var duel = Manager.FindEventByCode(Duel);
        Assert.True(hg != null && duel != null, $"shipped events '{SoloHg}' and '{Duel}' are required");

        // One synchronous block: the queue tick cannot run in between.
        var (hgOk, hgMsg) = Manager.AddToQueue(hg, both.Player);
        var (duelOk, duelMsg) = Manager.AddToQueue(duel, both.Player);
        var (otherOk, otherMsg) = Manager.AddToQueue(duel, other.Player);
        Assert.True(hgOk, "could not queue for " + SoloHg + ": " + hgMsg);
        Assert.True(otherOk, "partner could not queue for " + Duel + ": " + otherMsg);

        Manager.AttemptToComposeTeams();

        int matches = MatchesOf(both.Player.PlayerUID);
        Assert.True(matches >= 1, "the player ended up in no match at all; queues: " + SoloHg + "=" + hgOk + ", " + Duel + "=" + duelOk + " (" + duelMsg + ")");
        Assert.True(matches == 1, $"the player is a participant of {matches} matches at once");
        // The queue that formed the match consumed its registration; the other one must be gone too.
        Assert.True(Manager.FindRegistration(both.Player.PlayerUID, hg) == null
                    && Manager.FindRegistration(both.Player.PlayerUID, duel) == null,
            "the player is in a match but still registered in another queue");

        await both.ExecuteCommand("/eventadmin stop");
    }

    // R24b. A match waiting on its ready check already holds the player (FindLiveEventOf counts it).
    [AtlasScenario]
    public async Task Player_Should_NotJoinAnotherMatch_When_AlreadyInOne()
    {
        ITestPlayer busy = await World.JoinPlayer("alreadyin");
        ITestPlayer other = await World.JoinPlayer("alreadyfoe");
        var duel = Manager.FindEventByCode(Duel);
        var hg = Manager.FindEventByCode(SoloHg);
        foreach (var p in new[] { busy, other })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        Manager.AttemptToComposeTeams();
        Assert.True(MatchesOf(busy.Player.PlayerUID) == 1, "precondition: the player must be in the duel");

        CommandResult reg = await busy.ExecuteCommand("/canreg " + SoloHg);
        Manager.AttemptToComposeTeams();

        int matches = MatchesOf(busy.Player.PlayerUID);
        Assert.True(matches == 1, $"the player is a participant of {matches} matches at once (/canreg said: {reg.Message})");
        Assert.True(Manager.FindRegistration(busy.Player.PlayerUID, hg) == null,
            "the player is in a match but /canreg put them in another queue: " + reg.Message);

        await busy.ExecuteCommand("/eventadmin stop");
    }

    // R24d. Out of the match and sent home, but FindLiveEventOf still counts the roster entry
    // until the match ends: the player can't watch the very match they left.
    [AtlasScenario]
    public async Task ReleasedPlayer_Should_BeFree_While_TheMatchGoesOn()
    {
        UseCountdown(1);
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("released-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        ITestPlayer out1 = await World.JoinPlayer("releasedone");
        ITestPlayer a = await World.JoinPlayer("stillina");
        ITestPlayer b = await World.JoinPlayer("stillinb");
        var hg = Manager.FindEventByCode(SoloHg);
        foreach (var p in new[] { out1, a, b })
        {
            var (ok, msg) = Manager.AddToQueue(hg, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(out1, a, b);

        string uid = out1.Player.PlayerUID;
        await UntilLanded(match, out1, a, b);
        out1.Player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Void, Type = EnumDamageType.Injury }, 1000f);
        await World.Until(() => !match.IsActive(uid), "the player to be eliminated", 200);

        CommandResult leave = await out1.ExecuteCommand("/canspectate off");
        Assert.True(leave.Ok, "precondition: /canspectate off did not release the eliminated player: " + leave.Message);
        Assert.True(Manager.RunningEventsSnapshot().Contains(match) && match.EventState == EventState.Running,
            "precondition: the match must still be running with the two others");

        CommandResult watch = await out1.ExecuteCommand("/canspectate watch " + match.Id);
        Assert.True(watch.Ok, "the released player cannot watch the match they left: " + watch.Message);

        await a.ExecuteCommand("/eventadmin stop");
    }
}
