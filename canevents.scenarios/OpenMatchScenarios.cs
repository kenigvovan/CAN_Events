using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.scenarios;

// Open TDM: walked into through lobby pads, never ends, players come and go (docs/testing/04-tdm-battle.md T1-T14).
[Trait("Category", "E2E")]
public class OpenMatchScenarios : LiveScenarioBase
{
    // Lobby pads sit next to the world spawn, a few blocks apart per scenario, on the real ground
    // (a floating pad would drop the player out of its box before a scan sees them).
    private async Task<BlockPos> LobbyFloor(int slot)
    {
        var air = await SurfaceAt(World.Spawn.X - 30 - slot * 8, World.Spawn.Z + 10);
        return air.AddCopy(0, -1, 0);
    }

    private async Task<BlockPos> FloorNear(BlockPos pos)
    {
        var air = await SurfaceAt(pos.X, pos.Z);
        return air.AddCopy(0, -1, 0);
    }

    // Enters on a pad and confirms the class like a client would.
    private async Task EnterThroughPad(ITestPlayer p, JoinPad pad, RunningEvent match, string set = "Spearman")
    {
        await StepOnto(p, pad);
        ConfirmLoadout(p, match, set);
        await World.Until(() => match.IsActive(p.Player.PlayerUID), p.Player.PlayerName + " to be let in", 200);
    }

    [AtlasScenario]
    public async Task Player_Should_EnterOnTeamSpawnWithKit_When_StepsOnJoinPad()
    {
        var (match, place) = await StartOpenMatch();
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 1, await LobbyFloor(0));
        ITestPlayer p = await JoinSurvivor("tdmenter");
        await p.GiveItem(Own, 2);

        await EnterThroughPad(p, pad, match);

        Assert.True(match.TeamIndexOf(p.Player.PlayerUID) == 1, "joined the wrong team");
        await World.Until(() => DistanceXZ(p.Position, place.spawnPositions[1]) <= 3,
            "the player to land on team 2's spawn", 300);
        Assert.True(CountOf(p.Player, Own) == 0, "own items were not stashed");
        Assert.True(CountOf(p.Player, "game:spear-generic-steel") == 1, "no Spearman kit");
    }

    [AtlasScenario]
    public async Task Player_Should_NotEnter_When_ClassOfferIsNotConfirmed()
    {
        var (match, _) = await StartOpenMatch();
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(1));
        ITestPlayer p = await JoinSurvivor("tdmnoconfirm");
        await p.GiveItem(Own, 2);

        await StepOnto(p, pad);
        await World.Ticks(60);

        Assert.False(match.IsParticipant(p.Player.PlayerUID), "entered without confirming a class");
        Assert.True(CountOf(p.Player, Own) == 2, "the inventory was touched before entry");
        Assert.True(Settled(p.Player), "a snapshot was taken for an entry that never happened");
    }

    // B20: a saved default is the pick, so the pad lets the player straight in with it.
    [AtlasScenario]
    public async Task Pad_Should_EnterWithSavedDefault_WithoutAsking()
    {
        var (match, _) = await StartOpenMatch();
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(9));
        ITestPlayer p = await JoinSurvivor("tdmsaved");
        Manager.LoadoutPreferences.Set(p.Player.PlayerUID, OpenTdm, "Archer");
        AddCleanup(() => Manager.LoadoutPreferences.Clear(p.Player.PlayerUID, OpenTdm));

        await StepOnto(p, pad);
        await World.Until(() => match.IsActive(p.Player.PlayerUID), "entry with the saved class, no dialog", 200);
        Assert.True(!PacketsOf<LoadoutPickRequestMsg>(p).Any(m => m.EventId == match.Id), "asked for a class despite a saved default");
        await World.Until(() => CountOf(p.Player, "game:bow-long") == 1, "the saved Archer kit", 200);
    }

    [AtlasScenario]
    public async Task Pad_Should_Refuse_When_TeamIsAheadByMaxImbalance()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman"); // no class dialog: entry is immediate
        var red = AddPad(OpenTdm, JoinPads.JoinAction, 1, await LobbyFloor(2));

        var players = new List<ITestPlayer>();
        for (int i = 0; i < 3; i++) players.Add(await JoinSurvivor("tdmbal" + i));

        await StepOnto(players[0], red);
        await StepOnto(players[1], red);
        await World.Until(() => match.CountInTeam(1) == 2, "two players on team 2", 200);

        await StepOnto(players[2], red);
        await World.Ticks(30);
        Assert.True(match.CountInTeam(1) == 2, "a third player joined a team already two ahead");
        Assert.False(match.IsParticipant(players[2].Player.PlayerUID), "refused player is on the roster");
    }

    [AtlasScenario]
    public async Task Player_Should_GetInventoryBack_When_LeavingByPadAndCommand()
    {
        var (match, place) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");
        var join = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(3));
        var exit = AddPad(null, JoinPads.LeaveAction, -1, await FloorNear(place.spawnPositions[0].AddCopy(4, 0, 4)));

        ITestPlayer a = await JoinSurvivor("tdmleavea");
        ITestPlayer b = await JoinSurvivor("tdmleaveb");
        foreach (var p in new[] { a, b }) await p.GiveItem(Own, 2);

        await StepOnto(a, join);
        await StepOff(a, join);
        await StepOnto(b, join);
        await World.Until(() => match.IsActive(a.Player.PlayerUID) && match.IsActive(b.Player.PlayerUID), "both in", 200);

        await StepOnto(a, exit);
        var result = await b.ExecuteCommand("/canlive leave");
        Assert.True(result.Ok, "/canlive leave failed: " + result.Message);

        foreach (var p in new[] { a, b })
        {
            await World.Until(() => !match.IsParticipant(p.Player.PlayerUID), p.Player.PlayerName + " to be out", 200);
            Assert.True(CountOf(p.Player, Own) == 2, p.Player.PlayerName + " did not get own items back");
            Assert.True(CountOf(p.Player, "game:spear-generic-steel") == 0, p.Player.PlayerName + " kept the kit");
            Assert.True(Settled(p.Player), p.Player.PlayerName + "'s snapshot is still pending");
        }
    }

    [AtlasScenario]
    public async Task Pad_Should_NotFire_When_PlayerLandsOnItByTeleport_But_Should_AfterSteppingOn()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(4));
        ITestPlayer p = await JoinSurvivor("tdmtele");

        // Sent home onto the pad after leaving looks exactly like this.
        await p.TeleportTo(PadCentre(pad));
        await World.Ticks(30);
        Assert.False(match.IsParticipant(p.Player.PlayerUID), "a pad fired for a player who landed on it");

        await StepOff(p, pad);
        await StepOnto(p, pad);
        await World.Until(() => match.IsActive(p.Player.PlayerUID), "the pad to fire once walked onto", 200);
    }

    [AtlasScenario]
    public async Task Teammates_Should_NotDamageEachOther()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(5));
        ITestPlayer a = await JoinSurvivor("tdmffa");
        ITestPlayer b = await JoinSurvivor("tdmffb");
        await StepOnto(a, pad);
        await StepOff(a, pad);
        await StepOnto(b, pad);
        await World.Until(() => match.IsActive(b.Player.PlayerUID), "both on team 1", 200);

        var health = b.Player.Entity.GetBehavior<EntityBehaviorHealth>();
        float before = health.Health;
        b.Player.Entity.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Player, SourceEntity = a.Player.Entity, CauseEntity = a.Player.Entity,
            Type = EnumDamageType.SlashingAttack
        }, 5f);
        await World.Ticks(5);
        Assert.True(health.Health >= before - 0.01f, $"teammate hit for {before - health.Health}");
    }

    [AtlasScenario]
    public async Task Match_Should_KeepRunning_When_EveryoneLeaves()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(6));
        ITestPlayer p = await JoinSurvivor("tdmalone");
        await StepOnto(p, pad);
        await World.Until(() => match.IsActive(p.Player.PlayerUID), "in", 200);

        await p.ExecuteCommand("/canlive leave");
        await World.Ticks(90);
        Assert.True(match.EventState == EventState.Running, "the open match ended once empty");
        Assert.True(Manager.FindOpenMatch(OpenTdm) == match, "the open match was replaced");
    }

    [AtlasScenario]
    public async Task Player_Should_GetInventoryAndHomeBack_When_RejoiningAfterDisconnect()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(7));
        ITestPlayer p = await JoinSurvivor("tdmdrop");
        await p.GiveItem(Own, 2);
        await StepOnto(p, pad);
        await World.Until(() => match.IsActive(p.Player.PlayerUID), "in", 200);
        string uid = p.Player.PlayerUID;

        await Disconnect(p);
        Assert.False(match.IsParticipant(uid), "a disconnected player stays on the open match's roster");

        ITestPlayer back = await Rejoin("tdmdrop");
        await World.Until(() => CountOf(back.Player, Own) == 2, "own items back on rejoin", 300);
        Assert.True(Settled(back.Player), "the snapshot was not settled");
        await World.Until(() => !ReturnPosMarker.TryRead(back.Player, World.Api, out _), "sent home", 300);
    }

    [AtlasScenario]
    public async Task Join_Should_BeRefused_When_HitByAPlayerRecently()
    {
        var (match, _) = await StartOpenMatch();
        UseMutator(OpenTdm, EventMutators.Loadout, "Spearman");
        var pad = AddPad(OpenTdm, JoinPads.JoinAction, 0, await LobbyFloor(8));
        ITestPlayer p = await JoinSurvivor("tdmtagged");
        ITestPlayer foe = await JoinSurvivor("tdmtagger");

        p.Player.Entity.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Player, SourceEntity = foe.Player.Entity, CauseEntity = foe.Player.Entity,
            Type = EnumDamageType.SlashingAttack
        }, 1f);
        await StepOnto(p, pad);
        Assert.False(match.IsParticipant(p.Player.PlayerUID), "entered right after being hit");
    }

    [AtlasScenario]
    public async Task StopAll_Should_KeepOpenMatchDown()
    {
        var (match, _) = await StartOpenMatch();
        ITestPlayer admin = await World.JoinPlayer("tdmstopper");
        await admin.ExecuteCommand("/eventadmin stop");
        await World.Until(() => match.EventState == EventState.Finished, "the open match to stop", 200);

        // The keeper looks every 15 s; give it two goes.
        await World.Ticks(30 * 35);
        Assert.True(Manager.FindOpenMatch(OpenTdm) == null, "the keeper brought a stopped open match back");
    }

    [AtlasScenario]
    public async Task TeamArena_Should_BeIncomplete_When_FewerSpawnsThanTeams()
    {
        await World.Ticks(1);
        // One spawn for two teams: the place is incomplete, so nothing starts on it at all.
        int area = NewArea();
        var place = AddArena("live-onespawn-" + area, AreaCentre(area), 12, PlaceTags.OpenDeathmatch, 1);
        Assert.True(PlaceSetupCheck.Gaps(place).Count > 0, "a one-spawn team arena is reported as complete");
    }
}
