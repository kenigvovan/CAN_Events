using System.Collections.Generic;
using System.Linq;
using System.Text;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src
{
    public class SpectatorManager
    {
        private const string ModDataKey = "canevents_original_gamemode";
        private const double MaxDistanceFromArena = 50.0;

        private ICoreServerAPI sapi;
        // playerUID -> arena center position they should stay near (only while online)
        private Dictionary<string, BlockPos> spectatorAnchors = new Dictionary<string, BlockPos>();

        public SpectatorManager(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        public void MakeSpectator(IServerPlayer player, RunningEvent runningEvent)
        {
            // ModData, not memory: the original game mode must survive a disconnect or crash.
            if (player.GetModdata(ModDataKey) == null)
            {
                player.SetModdata(ModDataKey, Encoding.UTF8.GetBytes(((int)player.WorldData.CurrentGameMode).ToString()));
            }

            // Delayed so revive runs after the engine's death broadcast (we are inside
            // TriggerPlayerDeath). A player respawn or an event finish may get here first.
            sapi.Event.RegisterCallback((float dt) =>
            {
                // FinishEvent already handled everything
                if (runningEvent.EventState == EventState.Finished) return;

                // OnRespawn already handled it
                if (spectatorAnchors.ContainsKey(player.PlayerUID)) return;

                ApplySpectatorState(player, runningEvent);
            }, 100);
        }

        /// <summary>Revive if dead, set gamemode, teleport above the arena. Called from the delayed
        /// callback or from OnRespawn, whichever comes first.</summary>
        public void ApplySpectatorState(IServerPlayer player, RunningEvent runningEvent)
        {
            if (spectatorAnchors.ContainsKey(player.PlayerUID)) return;

            if (!player.Entity.Alive)
            {
                player.Entity.Revive();
            }

            player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
            player.BroadcastPlayerData();

            if (runningEvent.Place?.spawnPositions != null && runningEvent.Place.spawnPositions.Count > 0)
            {
                var center = runningEvent.Place.spawnPositions[0];
                spectatorAnchors[player.PlayerUID] = center.Copy();
                player.Entity.TeleportToDouble(center.X + 0.5, center.Y + 10, center.Z + 0.5);
            }

            player.SendMessage(0, Lang.Get("canevents:spectator-enter"), EnumChatType.Notification);
        }

        /// <summary>Restore gamemode for an online player.</summary>
        public void RemoveSpectator(IServerPlayer player)
        {
            byte[] savedBytes = player.GetModdata(ModDataKey);
            if (savedBytes != null)
            {
                string saved = Encoding.UTF8.GetString(savedBytes);
                if (int.TryParse(saved, out int modeInt))
                {
                    player.WorldData.CurrentGameMode = (EnumGameMode)modeInt;
                }
                else
                {
                    player.WorldData.CurrentGameMode = EnumGameMode.Survival;
                }
                player.BroadcastPlayerData();
                player.SetModdata(ModDataKey, null);
            }
            spectatorAnchors.Remove(player.PlayerUID);
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
        }

        /// <summary>On join, restore the original gamemode if they left as a spectator.</summary>
        public void RestoreOnReconnect(IServerPlayer player)
        {
            byte[] savedBytes = player.GetModdata(ModDataKey);
            if (savedBytes != null)
            {
                string saved = Encoding.UTF8.GetString(savedBytes);
                if (int.TryParse(saved, out int modeInt))
                {
                    player.WorldData.CurrentGameMode = (EnumGameMode)modeInt;
                }
                else
                {
                    player.WorldData.CurrentGameMode = EnumGameMode.Survival;
                }
                player.BroadcastPlayerData();
                player.SetModdata(ModDataKey, null);

                player.SendMessage(0, Lang.Get("canevents:spectator-gamemode-restored"), EnumChatType.Notification);
                sapi.World.Logger.Notification("SpectatorManager: Restored gamemode for {0} on reconnect", player.PlayerName);
            }
        }

        public void EnforceSpectatorBounds()
        {
            foreach (var kvp in spectatorAnchors.ToList())
            {
                var player = sapi.World.PlayerByUid(kvp.Key) as IServerPlayer;
                if (player?.Entity == null) continue;

                var anchor = kvp.Value;
                double dx = player.Entity.Pos.X - (anchor.X + 0.5);
                double dy = player.Entity.Pos.Y - (anchor.Y + 10);
                double dz = player.Entity.Pos.Z - (anchor.Z + 0.5);
                double distSq = dx * dx + dy * dy + dz * dz;

                if (distSq > MaxDistanceFromArena * MaxDistanceFromArena)
                {
                    player.Entity.TeleportToDouble(anchor.X + 0.5, anchor.Y + 10, anchor.Z + 0.5);
                    player.SendMessage(0, Lang.Get("canevents:spectator-too-far"), EnumChatType.Notification);
                }
            }
        }

        /// <summary>Drop the in-memory anchor only; ModData stays for RestoreOnReconnect. A stale
        /// anchor would make a mid-event reconnect look "already spectating" and skip the restore.</summary>
        public void OnSpectatorDisconnect(string playerUid)
        {
            spectatorAnchors.Remove(playerUid);
        }

        public bool IsSpectating(string playerUid)
        {
            return spectatorAnchors.ContainsKey(playerUid);
        }
    }
}
