using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Server;

namespace canevents.src.Mobs
{
    public static class MobLines
    {
        // Event code + line, so a missing mod is reported once per server run, not every match.
        private static readonly HashSet<string> warnedUnavailable = new HashSet<string>();

        public static List<MobEntry> Parse(JToken token)
            => token is JArray arr
                ? arr.OfType<JObject>().Select(MobEntry.Parse).Where(e => e != null).ToList()
                : new List<MobEntry>();

        // A line no installed mod can provide is dropped, with one warning.
        public static bool Usable(ICoreServerAPI sapi, MobEntry entry, string eventCode, Log log)
        {
            string why = MatchMobManager.Unavailable(sapi, entry);
            if (why == null) return true;
            if (warnedUnavailable.Add(eventCode + "|" + entry.Describe()))
                log.Warning("{0}: '{1}' is left out of its wave - {2}.", eventCode, entry.Describe(), why);
            return false;
        }

        // Mutators that touch mobs: horde scales counts, elite makes some tougher, nightmare all.
        public static int CountWithMutators(CANEvent ev, int count)
            => count <= 0 ? 0 : Math.Max(1, (int)Math.Round(count * (ev?.MutatorFloat(EventMutators.Horde, 1f) ?? 1f)));

        public static void ApplyMutators(CANEvent ev, Random rand, ref float healthMul, ref float damageMul)
        {
            if (ev == null) return;
            if (ev.HasMutator(EventMutators.Nightmare))
            {
                healthMul *= 1.25f;
                damageMul *= 1.5f;
            }
            if (ev.HasMutator(EventMutators.Elite) && rand.NextDouble() < EliteChance)
            {
                healthMul *= 2f;
                damageMul *= 1.3f;
            }
        }

        public const double EliteChance = 0.2;
    }
}
