using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.scenarios;

// R27 in docs/testing/03-risks.md: the entry fee is the player's own stake.
[Trait("Category", "E2E")]
public class EntryFeeScenarios : EventsScenarioBase
{
    // FFA that assembles from one entrant, so losing one of three still leaves a playable match.
    private const string SoloHg = "hungergames_solotest";
    private const string FeeItem = "game:gear-rusty";

    // 1x1_ranked ships with a fee of 1 gear-rusty and needs two entrants, so a lone player just waits in the queue.
    private const string PaidDuel = "1x1_ranked";

    // R27: no stake, no queue - and nothing is taken.
    [AtlasScenario]
    public async Task PaidQueue_Should_Refuse_When_PlayerCannotPay()
    {
        ITestPlayer broke = await World.JoinPlayer("brokeplayer");
        Assert.True(CountOf(broke.Player, FeeItem) == 0, "precondition: the player must not carry the fee");

        CommandResult reg = await broke.ExecuteCommand("/canreg " + PaidDuel);

        Assert.True(Manager.FindRegistration(broke.Player.PlayerUID, Manager.FindEventByCode(PaidDuel)) == null,
            "a player without the fee got into the paid queue: " + reg.Message);
        Assert.True(!EntryFeeEscrow.Has(broke.Player), "an escrow was written for a player who could not pay");
    }

    // R27: paying puts the stake in escrow; leaving the queue gives it back.
    [AtlasScenario]
    public async Task Fee_Should_BeRefunded_When_LeavingTheQueue()
    {
        ITestPlayer p = await World.JoinPlayer("queueleaver");
        await p.GiveItem(FeeItem, 1);
        var ev = Manager.FindEventByCode(PaidDuel);

        CommandResult reg = await p.ExecuteCommand("/canreg " + PaidDuel);
        Assert.True(Manager.FindRegistration(p.Player.PlayerUID, ev) != null, "could not queue: " + reg.Message);
        Assert.True(CountOf(p.Player, FeeItem) == 0 && EntryFeeEscrow.Has(p.Player),
            "the fee was not taken into escrow on joining the queue");

        CommandResult leave = await p.ExecuteCommand("/canleave");
        Assert.True(Manager.FindRegistration(p.Player.PlayerUID, ev) == null, "still queued after /canleave: " + leave.Message);
        Assert.True(CountOf(p.Player, FeeItem) == 1, $"after leaving the queue: {FeeItem} x{CountOf(p.Player, FeeItem)}, expected 1");
        Assert.True(!EntryFeeEscrow.Has(p.Player), "the escrow is still held after leaving the queue");
    }

    // R24a + R27: the match that formed drops its players' other queues; the stake it charged stays
    // in escrow, since the player is now playing for it.
    [AtlasScenario]
    public async Task Fee_Should_StayInEscrow_When_OtherQueuesAreDroppedForItsMatch()
    {
        var hg = Manager.FindEventByCode(SoloHg);
        var duel = Manager.FindEventByCode("1x1");
        Assert.True(hg != null && duel != null, $"shipped events '{SoloHg}' and '1x1' are required");
        var previousFee = hg.EntryFee;
        hg.EntryFee = new EntryFeeSpec { Items = new List<RewardItem> { new RewardItem { Code = FeeItem, Quantity = 1 } } };
        try
        {
            ITestPlayer p = await World.JoinPlayer("feekept");
            await p.GiveItem(FeeItem, 1);

            var (hgOk, hgMsg) = Manager.AddToQueue(hg, p.Player);
            var (duelOk, duelMsg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(hgOk && duelOk, "could not queue for both: " + hgMsg + " / " + duelMsg);
            Assert.True(EntryFeeEscrow.Has(p.Player) && CountOf(p.Player, FeeItem) == 0, "the HG fee was not taken");

            Manager.AttemptToComposeTeams();
            var match = Manager.RunningEventsSnapshot().SingleOrDefault(re => re.IsParticipant(p.Player.PlayerUID));
            Assert.True(match != null && match.Event.CodeEquals(SoloHg), "the paid HG match did not form");
            Assert.True(Manager.FindRegistration(p.Player.PlayerUID, duel) == null, "precondition: the 1x1 queue was not dropped");

            await World.Ticks(20);
            Assert.True(EntryFeeEscrow.Has(p.Player), "the stake left escrow while its match is on");
            Assert.True(CountOf(p.Player, FeeItem) == 0,
                $"the stake came back ({FeeItem} x{CountOf(p.Player, FeeItem)}) although the player is in its match");

            // Out of the match (stopped, nobody won): the stake comes back, and the dropped queue stays dropped.
            await p.ExecuteCommand("/eventadmin stop");
            await World.Until(() => CountOf(p.Player, FeeItem) == 1 && !EntryFeeEscrow.Has(p.Player),
                "the stake back once the match is over", 300);
            Assert.True(Manager.FindRegistration(p.Player.PlayerUID, duel) == null, "the dropped 1x1 queue came back");
        }
        finally
        {
            hg.EntryFee = previousFee;
        }
    }

    // R24a + R27: leaving the game and coming back mid-match (after the start, unlike the matchmaking
    // leaver above) neither brings the dropped queue back nor hands out the stake: it is a forfeit until
    // the match ends. Three players, so the match goes on without the leaver.
    [AtlasScenario]
    public async Task Fee_Should_NotComeBack_When_Reconnecting_While_ItsMatchGoesOn()
    {
        UseCountdown(1);
        var hg = Manager.FindEventByCode(SoloHg);
        var duel = Manager.FindEventByCode("1x1");
        Assert.True(hg != null && duel != null, $"shipped events '{SoloHg}' and '1x1' are required");
        int area = NewArea();
        var centre = AreaCentre(area);
        var place = AddPlace("feerelog-" + area, centre, 30, canevents.src.EventPlacesManagers.Places.PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 15f, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        var previousFee = hg.EntryFee;
        hg.EntryFee = new EntryFeeSpec { Items = new List<RewardItem> { new RewardItem { Code = FeeItem, Quantity = 1 } } };
        try
        {
            ITestPlayer p = await World.JoinPlayer("feerelog");
            ITestPlayer q = await World.JoinPlayer("feestays");
            ITestPlayer r = await World.JoinPlayer("feestaystoo");
            foreach (var x in new[] { p, q, r }) await x.GiveItem(FeeItem, 1);

            Assert.True(Manager.AddToQueue(hg, p.Player).ok && Manager.AddToQueue(duel, p.Player).ok, "p could not queue for both");
            foreach (var x in new[] { q, r }) Assert.True(Manager.AddToQueue(hg, x.Player).ok, x.Player.PlayerName + " could not queue");

            var match = await StartQueuedMatch(p, q, r);
            await UntilLanded(match, p, q, r);

            await Disconnect(p);
            ITestPlayer back = await Rejoin("feerelog");
            Assert.True(Manager.RunningEventsSnapshot().Contains(match), "precondition: the match must go on with the two others");

            await World.Ticks(20);
            Assert.True(Manager.FindRegistration(back.Player.PlayerUID, duel) == null, "the dropped 1x1 queue came back after the relog");
            Assert.True(CountOf(back.Player, FeeItem) == 0,
                $"the stake came back ({FeeItem} x{CountOf(back.Player, FeeItem)}) while its match is still on");

            // Nobody wins a stopped match: everyone's stake, the forfeited one too, goes back
            // (entryFeeRefundForfeitOnVoid, on by default).
            await q.ExecuteCommand("/eventadmin stop");
            await World.Until(() => CountOf(back.Player, FeeItem) == 1, "the relogged player's stake back after the stop", 300);
            Assert.True(CountOf(q.Player, FeeItem) == 1 && CountOf(r.Player, FeeItem) == 1,
                "the others' stakes were not refunded on the stop");
        }
        finally
        {
            hg.EntryFee = previousFee;
        }
    }

    // R24a + R27: the other way round - a free match forms, the paid queue it drops was never played,
    // so that stake comes back.
    [AtlasScenario]
    public async Task Fee_Should_BeRefunded_When_ItsQueueIsDroppedForAnotherMatch()
    {
        var hg = Manager.FindEventByCode(SoloHg);
        var paid = Manager.FindEventByCode(PaidDuel);
        Assert.True(hg != null && paid != null, $"shipped events '{SoloHg}' and '{PaidDuel}' are required");

        ITestPlayer p = await World.JoinPlayer("feereturned");
        await p.GiveItem(FeeItem, 1);

        var (paidOk, paidMsg) = Manager.AddToQueue(paid, p.Player);
        var (hgOk, hgMsg) = Manager.AddToQueue(hg, p.Player);
        Assert.True(paidOk && hgOk, "could not queue for both: " + paidMsg + " / " + hgMsg);
        Assert.True(EntryFeeEscrow.Has(p.Player), "precondition: the duel fee was not taken");

        Manager.AttemptToComposeTeams();
        var match = Manager.RunningEventsSnapshot().SingleOrDefault(re => re.IsParticipant(p.Player.PlayerUID));
        Assert.True(match != null && match.Event.CodeEquals(SoloHg), "the free HG match did not form");
        Assert.True(Manager.FindRegistration(p.Player.PlayerUID, paid) == null, "the paid queue was not dropped");

        await World.Ticks(20);
        Assert.True(!EntryFeeEscrow.Has(p.Player), "the dropped queue's stake is still in escrow");
        Assert.True(CountOf(p.Player, FeeItem) == 1, $"the dropped queue's stake was not refunded ({FeeItem} x{CountOf(p.Player, FeeItem)})");

        await p.ExecuteCommand("/eventadmin stop");
    }

    // R27: a player who dropped out before the start never played, so the stake must come back,
    // not wait in the pot for whoever wins.
    [AtlasScenario]
    public async Task Fee_Should_BeRefunded_When_PlayerLeftDuringMatchmaking()
    {
        var hg = Manager.FindEventByCode(SoloHg);
        Assert.True(hg != null, $"shipped event '{SoloHg}' is required");
        var previousFee = hg.EntryFee;
        hg.EntryFee = new EntryFeeSpec { Items = new List<RewardItem> { new RewardItem { Code = FeeItem, Quantity = 1 } } };
        try
        {
            ITestPlayer first = await World.JoinPlayer("feefirst");
            ITestPlayer second = await World.JoinPlayer("feesecond");
            ITestPlayer leaver = await World.JoinPlayer("feeleaver");
            foreach (var p in new[] { first, second, leaver }) await p.GiveItem(FeeItem, 1);

            foreach (var p in new[] { first, second, leaver })
            {
                var (ok, msg) = Manager.AddToQueue(hg, p.Player);
                Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
                Assert.True(CountOf(p.Player, FeeItem) == 0, p.Player.PlayerName + " was not charged the fee");
            }

            Manager.AttemptToComposeTeams();
            var match = Manager.RunningEventsSnapshot().SingleOrDefault(re => re.IsParticipant(leaver.Player.PlayerUID));
            Assert.True(match != null, "no match was assembled");
            Assert.True(match.IsParticipant(first.Player.PlayerUID) && match.IsParticipant(second.Player.PlayerUID),
                "the three entrants were not assembled into one match");
            Assert.True(match.EventState == EventState.AwaitingReady, "expected a ready check, state is " + match.EventState);

            // The other two confirm, so the match goes on without the leaver.
            Manager.SetMatchReady(first.Player, match.Id, true);
            Manager.SetMatchReady(second.Player, match.Id, true);

            await Disconnect(leaver);
            ITestPlayer back = await Rejoin("feeleaver");
            Assert.True(Manager.RunningEventsSnapshot().Contains(match),
                "precondition: the match must still be running when the leaver comes back");

            await World.Ticks(20);
            Assert.True(CountOf(back.Player, FeeItem) == 1,
                $"the leaver has {CountOf(back.Player, FeeItem)} x {FeeItem} after coming back; the stake was not refunded");
            Assert.True(!EntryFeeEscrow.Has(back.Player), "the leaver still holds an escrow");

            await first.ExecuteCommand("/eventadmin stop");
        }
        finally
        {
            hg.EntryFee = previousFee;
        }
    }
}
