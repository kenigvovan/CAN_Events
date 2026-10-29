using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.scenarios.Infrastructure;

// Helpers for open matches (TDM walked into through pads) and battle (queue, waiting room, rounds).
public abstract class LiveScenarioBase : EventsScenarioBase
{
    protected const string OpenTdm = "tdm_open";
    protected const string Battle = "battle";
    // Stacks (Atlas refuses an oversized stack) and is in no kit, reward or fee.
    protected const string Own = "game:flint";

    protected static double DistanceXZ(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // Entry refuses anything but survival; test players may join in another mode.
    protected async Task<ITestPlayer> JoinSurvivor(string name)
    {
        ITestPlayer p = await World.JoinPlayer(name);
        p.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
        p.Player.BroadcastPlayerData();
        await World.Ticks(2);
        return p;
    }

    // An arena with a spawn per team and the open match of `code` running on it; stopped afterwards.
    protected async Task<(RunningEvent match, EventPlace place)> StartOpenMatch(string code = OpenTdm, int spawns = 2)
    {
        int area = NewArea();
        var place = AddArena("live-" + area, AreaCentre(area), 12, PlaceTags.OpenDeathmatch, spawns);
        var ev = Manager.FindEventByCode(code);
        Assert.True(ev != null, $"event '{code}' is required (assets/canevents/config/events)");

        var (ok, msg) = Manager.StartOpenMatch(ev!);
        Assert.True(ok, "open match not started: " + msg);
        AddCleanup(() => Manager.StopOpenMatch(code));

        Manager.AttemptFindLocationForEvent();
        var match = Manager.FindOpenMatch(code);
        Assert.True(match != null, "the open match is not listed");
        await World.Until(() => match!.EventState == EventState.Running, "the open match to run", 600);
        return (match!, place);
    }

    // A zone of size x size floor blocks at `floor` (its north-west corner), removed afterwards.
    protected JoinPad AddPad(string? code, string action, int team, BlockPos floor, int size = 3, long placeId = 0)
    {
        var pad = Mod.JoinPads.Add(code, action, team, floor, floor.AddCopy(size - 1, 0, size - 1), placeId);
        AddCleanup(() => Mod.JoinPads.Remove(pad.Id));
        return pad;
    }

    protected static BlockPos PadCentre(JoinPad pad)
        => new BlockPos((pad.MinX + pad.MaxX) / 2, pad.MinY + 1, (pad.MinZ + pad.MaxZ) / 2, pad.Dim);

    // Pads fire only for a player who walked on (moved < 4 blocks between scans): stand two blocks
    // off, let a scan see that, then step on.
    protected async Task StepOnto(ITestPlayer player, JoinPad pad)
    {
        var centre = PadCentre(pad);
        await player.TeleportTo(new BlockPos(pad.MinX - 2, centre.Y, centre.Z, pad.Dim));
        await World.Ticks(15);
        await player.TeleportTo(centre);
        await World.Ticks(15);
    }

    // Off the pad again (a walk, so it doesn't look like a teleport either).
    protected async Task StepOff(ITestPlayer player, JoinPad pad)
    {
        await player.TeleportTo(new BlockPos(pad.MinX - 2, pad.MinY + 1, (pad.MinZ + pad.MaxZ) / 2, pad.Dim));
        await World.Ticks(15);
    }

    // Atlas can't send client packets, so the class answer goes straight into the server's handler.
    protected void ConfirmLoadout(ITestPlayer player, RunningEvent match, string set)
    {
        var handler = Manager.NetworkHandler;
        var method = typeof(ServerNetworkHandler).GetMethod("OnLoadoutPickResponse",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, "ServerNetworkHandler.OnLoadoutPickResponse not found (renamed?)");
        method!.Invoke(handler, new object[]
        {
            player.Player,
            new LoadoutPickResponseMsg { EventId = match.Id, EventCode = match.Event.Code, SetName = set }
        });
    }

    protected IReadOnlyList<T> PacketsOf<T>(ITestPlayer player) => player.Client.Packets<T>(CanEventsProtocol.MainChannelName);

    // Battle arena with a waiting room 40 blocks north of it; short lobby and rounds for tests.
    protected EventPlace AddBattleArena(int area, int lobbySeconds = 10, int winsToWin = 2, int roundSeconds = 30)
    {
        BlockPos centre = AreaCentre(area);
        var place = AddArena("battle-" + area, centre, 12, PlaceTags.Battle, 2);
        var room = centre.AddCopy(0, -1, -40);
        var set = Manager.placeConfig.SetWaitingRoom(place, room);
        Assert.True(set.Ok, "waiting room not set: " + set.Message);

        UseEventAttributes<WaitingRoomEB>(Battle, a => a["lobbySeconds"] = lobbySeconds);
        UseEventAttributes<BattleRoundsEB>(Battle, a =>
        {
            a["winsToWin"] = winsToWin;
            a["roundTimeLimitSeconds"] = roundSeconds;
            a["intermissionSeconds"] = 3;
        });
        return place;
    }

    protected static BlockPos WaitingRoomOf(EventPlace place)
    {
        Assert.True(PlaceConfigService.TryGetWaitingRoom(place, out var room), "place has no waiting room");
        return room;
    }

    // Queues everyone for battle, passes the ready check and stops in the waiting room (lobby countdown).
    protected async Task<RunningEvent> StartBattleToLobby(params ITestPlayer[] players)
    {
        var ev = Manager.FindEventByCode(Battle);
        Assert.True(ev != null, $"event '{Battle}' is required (assets/canevents/config/events)");
        foreach (var p in players)
        {
            var (ok, msg) = Manager.AddToQueue(ev!, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }

        Manager.AttemptToComposeTeams();
        var match = MatchOf(players[0]);
        Assert.True(match != null, "no battle was assembled");
        if (match!.EventState == EventState.AwaitingReady)
            foreach (var p in players) Manager.SetMatchReady(p.Player, match.Id, true);

        Manager.AttemptFindLocationForEvent();
        await World.Until(() => match.EventState == EventState.LocationFound && match.CountdownSeconds > 0,
            "the battle to reach its waiting room (state " + match.EventState + ")", 600);
        return match;
    }

    protected async Task UntilRunning(RunningEvent match, int ticks = 900)
        => await World.Until(() => match.EventState == EventState.Running, "the battle to start", ticks);

    // Round 1 opens after a held countdown, like every round; nothing can be hurt before it.
    protected async Task UntilRoundLive(RunningEvent match, int ticks = 900)
    {
        await UntilRunning(match, ticks);
        var rounds = match.GetBehavior<BattleRoundsEB>();
        Assert.True(rounds != null, "the match has no BattleRounds behavior");
        await World.Until(() => rounds!.RoundLive, "the round to start after its countdown", ticks);
    }

    protected static void Kill(ITestPlayer victim, ITestPlayer? by = null)
        => victim.Player.Entity.ReceiveDamage(new DamageSource
        {
            Source = by == null ? EnumDamageSource.Void : EnumDamageSource.Player,
            SourceEntity = by?.Player.Entity,
            CauseEntity = by?.Player.Entity,
            Type = EnumDamageType.Injury
        }, 1000f);

    protected static bool Settled(IServerPlayer player) => player.GetModdata(PlayerInventoryBackup.ModDataKey) == null;
}
