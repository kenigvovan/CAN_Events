using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// R21: an ArenaProtection event's arenas are a claim nobody but an admin in creative builds in or
// breaks, during a match and between matches. Asked the way the game asks before any edit
// (Claims.TestAccess), since Atlas players have no break action of their own.
[Trait("Category", "E2E")]
public class ArenaProtectionScenarios : EventsScenarioBase
{
    private const string Duel = "1x1";
    private const int Half = 12;

    private EnumWorldAccessResponse Access(ITestPlayer p, BlockPos pos)
        => World.Api.World.Claims.TestAccess(p.Player, pos, EnumBlockAccessFlags.BuildOrBreak);

    private (BlockPos centre, BlockPos inside, BlockPos outside) NewDuelArena(string name)
    {
        int area = NewArea();
        var centre = AreaCentre(area);
        AddArena(name + "-" + area, centre, Half, PlaceTags.Arena1x1, 2);
        Mod.ArenaProtection.Invalidate();
        return (centre, centre.AddCopy(3, 1, 3), centre.AddCopy(Half + 6, 1, 0));
    }

    private async Task<ITestPlayer> Survivor(string name)
    {
        var p = await World.JoinPlayer(name);
        p.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
        p.Player.BroadcastPlayerData();
        await World.Ticks(2);
        return p;
    }

    // Changes a setting for this scenario only; the edit lands in the event JSON.
    private void UseSetting(string key, string value)
    {
        string before = Manager.EventSettings(Duel).First(s => s.Setting.Key == key).Value;
        var (ok, msg) = Manager.SetEventSetting(Duel, "ArenaProtection", key, value);
        Assert.True(ok, $"could not set {key}={value}: {msg}");
        AddCleanup(() => Manager.SetEventSetting(Duel, "ArenaProtection", key, before));
    }

    [AtlasScenario]
    public async Task Arena_Should_BeClosedToEdits_BetweenMatches_ButNotAroundIt()
    {
        var (_, inside, outside) = NewDuelArena("guard");
        ITestPlayer p = await Survivor("guardplayer");

        Assert.True(Access(p, inside) == EnumWorldAccessResponse.DeniedByMod,
            $"a player may edit inside an idle protected arena: {Access(p, inside)}");
        Assert.True(Access(p, outside) == EnumWorldAccessResponse.Granted,
            $"the guard reaches past the arena bounds: {Access(p, outside)}");
    }

    [AtlasScenario]
    public async Task AdminInCreative_Should_BuildInTheArena_AdminInSurvival_Should_Not()
    {
        var (_, inside, _) = NewDuelArena("guardadmin");
        ITestPlayer admin = await Survivor("guardadmin");
        Assert.True(admin.Player.HasPrivilege(Vintagestory.API.Server.Privilege.controlserver), "precondition: the test player is an admin");

        Assert.True(Access(admin, inside) == EnumWorldAccessResponse.DeniedByMod,
            "an admin in survival got past the arena guard; only creative is exempt");

        admin.Player.WorldData.CurrentGameMode = EnumGameMode.Creative;
        admin.Player.BroadcastPlayerData();
        await World.Ticks(2);
        Assert.True(Access(admin, inside) == EnumWorldAccessResponse.Granted,
            $"an admin in creative cannot fix the arena: {Access(admin, inside)}");
    }

    [AtlasScenario]
    public async Task Arena_Should_StayClosed_DuringTheMatch_And_AfterIt()
    {
        UseCountdown(1);
        var (_, inside, _) = NewDuelArena("guardmatch");
        ITestPlayer a = await Survivor("guarda");
        ITestPlayer b = await Survivor("guardb");
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b }) Assert.True(Manager.AddToQueue(duel, p.Player).ok, p.Player.PlayerName + " could not queue");
        var match = await StartQueuedMatch(a, b);

        Assert.True(Access(a, inside) == EnumWorldAccessResponse.DeniedByMod, "a fighter may edit the arena mid-match");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => !Manager.RunningEventsSnapshot().Contains(match), "the match to end", 300);
        Assert.True(Access(a, inside) == EnumWorldAccessResponse.DeniedByMod, "the arena opened up once the match ended");
    }

    [AtlasScenario]
    public async Task ProtectArenaOff_Should_OpenTheArena()
    {
        var (_, inside, _) = NewDuelArena("guardoff");
        ITestPlayer p = await Survivor("guardoffplayer");
        Assert.True(Access(p, inside) == EnumWorldAccessResponse.DeniedByMod, "precondition: protected while on");

        UseSetting("protectArena", "false");
        Assert.True(Access(p, inside) == EnumWorldAccessResponse.Granted,
            $"still closed after protectArena=false: {Access(p, inside)}");
    }

    [AtlasScenario]
    public async Task BetweenMatchesOff_Should_GuardOnlyWhileItsMatchRuns()
    {
        UseCountdown(1);
        UseSetting("protectBetweenMatches", "false");
        var (_, inside, _) = NewDuelArena("guardlive");
        ITestPlayer a = await Survivor("guardlivea");
        ITestPlayer b = await Survivor("guardliveb");

        Assert.True(Access(a, inside) == EnumWorldAccessResponse.Granted,
            $"closed between matches although protectBetweenMatches=false: {Access(a, inside)}");

        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b }) Assert.True(Manager.AddToQueue(duel, p.Player).ok, p.Player.PlayerName + " could not queue");
        var match = await StartQueuedMatch(a, b);
        Assert.True(Access(a, inside) == EnumWorldAccessResponse.DeniedByMod, "open during its own match");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => !Manager.RunningEventsSnapshot().Contains(match), "the match to end", 300);
        Assert.True(Access(a, inside) == EnumWorldAccessResponse.Granted, "still closed after the match with protectBetweenMatches=false");
    }
}
