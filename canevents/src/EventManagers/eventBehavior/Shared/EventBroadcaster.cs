using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public static class EventBroadcaster
    {
        /// <summary>
        /// Says something to everyone following this match. HudAudience rather than OnlineParticipants:
        /// an observer watching the fight needs the same running commentary the fighters get
        /// ("player eliminated", the truce ending) - without it the HUD moves and nothing explains why.
        /// </summary>
        public static void Broadcast(RunningEvent runningEvent, string message)
        {
            foreach (var player in runningEvent.HudAudience)
                player.SendMessage(0, message, EnumChatType.Notification);
        }

        /// <summary>
        /// Announces to everyone online, unlike <see cref="Broadcast"/>, which speaks only to the
        /// players in a match.
        /// </summary>
        public static void BroadcastToServer(ICoreServerAPI sapi, string message)
        {
            foreach (var player in sapi.World.AllOnlinePlayers)
                (player as IServerPlayer)?.SendMessage(0, message, EnumChatType.Notification);
        }

        /// <summary>Tops a player up to their current max health. Takes IPlayer because many call
        /// sites hold a PlayerByUid result, which is not typed as a server player.</summary>
        public static void Heal(IPlayer player)
        {
            var healthBehavior = player?.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (healthBehavior != null)
            {
                healthBehavior.Health = healthBehavior.MaxHealth;
            }
        }

        /// <summary>The "health:0.5" mutator: start health as a fraction of max. Current health only -
        /// max health is deliberately untouched, so a crash leaves nothing to clean up.</summary>
        public static void SetHealthFraction(IPlayer player, float fraction)
        {
            var healthBehavior = player?.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (healthBehavior != null)
            {
                healthBehavior.Health = healthBehavior.MaxHealth * fraction;
            }
        }

        /// <summary>Stat code of the "speed" mutator, shared by apply and the restore-path removal.</summary>
        public const string SpeedStatCode = "canevents-mutator";

        /// <summary>The "speed:1.5" mutator. Non-persistent by design: the stat is not written to the
        /// save, so a crash or relog sheds it without any cleanup of ours.</summary>
        public static void ApplySpeedMutator(IPlayer player, float multiplier)
            => player?.Entity?.Stats.Set("walkspeed", SpeedStatCode, multiplier - 1f, false);

        public static void ClearSpeedMutator(IPlayer player)
            => player?.Entity?.Stats.Remove("walkspeed", SpeedStatCode);

        /// <summary>
        /// The mutators that touch the player rather than their kit. One place for both call sites -
        /// the match start (every mode, loadouts or not) and a respawn, where the mode's own heal
        /// would otherwise wipe the reduced health.
        /// </summary>
        public static void ApplyStatMutators(IPlayer player, events.CANEvent ev)
        {
            if (player == null || ev == null) return;

            float healthFraction = ev.MutatorFloat(events.EventMutators.Health, 1f);
            if (healthFraction < 1f) SetHealthFraction(player, healthFraction);

            float speed = ev.MutatorFloat(events.EventMutators.Speed, 1f);
            if (System.Math.Abs(speed - 1f) > 0.001f) ApplySpeedMutator(player, speed);
        }

        /// <summary>Fills a player's food bar, so a match is never a hunger race decided by what
        /// they ate beforehand. PlayerInventoryBackup restores the pre-event value at the end, so
        /// this is not free food; nutrient levels are deliberately untouched.</summary>
        public static void Feed(IPlayer player)
        {
            var hunger = player?.Entity?.GetBehavior<EntityBehaviorHunger>();
            // MaxSaturation is 0 only if the hunger tree was never initialised; writing off that
            // would starve the player instead.
            if (hunger != null && hunger.MaxSaturation > 0f)
            {
                hunger.Saturation = hunger.MaxSaturation;
            }
        }

        /// <summary>Heals every participant that is still online.</summary>
        public static void HealAll(RunningEvent runningEvent)
        {
            foreach (var player in runningEvent.OnlineParticipants) Heal(player);
        }
    }
}
