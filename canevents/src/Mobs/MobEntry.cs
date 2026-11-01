using System;
using System.Linq;
using canevents.src.Mobs.Ai;
using Newtonsoft.Json.Linq;

namespace canevents.src.Mobs
{
    // One line of a wave: what to spawn and how many.
    //   code       vanilla or modded entity code ("game:drifter-normal")
    //   rpg        canrpgmobs mob key, used only when that mod is installed
    //   rpgGroup   canrpgmobs spawn group
    //   count      base count, perPlayer adds that many per fighter beyond the first
    //   healthMul / damageMul   on top of the wave and player scaling
    //   tier / level / faction  canrpgmobs only; the faction must attack players on sight
    //   spawn      tag of the mob spawn points to use, empty = any
    //   archetype  canevents:arenaraider only: brute, archer, skirmisher, boss (see RaiderProfiles)
    //   weapon / outfit / name   raider gear and nametag, the archetype's defaults otherwise
    public sealed class MobEntry
    {
        public string Archetype;
        public string Weapon;
        public string Name;
        public string[] Outfit;
        public string Code;
        public string Rpg;
        public string RpgGroup;
        public int Count = 1;
        public float PerPlayer;
        public float HealthMul = 1f;
        public float DamageMul = 1f;
        public string Tier;
        public int Level;
        public string Faction;
        public string SpawnTag;

        public string Describe() => Archetype != null ? (Code ?? RaiderProfiles.EntityCode) + ":" + Archetype : Code ??(Rpg != null ? "rpg:" + Rpg : RpgGroup != null ? "rpgGroup:" + RpgGroup : "?");

        public int CountFor(int fighters)
            => Math.Max(0, Count + (int)Math.Round(PerPlayer * Math.Max(0, fighters - 1)));

        public static MobEntry Parse(JObject json)
        {
            if (json == null) return null;
            var entry = new MobEntry
            {
                Code = Text(json, "code"),
                Rpg = Text(json, "rpg"),
                RpgGroup = Text(json, "rpgGroup"),
                Count = json.Value<int?>("count") ?? 1,
                PerPlayer = json.Value<float?>("perPlayer") ?? 0f,
                HealthMul = json.Value<float?>("healthMul") ?? 1f,
                DamageMul = json.Value<float?>("damageMul") ?? 1f,
                Tier = Text(json, "tier"),
                Level = json.Value<int?>("level") ?? 0,
                Faction = Text(json, "faction"),
                SpawnTag = Text(json, "spawn"),
                Archetype = Text(json, "archetype"),
                Weapon = Text(json, "weapon"),
                Name = Text(json, "name"),
                Outfit = (json["outfit"] as JArray)?.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
            };
            // An archetype alone means our raider.
            if (entry.Code == null && entry.Archetype != null && entry.Rpg == null && entry.RpgGroup == null)
                entry.Code = RaiderProfiles.EntityCode;
            return entry.Code == null && entry.Rpg == null && entry.RpgGroup == null ? null : entry;
        }

        private static string Text(JObject json, string key)
        {
            string value = json.Value<string>(key)?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
