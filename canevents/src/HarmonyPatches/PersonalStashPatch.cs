using System;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.HarmonyPatches
{
    // Makes vanishing personal stashes (see PlayerInventoryBackup.IsVanishingStash) read-only during
    // a match; they escape snapshot/restore. Refusing to open them instead desyncs the client.
    // CanPlayerAccess covers moves, flips and shift-click in (engine resyncs on refusal);
    // ActivateSlot covers plain clicks and shift-click out (no engine check, we mark slots dirty).
    public static class PersonalStashPatch
    {
        private static readonly System.Collections.Generic.Dictionary<string, long> lastTold
            = new System.Collections.Generic.Dictionary<string, long>();
        private const long TellIntervalMs = 30_000;

        public static void Apply(Harmony harmony, ICoreServerAPI sapi)
        {
            var log = Log.For(sapi?.World?.Logger, "stash-lock");
            try
            {
                harmony.Patch(
                    AccessTools.Method(typeof(InventoryBasePlayer), nameof(InventoryBasePlayer.CanPlayerAccess)),
                    prefix: new HarmonyMethod(typeof(PersonalStashPatch), nameof(DenyAccess)));
                harmony.Patch(
                    AccessTools.Method(typeof(InventoryBase), nameof(InventoryBase.ActivateSlot)),
                    prefix: new HarmonyMethod(typeof(PersonalStashPatch), nameof(DenyClick)));
                log.Notification("Vanishing personal pockets (e.g. a trash slot) are read-only for the length of a match.");
            }
            catch (Exception e)
            {
                log.Error("Could not install the stash lock; a personal pocket that vanishes on close "
                    + "can carry items in and out of a match: {0}", e);
            }
        }

        public static bool DenyAccess(InventoryBasePlayer __instance, IPlayer player, ref bool __result)
        {
            if (!IsLocked(__instance, player as IServerPlayer)) return true;
            __result = false;
            return false;
        }

        // Engine doesn't revert here; marking both slots dirty resyncs the client on the next tick.
        public static bool DenyClick(InventoryBase __instance, int slotId, ItemSlot sourceSlot,
            ref ItemStackMoveOperation op, ref object __result)
        {
            var player = op?.ActingPlayer as IServerPlayer;
            if (!IsLocked(__instance, player)) return true;

            try
            {
                __instance[slotId]?.MarkDirty();
                sourceSlot?.MarkDirty();
            }
            catch
            {
                // Bad slot index, refuse anyway.
            }
            __result = null;
            return false;
        }

        private static bool IsLocked(IInventory inv, IServerPlayer player)
        {
            try
            {
                if (player == null || !PlayerInventoryBackup.IsVanishingStash(inv)) return false;
                if (!SavePlayerAttributes.LocksStashesOf(player)) return false;

                Tell(player);
                return true;
            }
            catch
            {
                // Don't lock everything on error.
                return false;
            }
        }

        private static void Tell(IServerPlayer player)
        {
            long now = Environment.TickCount64;
            if (lastTold.TryGetValue(player.PlayerUID, out long last) && now - last < TellIntervalMs) return;
            lastTold[player.PlayerUID] = now;
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("canevents:stash-locked"), EnumChatType.Notification);
        }
    }
}
