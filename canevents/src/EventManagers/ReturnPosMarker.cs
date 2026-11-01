using System;
using System.Linq;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Pre-event position mirrored into player ModData at teleport-in, so a crash (no OnEnd) or an early
    // release can still send the player home. The only reader and writer of that marker.
    public static class ReturnPosMarker
    {
        public const string Key = "canevents_return_pos";

        public static void Write(IServerPlayer player, BlockPos pos)
        {
            if (player == null || pos == null) return;
            var tree = new TreeAttribute();
            tree.SetInt("x", pos.X);
            tree.SetInt("y", pos.Y);
            tree.SetInt("z", pos.Z);
            // Written since 1.1.8; a marker from an older build has no "dim" and reads back as 0,
            // which is where every arena was before mini-dimensions were possible at all.
            tree.SetInt("dim", pos.dimension);
            player.SetModdata(Key, tree.ToBytes());
        }

        // An unreadable marker is removed, or it would be re-read on every login.
        public static bool TryRead(IServerPlayer player, ICoreServerAPI sapi, out BlockPos pos)
        {
            pos = null;
            byte[] data = player?.GetModdata(Key);
            if (data == null || data.Length == 0) return false;

            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);
                pos = new BlockPos(tree.GetInt("x"), tree.GetInt("y"), tree.GetInt("z"), tree.GetInt("dim"));
                return true;
            }
            catch (Exception e)
            {
                sapi?.World?.Logger?.Warning("Return position for {0} is unreadable: {1}", player.PlayerName, e.Message);
                player.RemoveModdata(Key);
                return false;
            }
        }

        public static void Clear(IServerPlayer player) => player?.RemoveModdata(Key);

        // Marker is removed only on landing; a hop that never lands keeps it for the next login.
        public static void SendHome(ICoreServerAPI sapi, IServerPlayer player, BlockPos pos, string reason,
            int delayMs = 0)
        {
            if (sapi == null || player == null || pos == null) return;
            string uid = player.PlayerUID;
            byte[] marker = player.GetModdata(Key);

            if (delayMs <= 0) Attempt(sapi, uid, pos, reason, marker);
            else sapi.Event.RegisterCallback(_ => Attempt(sapi, uid, pos, reason, marker), delayMs);
        }

        // A hop in flight is waited out by EventTeleport itself.
        private static void Attempt(ICoreServerAPI sapi, string uid, BlockPos pos, string reason, byte[] marker)
        {
            if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) return;

            EventTeleport.Send(sapi, player, pos, 0.5, 0.5, 0.5, reason, () =>
            {
                // Only the marker this hop was for: a newer match may have written its own since.
                if (sapi.World.PlayerByUid(uid) is IServerPlayer landed && Same(landed.GetModdata(Key), marker))
                    landed.RemoveModdata(Key);
            });
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || a.Length == 0) return b == null || b.Length == 0;
            return b != null && a.SequenceEqual(b);
        }
    }
}
