using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // What the HG setup panel shows for one place.
    public sealed class HungerGamesSetup
    {
        public long PlaceId;
        // null when no HG event matches the place.
        public string EventCode;
        public string[] EventCodes = new string[0];
        public string Overview;
        public string[] Tiers = new string[0];
        public string[] TierSummaries = new string[0];

        // Stored order; the panel removes/re-tiers by index.
        public int[] SpotX = new int[0];
        public int[] SpotY = new int[0];
        public int[] SpotZ = new int[0];
        public string[] SpotTier = new string[0];

        // Checklist
        public bool HasBounds;
        public int SpawnCount;
        public int RequiredSpawns;
        public bool ZoneSet;
        public float ZoneRadius;
        public bool UsesCornucopiaTier;
        public string[] Warnings = new string[0];

        // Raw JSON for the advanced editor.
        public string LootTablesJson = "{}";
        public string ZonePhasesJson = "[]";

        public string ScoreboardVisibility = BehaviorConfigKeys.ScoreboardVisibilities[0];
    }

    // HG arena setup: layout, zone, checklist, config save, preview. Place writes go through PlaceConfigService.
    public sealed class HungerGamesSetupService
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;

        public HungerGamesSetupService(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;
        }

        private PlaceConfigService PlaceConfig => manager.placeConfig;

        public List<CANEvent> EventsFor(EventPlace place) => manager.EventsForPlace<HungerGamesEB>(place);

        // Each variant keeps its own loot tables.
        public CANEvent ResolveEvent(EventPlace place, string preferredCode = null)
            => manager.ResolveEventForPlace<HungerGamesEB>(place, preferredCode);

        // keepCentre only resizes an existing zone; fitBounds centres it with the largest radius inside the bounds.
        public PlaceEditResult SetZone(EventPlace place, BlockPos feet, float radius, bool keepCentre, bool fitBounds)
        {
            var centre = feet;
            bool haveZone = TryReadZone(place, out var stored, out _);

            if (fitBounds)
            {
                if (!PlaceBounds.Has(place))
                    return PlaceEditResult.Fail("'" + place.Name + "' has no bounds to fit the zone to.");
                var min = place.BoundsMin; var max = place.BoundsMax;
                centre = new BlockPos((min.X + max.X) / 2, haveZone ? stored.Y : min.Y, (min.Z + max.Z) / 2, min.dimension);
                // Same measure as the checklist, so a fitted zone never trips its warning.
                radius = Math.Max(1f, RoomInsideBounds(place, centre.X, centre.Z));
            }
            else if (keepCentre && haveZone)
            {
                centre = stored;
            }
            return PlaceConfig.SetHungerGamesZone(place, centre, radius);
        }

        // Centre is the floor block.
        public static bool TryReadZone(EventPlace place, out BlockPos centre, out float radius)
        {
            centre = null;
            radius = 0f;
            if (!place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)
                || !JsonPos.TryRead(data, out centre)) return false;
            radius = data["InitialRadius"] != null ? data.Value<float>("InitialRadius") : 0f;
            return true;
        }

        public static bool TryLootSpot(EventPlace place, int index, out BlockPos spot)
        {
            spot = null;
            return PlaceConfigService.TryGetLootArray(place, index, out var arr)
                && JsonPos.TryRead(arr[index], out spot);
        }

        // radius <= 0 fits the bounds; spawnCount 0 uses the event's tribute cap.
        public PlaceEditResult GenerateLayout(EventPlace place, BlockPos stand, float radius, bool snapToTerrain,
            int spawnCount)
        {
            // Zone centre and loot on the floor block, spawns on the air block above it.
            int spawnY = stand.Y;
            var floor = stand.DownCopy();
            int cx = floor.X, cy = floor.Y, cz = floor.Z, dim = floor.dimension;

            // Rain map, not worldgen height: it tracks placed blocks. Only valid in dim 0; 0 = column not loaded.
            var ba = sapi.World.BlockAccessor;
            bool canSnap = snapToTerrain && dim == 0;
            int TopSolidY(int x, int z) => canSnap ? ba.GetRainMapHeightAt(x, z) : 0;
            int SpawnYAt(int x, int z) { int h = TopSolidY(x, z); return h > 0 ? h + 1 : spawnY; }
            int LootYAt(int x, int z) { int h = TopSolidY(x, z); return h > 0 ? h : cy; }

            if (radius <= 0f) radius = DefaultLayoutRadius(place);
            if (radius < 4f) radius = 4f;

            // Warn, don't clamp.
            bool radiusOverBounds = radius > MaxLayoutRadius(place, cx, cz);

            var ev = ResolveEvent(place);
            int n = ev?.AssemblingEB?.MaxPartiesCount ?? 8;
            n = Math.Max(2, Math.Min(24, n));
            // Loot count follows the tribute cap, not spawnN.
            int spawnN = spawnCount > 0 ? Math.Max(1, Math.Min(48, spawnCount)) : n;

            // Just inside the starting edge.
            var spawns = new List<BlockPos>();
            double rSpawn = radius * 0.85;
            for (int i = 0; i < spawnN; i++)
            {
                double ang = i * 2.0 * Math.PI / spawnN;
                int sx = cx + (int)Math.Round(Math.Cos(ang) * rSpawn);
                int sz = cz + (int)Math.Round(Math.Sin(ang) * rSpawn);
                spawns.Add(new BlockPos(sx, SpawnYAt(sx, sz), sz, dim));
            }

            // Best tier at the centre, mid ring, outer ring by the spawns.
            ReadTiers(ev?.GetBehavior<HungerGamesEB>()?.Attributes, out var tiers, out _);
            string outerTier = tiers.Length > 0 ? tiers[0] : null;
            string midTier = tiers.Length > 0 ? tiers[Math.Min(tiers.Length - 1, tiers.Length / 2)] : null;
            string centreTier = tiers.Length > 0 ? tiers[tiers.Length - 1] : null;

            var loot = new JArray { LootSpot(cx, LootYAt(cx, cz), cz, dim, centreTier) };
            AddLootRing(loot, cx, cz, dim, radius * 0.45, Math.Max(2, n / 3), midTier, LootYAt);
            AddLootRing(loot, cx, cz, dim, radius * 0.8, n, outerTier, LootYAt);

            var result = PlaceConfig.ApplyHungerGamesLayout(place, new BlockPos(cx, LootYAt(cx, cz), cz, dim),
                radius, spawns, loot);
            if (!result.Ok) return result;

            return PlaceEditResult.Success(result.Message
                + (radiusOverBounds ? " WARNING: the ring extends beyond the place bounds - spawns may land outside." : "")
                + " Tune anything below.");
        }

        // Distance to the nearest bounds edge. Bounds are stored min/max sorted (EventPlace.SetBounds).
        private static float RoomInsideBounds(EventPlace place, int cx, int cz)
        {
            if (!PlaceBounds.Has(place)) return float.MaxValue;
            var min = place.BoundsMin; var max = place.BoundsMax;
            return Math.Min(Math.Min(cx - min.X, max.X - cx), Math.Min(cz - min.Z, max.Z - cz));
        }

        // Same 2-block margin as DefaultLayoutRadius.
        private static float MaxLayoutRadius(EventPlace place, int cx, int cz)
            => RoomInsideBounds(place, cx, cz) - 2;

        private static float DefaultLayoutRadius(EventPlace place)
        {
            if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                int spanX = Math.Abs(place.BoundsMax.X - place.BoundsMin.X);
                int spanZ = Math.Abs(place.BoundsMax.Z - place.BoundsMin.Z);
                int half = Math.Min(spanX, spanZ) / 2 - 2; // 2-block margin inside the bounds
                return Math.Max(4, half);
            }
            return 40f;
        }

        private static void AddLootRing(JArray arr, int cx, int cz, int dim, double r, int count, string tier,
            Func<int, int, int> yAt)
        {
            if (tier == null || count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                // Half-step offset so rings don't line up on the same spokes.
                double ang = i * 2.0 * Math.PI / count + Math.PI / count;
                int x = cx + (int)Math.Round(Math.Cos(ang) * r);
                int z = cz + (int)Math.Round(Math.Sin(ang) * r);
                arr.Add(LootSpot(x, yAt(x, z), z, dim, tier));
            }
        }

        private static JObject LootSpot(int x, int y, int z, int dim, string tier)
        {
            var o = JsonPos.Write(x, y, z, dim);
            if (!string.IsNullOrEmpty(tier)) o["Tier"] = tier;
            return o;
        }

        // Blank JSON leaves that part untouched. applyToAllVariants writes into every HG variant of the place.
        public (bool ok, string message) SaveConfig(EventPlace place, string eventCode,
            string lootTablesJson, string zonePhasesJson, bool applyToAllVariants, string scoreboardVisibility = null)
        {
            if (string.IsNullOrEmpty(eventCode)) return (false, Lang.Get("canevents:event-not-found", ""));

            // Unknown values are dropped rather than written; the behavior would fall back to "all" anyway.
            string visibility = scoreboardVisibility?.Trim().ToLowerInvariant();
            if (!BehaviorConfigKeys.ScoreboardVisibilities.Contains(visibility)) visibility = null;

            JObject lootTables = null;
            JArray zonePhases = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(lootTablesJson))
                {
                    lootTables = JObject.Parse(lootTablesJson);
                    if (lootTables.Count == 0) return (false, Lang.Get("canevents:hgcfg-empty-tables"));
                }
                if (!string.IsNullOrWhiteSpace(zonePhasesJson))
                    zonePhases = JArray.Parse(zonePhasesJson);
            }
            catch (Exception ex)
            {
                return (false, Lang.Get("canevents:hgcfg-bad-json", ex.Message));
            }

            if (!applyToAllVariants) return SaveConfig(eventCode, lootTables, zonePhases, visibility);

            var targets = EventsFor(place).Select(e => e.Code).ToList();
            if (!targets.Any(c => c.Equals(eventCode, StringComparison.OrdinalIgnoreCase)))
                targets.Add(eventCode);

            var failures = new List<string>();
            foreach (var code in targets)
            {
                var (evOk, evMsg) = SaveConfig(code, lootTables, zonePhases, visibility);
                if (!evOk) failures.Add(code + ": " + evMsg);
            }
            return (failures.Count < targets.Count, failures.Count == 0
                ? Lang.Get("canevents:hgcfg-saved", string.Join(", ", targets))
                : string.Join("; ", failures));
        }

        // A null part is left untouched.
        private (bool ok, string message) SaveConfig(string code, JObject lootTables, JArray zonePhases,
            string scoreboardVisibility)
            => manager.EditEventBehaviorAttributes(code, BehaviorNames.HungerGames, attr =>
            {
                if (lootTables != null) attr["lootTables"] = lootTables.DeepClone();
                if (zonePhases != null) attr[ShrinkingZone.PhasesKey] = zonePhases.DeepClone();
                if (scoreboardVisibility != null) attr[BehaviorConfigKeys.ScoreboardVisibility] = scoreboardVisibility;
            }, "canevents:hgcfg-saved");

        // preferredCode null falls back to the place's default event.
        public HungerGamesSetup Describe(EventPlace place, string preferredCode = null)
        {
            var ev = ResolveEvent(place, preferredCode);
            var attrs = ev?.GetBehavior<HungerGamesEB>()?.Attributes;

            var setup = new HungerGamesSetup
            {
                PlaceId = place.Id,
                EventCode = ev?.Code,
                EventCodes = EventsFor(place).Select(e => e.Code).ToArray()
            };

            ReadTiers(attrs, out var tiers, out var summaries);
            setup.Tiers = tiers;
            setup.TierSummaries = summaries;
            string defaultTier = attrs != null ? attrs["defaultLootTier"].AsString("common") : "common";
            setup.Overview = attrs != null
                ? BuildOverview(ev, attrs)
                : "No Hunger Games event is configured for this place's tags - loot tiers can't be "
                  + "listed. You can still type a tier by hand.";

            ReadSpots(place, defaultTier, setup);

            setup.LootTablesJson = attrs?["lootTables"]?.Token?.ToString() ?? "{}";
            setup.ZonePhasesJson = attrs?[ShrinkingZone.PhasesKey]?.Token?.ToString() ?? "[]";
            string visibility = attrs?[BehaviorConfigKeys.ScoreboardVisibility]?.AsString(null)?.Trim().ToLowerInvariant();
            if (BehaviorConfigKeys.ScoreboardVisibilities.Contains(visibility)) setup.ScoreboardVisibility = visibility;

            FillChecklist(setup, place, ev, attrs, tiers);
            return setup;
        }

        private static void FillChecklist(HungerGamesSetup setup, EventPlace place, CANEvent ev,
            JsonObject attrs, string[] tiers)
        {
            var spotTiers = setup.SpotTier;
            setup.HasBounds = place.HasBounds;
            setup.SpawnCount = place.spawnPositions?.Count ?? 0;
            setup.RequiredSpawns = ev?.AssemblingEB?.MaxPartiesCount ?? 0;

            bool zoneSet = TryReadZone(place, out var zc, out float zr) && zr > 0f;
            double zx = zc?.X ?? 0, zz = zc?.Z ?? 0;
            setup.ZoneSet = zoneSet;
            setup.ZoneRadius = zr;

            string bestTier = tiers != null && tiers.Length > 0 ? tiers[tiers.Length - 1] : null;
            setup.UsesCornucopiaTier = bestTier != null && spotTiers != null
                && spotTiers.Any(t => string.Equals(t, bestTier, StringComparison.OrdinalIgnoreCase));

            var warnings = new List<string>();
            if (!setup.HasBounds) warnings.Add("No bounds set - the arena has no footprint.");
            if (setup.RequiredSpawns > 0 && setup.SpawnCount < setup.RequiredSpawns)
                warnings.Add(string.Format("Only {0}/{1} spawn points.", setup.SpawnCount, setup.RequiredSpawns));
            if (!zoneSet) warnings.Add("Zone not set - no storm, matches never resolve on time.");
            if (spotTiers == null || spotTiers.Length == 0)
                warnings.Add("No loot spots - tributes fight with only the starting loadout.");
            else if (bestTier != null && !setup.UsesCornucopiaTier)
                warnings.Add("No '" + bestTier + "' loot spot - nothing pulls tributes to the centre.");

            if (zoneSet && place.spawnPositions != null)
            {
                int outside = 0;
                double r2 = zr * zr;
                foreach (var sp in place.spawnPositions)
                {
                    double dx = sp.X + 0.5 - (zx + 0.5);
                    double dz = sp.Z + 0.5 - (zz + 0.5);
                    if (dx * dx + dz * dz > r2) outside++;
                }
                if (outside > 0)
                    warnings.Add(outside + " spawn(s) start outside the zone.");
            }

            // Allowed, just flagged.
            if (zoneSet && PlaceBounds.Has(place))
            {
                float inside = RoomInsideBounds(place, zc.X, zc.Z);
                if (zr > inside)
                    warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Zone (r={0:0.#}) reaches past the arena bounds (room for r={1:0.#}). Fine if intended.",
                        zr, Math.Max(0f, inside)));
            }

            if (attrs != null)
            {
                float grace = attrs["gracePeriodSeconds"].AsFloat(0f);
                if (grace > 0f && grace > FirstStormDamageTime(attrs))
                    warnings.Add(string.Format(
                        "Grace period ({0}s) outlasts the first calm zone phase - tributes take storm damage during the truce.",
                        (int)grace));

                if (!ConfigHasFood(attrs))
                    warnings.Add(string.Format(
                        "No food in the loadout or loot for a {0}s match - tributes may starve.",
                        (int)attrs[BehaviorConfigKeys.TimeLimitSeconds].AsFloat(BehaviorConfigKeys.HungerGamesTimeLimitSeconds)));
            }

            setup.Warnings = warnings.ToArray();
        }

        // First second the storm deals damage; float.MaxValue if never.
        private static float FirstStormDamageTime(JsonObject attrs)
        {
            if (!(attrs?[ShrinkingZone.PhasesKey]?.Token is JArray phases)) return float.MaxValue;

            var list = new List<(float end, float dps)>();
            foreach (var p in phases)
                if (p is JObject po)
                    list.Add((po[ShrinkingZone.EndsAtKey]?.Value<float>() ?? 0f,
                              po[ShrinkingZone.OutsideDpsKey]?.Value<float>() ?? ShrinkingZone.DefaultOutsideDps));
            list.Sort((a, b) => a.end.CompareTo(b.end));

            float prevEnd = 0f;
            foreach (var ph in list)
            {
                if (ph.dps > 0f) return prevEnd;
                prevEnd = ph.end;
            }
            return float.MaxValue;
        }

        private static readonly string[] FoodWords =
        {
            "bread", "meat", "fish", "berry", "fruit", "vegetable", "cheese", "pie", "porridge", "egg",
            "honey", "mushroom", "jerky", "poultry", "bushmeat", "redmeat", "soup", "stew", "sushi",
            "meal", "grain", "seeds", "nut", "carrot", "cabbage", "onion", "turnip", "parsnip", "pumpkin"
        };

        // Rough name match; only warns when nothing looks edible.
        private static bool ConfigHasFood(JsonObject attrs)
        {
            if (attrs == null) return true; // unknown, don't warn
            foreach (var code in AllItemCodes(attrs))
            {
                string c = code.ToLowerInvariant();
                foreach (var w in FoodWords)
                    if (c.Contains(w)) return true;
            }
            return false;
        }

        private static IEnumerable<string> AllItemCodes(JsonObject attrs)
        {
            if (attrs[BehaviorConfigKeys.LoadoutSets]?.Token is JArray sets)
                foreach (var s in sets)
                    if (s is JObject so && so["items"] is JArray items)
                        foreach (var it in items)
                        {
                            string code = (it as JObject)?["code"]?.Value<string>();
                            if (!string.IsNullOrEmpty(code)) yield return code;
                        }

            if (attrs["lootTables"]?.Token is JObject jo)
                foreach (var prop in jo.Properties())
                    if (prop.Value is JArray arr)
                        foreach (var it in arr)
                        {
                            string code = it.Value<string>("code");
                            if (!string.IsNullOrEmpty(code)) yield return code;
                        }
        }

        public static void ReadTiers(JsonObject attrs, out string[] tiers, out string[] summaries)
        {
            var tl = new List<string>();
            var sl = new List<string>();

            var tablesAttr = attrs?["lootTables"];
            if (tablesAttr != null && tablesAttr.Token is JObject jo)
            {
                foreach (var prop in jo.Properties())
                {
                    if (!(prop.Value is JArray arr)) continue;
                    tl.Add(prop.Name);

                    var names = new List<string>();
                    foreach (var it in arr)
                    {
                        string code = it.Value<string>("code");
                        if (string.IsNullOrEmpty(code)) continue;
                        int c = code.IndexOf(':');
                        names.Add(c >= 0 ? code.Substring(c + 1) : code);
                    }
                    int shown = Math.Min(5, names.Count);
                    string preview = names.Count == 0
                        ? "(empty)"
                        : string.Join(", ", names.Take(shown))
                          + (names.Count > shown ? " +" + (names.Count - shown) + " more" : "");
                    sl.Add(names.Count + " items: " + preview);
                }
            }

            tiers = tl.ToArray();
            summaries = sl.ToArray();
        }

        private static string BuildOverview(CANEvent ev, JsonObject attrs)
        {
            var a = ev.AssemblingEB;
            int minP = a?.MinPartiesCount ?? 0;
            int maxP = a?.MaxPartiesCount ?? 0;
            int partySize = a?.MaxPartySize ?? 1;
            string who = partySize <= 1 ? "tributes" : "teams";
            string comp = partySize <= 1 ? "One-life FFA" : "One-life, teams of up to " + partySize;

            int time = (int)attrs[BehaviorConfigKeys.TimeLimitSeconds].AsFloat(BehaviorConfigKeys.HungerGamesTimeLimitSeconds);
            int grace = (int)attrs["gracePeriodSeconds"].AsFloat(0f);

            string zone = "no shrinking zone";
            var phases = attrs[ShrinkingZone.PhasesKey];
            if (phases != null && phases.IsArray())
            {
                // Phase radii are proportions; ShrinkingZone scales the largest to the arena.
                var raw = phases.AsArray().Select(p => p[ShrinkingZone.RadiusKey].AsFloat(0f)).ToArray();
                float toPct = ShrinkingZone.ScaleFor(100f, raw.Length > 0 ? raw.Max() : 0f);
                zone = "zone shrinks " + string.Join("→", raw.Select(r => (int)Math.Round(r * toPct) + "%"))
                    + " of the arena over " + time + "s";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(comp).Append(", ").Append(minP).Append('-').Append(maxP).Append(' ').Append(who).Append(". ");
            sb.Append(char.ToUpper(zone[0])).Append(zone.Substring(1)).Append(". ");
            if (grace > 0) sb.Append("Grace ").Append(grace).Append("s (no PvP). ");
            sb.Append("Loot from the chests you place below. Winner = last standing (time-up → most kills, tie → draw).");
            return sb.ToString();
        }

        // Malformed entries stay as 0,0,0/"?" so panel indices match the stored array.
        private static void ReadSpots(EventPlace place, string defaultTier, HungerGamesSetup setup)
        {
            var lx = new List<int>(); var ly = new List<int>(); var lz = new List<int>(); var lt = new List<string>();

            if (place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data) && data["LootSpots"] is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (JsonPos.TryRead(t, out var pos))
                    {
                        lx.Add(pos.X); ly.Add(pos.Y); lz.Add(pos.Z);
                        string tier = (t as JObject)?["Tier"]?.Value<string>();
                        lt.Add(string.IsNullOrWhiteSpace(tier) ? defaultTier : tier);
                    }
                    else
                    {
                        lx.Add(0); ly.Add(0); lz.Add(0); lt.Add("?");
                    }
                }
            }

            setup.SpotX = lx.ToArray(); setup.SpotY = ly.ToArray(); setup.SpotZ = lz.ToArray(); setup.SpotTier = lt.ToArray();
        }

        // admin uid -> place id
        private readonly Dictionary<string, long> previews = new Dictionary<string, long>();
        private long previewTickId = -1;

        public void StartPreview(string playerUid, long placeId)
        {
            previews[playerUid] = placeId;
            if (previewTickId < 0)
                previewTickId = sapi.Event.RegisterGameTickListener(PreviewTick, 1000);
        }

        public void StopPreview(string playerUid)
        {
            previews.Remove(playerUid);
            StopPreviewTickIfIdle();
        }

        public void StopAllPreviews()
        {
            if (previewTickId >= 0) sapi.Event.UnregisterGameTickListener(previewTickId);
            previewTickId = -1;
            previews.Clear();
        }

        private void StopPreviewTickIfIdle()
        {
            if (previews.Count == 0 && previewTickId >= 0)
            {
                sapi.Event.UnregisterGameTickListener(previewTickId);
                previewTickId = -1;
            }
        }

        private void PreviewTick(float dt)
        {
            // Draw each place once even with several viewers; the client particle pool is finite.
            var drawn = new HashSet<long>();
            foreach (var kvp in previews.ToList())
            {
                var pl = sapi.World.PlayerByUid(kvp.Key) as IServerPlayer;
                if (pl == null) { previews.Remove(kvp.Key); continue; }  // gone offline
                if (!drawn.Add(kvp.Value)) continue;
                var place = manager.eventPlacesManager.FindById(kvp.Value);
                if (place == null) continue;
                // Bad hand-edited place data: stop this preview instead of throwing every tick.
                try
                {
                    DrawPreview(place);
                }
                catch (Exception e)
                {
                    previews.Remove(kvp.Key);
                    sapi.World.Logger.Warning("HG preview for place {0} failed, preview stopped: {1}",
                        kvp.Value, e.Message);
                    pl.SendMessage(0, Lang.Get("canevents:hg-preview-failed"),
                        Vintagestory.API.Common.EnumChatType.Notification);
                }
            }
            StopPreviewTickIfIdle();
        }

        // Dome = starting storm, grey rings = phase radii, green = spawns, tier colour = loot.
        private void DrawPreview(EventPlace place)
        {
            var ev = ResolveEvent(place);
            var attrs = ev?.GetBehavior<HungerGamesEB>()?.Attributes;
            ReadTiers(attrs, out var tiers, out _);

            if (place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data))
            {
                if (TryReadZone(place, out var zc, out float r) && r > 0f)
                {
                    // +1: at +0.5 the bottom ring is inside the floor block and hidden.
                    double cx = zc.X + 0.5, cy = zc.Y + 1.0, cz = zc.Z + 0.5;
                    // Rain map is only valid in dim 0; 0 = column not loaded.
                    var ba = sapi.World.BlockAccessor;
                    Func<double, double, double> groundY = zc.dimension != 0 ? null : (x, z) =>
                    {
                        int h = ba.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z));
                        return h > 0 ? h + 1.0 : double.NaN;
                    };
                    ZoneVisualizer.DrawDome(sapi, cx, cy, cz, r, ZoneVisualizer.StormColor, groundY);

                    // Same ShrinkingZone maths as the match.
                    if (attrs != null)
                    {
                        var schedule = new ShrinkingZone { InitialRadius = r };
                        schedule.ParsePhases(attrs);
                        for (int i = 0; i < schedule.PhaseCount; i++)
                        {
                            float pr = schedule.PhaseAt(i).radius;
                            if (pr <= 0.5f || pr >= r - 0.5f) continue; // the dome already shows r; 0 is a point
                            int segs = Math.Min(24, Math.Max(8, (int)(pr * 0.8f)));
                            ZoneVisualizer.DrawRing(sapi, cx, cy, cz, pr, segs, ZoneVisualizer.PhaseRingColor);
                        }
                    }
                }

                if (data["LootSpots"] is JArray arr)
                {
                    // Hundreds of rings exhausted the client particle pool; block highlight marks spots anyway.
                    bool sparse = arr.Count > 60;
                    foreach (var t in arr)
                    {
                        if (!JsonPos.TryRead(t, out var spot)) continue;
                        int colour = ZoneVisualizer.LootTierColor(TierIndex(tiers, (t as JObject)?["Tier"]?.Value<string>()));
                        if (sparse)
                            ZoneVisualizer.DrawPoint(sapi, spot.X + 0.5, spot.Y + 1.2, spot.Z + 0.5, colour);
                        else
                            ZoneVisualizer.DrawRing(sapi, spot.X + 0.5, spot.Y + 1.0, spot.Z + 0.5, 0.6f, 4, colour);
                    }
                }
            }

            if (place.spawnPositions != null)
                foreach (var sp in place.spawnPositions)
                    ZoneVisualizer.DrawRing(sapi, sp.X + 0.5, sp.Y + 0.5, sp.Z + 0.5, 0.5f, 5,
                        ZoneVisualizer.SpawnMarkerColor);
        }

        private static int TierIndex(string[] tiers, string tier)
        {
            if (tiers == null || string.IsNullOrEmpty(tier)) return -1;
            for (int i = 0; i < tiers.Length; i++)
                if (string.Equals(tiers[i], tier, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
