using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    public class CommandsImplementations
    {
        ICoreServerAPI sapi;
        public Dictionary<string, EventPlace> EventPlaceInProgress;
        DefaultEventPlacesManager placeManager;
        CANEventManager canEventManager;
        // Shared with the admin panel's packet handlers. Read through the manager rather than cached,
        // so command registration order can't capture a null.
        PlaceConfigService placeConfig => canEventManager?.placeConfig;
        public CommandsImplementations(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            EventPlaceInProgress = new Dictionary<string, EventPlace>();
            placeManager = this.sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager.eventPlacesManager;
            canEventManager = this.sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
        }
        public TextCommandResult NewEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            if (!EventPlaceInProgress.TryGetValue(player.PlayerUID, out var currentPlace))
            {
                currentPlace = new EventPlace();
                EventPlaceInProgress.Add(player.PlayerUID, currentPlace);
            }

            return TextCommandResult.Success(Lang.Get("canevents:place-created"));
        }
        public TextCommandResult ArenaAddSpawnPoint(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            var pos = player.Entity.Pos;

            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {

                eventPlace.spawnPositions.Add(pos.AsBlockPos);
                return TextCommandResult.Success(Lang.Get("canevents:spawn-set"));
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }

        public TextCommandResult DeleteEventPlace(TextCommandCallingArgs args)
        {
            long val = (long)args.Parsers[0].GetValue();

            if (placeManager.DeletePlace(val))
            {
                return TextCommandResult.Success(Lang.Get("canevents:place-deleted"));
            }
            return TextCommandResult.Error(Lang.Get("canevents:place-delete-failed"));
        }
        public TextCommandResult SetNameEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            string val = (string)args.Parsers[0].GetValue();

            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {
                eventPlace.Name = val;
                return TextCommandResult.Success(Lang.Get("canevents:name-set"));
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }
        public TextCommandResult Mark1(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {
                eventPlace.BoundsMin = player.Entity.Pos.AsBlockPos;
                return TextCommandResult.Success("Mark 1 set at " + eventPlace.BoundsMin);
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }

        public TextCommandResult Mark2(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {
                if (eventPlace.BoundsMin == null)
                {
                    return TextCommandResult.Error("Set mark1 first.");
                }

                eventPlace.SetBounds(eventPlace.BoundsMin, player.Entity.Pos.AsBlockPos);
                eventPlace.GenerateEdgeSpawns();

                int sizeX = eventPlace.BoundsMax.X - eventPlace.BoundsMin.X + 1;
                int sizeZ = eventPlace.BoundsMax.Z - eventPlace.BoundsMin.Z + 1;

                return TextCommandResult.Success(string.Format(
                    "Mark 2 set. Area: {0}x{1}. 2 spawn points auto-generated. Use /canep save to save.",
                    sizeX, sizeZ));
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }

        public TextCommandResult SaveEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {
                if (!placeManager.AddPlace(eventPlace))
                    return TextCommandResult.Error(Lang.Get("canevents:place-save-failed"));

                // Drop the in-progress entry so the next `/canep new` starts fresh: reusing it made
                // AddPlace reassign the Id and produce duplicate files on disk.
                EventPlaceInProgress.Remove(player.PlayerUID);
                return TextCommandResult.Success(Lang.Get("canevents:place-saved"));
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }
        public TextCommandResult AddPlaceType(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            string val = PlaceTags.Normalize((string)args.Parsers[0].GetValue());
            if (string.IsNullOrEmpty(val))
            {
                return TextCommandResult.Error(Lang.Get("canevents:place-type-invalid"));
            }
            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {
                eventPlace.AddType(val);
                return TextCommandResult.Success(Lang.Get("canevents:type-added"));
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }
        public TextCommandResult RegPlayer(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            string eventCode = null;
            if (args.Parsers != null && args.Parsers.Count > 0)
            {
                eventCode = (string)args.Parsers[0].GetValue();
            }

            return Wrap(canEventManager.Queueing.Join(player, eventCode));
        }

        public TextCommandResult LeaveQueue(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            // No code: out of every queue at once, not just the first one.
            return Wrap(canEventManager.Queueing.Leave(player));
        }

        /// <summary>
        /// Escape hatch out of spectator mode, for a player left stuck after a crash or an unusual
        /// teardown. Refuses while they are still playing, so it cannot exit a live match.
        /// </summary>
        public TextCommandResult LeaveSpectator(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");

            if (canEventManager != null && canEventManager.IsInLiveEvent(player.PlayerUID))
                return TextCommandResult.Error(Lang.Get("canevents:spectator-still-playing"));

            var spectator = canEventManager?.SpectatorManager;
            if (spectator == null) return TextCommandResult.Error("Spectator manager not available.");

            spectator.RemoveSpectator(player);
            return TextCommandResult.Success(Lang.Get("canevents:spectator-left"));
        }

        public TextCommandResult ShowStats(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            var leaderboard = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager?.Leaderboard;

            if (leaderboard == null)
            {
                return TextCommandResult.Error("Leaderboard not available.");
            }

            var allStats = leaderboard.GetAllStats();
            if (!allStats.TryGetValue(player.PlayerUID, out var stats))
            {
                return TextCommandResult.Success("No stats recorded yet.");
            }
            string msg = string.Format(
                "=== Your Stats ===\nWins: {0} | Losses: {1} | Win rate: {5:F0}%\nKills: {2} | Deaths: {3} | K/D: {6:F2}\nEvents Played: {4}",
                stats.Wins, stats.Losses, stats.Kills, stats.Deaths, stats.EventsPlayed, stats.WinRate, stats.KDRatio);

            // MMR is per-event (a combined rating is meaningless), so list it by event code.
            var byEvent = leaderboard.GetPlayerStatsByEvent(player.PlayerUID)
                .Where(kv => kv.Value.EventsPlayed > 0)
                .OrderByDescending(kv => kv.Value.Rating)
                .ToList();
            if (byEvent.Count > 0)
            {
                msg += "\n=== MMR ===";
                foreach (var kv in byEvent)
                    msg += string.Format("\n{0}: {1}", kv.Key, kv.Value.Rating);
            }

            return TextCommandResult.Success(msg);
        }

        // Admin commands

        public TextCommandResult AdminForceStart(TextCommandCallingArgs args)
        {
            string eventCode = (string)args.Parsers[0].GetValue();
            var scheduler = sapi.ModLoader.GetModSystem<canevents>()?.eventScheduler;

            if (scheduler == null)
            {
                return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));
            }

            if (scheduler.ForceStart(eventCode, out var reason))
            {
                return TextCommandResult.Success(Lang.Get("canevents:force-started", eventCode));
            }
            return TextCommandResult.Error(reason);
        }

        public TextCommandResult AdminStopAll(TextCommandCallingArgs args)
        {
            var toStop = canEventManager.CurrentlyRunningEvents.ToList();
            foreach (var re in toStop)
            {
                canEventManager.FinishEvent(re);
            }
            return TextCommandResult.Success(Lang.Get("canevents:stopped-all", toStop.Count));
        }

        public TextCommandResult AdminScheduleOn(TextCommandCallingArgs args)
        {
            var scheduler = sapi.ModLoader.GetModSystem<canevents>()?.eventScheduler;
            if (scheduler == null) return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));

            scheduler.SetEnabled(true);
            return TextCommandResult.Success(Lang.Get("canevents:schedule-enabled"));
        }

        public TextCommandResult AdminScheduleOff(TextCommandCallingArgs args)
        {
            var scheduler = sapi.ModLoader.GetModSystem<canevents>()?.eventScheduler;
            if (scheduler == null) return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));

            scheduler.SetEnabled(false);
            return TextCommandResult.Success(Lang.Get("canevents:schedule-disabled"));
        }

        public TextCommandResult AdminScheduleInterval(TextCommandCallingArgs args)
        {
            int minutes = (int)args.Parsers[0].GetValue();
            var scheduler = sapi.ModLoader.GetModSystem<canevents>()?.eventScheduler;
            if (scheduler == null) return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));

            scheduler.SetInterval(minutes);
            return TextCommandResult.Success(Lang.Get("canevents:schedule-interval", minutes));
        }

        // Queue blacklist

        public TextCommandResult AdminBlacklistAdd(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var data, out var name, out var error)) return error;

            bool changed = canEventManager.SetPlayerBlacklisted(data.PlayerUID, true);
            return TextCommandResult.Success(changed
                ? "Blacklisted '" + name + "' from the event queue."
                : "'" + name + "' is already blacklisted.");
        }

        public TextCommandResult AdminBlacklistRemove(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var data, out var name, out var error)) return error;

            bool changed = canEventManager.SetPlayerBlacklisted(data.PlayerUID, false);
            return TextCommandResult.Success(changed
                ? "Removed '" + name + "' from the event-queue blacklist."
                : "'" + name + "' was not blacklisted.");
        }

        public TextCommandResult AdminBlacklistList(TextCommandCallingArgs args)
        {
            var uids = canEventManager.BlacklistedUids.ToList();
            if (uids.Count == 0) return TextCommandResult.Success("Queue blacklist is empty.");

            var names = uids.Select(uid =>
                sapi.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? uid);
            return TextCommandResult.Success("Blacklisted players: " + string.Join(", ", names));
        }

        // Reward rate-cap

        public TextCommandResult AdminRateCapShow(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var data, out var name, out var error)) return error;

            var limiter = canEventManager.RewardDistributor?.RateLimiter;
            if (limiter == null) return TextCommandResult.Success("Reward rate-cap is disabled.");

            string status = limiter.DescribeAvailability(data.PlayerUID, UnixTime.Now);
            return TextCommandResult.Success(string.Format("'{0}': {1}", name, status));
        }

        public TextCommandResult AdminRateCapReset(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var data, out var name, out var error)) return error;

            var limiter = canEventManager.RewardDistributor?.RateLimiter;
            if (limiter == null) return TextCommandResult.Success("Reward rate-cap is disabled.");

            limiter.Reset(data.PlayerUID);
            return TextCommandResult.Success("Reset reward rate-cap for '" + name + "'.");
        }

        // Template editor

        private static TextCommandResult Wrap((bool ok, string message) r)
        {
            return r.ok ? TextCommandResult.Success(r.message) : TextCommandResult.Error(r.message);
        }

        /// <summary>Same, for the shared place-edit service (see PlaceConfigService).</summary>
        private static TextCommandResult Wrap(PlaceEditResult r)
        {
            return r.Ok ? TextCommandResult.Success(r.Message) : TextCommandResult.Error(r.Message);
        }

        /// <summary>The block the caller is standing on, for every "set this at my feet" command.</summary>
        private static BlockPos FeetBlock(IServerPlayer player) => player.Entity.Pos.AsBlockPos.DownCopy();

        /// <summary>
        /// The shape every per-behavior /canep edit has: take the block under the caller, find the
        /// place containing it, run the edit, relay the result.
        /// </summary>
        // System.Func spelled out: Vintagestory.API.Common declares a Func<> too.
        private TextCommandResult EditAtFeet(TextCommandCallingArgs args,
            System.Func<EventPlace, BlockPos, PlaceEditResult> edit)
        {
            var player = args.Caller.Player as IServerPlayer;
            var pos = FeetBlock(player);
            if (!TryGetPlaceUnderFeet(player, pos, out var target, out var noPlace)) return noPlace;

            return Wrap(edit(target, pos));
        }

        /// <summary>Same, for the read-only "list what is configured here" commands.</summary>
        private TextCommandResult DescribeAtFeet(TextCommandCallingArgs args, System.Func<EventPlace, string> describe)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (!TryGetPlaceUnderFeet(player, FeetBlock(player), out var target, out var noPlace)) return noPlace;

            return TextCommandResult.Success(describe(target));
        }

        public TextCommandResult TplEdit(TextCommandCallingArgs args)
        {
            var p = args.Caller.Player as IServerPlayer;
            return Wrap(canEventManager.TemplateEditor.Start(p, (string)args.Parsers[0].GetValue()));
        }

        public TextCommandResult TplAddSp(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.AddSpawn(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplClearSp(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.ClearSpawns(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplList(TextCommandCallingArgs args)
        {
            var p = args.Caller.Player as IServerPlayer;
            var session = canEventManager.TemplateEditor.GetSession(p);
            if (session == null) return TextCommandResult.Error("No edit session.");
            if (session.SpawnOffsets.Count == 0) return TextCommandResult.Success("No spawn offsets in session.");
            var sb = new System.Text.StringBuilder();
            sb.Append("Spawn offsets for '").Append(session.TemplateCode).Append("':\n");
            for (int i = 0; i < session.SpawnOffsets.Count; i++)
            {
                var o = session.SpawnOffsets[i];
                sb.AppendFormat("  [{0}] {1},{2},{3}\n", i, o.X, o.Y, o.Z);
            }
            return TextCommandResult.Success(sb.ToString());
        }

        public TextCommandResult TplSave(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.Save(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplCancel(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.Cancel(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplFinish(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.Finish(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult ParkourSetCheckpoint(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            int index = (int)args.Parsers[0].GetValue();
            return PlaceParkourCheckpoint(player, index, isFinish: false);
        }

        public TextCommandResult ParkourSetFinish(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            return PlaceParkourCheckpoint(player, index: 0, isFinish: true);
        }

        public TextCommandResult ParkourEditCheckpoint(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            int index = (int)args.Parsers[0].GetValue();
            // Find the checkpoint entry by the position under the player's feet.
            var pos = player.Entity.Pos.AsBlockPos.DownCopy();
            if (!TryFindStoredCheckpoint(player, pos, out var entry))
            {
                return TextCommandResult.Error("No checkpoint marked under your feet. Stand on the marked block.");
            }
            bool isFinish = entry.Value<bool>("IsFinish");
            string places = WriteCheckpointToOwningPlace(player, pos, index, isFinish);
            return TextCommandResult.Success(string.Format("{0} at {1} set to index={2}.{3}",
                isFinish ? "Finish" : "Checkpoint", pos, index,
                string.IsNullOrEmpty(places) ? "" : " " + places));
        }

        public TextCommandResult ParkourRemoveCheckpoint(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            var pos = player.Entity.Pos.AsBlockPos.DownCopy();
            string places = RemoveCheckpointFromOwningPlace(player, pos);
            if (string.IsNullOrEmpty(places))
            {
                return TextCommandResult.Error("No marked checkpoint at " + pos + ".");
            }
            return TextCommandResult.Success("Removed checkpoint at " + pos + ". " + places);
        }

        private string RemoveCheckpointFromOwningPlace(IServerPlayer player, BlockPos pos)
        {
            var target = FindPlaceContaining(player, pos);
            if (target == null || !target.TryGetBehaviorData(BehaviorKeys.Parkour, out var pdata)) return "";
            if (pdata["Checkpoints"] is not JArray arr) return "";
            int removed = 0;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is not JObject e) continue;
                if (JsonPos.Matches(e, pos))
                {
                    arr.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0) PersistIfSaved(target);
            return removed > 0
                ? string.Format("Removed from place '{0}' (id {1}).", target.Name, target.Id)
                : "";
        }

        /// <summary>
        /// Resolves a player name argument to their stored data, returning false with the error result
        /// already filled in.
        /// </summary>
        private bool TryResolvePlayerArg(TextCommandCallingArgs args, int parserIndex,
            out IServerPlayerData data, out string name, out TextCommandResult error)
        {
            name = (string)args.Parsers[parserIndex].GetValue();
            data = sapi.PlayerData.GetPlayerDataByLastKnownName(name);
            error = data == null
                ? TextCommandResult.Error(Lang.Get("canevents:player-not-found", name))
                : null;
            return data != null;
        }

        /// <summary>
        /// The place the caller is standing in, or false with the error already filled in.
        /// </summary>
        private bool TryGetPlaceUnderFeet(IServerPlayer player, BlockPos pos,
            out EventPlace target, out TextCommandResult error)
        {
            target = FindPlaceContaining(player, pos);
            error = target == null
                ? TextCommandResult.Error(Lang.Get("canevents:no-place-at-position"))
                : null;
            return target != null;
        }

        /// <summary>
        /// Looks up a stored checkpoint entry at the given position: in-progress place first, then the
        /// saved ones. Callers re-resolve the owning place through FindPlaceContaining.
        /// </summary>
        private bool TryFindStoredCheckpoint(IServerPlayer player, BlockPos pos, out JObject entry)
        {
            entry = null;
            var candidates = new List<EventPlace>();
            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var inProgress) && inProgress.HasBounds)
                candidates.Add(inProgress);
            foreach (var pl in placeManager.EventPlaces)
                if (pl.HasBounds) candidates.Add(pl);

            foreach (var pl in candidates)
            {
                if (!pl.TryGetBehaviorData(BehaviorKeys.Parkour, out var pdata)) continue;
                if (pdata["Checkpoints"] is not JArray arr) continue;
                foreach (var t in arr)
                {
                    if (t is not JObject e) continue;
                    if (JsonPos.Matches(e, pos))
                    {
                        entry = e;
                        return true;
                    }
                }
            }
            return false;
        }

        public TextCommandResult ParkourSetLives(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            int lives = (int)args.Parsers[0].GetValue();
            if (lives < 1) return TextCommandResult.Error("Lives must be >= 1.");

            var pos = player.Entity.Pos.AsBlockPos;
            var target = FindPlaceContaining(player, pos);
            if (target == null) return TextCommandResult.Error(Lang.Get("canevents:stand-in-bounds"));

            var pdata = target.GetOrCreateBehaviorData(BehaviorKeys.Parkour);
            pdata["Lives"] = lives;
            PersistIfSaved(target);
            return TextCommandResult.Success(string.Format("Lives for '{0}' set to {1}.", target.Name, lives));
        }

        public TextCommandResult ParkourListCheckpoints(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            var pos = player.Entity.Pos.AsBlockPos;
            var target = FindPlaceContaining(player, pos);
            if (target == null)
                return TextCommandResult.Error(Lang.Get("canevents:stand-in-bounds"));
            if (!target.TryGetBehaviorData(BehaviorKeys.Parkour, out var pdata)
                || pdata["Checkpoints"] is not JArray arr
                || arr.Count == 0)
                return TextCommandResult.Success("No checkpoints in '" + target.Name + "'.");

            var entries = new List<(int index, bool isFinish, int x, int y, int z)>();
            foreach (var t in arr)
            {
                if (t is not JObject e) continue;
                entries.Add((e.Value<int>("Index"), e.Value<bool>("IsFinish"),
                    e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z")));
            }
            entries.Sort((a, b) =>
            {
                if (a.isFinish != b.isFinish) return a.isFinish ? 1 : -1;
                return a.index.CompareTo(b.index);
            });
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format("Place '{0}' has {1} checkpoint(s):", target.Name, entries.Count));
            foreach (var f in entries)
            {
                sb.AppendLine(string.Format("  {0} - ({1}, {2}, {3})",
                    f.isFinish ? "FINISH" : ("idx=" + f.index), f.x, f.y, f.z));
            }
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private TextCommandResult PlaceParkourCheckpoint(IServerPlayer player, int index, bool isFinish)
        {
            // The block UNDER the player's feet. The BE is spawned on it at event start; nothing is
            // placed in the world here.
            var pos = player.Entity.Pos.AsBlockPos.DownCopy();
            var ba = sapi.World.BlockAccessor;
            int floorBlockId = ba.GetBlockId(pos);
            if (floorBlockId == 0)
            {
                return TextCommandResult.Error("No solid block under your feet - stand on the surface where the checkpoint should be.");
            }

            string places = WriteCheckpointToOwningPlace(player, pos, index, isFinish);

            return TextCommandResult.Success(string.Format("{0} marked at {1}.{2}",
                isFinish ? "Finish" : ("Checkpoint idx=" + index), pos,
                string.IsNullOrEmpty(places) ? "" : " " + places));
        }

        /// <summary>
        /// Writes the checkpoint into the matching place's Parkour behavior data, preferring the
        /// in-progress place over a saved one when both bounds contain the position.
        /// </summary>
        private string WriteCheckpointToOwningPlace(IServerPlayer player, BlockPos pos, int index, bool isFinish)
        {
            var target = FindPlaceContaining(player, pos);
            if (target == null) return "(no place bounds contain this position - block placed in world only)";

            var pdata = target.GetOrCreateBehaviorData(BehaviorKeys.Parkour);
            var arr = pdata["Checkpoints"] as JArray;
            if (arr == null)
            {
                arr = new JArray();
                pdata["Checkpoints"] = arr;
            }

            // Replace any existing entry with the same (index,isFinish) OR same position
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                var entry = arr[i] as JObject;
                if (entry == null) continue;
                bool sameKey = entry.Value<bool>("IsFinish") == isFinish
                    && (isFinish || entry.Value<int>("Index") == index);
                if (sameKey || JsonPos.Matches(entry, pos)) arr.RemoveAt(i);
            }

            arr.Add(new JObject
            {
                ["X"] = pos.X,
                ["Y"] = pos.Y,
                ["Z"] = pos.Z,
                ["Dim"] = pos.dimension,
                ["Index"] = index,
                ["IsFinish"] = isFinish,
            });

            // Persist only saved places. In-progress places are written when /canep save runs.
            if (placeManager.EventPlaces.Contains(target))
            {
                placeManager.SavePlace(target);
            }
            return string.Format("Saved to place '{0}' (id {1}).", target.Name, target.Id);
        }

        // Match only by X/Z: courses are often vertical while admins mark bounds at floor level, so
        // checking Y here would turn valid checkpoints into orphans.
        private static bool BoundsContain(EventPlace place, BlockPos pos)
        {
            return pos.X >= place.BoundsMin.X && pos.X <= place.BoundsMax.X
                && pos.Z >= place.BoundsMin.Z && pos.Z <= place.BoundsMax.Z;
        }

        // Hunger Games place setup

        public TextCommandResult HgZoneSet(TextCommandCallingArgs args)
        {
            float radius = Convert.ToSingle(args.Parsers[0].GetValue());
            return EditAtFeet(args, (target, pos) => placeConfig.SetHungerGamesZone(target, pos, radius));
        }

        // King of the Hill place setup

        public TextCommandResult KothHillSet(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            float radius = Convert.ToSingle(args.Parsers[0].GetValue());
            if (radius <= 0f) return TextCommandResult.Error("Radius must be positive.");

            // Height is optional; an omitted OptionalFloat yields 0f rather than null, so treat any
            // non-positive height as unset and fall back to the documented default of 5.
            var rawHeight = args.Parsers[1].GetValue();
            float height = rawHeight == null ? 5f : Convert.ToSingle(rawHeight);

            var pos = FeetBlock(player);
            var target = FindPlaceContaining(player, pos);
            if (target == null)
            {
                // Places without bounds are invisible to this lookup - the usual cause, so say so.
                int withBounds = placeManager.EventPlaces.Count(p => p.HasBounds);
                int total = placeManager.EventPlaces.Count;
                return TextCommandResult.Error(string.Format(
                    "No place bounds contain your position ({0},{1},{2}). "
                    + "{3} of {4} places have bounds at all. "
                    + "A place needs bounds (Arenas tab → Mark 1 / Mark 2, or /canep mark1 + mark2 + save) "
                    + "before a hill can be attached to it.",
                    pos.X, pos.Y, pos.Z, withBounds, total));
            }

            return Wrap(placeConfig.SetKothHill(target, pos, radius, height));
        }

        // Capture the Flag place setup

        /// <summary>
        /// Sets team N's flag post to the block under the caller. Addressed by team index, not by
        /// position, so re-running it for a team moves that post rather than adding a second one.
        /// </summary>
        public TextCommandResult CtfPostSet(TextCommandCallingArgs args)
        {
            int team = (int)args.Parsers[0].GetValue();
            return EditAtFeet(args, (target, pos) => placeConfig.SetCtfPost(target, pos, team));
        }

        public TextCommandResult CtfPostList(TextCommandCallingArgs args)
            => DescribeAtFeet(args, target => placeConfig.DescribeCtfPosts(target));

        public TextCommandResult CtfPostClear(TextCommandCallingArgs args)
            => EditAtFeet(args, (target, _) => placeConfig.ClearCtfPosts(target));

        public TextCommandResult HgLootAdd(TextCommandCallingArgs args)
        {
            string tier = args.Parsers[0].GetValue() as string;
            return EditAtFeet(args, (target, pos) => placeConfig.AddHungerGamesLoot(target, pos, tier));
        }

        public TextCommandResult HgLootClear(TextCommandCallingArgs args)
            => EditAtFeet(args, (target, _) => placeConfig.ClearHungerGamesLoot(target));

        public TextCommandResult HgLootList(TextCommandCallingArgs args)
            => DescribeAtFeet(args, target => placeConfig.DescribeHungerGamesLoot(target));

        /// <summary>
        /// The place whose footprint contains this position: the caller's unfinished place first, then
        /// the saved ones. Every /canep command decides what it acts on through this one definition.
        /// </summary>
        private EventPlace FindPlaceContaining(IServerPlayer player, BlockPos pos)
        {
            if (EventPlaceInProgress.TryGetValue(player.PlayerUID, out var inProgress)
                && inProgress.HasBounds && BoundsContain(inProgress, pos))
            {
                return inProgress;
            }
            foreach (var pl in placeManager.EventPlaces)
            {
                if (!pl.HasBounds) continue;
                if (BoundsContain(pl, pos)) return pl;
            }
            return null;
        }

        /// <summary>Writes the place to disk, but only once it is a saved one.</summary>
        private void PersistIfSaved(EventPlace target)
        {
            if (placeManager.EventPlaces.Contains(target)) placeManager.SavePlace(target);
        }
    }
}
