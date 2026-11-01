using System;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    // Target pick for a bot's own movement and bow tasks, refreshed once a second rather than every
    // task tick: the nearest opponent (BotMatchLink.IsOpponent, so team and role rules hold), pulled
    // towards the wounded, and kept unless another is clearly better.
    public static class BotTargeting
    {
        private const float SearchRange = 30f;
        private const long RefreshMs = 1000;
        private const double KeepMargin = 4.0;
        private const double WoundedWeight = 6.0;

        private sealed class Pick
        {
            public Entity Target;
            public long UntilMs;
        }

        private static readonly Dictionary<long, Pick> picks = new Dictionary<long, Pick>();

        public static Entity TargetOf(Entity bot)
        {
            if (bot?.World == null) return null;
            long now = bot.World.ElapsedMilliseconds;
            if (!picks.TryGetValue(bot.EntityId, out var pick)) picks[bot.EntityId] = pick = new Pick();

            if (now >= pick.UntilMs)
            {
                pick.UntilMs = now + RefreshMs;
                pick.Target = Choose(bot, pick.Target);
            }
            else if (pick.Target != null && !BotMatchLink.IsOpponent(bot, pick.Target)) pick.Target = null;
            return pick.Target;
        }

        public static void Forget(Entity bot)
        {
            if (bot != null) picks.Remove(bot.EntityId);
        }

        private static Entity Choose(Entity bot, Entity current)
        {
            Entity best = null;
            double bestCost = double.MaxValue, currentCost = double.MaxValue;
            foreach (var e in bot.World.GetEntitiesAround(bot.Pos.XYZ, SearchRange, SearchRange / 2f, e => BotMatchLink.IsOpponent(bot, e)))
            {
                double cost = Math.Sqrt(bot.Pos.SquareDistanceTo(e.Pos)) - WoundOf(e) * WoundedWeight;
                if (e == current) currentCost = cost;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = e;
                }
            }
            return current != null && currentCost <= bestCost + KeepMargin ? current : best;
        }

        private static float WoundOf(Entity e)
        {
            var health = e.GetBehavior<EntityBehaviorHealth>();
            return health == null || health.MaxHealth <= 0f ? 0f : Math.Max(0f, 1f - health.Health / health.MaxHealth);
        }
    }
}
