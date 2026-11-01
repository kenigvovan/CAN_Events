using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
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

    // B27, seen in game: the death that ended the round came within MakeSpectator's 100 ms delay. The
    // intermission revived the fallen first, then the late seating made them a spectator for all of the next round.
    [AtlasScenario]
    public async Task Fallen_Should_NotBeSeated_When_RoundEndsWithinSeatingDelay()
    {
        AddBattleArena(NewArea(), winsToWin: 3, roundSeconds: 120);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btlseat");
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);
        var rounds = match.GetBehavior<BattleRoundsEB>()!;
        string uid = b.Player.PlayerUID;
        var data = b.Player.WorldData;

        Kill(b, a);
        await World.Until(() => !match.IsActive(uid), "b out of the round", 100);
        // The match ticks once a second, so in game the window is hit about one time in ten; ending the
        // round by hand puts the intermission inside it every time.
        Assert.False(match.Spectator.IsSpectating(uid), "precondition: b was seated before the round could end");
        rounds.OnTick(0f);
        Assert.False(rounds.RoundLive, "the round did not end with one team left");

        await World.Ticks(15);
        Assert.True(match.IsActive(uid) && b.Player.Entity.Alive, "b was not revived for the next round");
        Assert.True(data.CurrentGameMode == EnumGameMode.Survival && !match.Spectator.IsSpectating(uid),
            $"the late seating made b a spectator in the intermission (mode {data.CurrentGameMode})");
        Assert.True(b.Player.GetModdata(SpectatorManager.ModDataKey) == null, "a saved game mode was left in ModData");

        await World.Until(() => rounds.RoundLive, "the next round to start", 300);
        Assert.True(data.CurrentGameMode == EnumGameMode.Survival, $"b plays round 2 as {data.CurrentGameMode}");
        Assert.True(CountOf(b.Player, "game:spear-generic-steel") == 1, "b got no kit for round 2");
        await a.ExecuteCommand("/eventadmin stop");
    }

    // B27 the other way round: only a RemoveSpectator calls the delayed seating off. A player still in play
    // (a parkour finisher) is seated as before.
    [AtlasScenario]
    public async Task DelayedSeating_Should_BeCalledOff_Only_By_RemoveSpectator()
    {
        AddBattleArena(NewArea(), winsToWin: 3, roundSeconds: 120);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btlpend");
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);
        var data = a.Player.WorldData;

        match.Spectator.MakeSpectator(a.Player, match);
        match.Spectator.RemoveSpectator(a.Player);
        await World.Ticks(15);
        Assert.True(data.CurrentGameMode == EnumGameMode.Survival && !match.Spectator.IsSpectating(a.Player.PlayerUID),
            $"a seating called off by RemoveSpectator still ran (mode {data.CurrentGameMode})");

        match.Spectator.MakeSpectator(a.Player, match);
        await World.Until(() => data.CurrentGameMode == EnumGameMode.Spectator, "an active player to be seated", 100);

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => data.CurrentGameMode == EnumGameMode.Survival, "the game mode back after the match", 300);
    }

    // B28: the intermission's hop to the spawn came while the hop to the spectator seat was still in flight
    // and was dropped, so the revived player started the round mid-arena. A hop now waits for the one in
    // flight, and of two the newer one wins.
    [AtlasScenario]
    public async Task Teleport_Should_WaitForOneInFlight_And_NewestShouldWin()
    {
        var centre = AreaCentre(NewArea());
        var east = centre.AddCopy(6, 2, 0);
        var west = centre.AddCopy(-6, 2, 0);
        ITestPlayer p = await JoinSurvivor("tpqueue");

        EventTeleport.Send(World.Api, p.Player, east, 0.5, 0, 0.5, "test older");
        EventTeleport.Send(World.Api, p.Player, west, 0.5, 0, 0.5, "test newer");
        await World.Until(() => !p.Player.Entity.Teleporting && DistanceXZ(p.Position, west) <= 1.5, "the newer hop to land", 300);
        await World.Ticks(30);
        Assert.True(DistanceXZ(p.Position, west) <= 1.5, "the older hop landed after the newer one");

        // As if the last landing were still loading on the client.
        p.Player.Entity.Teleporting = true;
        bool accepted = EventTeleport.Send(World.Api, p.Player, east, 0.5, 0, 0.5, "test waiting");
        Assert.True(accepted, "a hop asked for while another is in flight was refused");
        await World.Ticks(20);
        Assert.True(DistanceXZ(p.Position, west) <= 1.5, "a hop went through while another was in flight");

        p.Player.Entity.Teleporting = false;
        await World.Until(() => DistanceXZ(p.Position, east) <= 1.5, "the waiting hop to land once the one in flight did", 300);
    }

    // B29: vanilla ignores a respawn key from someone alive but still raises PlayerRespawn for mods. A late
    // click after the intermission had revived the player cleared the kit they were just given.
    [AtlasScenario]
    public async Task LateRespawnClick_Should_NotClearKit_Of_ALivingPlayer()
    {
        AddBattleArena(NewArea(), winsToWin: 3, roundSeconds: 120);
        UseMutator(Battle, EventMutators.Loadout, "Spearman");
        var (a, b) = await TwoFighters("btllate");
        var match = await StartBattleToLobby(a, b);
        Manager.PickSideInLobby(a.Player, Battle, 0);
        Manager.PickSideInLobby(b.Player, Battle, 1);
        await UntilRoundLive(match);
        await World.Until(() => CountOf(a.Player, "game:spear-generic-steel") == 1, "a's kit", 100);

        Assert.True(a.Player.Entity.Alive, "precondition: a must be alive");
        Manager.OnRespawn(a.Player);
        await World.Ticks(20);
        Assert.True(CountOf(a.Player, "game:spear-generic-steel") == 1, "a respawn key from a living player cleared the kit");
        await a.ExecuteCommand("/eventadmin stop");
    }
}
