using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.Network;

namespace canevents.scenarios;

// B26: team pennants (TeamMarkersEB), checked on what the server sends; the drawing itself is client-only.
[Trait("Category", "E2E")]
public class TeamMarkerScenarios : LiveScenarioBase
{
    private Dictionary<string, int> LastTeamMarkers(ITestPlayer p, RunningEvent match)
    {
        var msg = PacketsOf<TeamMarkersMsg>(p).LastOrDefault(m => m.EventId == match.Id);
        var map = new Dictionary<string, int>();
        int n = Math.Min(msg?.Uids?.Length ?? 0, msg?.Teams?.Length ?? 0);
        for (int i = 0; i < n; i++) map[msg!.Uids[i]] = msg.Teams[i];
        return map;
    }

    // Pennants in team colours over everyone in play; the dead drop out, the end clears them.
    [AtlasScenario]
    public async Task TeamMarkers_Should_Follow_WhoIsInPlay_And_Clear_AtTheEnd()
    {
        AddBattleArena(NewArea(), winsToWin: 3);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var players = new List<ITestPlayer>();
        for (int i = 0; i < 3; i++) players.Add(await JoinSurvivor("btlmark" + i));
        var match = await StartBattleToLobby(players.ToArray());
        Manager.PickSideInLobby(players[0].Player, Battle, 0);
        Manager.PickSideInLobby(players[1].Player, Battle, 1);
        Manager.PickSideInLobby(players[2].Player, Battle, 0);
        await UntilRoundLive(match);

        var a = players[0];
        string uidOf(int i) => players[i].Player.PlayerUID;
        await World.Until(() => LastTeamMarkers(a, match).Count == 3, "pennants for all three", 100);
        var seen = LastTeamMarkers(a, match);
        Assert.True(seen[uidOf(0)] == 0 && seen[uidOf(2)] == 0 && seen[uidOf(1)] == 1,
            "pennants carry the wrong teams: " + string.Join(", ", seen.Select(kv => kv.Key + "=" + kv.Value)));

        Kill(players[2], players[1]);
        await World.Until(() => !LastTeamMarkers(a, match).ContainsKey(uidOf(2)), "the dead player's pennant to go", 100);

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => LastTeamMarkers(a, match).Count == 0, "the pennants cleared at the end", 200);
    }

    // With teamMarkersForEnemies off, a fighter sees only their own team's pennants.
    [AtlasScenario]
    public async Task TeamMarkers_Should_ShowOnlyOwnTeam_When_EnemiesHidden()
    {
        string before = Manager.EventSettings(Battle).First(s => s.Setting.Key == "teamMarkersForEnemies").Value;
        Assert.True(Manager.SetEventSetting(Battle, "TeamMarkers", "teamMarkersForEnemies", "false").ok, "could not turn enemy pennants off");
        AddCleanup(() => Manager.SetEventSetting(Battle, "TeamMarkers", "teamMarkersForEnemies", before));

        AddBattleArena(NewArea(), winsToWin: 3);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        ITestPlayer a = await JoinSurvivor("btlmarkowna");
        ITestPlayer b = await JoinSurvivor("btlmarkownb");
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);

        await World.Until(() => LastTeamMarkers(a, match).Count > 0, "a's pennant list", 100);
        await World.Ticks(30);
        Assert.False(LastTeamMarkers(a, match).ContainsKey(b.Player.PlayerUID), "a sees the enemy's pennant with enemies hidden");
        await a.ExecuteCommand("/eventadmin stop");
    }
}
