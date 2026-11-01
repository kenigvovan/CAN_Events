using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canevents.src.Compat;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src
{
    public enum SpectatorRole
    {
        // Knocked out participant, seated until the match ends.
        Eliminated,

        // Admin watching a match they're not in; can leave anytime, gets teleported back.
        Observer
    }

    public class SpectatorManager
    {
        public const string ModDataKey = "canevents_original_gamemode";
        private const string ReturnPosKey = "canevents_spectator_returnpos";
        private const double MaxDistanceFromArena = 50.0;

        // Extra leash beyond the arena bounds for observers.
        private const double ObserverLeashMargin = 40.0;

        // An observer this close to any participant is never pulled back.
        private const double ObserverCameraRange = 60.0;

        private const int ObserverHoverHeight = 18;
        private const int EliminatedHoverHeight = 10;

        // How far past the arena bounds an eliminated player may fly.
        private const double EliminatedBoundsMargin = 8.0;

        private ICoreServerAPI sapi;

        // Online players only, by uid.
        private Dictionary<string, SpectatorSeat> seats = new Dictionary<string, SpectatorSeat>();

        // MakeSpectator's delayed seating not yet run.
        private readonly HashSet<string> pendingSeats = new HashSet<string>();

        private class SpectatorSeat
        {
            public BlockPos Anchor;
            public long EventId;
            public SpectatorRole Role;

            // Max distance from Anchor; observers get the whole arena (see LeashFor).
            public double Leash;

            // Arena bounds, when it has them: the leash is then this box, not a sphere around Anchor,
            // which sits above the top and would keep spectators off a deep arena's floor.
            public BlockPos BoundsMin, BoundsMax;
        }

        public SpectatorManager(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        public void MakeSpectator(IServerPlayer player, RunningEvent runningEvent)
        {
            SaveOriginalWorldData(player);
            pendingSeats.Add(player.PlayerUID);

            // Delayed so revive runs after the engine's death broadcast (we're inside
            // TriggerPlayerDeath). Respawn or FinishEvent may get here first.
            sapi.Event.RegisterCallback((float dt) =>
            {
                // Gone: a RemoveSpectator in between (next round already started) called it off.
                if (!pendingSeats.Remove(player.PlayerUID)) return;
                if (runningEvent.EventState == EventState.Finished) return;

                if (seats.ContainsKey(player.PlayerUID)) return;

                ApplySpectatorState(player, runningEvent);
            }, 100);
        }

        // Called from the delayed callback or OnRespawn, whichever comes first.
        public void ApplySpectatorState(IServerPlayer player, RunningEvent runningEvent)
        {
            if (seats.ContainsKey(player.PlayerUID)) return;

            SaveOriginalWorldData(player);
            Seat(player, runningEvent, SpectatorRole.Eliminated, EliminatedHoverHeight);
            player.SendMessage(0, Lang.Get("canevents:spectator-enter"), EnumChatType.Notification);
        }

        public bool MakeObserver(IServerPlayer player, RunningEvent runningEvent)
        {
            if (player?.Entity == null || runningEvent == null) return false;
            if (seats.ContainsKey(player.PlayerUID)) return false;

            SaveOriginalWorldData(player);
            SaveReturnPosition(player);
            Seat(player, runningEvent, SpectatorRole.Observer, ObserverHoverHeight);
            return true;
        }

        private void Seat(IServerPlayer player, RunningEvent runningEvent, SpectatorRole role, int hover)
        {
            if (!player.Entity.Alive) player.Entity.Revive();

            // Downed won't clear its crawl/immobile/drag-seat state for us, and the engine skips
            // body-metrics updates in Spectator mode.
            DownedCompat.Clear(player);
            // Out of the fight: no aura on teammates below, no pet, totem or trap fighting on.
            if (role == SpectatorRole.Eliminated) RpgClassesCompat.ResetCombat(player.Entity);

            player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
            // Without FreeMove a spectator can't move at all (no gravity in Spectator). Mounting resets it.
            player.WorldData.FreeMove = true;

            // No noclip for eliminated players, otherwise they can scout enemies through walls for their team.
            player.WorldData.NoClip = role == SpectatorRole.Observer;
            player.BroadcastPlayerData();

            var anchor = AnchorOf(runningEvent);
            if (anchor != null)
            {
                var place = runningEvent.Place;
                bool bounded = place.HasBounds && place.BoundsMin != null && place.BoundsMax != null;
                seats[player.PlayerUID] = new SpectatorSeat
                {
                    Anchor = anchor.Copy(),
                    EventId = runningEvent.Id,
                    Role = role,
                    Leash = LeashFor(runningEvent, role),
                    BoundsMin = bounded ? place.BoundsMin.Copy() : null,
                    BoundsMax = bounded ? place.BoundsMax.Copy() : null
                };
                EventTeleport.Send(sapi, player, anchor, 0.5, hover, 0.5,
                    role == SpectatorRole.Observer ? "observer seat" : "eliminated, spectator seat");
            }
            else
            {
                // Still record the seat so the gamemode gets restored later.
                seats[player.PlayerUID] = new SpectatorSeat { EventId = runningEvent.Id, Role = role };
            }
        }

        // Observers get half the arena diagonal plus a margin (big maps like Hunger Games).
        private static double LeashFor(RunningEvent runningEvent, SpectatorRole role)
        {
            if (role != SpectatorRole.Observer) return MaxDistanceFromArena;

            var place = runningEvent?.Place;
            if (place == null || !place.HasBounds || place.BoundsMin == null || place.BoundsMax == null)
                return MaxDistanceFromArena;

            double dx = place.BoundsMax.X - place.BoundsMin.X;
            double dy = place.BoundsMax.Y - place.BoundsMin.Y;
            double dz = place.BoundsMax.Z - place.BoundsMin.Z;
            double halfDiagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2.0;

            return Math.Max(MaxDistanceFromArena, halfDiagonal + ObserverLeashMargin);
        }

        // Arena center if it has bounds, else first spawn point.
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

        public void RemoveSpectator(IServerPlayer player)
        {
            if (player == null) return;
            pendingSeats.Remove(player.PlayerUID);

            bool wasObserver = seats.TryGetValue(player.PlayerUID, out var seat)
                && seat.Role == SpectatorRole.Observer;

            RestoreOriginalWorldData(player);
            seats.Remove(player.PlayerUID);

            // Eliminated players are returned by the match's TPThereAndBack.
            if (wasObserver) ReturnToSavedPosition(player);
        }

        // Offline ones are handled by RestoreOnReconnect.
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

            // Observers aren't in ParticipantsBefore.
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

        public void RestoreOnReconnect(IServerPlayer player)
        {
            if (player?.GetModdata(ModDataKey) == null) return;

            RestoreOriginalWorldData(player);
            // Delayed, otherwise the engine's join positioning overrides the teleport.
            ReturnToSavedPosition(player, delayMs: 300);

            player.SendMessage(0, Lang.Get("canevents:spectator-gamemode-restored"), EnumChatType.Notification);
            sapi.World.Logger.Notification("SpectatorManager: Restored gamemode for {0} on reconnect", player.PlayerName);
        }

        // Called from each match's own tick, so only that match's seats are checked.
        public void EnforceSpectatorBounds(RunningEvent runningEvent)
        {
            if (runningEvent == null) return;

            foreach (var kvp in seats.ToList())
            {
                if (kvp.Value.EventId != runningEvent.Id) continue;

                var player = sapi.World.PlayerByUid(kvp.Key) as IServerPlayer;
                if (player?.Entity == null) continue;

                // Skip while a teleport is pending: re-teleporting resets CurrentChunkSentRadius and
                // the client can end up never finishing chunk loading.
                if (player.Entity.Teleporting) continue;

                var anchor = kvp.Value.Anchor;
                if (anchor == null) continue;

                int hover = kvp.Value.Role == SpectatorRole.Observer
                    ? ObserverHoverHeight : EliminatedHoverHeight;

                if (kvp.Value.BoundsMin != null)
                {
                    double margin = kvp.Value.Role == SpectatorRole.Observer ? ObserverLeashMargin : EliminatedBoundsMargin;
                    if (InsideBounds(player.Entity.Pos, kvp.Value, margin, hover)) continue;
                }
                else
                {
                    double leash = kvp.Value.Leash > 0 ? kvp.Value.Leash : MaxDistanceFromArena;
                    double dx = player.Entity.Pos.X - (anchor.X + 0.5);
                    double dy = player.Entity.Pos.Y - (anchor.Y + hover);
                    double dz = player.Entity.Pos.Z - (anchor.Z + 0.5);
                    if (dx * dx + dy * dy + dz * dz <= leash * leash) continue;
                }

                // Observer near a fighter is fine even past the leash.
                if (kvp.Value.Role == SpectatorRole.Observer && IsNearAParticipant(runningEvent, player))
                    continue;

                EventTeleport.Send(sapi, player, anchor, 0.5, hover, 0.5, "spectator pulled back");
                player.SendMessage(0, Lang.Get("canevents:spectator-too-far"), EnumChatType.Notification);
            }
        }

        // The top gets the hover height on top of the margin, so the seat itself is always inside.
        private static bool InsideBounds(EntityPos pos, SpectatorSeat seat, double margin, int hover)
        {
            var min = seat.BoundsMin;
            var max = seat.BoundsMax;
            return pos.Dimension == min.dimension
                   && pos.X >= Math.Min(min.X, max.X) - margin && pos.X <= Math.Max(min.X, max.X) + 1 + margin
                   && pos.Z >= Math.Min(min.Z, max.Z) - margin && pos.Z <= Math.Max(min.Z, max.Z) + 1 + margin
                   && pos.Y >= Math.Min(min.Y, max.Y) - margin && pos.Y <= Math.Max(min.Y, max.Y) + hover + margin;
        }

        // Bots count too.
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

        // ModData stays for RestoreOnReconnect; a stale seat would make it skip the restore.
        public void OnSpectatorDisconnect(string playerUid)
        {
            seats.Remove(playerUid);
            pendingSeats.Remove(playerUid);
        }

        public bool IsSpectating(string playerUid)
        {
            return seats.ContainsKey(playerUid);
        }

        public long SeatedEventId(string playerUid)
            => seats.TryGetValue(playerUid, out var seat) ? seat.EventId : 0;

        // In ModData to survive disconnects/crashes. Written once so a re-seat doesn't save spectator values.
        private void SaveOriginalWorldData(IServerPlayer player)
        {
            if (player == null || player.GetModdata(ModDataKey) != null) return;

            // "<gamemode>|<freemove>|<noclip>"; old builds stored just the gamemode number.
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

            // Missing in old data -> off.
            player.WorldData.FreeMove = parts.Length > 1 && parts[1] == "1";
            player.WorldData.NoClip = parts.Length > 2 && parts[2] == "1";

            player.BroadcastPlayerData();
            player.SetModdata(ModDataKey, null);
        }

        // ModData so it survives a disconnect.
        private void SaveReturnPosition(IServerPlayer player)
        {
            var pos = player.Entity?.Pos;
            if (pos == null) return;

            var tree = new TreeAttribute();
            tree.SetDouble("x", pos.X);
            tree.SetDouble("y", pos.Y);
            tree.SetDouble("z", pos.Z);
            // Pos.Y is dimension-local, so store the dimension too.
            tree.SetInt("dim", pos.Dimension);
            player.SetModdata(ReturnPosKey, tree.ToBytes());
        }

        private void ReturnToSavedPosition(IServerPlayer player, int delayMs = 0)
        {
            byte[] data = player?.GetModdata(ReturnPosKey);
            if (data == null || data.Length == 0) return;

            double x, y, z;
            int dim;
            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);
                x = tree.GetDouble("x");
                y = tree.GetDouble("y");
                z = tree.GetDouble("z");
                dim = tree.GetInt("dim");
            }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("SpectatorManager: unreadable return position for {0}: {1}",
                    player.PlayerName, e.Message);
                player.RemoveModdata(ReturnPosKey);
                return;
            }

            // Clear before teleporting so a throw doesn't make it fire on every login.
            player.RemoveModdata(ReturnPosKey);

            if (delayMs <= 0)
            {
                EventTeleport.Send(sapi, player, x, y, z, dim, "observer leaving the seat");
                return;
            }

            string uid = player.PlayerUID;
            sapi.Event.RegisterCallback(_ =>
            {
                if (sapi.World.PlayerByUid(uid) is IServerPlayer pl)
                    EventTeleport.Send(sapi, pl, x, y, z, dim, "spectator restored on reconnect");
            }, delayMs);
        }
    }
}
