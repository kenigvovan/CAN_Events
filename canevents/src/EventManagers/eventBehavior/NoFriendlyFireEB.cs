using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    /// <summary>
    /// Cancels damage between same-team players. No-op when Config.allowFriendlyFire is set.
    /// </summary>
    public class NoFriendlyFireEB : CANEventBehavior
    {
        private readonly Shared.DamageHookSet hooks = new Shared.DamageHookSet();

        public NoFriendlyFireEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "NoFriendlyFire"; set { } }

        public override void OnStart()
        {
            if (runningEvent.Config != null && runningEvent.Config.allowFriendlyFire) return;

            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                var player = runningEvent.sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player == null) continue;
                HookDamage(player);
            }
        }

        // A mid-match reconnect spawns a fresh entity, leaving the hook on the dead one and silently
        // restoring friendly fire. Also hooks anyone who was offline at OnStart.
        public override void OnParticipantJoinedGame()
        {
            if (runningEvent.Config != null && runningEvent.Config.allowFriendlyFire) return;

            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                var player = runningEvent.sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player == null) continue;
                hooks.RehookIfStale(player);
                HookDamage(player);   // no-op when already hooked
            }
        }

        public override void OnEnd()
        {
            hooks.UnhookAll();
        }

        private void HookDamage(IServerPlayer victim)
        {
            string victimUid = victim.PlayerUID;

            hooks.Hook(victim, (damage, source) =>
            {
                if (damage <= 0f) return damage;
                // GetCauseEntity() resolves the archer behind an arrow; SourceEntity is the arrow,
                // which let every ranged hit through and limited this to melee.
                if (source?.GetCauseEntity() is not EntityPlayer attackerEntity) return damage;
                string attackerUid = attackerEntity.PlayerUID;
                if (attackerUid == victimUid) return damage;

                // -1 = not a participant of this match; leave their damage alone.
                int aTeam = runningEvent.TeamIndexOf(attackerUid);
                int vTeam = runningEvent.TeamIndexOf(victimUid);
                if (aTeam < 0 || vTeam < 0) return damage;
                if (aTeam != vTeam) return damage;
                return 0f;
            });
        }
    }
}
