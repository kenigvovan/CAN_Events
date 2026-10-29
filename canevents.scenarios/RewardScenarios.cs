using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;

namespace canevents.scenarios;

// R25 in docs/testing/03-risks.md: winner rewards and their rate cap.
[Trait("Category", "E2E")]
public class RewardScenarios : EventsScenarioBase
{
    private const string Duel = "1x1";
    // 1x1 pays 3 of these to the winner.
    private const string Reward = "game:gear-rusty";
    private const int PerWin = 3;

    // Burst of two wins; one token back every 0.01 h (36 s), the lowest the admin settings allow.
    private void UseSmallRateCap()
    {
        var c = Mod.config;
        int burst = c.rewardRateCapBurst;
        double refill = c.rewardRateCapRefillHours;
        bool perEvent = c.rewardRateCapPerEvent;
        c.rewardRateCapBurst = 2;
        c.rewardRateCapRefillHours = 0.01;
        c.rewardRateCapPerEvent = false;
        Manager.RewardDistributor.ReconfigureRateCap();
        AddCleanup(() =>
        {
            c.rewardRateCapBurst = burst;
            c.rewardRateCapRefillHours = refill;
            c.rewardRateCapPerEvent = perEvent;
            Manager.RewardDistributor.ReconfigureRateCap();
        });
    }

    private async Task PlayDuelWonBy(ITestPlayer winner, ITestPlayer loser, string eventCode = Duel)
    {
        var duel = Manager.FindEventByCode(eventCode);
        foreach (var p in new[] { winner, loser })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        var match = await StartQueuedMatch(winner, loser);
        await UntilLanded(match, winner, loser);
        loser.Player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Void, Type = EnumDamageType.Injury }, 1000f);
        await World.Until(() => !match.IsActive(loser.Player.PlayerUID), "the loser to be out after the lethal hit", 100);
        await World.Until(() => !Manager.RunningEventsSnapshot().Contains(match), "the duel to finish", 600);
    }

    [AtlasScenario]
    public async Task Winner_Should_BeRewarded_UpToTheCap_And_Again_After_Refill()
    {
        UseCountdown(1);
        UseSmallRateCap();
        int area = NewArea();
        AddArena("reward-" + area, AreaCentre(area), 12, PlaceTags.Arena1x1, 2);

        ITestPlayer winner = await World.JoinPlayer("capwinner");
        ITestPlayer loser = await World.JoinPlayer("caploser");
        await winner.ExecuteCommand("/eventadmin ratecap reset capwinner");
        var duel = Manager.FindEventByCode(Duel);

        await PlayDuelWonBy(winner, loser);
        Assert.True(CountOf(winner.Player, Reward) == PerWin, $"after win 1: {Reward} x{CountOf(winner.Player, Reward)}, expected {PerWin}");

        await PlayDuelWonBy(winner, loser);
        Assert.True(CountOf(winner.Player, Reward) == 2 * PerWin, $"after win 2 (cap reached): {Reward} x{CountOf(winner.Player, Reward)}, expected {2 * PerWin}");

        await PlayDuelWonBy(winner, loser);
        Assert.True(CountOf(winner.Player, Reward) == 2 * PerWin, $"win 3 went past the cap: {Reward} x{CountOf(winner.Player, Reward)}, expected {2 * PerWin}");
        var (active, remaining, _) = Manager.RewardDistributor.GetRewardCapStatus(winner.Player.PlayerUID, duel);
        Assert.True(active && remaining == 0, $"cap status after win 3: active={active}, remaining={remaining}");

        await World.Until(() => Manager.RewardDistributor.GetRewardCapStatus(winner.Player.PlayerUID, duel).remaining >= 1,
            "one reward token to refill (36 s)", 3000);
        await PlayDuelWonBy(winner, loser);
        Assert.True(CountOf(winner.Player, Reward) == 3 * PerWin, $"after the refill: {Reward} x{CountOf(winner.Player, Reward)}, expected {3 * PerWin}");
    }

    // R26 + R27: a rated duel moves Elo both ways by the same amount, and the pot of stakes goes to the winner.
    // 1x1_ranked: fee 1 gear-rusty each, no rake, winner reward 1 gear-rusty, Elo K=32 from 1000.
    [AtlasScenario]
    public async Task RatedDuel_Should_MoveEloBothWays_And_PayThePotToTheWinner()
    {
        const string Ranked = "1x1_ranked";
        UseCountdown(1);
        int area = NewArea();
        AddArena("ranked-" + area, AreaCentre(area), 12, PlaceTags.Arena1x1, 2);

        ITestPlayer winner = await World.JoinPlayer("elowinner");
        ITestPlayer loser = await World.JoinPlayer("eloloser");
        await winner.ExecuteCommand("/eventadmin ratecap reset elowinner");
        foreach (var p in new[] { winner, loser }) await p.GiveItem(Reward, 1);

        var board = Manager.Leaderboard;
        int winnerBefore = board.GetRating(winner.Player.PlayerUID, Ranked);
        int loserBefore = board.GetRating(loser.Player.PlayerUID, Ranked);

        await PlayDuelWonBy(winner, loser, Ranked);

        int gained = board.GetRating(winner.Player.PlayerUID, Ranked) - winnerBefore;
        int lost = loserBefore - board.GetRating(loser.Player.PlayerUID, Ranked);
        Assert.True(gained > 0, $"the winner's rating did not go up ({winnerBefore} -> {winnerBefore + gained})");
        Assert.True(lost == gained, $"the loser lost {lost} while the winner gained {gained}");
        Assert.True(winnerBefore != 1000 || loserBefore != 1000 || gained == 16,
            $"equal 1000 ratings with K=32 should move 16, moved {gained}");

        Assert.True(CountOf(winner.Player, Reward) == 3,
            $"winner holds {Reward} x{CountOf(winner.Player, Reward)}: expected the pot (2) plus the winner reward (1)");
        Assert.True(CountOf(loser.Player, Reward) == 0, $"loser still holds {Reward} x{CountOf(loser.Player, Reward)}");
        Assert.True(!EntryFeeEscrow.Has(winner.Player) && !EntryFeeEscrow.Has(loser.Player), "an escrow was left after the duel");
    }
}
