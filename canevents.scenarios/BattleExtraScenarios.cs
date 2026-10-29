using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// More Battle cases (docs/testing/04-tdm-battle.md B19-B24). Apart from BattleScenarios because a class
// shares one server, and that one already fills its 16 client slots.
[Trait("Category", "E2E")]
public class BattleExtraScenarios : LiveScenarioBase
{
    private async Task<(ITestPlayer a, ITestPlayer b)> TwoFighters(string prefix)
        => (await JoinSurvivor(prefix + "a"), await JoinSurvivor(prefix + "b"));

    // B22, seen in game: a deleted arena's zones were left on the same blocks and listed first, so the
    // step went to a place with no lobby and was dropped without a word: no side, no class dialog.
    [AtlasScenario]
    public async Task SideZone_Should_PickSideAndAskClass_When_StaleZoneOverlaps()
    {
        var place = AddBattleArena(NewArea());
        var room = WaitingRoomOf(place);
        var floor = room.AddCopy(4, 0, 0);
        AddPad(Battle, JoinPads.SideAction, 1, floor, 3, place.Id + 100000);
        var red = AddPad(Battle, JoinPads.SideAction, 1, floor, 3, place.Id);

        var (a, b) = await TwoFighters("btlstale");
        var match = await StartBattleToLobby(a, b);
        await World.Until(() => DistanceXZ(a.Position, room) <= 2, "a in the waiting room", 300);

        await StepOnto(a, red);
        await World.Until(() => match.TeamChoices.TryGetValue(a.Player.PlayerUID, out int t) && t == 1,
            "the zone under the stale one to pick team 2", 200);
        await World.Until(() => PacketsOf<LoadoutPickRequestMsg>(a).Any(m => m.EventId == match.Id),
            "the class dialog after the side", 100);
        await a.ExecuteCommand("/eventadmin stop");
    }

    // B24: deleting an arena takes its side zones along; another place's zones on the same room stay.
    [AtlasScenario]
    public Task DeletingPlace_Should_RemoveItsSideZones_Only()
    {
        var place = AddBattleArena(NewArea());
        var other = AddBattleArena(NewArea());
        var floor = WaitingRoomOf(place).AddCopy(4, 0, 0);
        var own = AddPad(Battle, JoinPads.SideAction, 0, floor, 3, place.Id);
        var kept = AddPad(Battle, JoinPads.SideAction, 1, floor, 3, other.Id);

        var result = Manager.placeConfig.DeletePlace(place);
        Assert.True(result.Ok, "the place was not deleted: " + result.Message);

        var ids = Mod.JoinPads.All().Select(p => p.Id).ToList();
        Assert.False(ids.Contains(own.Id), "the deleted arena's side zone is still there");
        Assert.True(ids.Contains(kept.Id), "another arena's side zone went with it");
        return Task.CompletedTask;
    }

    // B23, seen in game: bounds up to Y 57 put the seat 60+ blocks over the floor, and the 50-block
    // sphere around it pulled every descent back up.
    [AtlasScenario]
    public async Task Eliminated_Should_FlyDownToTheFloor_Of_ATallArena()
    {
        int area = NewArea();
        var centre = AreaCentre(area);
        var place = AddBattleArena(area, winsToWin: 3, roundSeconds: 120);
        place.SetBounds(centre.AddCopy(-12, -4, -12), centre.AddCopy(12, 70, 12));
        UseMutator(Battle, EventMutators.Loadout, "Spearman");

        var players = new List<ITestPlayer>();
        for (int i = 0; i < 3; i++) players.Add(await JoinSurvivor("btltall" + i));
        var match = await StartBattleToLobby(players.ToArray());
        Manager.PickSideInLobby(players[0].Player, Battle, 0);
        Manager.PickSideInLobby(players[1].Player, Battle, 1);
        Manager.PickSideInLobby(players[2].Player, Battle, 0);
        await UntilRoundLive(match);

        // Two on team 1, so the round goes on after one of them dies.
        var fallen = players[2];
        Kill(fallen, players[1]);
        await World.Until(() => fallen.Player.WorldData.CurrentGameMode == EnumGameMode.Spectator
                                && fallen.Position.Y > centre.Y + 60, "the fallen player seated above the arena", 300);

        await fallen.TeleportTo(centre.AddCopy(0, 2, 0));
        await World.Ticks(120);
        Assert.True(fallen.Position.Y < centre.Y + 20,
            $"the spectator was pulled back up to Y {fallen.Position.Y} from the arena floor (Y {centre.Y + 2})");

        await fallen.TeleportTo(centre.AddCopy(60, 2, 0));
        await World.Until(() => Math.Abs(fallen.Position.X - centre.X) < 30, "a spectator far outside the bounds to be pulled back", 300);
        await players[0].ExecuteCommand("/eventadmin stop");
    }

    // B19: the intermission dialog highlights this match's class; the "always use" switch follows only a
    // saved default, so confirming it as shown must not save one.
    // B20: a saved default is the pick everywhere, so neither the lobby nor the intermission asks.
    [AtlasScenario]
    public async Task IntermissionPick_Should_Preselect_MatchClass_And_SkipSavedDefault()
    {
        AddBattleArena(NewArea(), winsToWin: 3);
        var (a, b) = await TwoFighters("btlintpick");
        Manager.LoadoutPreferences.Clear(a.Player.PlayerUID, Battle);
        Manager.LoadoutPreferences.Set(b.Player.PlayerUID, Battle, "Plate");
        AddCleanup(() => Manager.LoadoutPreferences.Clear(b.Player.PlayerUID, Battle));

        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        ConfirmLoadout(a, match, "Archer");
        await UntilRoundLive(match);
        Assert.True(CountOf(b.Player, "game:armor-body-plate-steel") == 1, "b's saved Plate was not given");

        Kill(b, a);
        LoadoutPickRequestMsg IntermissionPick(ITestPlayer p) => PacketsOf<LoadoutPickRequestMsg>(p)
            .LastOrDefault(m => m.EventId == match.Id && m.CountdownLangKey == "canevents:gui-class-change-closes-in");
        await World.Until(() => IntermissionPick(a) != null, "the class-change offer in the intermission", 300);

        var forA = IntermissionPick(a);
        Assert.True(forA.Preselected == "Archer", $"a: highlighted '{forA.Preselected}', not this match's Archer");
        Assert.True(string.IsNullOrEmpty(forA.CurrentDefault),
            $"a has no saved default, yet the offer says '{forA.CurrentDefault}' (the switch would come on)");

        Assert.True(!PacketsOf<LoadoutPickRequestMsg>(b).Any(m => m.EventId == match.Id),
            "b has a saved default, yet was asked for a class");
        await World.Until(() => CountOf(b.Player, "game:armor-body-plate-steel") == 1, "b's Plate again for round 2", 300);
        await a.ExecuteCommand("/eventadmin stop");
    }
}
