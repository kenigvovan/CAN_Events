using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public static class EventBroadcaster
    {
        // HudAudience, so spectators get the same messages as fighters.
        public static void Broadcast(RunningEvent runningEvent, string message)
        {
            foreach (var player in runningEvent.HudAudience)
                player.SendMessage(0, message, EnumChatType.Notification);
        }

        public static void KillFeed(RunningEvent runningEvent, string killerName, string victimName, string verb)
            => runningEvent?.NetworkHandler?.SendKillFeed(runningEvent, killerName, victimName, verb);

        public static void Scoreboard(RunningEvent runningEvent, ScoreboardData data)
            => runningEvent?.NetworkHandler?.SendScoreboard(runningEvent, data);

        public static void FlagCarriers(RunningEvent runningEvent, string[] carrierUids, int[] flagTeams)
            => runningEvent?.NetworkHandler?.SendCtfCarriers(runningEvent, carrierUids, flagTeams);

        // active=false clears the dome.
        public static void ZoneState(RunningEvent runningEvent, bool active, int aliveCount,
            double x = 0, double y = 0, double z = 0, float radiusNow = 0, float radiusTarget = 0, float secondsToTarget = 0)
            => runningEvent?.NetworkHandler?.SendHgZoneState(runningEvent, active, aliveCount,
                x, y, z, radiusNow, radiusTarget, secondsToTarget);

        public static void BroadcastToServer(ICoreServerAPI sapi, string message)
        {
            foreach (var player in sapi.World.AllOnlinePlayers)
            {
                if (!(player is IServerPlayer sp) || AnnouncementPrefs.IsQuiet(sp)) continue;
                sp.SendMessage(0, message, EnumChatType.Notification);
            }
        }

        public static void Heal(IPlayer player)
        {
            var healthBehavior = player?.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (healthBehavior != null)
            {
                healthBehavior.Health = healthBehavior.MaxHealth;
            }
        }

        // "health:0.5" mutator. Only current health, max is untouched so a crash leaves nothing behind.
        public static void SetHealthFraction(IPlayer player, float fraction)
        {
            var healthBehavior = player?.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (healthBehavior != null)
            {
                healthBehavior.Health = healthBehavior.MaxHealth * fraction;
            }
        }

        // Separate walkspeed stat code per source, so one doesn't overwrite or clear another.
        public const string SpeedStatCode = "canevents-mutator";
        public const string MurdererSpeedStatCode = "canevents-mm-murderer";
        public const string DisarmSlowStatCode = "canevents-mm-disarm";

        private static readonly string[] AllSpeedStatCodes = { SpeedStatCode, MurdererSpeedStatCode, DisarmSlowStatCode };

        // persistent=false: not saved, so a crash or relog drops it.
        public static void ApplySpeed(IPlayer player, string statCode, float multiplier)
            => player?.Entity?.Stats.Set("walkspeed", statCode, multiplier - 1f, false);

        public static void ClearSpeed(IPlayer player, string statCode)
            => player?.Entity?.Stats.Remove("walkspeed", statCode);

        public static void ApplySpeedMutator(IPlayer player, float multiplier)
            => ApplySpeed(player, SpeedStatCode, multiplier);

        // Clears all speed stat codes, not just the mutator.
        public static void ClearSpeedMutator(IPlayer player)
        {
            foreach (var code in AllSpeedStatCodes) ClearSpeed(player, code);
        }

        // Called at match start and on respawn (after the mode's heal).
        public static void ApplyStatMutators(IPlayer player, events.CANEvent ev)
        {
            if (player == null || ev == null) return;

            float healthFraction = ev.MutatorFloat(events.EventMutators.Health, 1f);
            if (healthFraction < 1f) SetHealthFraction(player, healthFraction);

            float speed = ev.MutatorFloat(events.EventMutators.Speed, 1f);
            if (System.Math.Abs(speed - 1f) > 0.001f) ApplySpeedMutator(player, speed);
        }

        // PlayerInventoryBackup restores the pre-event saturation at the end.
        public static void Feed(IPlayer player)
        {
            var hunger = player?.Entity?.GetBehavior<EntityBehaviorHunger>();
            // MaxSaturation 0 = hunger tree not initialised yet.
            if (hunger != null && hunger.MaxSaturation > 0f)
            {
                hunger.Saturation = hunger.MaxSaturation;
            }
        }

        public static void HealAll(RunningEvent runningEvent)
        {
            foreach (var player in runningEvent.OnlineParticipants) Heal(player);
        }
    }
}
