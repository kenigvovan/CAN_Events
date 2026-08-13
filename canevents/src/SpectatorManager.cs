using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canevents.src.Compat;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src
{
    /// <summary>Why a player is sitting in the spectator seat. The seat itself is identical either
    /// way; the difference is who is allowed to leave it and when it ends by itself.</summary>
    public enum SpectatorRole
    {
        /// <summary>A participant who was knocked out of a match they were playing. The seat lasts
        /// until that match finishes.</summary>
        Eliminated,

        /// <summary>An admin watching a match they are not in. They may leave at any time, and are
        /// teleported back to where they came from when they do.</summary>
        Observer
    }

    public class SpectatorManager
    {
        private const string ModDataKey = "canevents_original_gamemode";
        private const string ReturnPosKey = "canevents_spectator_returnpos";
        private const double MaxDistanceFromArena = 50.0;

        /// <summary>Slack added to an observer's leash beyond the arena's own footprint, for a
        /// fighter who stepped just outside it.</summary>
        private const double ObserverLeashMargin = 40.0;

        /// <summary>How close to a participant still counts as "filming them" rather than "wandered
        /// off", whatever the distance to the arena's middle says.</summary>
        private const double ObserverCameraRange = 60.0;

        // How far above the arena an observer is dropped in. Higher than an eliminated player's perch:
        // an observer arrives to look at the whole fight, not at the spot they died on.
        private const int ObserverHoverHeight = 18;
        private const int EliminatedHoverHeight = 10;

        private ICoreServerAPI sapi;

        // The seat a player currently occupies; only while online. Keyed by playerUID.
        private Dictionary<string, SpectatorSeat> seats = new Dictionary<string, SpectatorSeat>();

        private class SpectatorSeat
        {
            public BlockPos Anchor;
            public long EventId;
            public SpectatorRole Role;

            /// <summary>How far from the anchor this seat may roam. An eliminated player is kept on a
            /// short leash above the arena they died in; an observer needs the run of the whole
            /// footprint, or the broadcaster camera is yanked back the moment it jumps to a fighter
            /// on the far side of a Hunger Games map.</summary>
            public double Leash;
        }

        public SpectatorManager(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        public void MakeSpectator(IServerPlayer player, RunningEvent runningEvent)
        {
            SaveOriginalWorldData(player);

            // Delayed so revive runs after the engine's death broadcast (we are inside
            // TriggerPlayerDeath). A player respawn or an event finish may get here first.
            sapi.Event.RegisterCallback((float dt) =>
            {
                // FinishEvent already handled everything
                if (runningEvent.EventState == EventState.Finished) return;

                // OnRespawn already handled it
                if (seats.ContainsKey(player.PlayerUID)) return;

                ApplySpectatorState(player, runningEvent);
            }, 100);
        }

        /// <summary>Revive if dead, set gamemode, teleport above the arena. Called from the delayed
        /// callback or from OnRespawn, whichever comes first.</summary>
        public void ApplySpectatorState(IServerPlayer player, RunningEvent runningEvent)
        {
            if (seats.ContainsKey(player.PlayerUID)) return;

            SaveOriginalWorldData(player);
            Seat(player, runningEvent, SpectatorRole.Eliminated, EliminatedHoverHeight);
            player.SendMessage(0, Lang.Get("canevents:spectator-enter"), EnumChatType.Notification);
        }

        /// <summary>
        /// Seats a player who is NOT in the match as an observer of it, remembering where they stood so
        /// leaving puts them back. Returns false when they are already spectating something.
        /// </summary>
        public bool MakeObserver(IServerPlayer player, RunningEvent runningEvent)
        {
            if (player?.Entity == null || runningEvent == null) return false;
            if (seats.ContainsKey(player.PlayerUID)) return false;

            SaveOriginalWorldData(player);
            SaveReturnPosition(player);
            Seat(player, runningEvent, SpectatorRole.Observer, ObserverHoverHeight);
            return true;
        }

        // The seat itself: strip whatever state the body is in, switch to Spectator, park above the
        // arena and remember the anchor the bounds check pulls them back to.
        private void Seat(IServerPlayer player, RunningEvent runningEvent, SpectatorRole role, int hover)
        {
            if (!player.Entity.Alive) player.Entity.Revive();

            // A knock-down mod (Downed) leaves the body crawling, immobilized and possibly mounted on
            // a drag seat. Nothing in that mod will undo any of it for a player we pull aside, and the
            // engine skips its own body-metrics update in Spectator mode - so clear it here.
            DownedCompat.Clear(player);

            player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
            // Free flight is what vanilla's /gamemode spectator turns on, and without it the
            // "spectator" just hangs in place: the engine skips gravity in Spectator mode, so they
            // float where they were put and cannot move. Being mounted (a drag seat, a boat) forces
            // FreeMove back off behind our back, hence the unmount in DownedCompat.Clear.
            player.WorldData.FreeMove = true;

            // No-clip only for an observer, who is filming and needs to get through the arena wall.
            // An eliminated player must NOT have it: their team is still fighting, and a dead
            // teammate drifting through walls to read the enemy's position is a voice-chat wallhack.
            // They still fly, they just cannot leave the room.
            player.WorldData.NoClip = role == SpectatorRole.Observer;
            player.BroadcastPlayerData();

            var anchor = AnchorOf(runningEvent);
            if (anchor != null)
            {
                seats[player.PlayerUID] = new SpectatorSeat
                {
                    Anchor = anchor.Copy(),
                    EventId = runningEvent.Id,
                    Role = role,
                    Leash = LeashFor(runningEvent, role)
                };
                player.Entity.TeleportToDouble(anchor.X + 0.5, anchor.Y + hover, anchor.Z + 0.5);
            }
            else
            {
                // No arena to hover over yet: still record the seat, or nothing would ever release
                // the gamemode. The bounds check skips a seat with no anchor.
                seats[player.PlayerUID] = new SpectatorSeat { EventId = runningEvent.Id, Role = role };
            }
        }

        // How far this seat may go. An eliminated player stays on the default leash - the point is to
        // keep them looking at the fight, not wandering off to fetch gear. An observer gets the whole
        // arena plus a margin, measured from its footprint: they are meant to follow the fighters,
        // and a Hunger Games map is far wider than any fixed number would allow.
        private static double LeashFor(RunningEvent runningEvent, SpectatorRole role)
        {
            if (role != SpectatorRole.Observer) return MaxDistanceFromArena;

            var place = runningEvent?.Place;
            if (place == null || !place.HasBounds || place.BoundsMin == null || place.BoundsMax == null)
                return MaxDistanceFromArena;

            // Half-diagonal reaches every corner from the middle; the margin covers a fighter who
            // stepped just outside, so the camera does not fight the leash at the edge.
            double dx = place.BoundsMax.X - place.BoundsMin.X;
            double dy = place.BoundsMax.Y - place.BoundsMin.Y;
            double dz = place.BoundsMax.Z - place.BoundsMin.Z;
            double halfDiagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2.0;

            return Math.Max(MaxDistanceFromArena, halfDiagonal + ObserverLeashMargin);
        }

        // Where a spectator hovers: the middle of the arena footprint when there is one, else the
        // first spawn point (which is all an unbounded place has).
        private static BlockPos AnchorOf(RunningEvent runningEvent)
        {
            var place = runningEvent?.Place;
            if (place == null) return null;

            if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                return new BlockPos(
                    (place.BoundsMin.X + place.BoundsMax.X) / 2,
                    Math.Max(place.BoundsMin.Y, place.BoundsMax.Y),
                    (place.BoundsMin.Z + place.BoundsMax.Z) / 2,
                    place.BoundsMin.dimension);
            }

            if (place.spawnPositions != null && place.spawnPositions.Count > 0)
                return place.spawnPositions[0];

            return null;
        }

        /// <summary>Restore gamemode for an online player.</summary>
        public void RemoveSpectator(IServerPlayer player)
        {
            if (player == null) return;

            bool wasObserver = seats.TryGetValue(player.PlayerUID, out var seat)
                && seat.Role == SpectatorRole.Observer;

            RestoreOriginalWorldData(player);
            seats.Remove(player.PlayerUID);

            // An observer walked into this on purpose from somewhere else in the world, so put them
            // back there. An eliminated participant is handled by their match's TPThereAndBack.
            if (wasObserver) ReturnToSavedPosition(player);
        }

        /// <summary>Remove all spectators for a finishing event; offline ones are fixed by
        /// RestoreOnReconnect.</summary>
        public void RemoveAllSpectators(RunningEvent runningEvent)
        {
            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player != null)
                {
                    RemoveSpectator(player);
                }
            }

            // Observers are not on the roster, so they need their own pass. Snapshot first:
            // RemoveSpectator mutates the dictionary.
            foreach (var uid in seats.Where(kv => kv.Value.EventId == runningEvent.Id
                                                  && kv.Value.Role == SpectatorRole.Observer)
                                     .Select(kv => kv.Key).ToList())
            {
                if (sapi.World.PlayerByUid(uid) is IServerPlayer observer)
                {
                    observer.SendMessage(0, Lang.Get("canevents:spectator-match-over"), EnumChatType.Notification);
                    RemoveSpectator(observer);
                }
                else seats.Remove(uid);
            }
        }

        /// <summary>On join, restore the original gamemode if they left as a spectator.</summary>
        public void RestoreOnReconnect(IServerPlayer player)
        {
            if (player?.GetModdata(ModDataKey) == null) return;

            RestoreOriginalWorldData(player);
            // Deferred: the engine is still placing the player at this point, and an immediate
            // teleport is overwritten by its own join-time positioning.
            ReturnToSavedPosition(player, delayMs: 300);

            player.SendMessage(0, Lang.Get("canevents:spectator-gamemode-restored"), EnumChatType.Notification);
            sapi.World.Logger.Notification("SpectatorManager: Restored gamemode for {0} on reconnect", player.PlayerName);
        }

        /// <summary>
        /// Pulls spectators of this match back if they wandered off. Called from the match's own tick,
        /// and only looks at seats belonging to it - with two matches live, a manager-wide sweep ran
        /// twice per tick and each pass re-checked the other match's seats for nothing.
        /// </summary>
        public void EnforceSpectatorBounds(RunningEvent runningEvent)
        {
            if (runningEvent == null) return;

            foreach (var kvp in seats.ToList())
            {
                if (kvp.Value.EventId != runningEvent.Id) continue;

                var player = sapi.World.PlayerByUid(kvp.Key) as IServerPlayer;
                if (player?.Entity == null) continue;

                var anchor = kvp.Value.Anchor;
                if (anchor == null) continue;

                int hover = kvp.Value.Role == SpectatorRole.Observer
                    ? ObserverHoverHeight : EliminatedHoverHeight;

                double leash = kvp.Value.Leash > 0 ? kvp.Value.Leash : MaxDistanceFromArena;

                double dx = player.Entity.Pos.X - (anchor.X + 0.5);
                double dy = player.Entity.Pos.Y - (anchor.Y + hover);
                double dz = player.Entity.Pos.Z - (anchor.Z + 0.5);
                double distSq = dx * dx + dy * dy + dz * dz;

                if (distSq <= leash * leash) continue;

                // Filming a fighter who ran to the far edge is the whole job, and a leash measured
                // from the arena's middle cannot know how far that is - especially on a place with no
                // recorded footprint, where the leash is just the default. Being next to a
                // participant counts as being where you belong.
                if (kvp.Value.Role == SpectatorRole.Observer && IsNearAParticipant(runningEvent, player))
                    continue;

                player.Entity.TeleportToDouble(anchor.X + 0.5, anchor.Y + hover, anchor.Z + 0.5);
                player.SendMessage(0, Lang.Get("canevents:spectator-too-far"), EnumChatType.Notification);
            }
        }

        // Is this observer within camera range of anyone in the match? Bots count: a bots-only match
        // is still something to film.
        private static bool IsNearAParticipant(RunningEvent runningEvent, IServerPlayer observer)
        {
            var pos = observer.Entity.Pos;

            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                var body = runningEvent.BodyOf(uid);
                if (body == null) continue;

                double dx = pos.X - body.Pos.X;
                double dy = pos.Y - body.Pos.Y;
                double dz = pos.Z - body.Pos.Z;
                if (dx * dx + dy * dy + dz * dz <= ObserverCameraRange * ObserverCameraRange)
                    return true;
            }
            return false;
        }

        /// <summary>Drop the in-memory seat only; ModData stays for RestoreOnReconnect. A stale
        /// seat would make a mid-event reconnect look "already spectating" and skip the restore.</summary>
        public void OnSpectatorDisconnect(string playerUid)
        {
            seats.Remove(playerUid);
        }

        public bool IsSpectating(string playerUid)
        {
            return seats.ContainsKey(playerUid);
        }

        /// <summary>Is this player watching a match they are not playing in?</summary>
        public bool IsObserving(string playerUid)
            => seats.TryGetValue(playerUid, out var seat) && seat.Role == SpectatorRole.Observer;

        /// <summary>The match this player is seated for, or 0.</summary>
        public long SeatedEventId(string playerUid)
            => seats.TryGetValue(playerUid, out var seat) ? seat.EventId : 0;

        /// <summary>Every player currently observing the given match.</summary>
        public IEnumerable<string> ObserversOf(long eventId)
        {
            foreach (var kv in seats)
                if (kv.Value.EventId == eventId && kv.Value.Role == SpectatorRole.Observer)
                    yield return kv.Key;
        }

        // ModData, not memory: the original world data must survive a disconnect or crash. Written
        // once - a second seat (respawn, re-entry) must not overwrite it with the spectator values.
        private void SaveOriginalWorldData(IServerPlayer player)
        {
            if (player == null || player.GetModdata(ModDataKey) != null) return;

            // "<gamemode>|<freemove>|<noclip>". The single-number form older builds wrote is still
            // read back (see RestoreOriginalWorldData), so an upgrade mid-match loses nothing.
            string packed = string.Format("{0}|{1}|{2}",
                (int)player.WorldData.CurrentGameMode,
                player.WorldData.FreeMove ? 1 : 0,
                player.WorldData.NoClip ? 1 : 0);
            player.SetModdata(ModDataKey, Encoding.UTF8.GetBytes(packed));
        }

        private void RestoreOriginalWorldData(IServerPlayer player)
        {
            byte[] savedBytes = player?.GetModdata(ModDataKey);
            if (savedBytes == null) return;

            var parts = Encoding.UTF8.GetString(savedBytes).Split('|');

            player.WorldData.CurrentGameMode = parts.Length > 0 && int.TryParse(parts[0], out int modeInt)
                ? (EnumGameMode)modeInt
                : EnumGameMode.Survival;

            // Free flight and no-clip were turned on for the seat; anything not recorded (old data)
            // goes back to off, which is what a survival player had.
            player.WorldData.FreeMove = parts.Length > 1 && parts[1] == "1";
            player.WorldData.NoClip = parts.Length > 2 && parts[2] == "1";

            player.BroadcastPlayerData();
            player.SetModdata(ModDataKey, null);
        }

        // Where an observer stood before they started watching. ModData so a disconnect mid-watch
        // still gets them home on the next login.
        private void SaveReturnPosition(IServerPlayer player)
        {
            var pos = player.Entity?.Pos;
            if (pos == null) return;

            var tree = new TreeAttribute();
            tree.SetDouble("x", pos.X);
            tree.SetDouble("y", pos.Y);
            tree.SetDouble("z", pos.Z);
            player.SetModdata(ReturnPosKey, tree.ToBytes());
        }

        private void ReturnToSavedPosition(IServerPlayer player, int delayMs = 0)
        {
            byte[] data = player?.GetModdata(ReturnPosKey);
            if (data == null || data.Length == 0) return;

            double x, y, z;
            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);
                x = tree.GetDouble("x");
                y = tree.GetDouble("y");
                z = tree.GetDouble("z");
            }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("SpectatorManager: unreadable return position for {0}: {1}",
                    player.PlayerName, e.Message);
                player.RemoveModdata(ReturnPosKey);
                return;
            }

            // Cleared first: a teleport that throws must not leave a marker that fires again on every
            // later login and yanks the player back mid-game.
            player.RemoveModdata(ReturnPosKey);

            if (delayMs <= 0)
            {
                player.Entity?.TeleportToDouble(x, y, z);
                return;
            }

            string uid = player.PlayerUID;
            sapi.Event.RegisterCallback(_ =>
            {
                (sapi.World.PlayerByUid(uid) as IServerPlayer)?.Entity?.TeleportToDouble(x, y, z);
            }, delayMs);
        }
    }
}
