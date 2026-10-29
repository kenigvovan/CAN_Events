using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.scenarios;

// Battle: queue -> waiting room (side + class) -> rounds to winsToWin (docs/testing/04-tdm-battle.md B1-B16).
[Trait("Category", "E2E")]
public class BattleScenarios : LiveScenarioBase
{
    private async Task<(ITestPlayer a, ITestPlayer b)> TwoFighters(string prefix)
    {
        ITestPlayer a = await JoinSurvivor(prefix + "a");
        ITestPlayer b = await JoinSurvivor(prefix + "b");
        return (a, b);
    }

    [AtlasScenario]
    public async Task Players_Should_LandInWaitingRoomWithEmptyInventory_And_TakeNoDamage()
    {
        var place = AddBattleArena(NewArea());
        var (a, b) = await TwoFighters("btlroom");
        await a.GiveItem(Own, 2);

        var match = await StartBattleToLobby(a, b);
        var room = WaitingRoomOf(place);
        await World.Until(() => DistanceXZ(a.Position, room) <= 2, "the player to be in the waiting room", 300);
        Assert.True(CountOf(a.Player, Own) == 0, "own items were not stashed on the way into the room");

        var health = a.Player.Entity.GetBehavior<EntityBehaviorHealth>();
        float before = health.Health;
        a.Player.Entity.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Player, SourceEntity = b.Player.Entity, CauseEntity = b.Player.Entity,
            Type = EnumDamageType.SlashingAttack
        }, 5f);
        await World.Ticks(5);
        Assert.True(health.Health >= before - 0.01f, "took damage in the waiting room");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => CountOf(a.Player, Own) == 2, "own items back after a stop in the lobby", 300);
        Assert.True(Settled(a.Player), "snapshot left pending after a stop in the lobby");
    }

    // A1: the first state carries the lobby length, not the not-yet-set countdown (0).
    [AtlasScenario]
    public async Task SideDialogState_Should_CarryLobbySeconds()
    {
        AddBattleArena(NewArea(), lobbySeconds: 20);
        var (a, b) = await TwoFighters("btldlg");
        await StartBattleToLobby(a, b);

        await World.Until(() => PacketsOf<TeamPickStateMsg>(a).Count > 0, "a side-pick state packet", 100);
        var first = PacketsOf<TeamPickStateMsg>(a)[0];
        Assert.True(first.SecondsLeft >= 15, $"first side-pick state says {first.SecondsLeft}s left");
        Assert.False(first.Closed, "the first side-pick state is already closed");
        await a.ExecuteCommand("/eventadmin stop");
    }

    [AtlasScenario]
    public async Task Side_Should_BeRefused_When_TeamIsFull()
    {
        AddBattleArena(NewArea());
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btlfull");
        await StartBattleToLobby(a, b);

        var (okA, _) = Manager.PickSideInLobby(a.Player, Battle, 0);
        var (okB, msg) = Manager.PickSideInLobby(b.Player, Battle, 0);
        Assert.True(okA, "first pick refused");
        Assert.False(okB, "second pick onto a team one ahead (maxImbalance 1) was accepted");
        Assert.False(string.IsNullOrEmpty(msg), "no reason given for the refusal");
        await a.ExecuteCommand("/eventadmin stop");
    }

    // What a player saw in game: a class card clicked but not confirmed must not start the match early.
    [AtlasScenario]
    public async Task EarlyStart_Should_Wait_For_ClassConfirm()
    {
        AddBattleArena(NewArea(), lobbySeconds: 30);
        var (a, b) = await TwoFighters("btlearly");
        var match = await StartBattleToLobby(a, b);

        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await World.Ticks(5);
        Assert.True(match.CountdownSeconds > 10, $"countdown cut to {match.CountdownSeconds}s before any class was confirmed");

        ConfirmLoadout(a, match, "Archer");
        ConfirmLoadout(b, match, "Spearman");
        await World.Ticks(5);
        Assert.True(match.CountdownSeconds <= 5.5f, $"countdown not cut once everyone confirmed ({match.CountdownSeconds}s)");

        await UntilRunning(match);
        Assert.True(CountOf(a.Player, "game:bow-long") == 1, "the confirmed Archer class was not given");
        await a.ExecuteCommand("/eventadmin stop");
    }

    [AtlasScenario]
    public async Task Teams_Should_FollowPicks_And_Unpicked_Should_FillSmallest()
    {
        var place = AddBattleArena(NewArea());
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var players = new List<ITestPlayer>();
        for (int i = 0; i < 4; i++) players.Add(await JoinSurvivor("btlteam" + i));
        var match = await StartBattleToLobby(players.ToArray());

        Manager.PickSideInLobby(players[0].Player, Battle, 1);
        Manager.PickSideInLobby(players[1].Player, Battle, 0);
        Manager.PickSideInLobby(players[2].Player, Battle, 1);
        // players[3] doesn't pick: team 0 is the smaller one.

        await UntilRunning(match);
        Assert.True(match.TeamIndexOf(players[0].Player.PlayerUID) == 1, "pick of team 2 not kept");
        Assert.True(match.TeamIndexOf(players[1].Player.PlayerUID) == 0, "pick of team 1 not kept");
        Assert.True(match.TeamIndexOf(players[3].Player.PlayerUID) == 0, "unpicked player not put in the smaller team");

        await World.Until(() => DistanceXZ(players[0].Position, place.spawnPositions[1]) <= 3,
            "team 2 to stand at spawn 2", 300);
        await players[0].ExecuteCommand("/eventadmin stop");
    }

    [AtlasScenario]
    public async Task Round_Should_ScoreAndReviveEveryoneWithFreshKit()
    {
        AddBattleArena(NewArea(), winsToWin: 3);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btlround");
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);

        Kill(b, a);
        await World.Until(() => !match.IsActive(b.Player.PlayerUID), "the dead player to be out of the round", 100);
        // Intermission: everyone back in play, alive, kit emptied until the round starts.
        await World.Until(() => match.IsActive(b.Player.PlayerUID) && b.Player.Entity.Alive, "revive for the next round", 300);
        await World.Until(() => CountOf(b.Player, "game:spear-generic-steel") == 1, "a fresh kit for round 2", 300);
        Assert.True(CountOf(a.Player, "game:spear-generic-steel") == 1, "the winner did not get exactly one fresh kit");

        var scores = match.Scorer?.BuildScoreboard().TeamScores;
        Assert.True(scores != null && scores[0] == 1 && scores[1] == 0, "round score is not 1:0");
        await a.ExecuteCommand("/eventadmin stop");
    }

    [AtlasScenario]
    public async Task Match_Should_End_At_WinsToWin_And_RestoreInventory()
    {
        AddBattleArena(NewArea(), winsToWin: 1);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btlwin");
        await a.GiveItem(Own, 2);
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);

        Kill(b, a);
        await World.Until(() => match.EventState == EventState.Finished, "the battle to end after one won round", 300);
        await World.Until(() => CountOf(a.Player, Own) == 2, "own items back", 300);
        Assert.True(Settled(a.Player) && Settled(b.Player), "snapshots left pending after the battle");
    }

    [AtlasScenario]
    public async Task Round_Should_Draw_When_TimeRunsOutEven()
    {
        AddBattleArena(NewArea(), winsToWin: 3, roundSeconds: 10);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btldraw");
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);
        float roundStart = match.ElapsedSeconds;

        await World.Until(() => match.ElapsedSeconds >= roundStart + 12, "the round clock to run out", 600);
        var scores = match.Scorer?.BuildScoreboard().TeamScores;
        Assert.True(scores != null && scores[0] == 0 && scores[1] == 0, "a timed-out even round gave someone a point");
        await a.ExecuteCommand("/eventadmin stop");
    }

    [AtlasScenario]
    public async Task Leaver_Should_GoHomeWithInventory_And_TeamShouldForfeit()
    {
        AddBattleArena(NewArea(), winsToWin: 3);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btlquit");
        await b.GiveItem(Own, 2);
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRunning(match);

        var result = await b.ExecuteCommand("/canlive leave");
        Assert.True(result.Ok, "/canlive leave refused in battle: " + result.Message);
        await World.Until(() => CountOf(b.Player, Own) == 2, "the leaver's own items back", 300);
        await World.Until(() => match.EventState == EventState.Finished, "the emptied team to forfeit", 300);
        Assert.False(match.GetWinners().Contains(b.Player.PlayerUID), "the leaver is among the winners");
        Assert.True(match.GetWinners().Contains(a.Player.PlayerUID), "the remaining team didn't win by forfeit");
    }

    [AtlasScenario]
    public async Task LobbyDropout_Should_NotAbort_When_EnoughPlayersRemain()
    {
        AddBattleArena(NewArea());
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var players = new List<ITestPlayer>();
        for (int i = 0; i < 4; i++) players.Add(await JoinSurvivor("btldrop" + i));
        var match = await StartBattleToLobby(players.ToArray());

        // Whatever teams the queue made, two leaving the lobby still leaves a 1v1 after regrouping.
        var queueTeam = match.ParticipantsByGroupsBefore[0].ToList();
        foreach (var uid in queueTeam)
        {
            var p = players.First(x => x.Player.PlayerUID == uid);
            await p.ExecuteCommand("/canlive leave");
        }
        await World.Until(() => match.EventState != EventState.LocationFound, "the lobby to close", 900);
        Assert.True(match.EventState == EventState.Running, "the battle was called off although a 1v1 was left");
        Assert.True(match.ParticipantsByGroupsStillInEvent.Count == 2, "the remaining two are not on opposite teams");
        await players[0].ExecuteCommand("/eventadmin stop");
    }

    [AtlasScenario]
    public async Task SideZones_Should_PickSide_And_OnlyInTheirOwnRoom()
    {
        var place = AddBattleArena(NewArea());
        var other = AddBattleArena(NewArea());
        other.Enabled = false; // only `place` gets the match; its zones must not leak to `other`
        UseMutator(Battle, EventMutators.Loadout, "Spearman");

        var room = WaitingRoomOf(place);
        var red = AddPad(Battle, JoinPads.SideAction, 1, room.AddCopy(4, 0, 0), 3, place.Id);
        var foreign = AddPad(Battle, JoinPads.SideAction, 0, WaitingRoomOf(other).AddCopy(4, 0, 0), 3, other.Id);
        Assert.True(Mod.JoinPads.HasSidePads(Battle, place.Id), "the room's own zone is not seen");
        Assert.False(Mod.JoinPads.HasSidePads(Battle, other.Id + 100000), "zones of one room count for another");

        var (a, b) = await TwoFighters("btlzone");
        var match = await StartBattleToLobby(a, b);
        await World.Until(() => DistanceXZ(a.Position, room) <= 2, "a in the waiting room", 300);

        await StepOnto(a, red);
        await World.Until(() => match.TeamChoices.TryGetValue(a.Player.PlayerUID, out int t) && t == 1,
            "standing on the red zone to pick team 2", 200);
        Assert.True(PacketsOf<TeamPickStateMsg>(a).Count == 0, "the side dialog was sent although the room has zones");
        await a.ExecuteCommand("/eventadmin stop");
    }

}
