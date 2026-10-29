using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    public class NoFriendlyFireEB : CANEventBehavior
    {
        private readonly Shared.DamageHookSet hooks = new Shared.DamageHookSet();

        public NoFriendlyFireEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.NoFriendlyFire;

        public override void OnStart()
        {
            if (FriendlyFireAllowed) return;
            HookEveryone();
        }

        // Bot bodies can spawn after OnStart (chunk loading), hence the tick when bots are present.
        private void HookEveryone()
            => hooks.HookAll(runningEvent, runningEvent.ParticipantsBefore, BlockTeamDamage, includeBots: true);

        // Open matches too: bots can be added to them later.
        public override bool WantsTick
            => runningEvent?.IsOpenMatch == true || runningEvent?.ParticipantsBefore.Any(Bots.BotIdentity.IsBot) == true;

        public override void OnTick(float dt)
        {
            if (!FriendlyFireAllowed) HookEveryone();
        }

        // Config switch or the duel "friendlyfire" mutator.
        private bool FriendlyFireAllowed
            => (runningEvent.Config != null && runningEvent.Config.allowFriendlyFire)
               || runningEvent.Event?.HasMutator(EventMutators.FriendlyFire) == true;

        // A reconnect spawns a fresh entity, so the old hook is gone.
        public override void OnParticipantJoinedGame()
        {
            if (FriendlyFireAllowed) return;

            hooks.RehookAll(runningEvent);
            HookEveryone();
        }

        public override void OnParticipantAdmitted(IServerPlayer player)
        {
            if (!FriendlyFireAllowed) HookEveryone();
        }

        public override void OnParticipantWithdrawn(IServerPlayer player) => hooks.Unhook(player.PlayerUID);

        public override void OnParticipantDropped(string uid) => hooks.Unhook(uid);

        public override void OnEnd()
        {
            hooks.UnhookAll();
        }

        private float BlockTeamDamage(string victimUid, float damage, DamageSource source)
        {
            if (!Shared.DamageHookSet.TryHostileHit(runningEvent, victimUid, damage, source, out var attackerUid, includeBots: true))
                return damage;

            // -1 = not in this match.
            int aTeam = runningEvent.TeamIndexOf(attackerUid);
            int vTeam = runningEvent.TeamIndexOf(victimUid);
            if (aTeam < 0 || vTeam < 0) return damage;
            if (aTeam != vTeam) return damage;
            return 0f;
        }
    }
}
