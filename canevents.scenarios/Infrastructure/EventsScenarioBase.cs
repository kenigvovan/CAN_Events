using Atlas.Api;
using Atlas.XUnit;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using EventsMod = global::canevents.src.canevents;

namespace canevents.scenarios.Infrastructure;

// One server per class and the mod's state is not rolled back between scenarios,
// so every scenario builds in its own area of the map and undoes its config changes.
public abstract class EventsScenarioBase : AtlasScenarioBase, IDisposable
{
    private const int AreaSpacingBlocks = 200;
    private static int lastArea = -1;

    private readonly List<Action> cleanups = new();
    private ICoreServerAPI? cleanupApi;

    // Cleanups of the previous scenario, waiting for the game thread (see Dispose).
    private static readonly object CleanupLock = new();
    private static List<Action>? queuedCleanups;

    // Every helper reaches the mod through here, so the previous scenario is undone before this one changes anything.
    protected EventsMod Mod
    {
        get
        {
            RunQueuedCleanups();
            return World.Api.ModLoader.GetModSystem<EventsMod>();
        }
    }

    private static void RunQueuedCleanups()
    {
        List<Action>? pending;
        lock (CleanupLock)
        {
            pending = queuedCleanups;
            queuedCleanups = null;
        }
        if (pending == null) return;
        // The last scenario of a class may leave cleanups for a host that has shut down since.
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            try { pending[i](); }
            catch (Exception) { }
        }
    }
    protected CANEventManager Manager => Mod.CANEventManager;

    protected static int NewArea() => Interlocked.Increment(ref lastArea);

    // Area 0 is one step east of the world spawn, which stays free as the players' "home".
    protected BlockPos AreaCentre(int area) => World.Spawn.AddCopy((area + 1) * AreaSpacingBlocks, 0, 0);

    // Kept in memory only: no file in ModConfig, so later boots don't see it.
    protected EventPlace AddPlace(string name, BlockPos centre, int halfSize, params string[] tags)
    {
        var place = new EventPlace { Name = name };
        foreach (var tag in tags) place.AddType(tag);
        place.SetBounds(centre.AddCopy(-halfSize, -4, -halfSize), centre.AddCopy(halfSize, 16, halfSize));
        Assert.True(Manager.eventPlacesManager.AddPlace(place, skipJsonExport: true), "place '" + name + "' was not added");
        DisableAfterScenario(place);
        return place;
    }

    // Places outlive the scenario on the shared class host; a later scenario's arena search must not pick them.
    protected void DisableAfterScenario(EventPlace place) => AddCleanup(() => place.Enabled = false);

    // Spawns on a line through the centre, spawn i = team i.
    protected EventPlace AddArena(string name, BlockPos centre, int halfSize, string tag, int spawns)
    {
        var place = AddPlace(name, centre, halfSize, tag);
        for (int i = 0; i < spawns; i++)
            place.spawnPositions.Add(centre.AddCopy(-halfSize / 2 + i * halfSize / Math.Max(1, spawns - 1), 0, 0));
        return place;
    }

    // Undone in reverse order when the scenario ends.
    protected void AddCleanup(Action undo)
    {
        cleanupApi ??= World.Api;
        cleanups.Add(undo);
    }

    // Matches clone their behaviors with the template's attribute object, so swapping it on the
    // template changes every match assembled afterwards; the original comes back after the scenario.
    protected void UseEventAttributes<TBehavior>(string eventCode, Action<JObject> change)
        where TBehavior : CANEventBehavior
    {
        var ev = Manager.FindEventByCode(eventCode);
        Assert.True(ev != null, $"event '{eventCode}' is required");
        var behavior = ev!.GetBehavior<TBehavior>();
        Assert.True(behavior != null, $"event '{eventCode}' has no {typeof(TBehavior).Name}");
        JsonObject original = behavior!.Attributes;
        var attrs = (JObject)original.Token.DeepClone();
        change(attrs);
        behavior.Attributes = new JsonObject(attrs);
        AddCleanup(() => behavior.Attributes = original);
    }

    // RunningEvent keeps a reference to the event template, so its mutators apply to matches assembled afterwards.
    protected void UseMutator(string eventCode, string key, string? value)
    {
        var ev = Manager.FindEventByCode(eventCode);
        Assert.True(ev != null, $"event '{eventCode}' is required");
        bool had = ev!.Mutators.TryGetValue(key, out var previous);
        ev.Mutators[key] = value!;
        AddCleanup(() =>
        {
            if (had) ev.Mutators[key] = previous!;
            else ev.Mutators.Remove(key);
        });
    }

    protected void UseCountdown(int seconds)
    {
        int previous = Mod.config.countdownSeconds;
        Mod.config.countdownSeconds = seconds;
        AddCleanup(() => Mod.config.countdownSeconds = previous);
    }

    // First air block above the terrain; loads the column if nobody is near it.
    protected async Task<BlockPos> SurfaceAt(int x, int z, int dim = 0)
    {
        var ba = World.Api.World.BlockAccessor;
        if (ba.GetRainMapHeightAt(x, z) <= 0)
            World.Api.WorldManager.LoadChunkColumnPriority((int)Math.Floor(x / 32.0), (int)Math.Floor(z / 32.0));
        await World.Until(() => ba.GetRainMapHeightAt(x, z) > 0, $"column {x},{z} to load", 600);
        return new BlockPos(x, ba.GetRainMapHeightAt(x, z) + 1, z, dim);
    }

    protected RunningEvent MatchOf(ITestPlayer player)
        => Manager.RunningEventsSnapshot().SingleOrDefault(re => re.IsParticipant(player.Player.PlayerUID));

    // Players must already be queued. Assembles, confirms the ready check for everyone,
    // finds the arena and waits for the match to run (countdown included).
    protected async Task<RunningEvent> StartQueuedMatch(params ITestPlayer[] players)
    {
        Manager.AttemptToComposeTeams();
        var match = MatchOf(players[0]);
        Assert.True(match != null, "no match was assembled for " + players[0].Player.PlayerName);
        foreach (var p in players)
            Assert.True(match.IsParticipant(p.Player.PlayerUID), p.Player.PlayerName + " is not in the assembled match");

        if (match.EventState == EventState.AwaitingReady)
            foreach (var p in players) Manager.SetMatchReady(p.Player, match.Id, true);

        Manager.AttemptFindLocationForEvent();
        await World.Until(() => match.EventState == EventState.Running,
            "match " + match.Event.Code + " to start (state " + match.EventState + ")", 1200);
        return match;
    }

    // Running comes before the arena teleport lands; a hit while it is still pending is lost, and on a
    // busy test machine that window is seconds long. Not just !Teleporting: EventTeleport preloads the
    // target chunks first, and until it hops the player stands at home with Teleporting still false.
    protected async Task UntilLanded(RunningEvent match, params ITestPlayer[] players)
        => await World.Until(() => players.All(p => p.Player.Entity != null && !p.Player.Entity.Teleporting
                                                   && PlaceBounds.Contains(match.Place, p.Position)),
            "everyone to land in the arena", 400);

    protected async Task Disconnect(ITestPlayer player)
    {
        player.Player.Disconnect();
        await World.Until(() => !player.IsConnected, player.Player.PlayerName + " to disconnect", 100);
        // Atlas frees the joined name a few ticks after the engine's teardown.
        await World.Ticks(10);
    }

    // Same name, same name-derived uid: the server sees the same account coming back.
    protected Task<ITestPlayer> Rejoin(string name) => World.JoinPlayer(name);

    protected static int CountOf(IServerPlayer player, string code)
    {
        int total = 0;
        foreach (var inv in player.InventoryManager.Inventories.Values)
        {
            if (inv.ClassName == "creative") continue;
            foreach (var slot in inv)
                if (slot.Itemstack?.Collectible?.Code?.ToString() == code) total += slot.Itemstack.StackSize;
        }
        return total;
    }

    // xUnit disposes off the game thread, and the cleanups touch blocks, events and mod state. They wait in
    // queuedCleanups for the game thread: the next scenario's first use of Mod, or the server's next tick.
    public virtual void Dispose()
    {
        try { cleanupApi ??= World.Api; } catch (Exception) { }
        if (cleanupApi == null) return;

        // The class's server holds 16 clients and joined players stay on, so a class with many
        // scenarios ran out of slots ("connection queue is full"). First in the list = runs last,
        // after everything else of this scenario is undone. Same idea as CAN_claims' DisconnectEarlierPlayers.
        var api = cleanupApi;
        cleanups.Insert(0, () =>
        {
            foreach (var p in api.World.AllOnlinePlayers.OfType<IServerPlayer>().ToList())
            {
                // Or the teleport watch logs each as a disconnect-on-arrival error.
                global::canevents.src.EventTeleport.ExpectDisconnect(api, p.PlayerUID);
                p.Disconnect();
            }
        });

        lock (CleanupLock)
        {
            queuedCleanups ??= new List<Action>();
            queuedCleanups.AddRange(cleanups);
        }
        cleanups.Clear();
        cleanupApi.Event.EnqueueMainThreadTask(RunQueuedCleanups, "canevents-scenario-cleanup");
    }
}
