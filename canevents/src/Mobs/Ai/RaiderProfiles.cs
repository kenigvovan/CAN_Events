using System;
using System.Collections.Generic;
using canevents.src.Bots;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.Mobs.Ai
{
    // How a raider fights. Ranges in blocks, the band a ranged raider tries to stay in.
    public sealed class RaiderProfile
    {
        public string Id;
        public string Weapon;
        public float SpeedMul = 1f;
        public bool Ranged;
        public float MinRange;
        public float MaxRange;
        // After landing a hit: back off this long, then come again.
        public float HitAndRunSeconds;
        public float UnarmedDamage = 2f;
    }

    public static class RaiderProfiles
    {
        public const string EntityCode = "canevents:arenaraider";
        public const string ArchetypeAttribute = "caneventsArchetype";
        public const string RetreatUntilAttribute = "caneventsRetreatUntil";

        private static readonly Dictionary<string, RaiderProfile> profiles = new Dictionary<string, RaiderProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["brute"] = new RaiderProfile { Id = "brute", Weapon = "game:spear-generic-steel", SpeedMul = 0.9f, UnarmedDamage = 3f },
            ["skirmisher"] = new RaiderProfile { Id = "skirmisher", Weapon = "game:spear-generic-flint", SpeedMul = 1.3f, HitAndRunSeconds = 1.6f },
            ["archer"] = new RaiderProfile { Id = "archer", Weapon = "game:bow-long", Ranged = true, MinRange = 7f, MaxRange = 20f, UnarmedDamage = 1.5f },
            ["boss"] = new RaiderProfile { Id = "boss", Weapon = "game:spear-generic-steel", SpeedMul = 1.05f, UnarmedDamage = 5f },
            // Roster bots: steps back after each hit instead of trading blows nose to nose.
            ["duelist"] = new RaiderProfile { Id = "duelist", HitAndRunSeconds = 0.9f },
        };

        // Bots fight by what they hold: a bow makes an archer, anything else a duelist.
        public static void SetForBot(Entity bot)
        {
            var held = (bot as Vintagestory.API.Common.EntityAgent)?.RightHandItemSlot?.Itemstack?.Collectible;
            bool bow = held?.Code?.Path?.StartsWith("bow-") == true;
            bot.WatchedAttributes.SetString(ArchetypeAttribute, bow ? "archer" : "duelist");
        }

        public static RaiderProfile Of(string id)
            => id != null && profiles.TryGetValue(id, out var p) ? p : profiles["brute"];

        public static RaiderProfile Of(Entity entity)
            => Of(entity?.WatchedAttributes?.GetString(ArchetypeAttribute));

        public static bool IsRaider(Entity entity) => entity?.WatchedAttributes?.HasAttribute(ArchetypeAttribute) == true;

        // After the spawn: the seraph inventory exists only once the entity is initialized.
        public static void Equip(ICoreServerAPI sapi, Entity body, MobEntry entry)
        {
            if (!(body is EntityArenaBot raider)) return;
            var profile = Of(entry.Archetype);
            raider.WatchedAttributes.SetString(ArchetypeAttribute, profile.Id);
            raider.WatchedAttributes.SetFloat(BotMatchLink.DamageAttribute, profile.UnarmedDamage);

            var slot = raider.RightHandItemSlot;
            var stack = ItemLookup.StackOf(sapi.World, entry.Weapon ?? profile.Weapon);
            if (slot != null && stack != null)
            {
                slot.Itemstack = stack;
                slot.MarkDirty();
            }

            if (entry.Outfit != null && entry.Outfit.Length > 0)
                EventOutfit.Dress(raider, sapi, new List<string[]> { entry.Outfit }, "mob:" + raider.EntityId, 0);

            raider.GetBehavior<EntityBehaviorNameTag>()?.SetName(entry.Name
                ?? Vintagestory.API.Config.Lang.Get("canevents:raider-" + profile.Id));
        }
    }
}
