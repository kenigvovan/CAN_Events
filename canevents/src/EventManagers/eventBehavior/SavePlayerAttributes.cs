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
    // Saves each participant's real inventory + health at event start, clears it for the loadout and
    // restores it at the end. Snapshot/restore itself (plus its ModData crash-recovery mirror) lives in
    // PlayerInventoryBackup, shared with the reconnect path; this behavior adds the event-time
    // concerns: clearing on respawn and stopping inventory drops on death.
    public class SavePlayerAttributes : CANEventBehavior
    {
        private readonly Shared.DamageHookSet damageHooks = new Shared.DamageHookSet();

        public SavePlayerAttributes(string name, JsonObject attributes, RunningEvent runningEvent) : base(name, attributes, runningEvent)
        {
        }

        public override string Name { set { } get => "SavePlayerAttributes"; }

        /// <summary>
        /// Players fight with their own gear: no kit, no death-time rescue, no restore at the end.
        /// Protecting real belongings would dupe them (items dropped mid-match survive AND get
        /// restored from the start-of-match snapshot). Read from the "keepOwnGear" attribute; a
        /// tournament sets that attribute on its own copy of the event (see TournamentEventTweaks),
        /// since every match builds its behaviors from attributes alone.
        /// </summary>
        public bool KeepOwnGear { get; private set; }

        public override void Initialize()
        {
            base.Initialize();
            KeepOwnGear = Config.Bool("keepOwnGear", false);
            Config.WarnUnknown();
        }

        public override void OnStart()
        {
            // Snapshot then clear in the same step. Snapshotting here rather than in OnBeforeStart
            // closes the dupe window where items dropped during the countdown stay in the world AND
            // get restored at OnEnd.
            foreach (var player in runningEvent.OnlineParticipants)
            {
                // Never in play (dropped out before the arena was found): no loadout, no teleport,
                // so clearing here would take real belongings for a match they are not playing.
                if (!runningEvent.IsActive(player.PlayerUID)) continue;

                // Own-gear matches have no end-of-match restore, so a stale backup from a match that
                // never restored would later overwrite the player's real inventory. Settle it first,
                // exactly as the reconnect path would have.
                if (KeepOwnGear && PlayerInventoryBackup.Restore(player, runningEvent.sapi))
                    runningEvent.sapi.World.Logger.Warning(
                        "SavePlayerAttributes: settled a stale inventory backup for {0} before an own-gear match.",
                        player.PlayerName);

                PlayerInventoryBackup.Save(player, runningEvent.sapi, gearStaysWithPlayer: KeepOwnGear);
                if (!KeepOwnGear) PlayerInventoryBackup.ClearInventory(player);
                // Strictly after Save: the snapshot must hold the player's real saturation, not
                // the full bar we hand them for the match.
                Shared.EventBroadcaster.Feed(player);
                // The death-time rescue exists to stop ISSUED gear from dropping; own gear is
                // meant to drop.
                if (!KeepOwnGear) HookLethalDamage(player);
            }

            // Catches deaths that bypass the damage pipeline (e.g. /kill, void fall).
            // Fires inside Die() before EntityPlayer's inventory-drop behavior runs.
            runningEvent.sapi.Event.OnEntityDeath += OnAnyEntityDeath;
        }

        public override void OnEnd()
        {
            runningEvent.sapi.Event.OnEntityDeath -= OnAnyEntityDeath;

            damageHooks.UnhookAll();

            // Offline players keep their ModData backup and are restored on reconnect
            // (CANEventManager.RestoreEventBackup).
            foreach (var player in runningEvent.OnlineParticipants)
                PlayerInventoryBackup.Restore(player, runningEvent.sapi);
        }

        private void OnAnyEntityDeath(Entity entity, DamageSource source)
        {
            if (!(entity is EntityPlayer entityPlayer)) return;
            var player = runningEvent.sapi.World.PlayerByUid(entityPlayer.PlayerUID) as IServerPlayer;
            if (player == null) return;
            if (!runningEvent.IsParticipant(player.PlayerUID)) return;
            // Own gear is supposed to drop; only issued gear is swept up before the body does.
            if (KeepOwnGear) return;

            PlayerInventoryBackup.ClearInventory(player);
        }

        private void HookLethalDamage(IServerPlayer player)
        {
            // Captured rather than read through the set: the hook needs this player's own
            // health to compare the incoming damage against.
            var health = player.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;

            damageHooks.Hook(player, (damage, dmgSource) =>
            {
                // If this hit would kill, clear the inventory before EntityPlayer.Die() drops it.
                // The saved backup is untouched and restored in OnEnd.
                if (damage >= health.Health
                    // ...unless a knock-down mod is about to swallow the blow. It hooks onDamaged
                    // after us on every hit, so "lethal" here does not mean the player dies - and
                    // emptying the inventory of someone who only got knocked down looks like theft.
                    // A death that happens anyway is still covered by OnAnyEntityDeath, which runs
                    // inside Die() ahead of the engine's drop.
                    && !Compat.DownedCompat.WouldGoDownInsteadOfDying(player.Entity, dmgSource))
                {
                    PlayerInventoryBackup.ClearInventory(player);
                }
                return damage;
            });
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            if (runningEvent.IsParticipant(player.PlayerUID))
            {
                // Cleared either way: the engine hands out default starting items on respawn, and a
                // player who just lost their own gear must not be handed a consolation kit.
                PlayerInventoryBackup.ClearInventory(player);
                // Full bar on respawn. Vanilla's revive handling is off for participants via
                // noSatietyRestoreOnRevive in CANEventManager.OnRespawn, so this is the only writer
                // of saturation here regardless of callback order.
                Shared.EventBroadcaster.Feed(player);
            }
        }
    }
}
