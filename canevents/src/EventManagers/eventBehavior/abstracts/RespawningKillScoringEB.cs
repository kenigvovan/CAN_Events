using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    // Modes with respawns: re-kit, heal, and respawn at an arena spawn instead of the bed.
    public abstract class RespawningKillScoringEB : KillScoringEB, IProvidesRespawnPoint
    {
        protected RespawningKillScoringEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        // False = any spawn (FFA).
        protected virtual bool RespawnAtTeamSpawn => false;

        public bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos)
            => RespawnAtTeamSpawn
                ? EventSpawns.TryGetTeamSpawn(runningEvent, playerUid, out pos)
                : EventSpawns.TryGetRandom(runningEvent, out pos);

        public sealed override void OnParticipantRespawn(IServerPlayer player)
        {
            if (!runningEvent.IsActive(player.PlayerUID)) return;
            // Clear first: order vs SavePlayerAttributes is not fixed, and the engine may hand items over on respawn.
            if (!runningEvent.KeepOwnGear) PlayerInventoryBackup.ClearInventory(player);
            GiveLoadoutAndHeal(player);
            AfterRespawnEquipped(player);
        }

        protected virtual void AfterRespawnEquipped(IServerPlayer player) { }
    }
}
