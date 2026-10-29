using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    public class TPThereAndBack : CANEventBehavior
    {
        private Dictionary<string, BlockPos> SavedPlayerPos = new Dictionary<string, BlockPos>();

        // Pre-event position mirrored to player ModData; see ReturnPosMarker.
        public const string ReturnPosModDataKey = ReturnPosMarker.Key;

        public TPThereAndBack(string name, JsonObject attributes, RunningEvent runningEvent) : base(name, attributes, runningEvent)
        {
        }

        public override string Name => BehaviorNames.TPThereAndBack;

        public override void OnBeforeStart()
        {
            // Shuffle unless a mode needs team-indexed spawns. Must be set before bots spawn.
            bool ordered = runningEvent.Behaviors.Any(b => b is ISpawnOrderMatters);
            if (Config.Bool("randomizeSpawns", !ordered)) runningEvent.EnableSpawnShuffle();
        }

        public override void OnStart()
        {
            var logger = runningEvent.sapi.World.Logger;
            if (runningEvent.Place == null)
            {
                logger.Error("TPThereAndBack.OnStart: runningEvent.Place is null, no teleport");
                return;
            }
            var spawnPositions = runningEvent.Place.spawnPositions;
            int totalPlayers = runningEvent.ParticipantsBefore?.Count ?? 0;
            int teamCount = runningEvent.ParticipantsByGroupsBefore?.Count ?? 0;
            string mode = EventSpawns.LayoutName(spawnPositions?.Count ?? 0, totalPlayers, teamCount);
            logger.Notification("TPThereAndBack.OnStart: place='{0}' spawnCount={1} players={2} teams={3} mode={4}",
                runningEvent.Place.Name,
                spawnPositions?.Count ?? -1,
                totalPlayers, teamCount, mode);

            if (spawnPositions == null || spawnPositions.Count == 0)
            {
                logger.Error("No spawn positions defined for event place {0}", runningEvent.Place.Name);
                return;
            }

            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                // ArenaBotsEB spawns bot bodies.
                if (Bots.BotIdentity.IsBot(plGuid)) continue;
                // Left before start: still on the roster but never teleported in.
                if (!runningEvent.IsActive(plGuid)) continue;

                var pl = runningEvent.sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (pl == null)
                {
                    logger.Warning("TPThereAndBack: participant uid={0} not found in World", plGuid);
                    continue;
                }
                // Taken here, not in OnBeforeStart: players move during the countdown.
                RememberReturnPos(pl);
                SendToSpawn(pl, "match start ");
            }
        }

        // A waiting room took it already; the lobby is not home.
        public void RememberReturnPos(IServerPlayer pl)
        {
            if (SavedPlayerPos.ContainsKey(pl.PlayerUID)) return;
            var returnPos = pl.Entity.Pos.AsBlockPos.Copy();
            SavedPlayerPos[pl.PlayerUID] = returnPos;
            ReturnPosMarker.Write(pl, returnPos);
        }

        public void SendToSpawn(IServerPlayer pl, string reason)
        {
            var sp = EventSpawns.Resolve(runningEvent, pl.PlayerUID);
            if (sp == null) return;
            EventTeleport.Send(runningEvent.sapi, pl, sp.X + 0.5, sp.Y + 0.5, sp.Z + 0.5, sp.dimension,
                reason + runningEvent.Event.Code);
        }

        public override void OnParticipantAdmitted(IServerPlayer player)
        {
            RememberReturnPos(player);
            SendToSpawn(player, "joined ");
        }

        // The ModData marker stays; reconnect sends them home from it.
        public override void OnParticipantDropped(string uid) => ForgetReturnPos(uid);

        public override void OnParticipantWithdrawn(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (!SavedPlayerPos.TryGetValue(uid, out var home)) return;
            SavedPlayerPos.Remove(uid);
            ReturnPosMarker.SendHome(runningEvent.sapi, player, home, "left " + runningEvent.Event.Code);
        }

        // For players who already went home early, so OnEnd doesn't teleport them again.
        public void ForgetReturnPos(string playerUid)
        {
            if (playerUid != null) SavedPlayerPos.Remove(playerUid);
        }

        public override void OnEnd()
        {
            // Marker is cleared on landing; an unfinished hop is retried at login.
            foreach (var pl in runningEvent.OnlineParticipants)
                if (SavedPlayerPos.TryGetValue(pl.PlayerUID, out var savedPos))
                    ReturnPosMarker.SendHome(runningEvent.sapi, pl, savedPos, "return home " + runningEvent.Event.Code);
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            // Respawn points come from the Harmony patch on ServerPlayer.GetSpawnPosition.
        }
    }
}
