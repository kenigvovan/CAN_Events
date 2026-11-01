using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // Snapshot inventory at start, clear for the loadout, restore at end (PlayerInventoryBackup).
    // Also clears on respawn and stops issued gear dropping on death.
    public class SavePlayerAttributes : CANEventBehavior
    {
        private readonly Shared.DamageHookSet damageHooks = new Shared.DamageHookSet();

        // Undone first in OnEnd, see Unsubscribers.
        private readonly Shared.Unsubscribers subscriptions = new Shared.Unsubscribers();

        public SavePlayerAttributes(string name, JsonObject attributes, RunningEvent runningEvent) : base(name, attributes, runningEvent)
        {
        }

        public override string Name => BehaviorNames.SavePlayerAttributes;

        // No kit, no death rescue, no restore: restoring would dupe items dropped mid-match.
        // Tournaments set it on their event copy (TournamentEventTweaks).
        public bool KeepOwnGear { get; private set; }

        // null inherits the server default.
        public Shared.IssuedGearDrop? IssuedGearDropOverride { get; private set; }

        // false sends eliminated players home instead of spectating.
        public bool SpectateOnElimination { get; private set; } = true;

        // Called by PersonalStashPatch on every slot interaction: keep it cheap, never throw.
        public static bool LocksStashesOf(IServerPlayer player)
        {
            var re = HarmonyPatches.MatchDeathExternalsPatch.MatchOf(player);
            return re?.GetBehavior<SavePlayerAttributes>() != null;
        }

        public override void Initialize()
        {
            base.Initialize();
            KeepOwnGear = Config.Bool("keepOwnGear", false);
            IssuedGearDropOverride = Shared.IssuedGear.ParseOverride(Config.String("issuedGearDrop"), log);
            SpectateOnElimination = Config.Bool("spectateOnElimination", true);
            Config.WarnUnknown();
        }

        // Taken early by a waiting room; OnStart leaves these alone.
        private readonly HashSet<string> snapshotted = new HashSet<string>();

        public override void OnStart()
        {
            // Snapshot here, not in OnBeforeStart: items dropped during the countdown would be duped.
            // A waiting room snapshots at teleport instead (SnapshotNow), so nothing can be dropped there.
            foreach (var player in runningEvent.ActiveOnlineParticipants)
            {
                if (!snapshotted.Contains(player.PlayerUID)) TakeSnapshot(player);
                // Again for those from the waiting room: buffs cast there must not reach the fight.
                else Compat.RpgClassesCompat.ResetCombat(player.Entity);
            }

            // For deaths that bypass the damage pipeline (/kill, void). Fires inside Die() before the inventory drop.
            runningEvent.sapi.Event.OnEntityDeath += OnAnyEntityDeath;
            subscriptions.Add(() => runningEvent.sapi.Event.OnEntityDeath -= OnAnyEntityDeath);
        }

        public void SnapshotNow(IServerPlayer player)
        {
            if (snapshotted.Add(player.PlayerUID)) TakeSnapshot(player);
        }

        private void TakeSnapshot(IServerPlayer player)
        {
            // Own-gear has no restore at end, so settle a stale backup now or it overwrites later.
            if (KeepOwnGear && PlayerInventoryBackup.Restore(player, runningEvent.sapi))
                runningEvent.sapi.World.Logger.Warning(
                    "SavePlayerAttributes: settled a stale inventory backup for {0} before an own-gear match.",
                    player.PlayerName);

            // Event code goes into the snapshot for the audit trail.
            bool saved = PlayerInventoryBackup.Save(player, runningEvent.sapi,
                gearStaysWithPlayer: KeepOwnGear, eventCode: runningEvent.Event?.Code);

            // Refused = an older snapshot is unsettled and current items are saved nowhere; sit out untouched.
            if (!saved && !KeepOwnGear)
            {
                runningEvent.sapi.World.Logger.Error(
                    "[inventory] {0} kept out of '{1}': an earlier snapshot was never settled, so what "
                    + "they carry now is copied nowhere and cannot be cleared for the kit. "
                    + "Check /eventadmin inv status {0}.",
                    player.PlayerName, runningEvent.Event?.Code ?? "?");
                runningEvent.MarkReleased(player.PlayerUID);
                runningEvent.EliminatePlayer(player.PlayerUID);
                player.SendMessage(0, Lang.Get("canevents:inventory-unsettled-sitout"), EnumChatType.Notification);
                return;
            }
            if (!saved)
                runningEvent.sapi.World.Logger.Error(
                    "[inventory] {0} enters own-gear match '{1}' with an unsaved inventory - an earlier "
                    + "snapshot was never settled. Check /eventadmin inv status {0}.",
                    player.PlayerName, runningEvent.Event?.Code ?? "?");

            EnterMatch(player);
        }

        // Snapshot already taken: empty the inventory for the kit, top up food, guard the lethal hit.
        private void EnterMatch(IServerPlayer player)
        {
            if (!KeepOwnGear) PlayerInventoryBackup.ClearInventory(player);
            // After Save, so the snapshot keeps the real saturation.
            Shared.EventBroadcaster.Feed(player);
            Compat.RpgClassesCompat.ResetCombat(player.Entity);
            if (!KeepOwnGear) HookLethalDamage(player);
        }

        public override void OnEnd()
        {
            subscriptions.UnhookAll();
            damageHooks.UnhookAll();

            // Offline players are restored on reconnect (CANEventManager.RestoreEventBackup).
            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (!runningEvent.HoldsPlayer(player.PlayerUID)) continue;
                PlayerInventoryBackup.Restore(player, runningEvent.sapi);
                Compat.RpgClassesCompat.ResetCombat(player.Entity);
            }
        }

        // From CANEventManager.TryReleaseEliminated: own inventory is back, stop the lethal-hit sweep.
        public void Release(string playerUid) => damageHooks.Unhook(playerUid);

        // Open-match entry, step 1: take the snapshot, touch nothing else. False = an older snapshot is
        // unsettled; the player stays out with their inventory as it is.
        public bool PrepareAdmit(IServerPlayer player)
        {
            if (KeepOwnGear) return true;
            if (PlayerInventoryBackup.Save(player, runningEvent.sapi, eventCode: runningEvent.Event?.Code)) return true;

            runningEvent.sapi.World.Logger.Error(
                "[inventory] {0} not let into '{1}': an earlier snapshot was never settled. Check /eventadmin inv status {0}.",
                player.PlayerName, runningEvent.Event?.Code ?? "?");
            return false;
        }

        // The entry fell through after PrepareAdmit: settle the snapshot just taken, nothing was cleared yet.
        public void RollbackAdmit(IServerPlayer player)
        {
            if (!KeepOwnGear) PlayerInventoryBackup.Restore(player, runningEvent.sapi);
        }

        public override void OnParticipantAdmitted(IServerPlayer player) => EnterMatch(player);

        public override void OnParticipantWithdrawn(IServerPlayer player)
        {
            Release(player.PlayerUID);
            if (!KeepOwnGear) PlayerInventoryBackup.Restore(player, runningEvent.sapi);
            Compat.RpgClassesCompat.ResetCombat(player.Entity);
        }

        public override void OnParticipantDropped(string uid) => Release(uid);

        // Rejoin spawns a fresh entity.
        public override void OnParticipantJoinedGame() => damageHooks.RehookAll(runningEvent);

        private void OnAnyEntityDeath(Entity entity, DamageSource source)
        {
            if (!(entity is EntityPlayer entityPlayer)) return;
            var player = runningEvent.sapi.World.PlayerByUid(entityPlayer.PlayerUID) as IServerPlayer;
            if (player == null) return;
            if (!runningEvent.HoldsPlayer(player.PlayerUID)) return;
            if (KeepOwnGear) return;

            PlayerInventoryBackup.DropDeathDrops(player, runningEvent.sapi);
            PlayerInventoryBackup.ClearInventory(player);
        }

        private void HookLethalDamage(IServerPlayer player)
        {
            var health = player.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;

            string uid = player.PlayerUID;
            damageHooks.Hook(player, (damage, dmgSource) =>
            {
                // Lethal hit: clear before EntityPlayer.Die() drops it.
                if (runningEvent.HoldsPlayer(uid)
                    && damage >= health.Health
                    // Downed mod hooks onDamaged after us and may turn this into a knock-down.
                    && !Compat.DownedCompat.WouldGoDownInsteadOfDying(player.Entity, dmgSource))
                {
                    PlayerInventoryBackup.DropDeathDrops(player, runningEvent.sapi);
                    PlayerInventoryBackup.ClearInventory(player);
                }
                return damage;
            });
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (!runningEvent.HoldsPlayer(uid)) return;

            // Engine gives starting items on respawn; skip only if a kit behavior ahead already re-equipped.
            if (KeepOwnGear || !runningEvent.IsActive(uid) || !KitGivenBeforeThis())
                PlayerInventoryBackup.ClearInventory(player);
            // Vanilla revive saturation is off via noSatietyRestoreOnRevive (CANEventManager.OnRespawn).
            Shared.EventBroadcaster.Feed(player);
            if (runningEvent.IsActive(uid)) Compat.RpgClassesCompat.ResetCombat(player.Entity);
        }

        // Respawn hooks run in behavior (JSON) order.
        private bool KitGivenBeforeThis()
        {
            foreach (var b in runningEvent.Behaviors)
            {
                if (ReferenceEquals(b, this)) return false;
                if (b is abstracts.RespawningKillScoringEB) return true;
            }
            return false;
        }
    }
}
