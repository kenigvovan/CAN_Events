using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server side of the Hunger Games setup panel: a live picture of a place (loot tiers, match
    // overview, loot spots) plus per-spot remove and re-tier. Tiers and overview are read straight
    // off the template event's HungerGames behavior Attributes, so no config copy needs syncing.
    public partial class ServerNetworkHandler
    {
        private void OnRequestHgSetup(IServerPlayer player, RequestHgSetupMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            SendHgSetup(player, place, msg.EventCode);
        }

        // Place setup: zone and loot spots. Addressed by place id, coordinates from the admin's feet;
        // the edits live in PlaceConfigService, shared with the /canep commands.

        // A HG place edit is the shared PlaceEdit flow plus this mode's own panel refresh.
        private void HgEdit(IServerPlayer player, long placeId, Func<EventPlace, PlaceEditResult> edit,
            bool refreshOnFailure = false)
            => PlaceEdit(player, placeId, edit, place => SendHgSetup(player, place), refreshOnFailure);

        private void OnSetHgZone(IServerPlayer player, SetHgZoneMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.SetHungerGamesZone(p, FeetBlock(player), msg.Radius));

        private void OnAddHgLoot(IServerPlayer player, AddHgLootMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.AddHungerGamesLoot(p, FeetBlock(player), msg.Tier));

        private void OnClearHgLoot(IServerPlayer player, ClearHgLootMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.ClearHungerGamesLoot(p));

        // Index-addressed: refresh even on rejection, see PlaceEdit.
        private void OnRemoveHgLoot(IServerPlayer player, RemoveHgLootMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.RemoveHungerGamesLoot(p, msg.Index), refreshOnFailure: true);

        private void OnSetHgLootTier(IServerPlayer player, SetHgLootTierMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.SetHungerGamesLootTier(p, msg.Index, msg.Tier), refreshOnFailure: true);

        private void OnListHgLoot(IServerPlayer player, ListHgLootMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            ReplyOk(player, PlaceConfig.DescribeHungerGamesLoot(place), place.Id);
        }

        // In-world preview: zone ring + loot/spawn markers, drawn while an admin has it on.

        private readonly Dictionary<string, long> hgPreview = new Dictionary<string, long>();
        private long hgPreviewTickId = -1;

        private void OnHgPreview(IServerPlayer player, HgPreviewMsg msg)
        {
            if (!RequireAdmin(player)) return;

            if (msg.Enabled)
            {
                if (!TryResolvePlace(player, msg.PlaceId, out _)) return;
                hgPreview[player.PlayerUID] = msg.PlaceId;
                if (hgPreviewTickId < 0)
                    hgPreviewTickId = sapi.Event.RegisterGameTickListener(HgPreviewTick, 1000);
            }
            else
            {
                hgPreview.Remove(player.PlayerUID);
                StopPreviewTickIfIdle();
            }
        }

        internal void StopHgPreviewTick()
        {
            if (hgPreviewTickId >= 0) sapi.Event.UnregisterGameTickListener(hgPreviewTickId);
            hgPreviewTickId = -1;
            hgPreview.Clear();
        }

        private void StopPreviewTickIfIdle()
        {
            if (hgPreview.Count == 0 && hgPreviewTickId >= 0)
            {
                sapi.Event.UnregisterGameTickListener(hgPreviewTickId);
                hgPreviewTickId = -1;
            }
        }

        private void HgPreviewTick(float dt)
        {
            foreach (var kvp in hgPreview.ToList())
            {
                var pl = sapi.World.PlayerByUid(kvp.Key) as IServerPlayer;
                if (pl == null) { hgPreview.Remove(kvp.Key); continue; }  // gone offline
                var place = eventManager.eventPlacesManager.FindById(kvp.Value);
                if (place == null) continue;
                // Hand-editable place data can throw here, and this runs on a timer - so log once
                // and drop that viewer's preview rather than erroring every tick forever.
                try
                {
                    DrawHgPreview(place);
                }
                catch (Exception e)
                {
                    hgPreview.Remove(kvp.Key);
                    sapi.World.Logger.Warning("HG preview for place {0} failed, preview stopped: {1}",
                        kvp.Value, e.Message);
                    // Fully qualified: this file cannot `using Vintagestory.API.Common` - its Func<>
                    // collides with System.Func<>.
                    pl.SendMessage(0, Lang.Get("canevents:hg-preview-failed"),
                        Vintagestory.API.Common.EnumChatType.Notification);
                }
            }
            StopPreviewTickIfIdle();
        }

        // Server-side particles reach only nearby clients, so this costs nothing to players elsewhere.
        // Grey ring = the storm's starting size, blue dots = spawns, tier-coloured dots = loot chests.
        private void DrawHgPreview(EventPlace place)
        {
            var ev = ResolveHgEvent(place);
            ReadHgTiers(ev?.GetBehavior<HungerGamesEB>()?.Attributes, out var tiers, out _);

            if (place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data))
            {
                if (data["X"] != null && data["Y"] != null && data["Z"] != null && data["InitialRadius"] != null)
                {
                    // Y is the floor block; +1 stands the preview dome on its surface. At +0.5 the
                    // bottom ring spawns inside the block and the terrain hides it.
                    double cx = data.Value<double>("X") + 0.5, cy = data.Value<double>("Y") + 1.0, cz = data.Value<double>("Z") + 0.5;
                    float r = (float)data.Value<double>("InitialRadius");
                    ZoneVisualizer.DrawDome(sapi, cx, cy, cz, r, ZoneVisualizer.StormColor);
                }

                if (data["LootSpots"] is JArray arr)
                {
                    foreach (var t in arr)
                    {
                        if (!JsonPos.TryRead(t, out var spot)) continue;
                        int ti = TierIndex(tiers, (t as JObject)?["Tier"]?.Value<string>());
                        ZoneVisualizer.DrawRing(sapi, spot.X + 0.5, spot.Y + 1.0, spot.Z + 0.5,
                            0.6f, 6, ZoneVisualizer.OwnerColor(ti));
                    }
                }
            }

            if (place.spawnPositions != null)
                foreach (var sp in place.spawnPositions)
                    ZoneVisualizer.DrawRing(sapi, sp.X + 0.5, sp.Y + 0.5, sp.Z + 0.5, 0.5f, 5,
                        ZoneVisualizer.OwnerColor(-2)); // contested/blue reads as "spawn"
        }

        private static int TierIndex(string[] tiers, string tier)
        {
            if (tiers == null || string.IsNullOrEmpty(tier)) return -1;
            for (int i = 0; i < tiers.Length; i++)
                if (string.Equals(tiers[i], tier, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // One-click arena: ring of spawns + zone + tiered loot, all from the admin's feet.
        private void OnGenerateHgLayout(IServerPlayer player, GenerateHgLayoutMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            if (!RequireNotOccupied(player, place)) return;

            // Zone centre + loot sit on the floor block ("block under your feet"); spawns sit on the
            // air block above it, so a tribute teleported to spawnY+0.5 stands on the floor below.
            var stand = player.Entity.Pos.AsBlockPos;
            int spawnY = stand.Y;
            var floor = stand.DownCopy();
            int cx = floor.X, cy = floor.Y, cz = floor.Z, dim = floor.dimension;

            // Terrain snapping makes each column's Y follow the ground; off, everything keeps the caller's
            // flat level (schematic floors). Rain map, not the worldgen terrain map: it updates on block
            // place/remove, so a built arena floor counts as ground. It is meaningless outside dimension 0
            // and returns 0 for an unloaded column; both fall back to the flat level rather than Y≈1.
            var ba = sapi.World.BlockAccessor;
            bool canSnap = msg.SnapToTerrain && dim == 0;
            // Rain-map value is the Y of the topmost solid block itself; 0 means "column not loaded".
            int TopSolidY(int x, int z) => canSnap ? ba.GetRainMapHeightAt(x, z) : 0;
            int SpawnYAt(int x, int z) { int h = TopSolidY(x, z); return h > 0 ? h + 1 : spawnY; }
            int LootYAt(int x, int z) { int h = TopSolidY(x, z); return h > 0 ? h : cy; }

            float radius = msg.Radius > 0f ? msg.Radius : DefaultLayoutRadius(place);
            if (radius < 4f) radius = 4f;

            // The admin's radius is authoritative (the storm reads only InitialRadius), but a ring poking
            // out of the bounds puts spawns outside them - so warn, don't clamp.
            bool radiusOverBounds = radius > MaxLayoutRadius(place, cx, cz);

            var ev = ResolveHgEvent(place);
            int n = ev?.AssemblingEB?.MaxPartiesCount ?? 8;
            n = Math.Max(2, Math.Min(24, n));

            // Zone.
            var data = place.GetOrCreateBehaviorData(BehaviorKeys.HungerGames);
            data["X"] = cx; data["Y"] = LootYAt(cx, cz); data["Z"] = cz;
            data["InitialRadius"] = radius;

            // Spawn ring, just inside the starting edge so tributes don't begin in the storm.
            var spawns = new List<BlockPos>();
            double rSpawn = radius * 0.85;
            for (int i = 0; i < n; i++)
            {
                double ang = i * 2.0 * Math.PI / n;
                int sx = cx + (int)Math.Round(Math.Cos(ang) * rSpawn);
                int sz = cz + (int)Math.Round(Math.Sin(ang) * rSpawn);
                spawns.Add(new BlockPos(sx, SpawnYAt(sx, sz), sz, dim));
            }
            place.spawnPositions = spawns;

            // Loot: best tier at the centre (cornucopia), a mid ring, and an outer ring by the spawns.
            ReadHgTiers(ev?.GetBehavior<HungerGamesEB>()?.Attributes, out var tiers, out _);
            string outerTier = tiers.Length > 0 ? tiers[0] : null;
            string midTier = tiers.Length > 0 ? tiers[Math.Min(tiers.Length - 1, tiers.Length / 2)] : null;
            string centreTier = tiers.Length > 0 ? tiers[tiers.Length - 1] : null;

            var loot = new JArray { LootSpot(cx, LootYAt(cx, cz), cz, dim, centreTier) };
            AddLootRing(loot, cx, cz, dim, radius * 0.45, Math.Max(2, n / 3), midTier, LootYAt);
            AddLootRing(loot, cx, cz, dim, radius * 0.8, n, outerTier, LootYAt);
            data["LootSpots"] = loot;

            eventManager.eventPlacesManager.SavePlace(place);
            ReplyOk(player, string.Format(
                "Generated HG layout on '{0}': {1} spawns, zone r={2:0}, {3} loot chests.{4} Tune anything below.",
                place.Name, n, radius, loot.Count,
                radiusOverBounds ? " WARNING: the ring extends beyond the place bounds - spawns may land outside." : ""), place.Id);
            SendHgSetup(player, place);
            SendPlaceListTo(player);
        }

        /// <summary>Largest ring radius staying inside the place bounds (2-block margin, as
        /// DefaultLayoutRadius); unbounded when the place has none. Used only to warn the admin.</summary>
        private static float MaxLayoutRadius(EventPlace place, int cx, int cz)
        {
            if (!place.HasBounds || place.BoundsMin == null || place.BoundsMax == null) return float.MaxValue;
            int minX = Math.Min(place.BoundsMin.X, place.BoundsMax.X), maxX = Math.Max(place.BoundsMin.X, place.BoundsMax.X);
            int minZ = Math.Min(place.BoundsMin.Z, place.BoundsMax.Z), maxZ = Math.Max(place.BoundsMin.Z, place.BoundsMax.Z);
            return Math.Min(Math.Min(cx - minX, maxX - cx), Math.Min(cz - minZ, maxZ - cz)) - 2;
        }

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
                // Offset each ring's phase so inner/outer chests don't line up on the same spokes.
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

        private void OnSaveHgConfig(IServerPlayer player, SaveHgConfigMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (msg == null || string.IsNullOrEmpty(msg.EventCode))
            {
                ReplyFail(player, Lang.Get("canevents:event-not-found", ""));
                return;
            }

            // Parse here so a malformed payload is rejected before it touches the config file;
            // empty/whitespace leaves that part untouched.
            JObject lootTables = null;
            JArray zonePhases = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(msg.LootTablesJson))
                {
                    lootTables = JObject.Parse(msg.LootTablesJson);
                    if (lootTables.Count == 0) { ReplyFail(player, Lang.Get("canevents:hgcfg-empty-tables")); return; }
                }
                if (!string.IsNullOrWhiteSpace(msg.ZonePhasesJson))
                    zonePhases = JArray.Parse(msg.ZonePhasesJson);
            }
            catch (Exception ex)
            {
                ReplyFail(player, Lang.Get("canevents:hgcfg-bad-json", ex.Message));
                return;
            }

            var place = eventManager.eventPlacesManager.FindById(msg.PlaceId);

            bool ok;
            string message;
            if (msg.ApplyToAllHgEvents)
            {
                // One arena, one loot pool: write the same tables into every HG variant, so tuning
                // can't land in whichever variant the panel happened to have loaded.
                var targets = HgEventsForPlace(place).Select(e => e.Code).ToList();
                if (!targets.Any(c => c.Equals(msg.EventCode, StringComparison.OrdinalIgnoreCase)))
                    targets.Add(msg.EventCode);

                var failures = new List<string>();
                foreach (var code in targets)
                {
                    var (evOk, evMsg) = eventManager.UpdateEventHgConfig(code, lootTables, zonePhases);
                    if (!evOk) failures.Add(code + ": " + evMsg);
                }
                ok = failures.Count < targets.Count;
                message = failures.Count == 0
                    ? Lang.Get("canevents:hgcfg-saved", string.Join(", ", targets))
                    : string.Join("; ", failures);
            }
            else
            {
                (ok, message) = eventManager.UpdateEventHgConfig(msg.EventCode, lootTables, zonePhases);
            }

            Reply(player, ok, message);
            if (!ok) return;

            // Reflect the saved config back into the panel (tiers/preview/editor buffers).
            if (place != null) SendHgSetup(player, place, msg.EventCode);
            BroadcastEventList(); // the browser preview reads event config too
        }

        /// <summary>Pushes the full HG setup picture for one place to one admin; preferredCode is the
        /// variant picked in the panel, null falls back to the place's default event.</summary>
        private void SendHgSetup(IServerPlayer player, EventPlace place, string preferredCode = null)
        {
            var ev = ResolveHgEvent(place, preferredCode);
            var hg = ev?.GetBehavior<HungerGamesEB>();
            var attrs = hg?.Attributes;

            var msg = new HgSetupMsg
            {
                PlaceId = place.Id,
                EventCode = ev?.Code,
                HgEventCodes = HgEventsForPlace(place).Select(e => e.Code).ToArray()
            };

            ReadHgTiers(attrs, out var tiers, out var summaries);
            msg.Tiers = tiers;
            msg.TierSummaries = summaries;
            string defaultTier = attrs != null ? attrs["defaultLootTier"].AsString("common") : "common";
            msg.Overview = attrs != null
                ? BuildHgOverview(ev, attrs)
                : "No Hunger Games event is configured for this place's tags - loot tiers can't be "
                  + "listed. You can still type a tier by hand.";

            ReadHgSpots(place, defaultTier, out var xs, out var ys, out var zs, out var st);
            msg.SpotX = xs; msg.SpotY = ys; msg.SpotZ = zs; msg.SpotTier = st;

            // Raw config for the advanced editor.
            msg.LootTablesJson = attrs?["lootTables"]?.Token?.ToString() ?? "{}";
            msg.ZonePhasesJson = attrs?["zonePhases"]?.Token?.ToString() ?? "[]";

            FillHgReadiness(msg, place, ev, attrs, tiers, st);

            serverChannel.SendPacket(msg, player);
        }

        // "Will this play well?": the checklist and the warnings the panel renders.
        private static void FillHgReadiness(HgSetupMsg msg, EventPlace place, CANEvent ev,
            JsonObject attrs, string[] tiers, string[] spotTiers)
        {
            msg.HasBounds = place.HasBounds;
            msg.SpawnCount = place.spawnPositions?.Count ?? 0;
            msg.RequiredSpawns = ev?.AssemblingEB?.MaxPartiesCount ?? 0;

            // Zone is "set" only with a real centre and a positive radius.
            double zx = 0, zz = 0, zr = 0;
            bool zoneSet = false;
            if (place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)
                && data["X"] != null && data["Y"] != null && data["Z"] != null && data["InitialRadius"] != null)
            {
                zx = data.Value<double>("X");
                zz = data.Value<double>("Z");
                zr = data.Value<double>("InitialRadius");
                zoneSet = zr > 0;
            }
            msg.ZoneSet = zoneSet;
            msg.ZoneRadius = (float)zr;

            string bestTier = tiers != null && tiers.Length > 0 ? tiers[tiers.Length - 1] : null;
            msg.UsesCornucopiaTier = bestTier != null && spotTiers != null
                && spotTiers.Any(t => string.Equals(t, bestTier, StringComparison.OrdinalIgnoreCase));

            var warnings = new List<string>();
            if (!msg.HasBounds) warnings.Add("No bounds set - the arena has no footprint.");
            if (msg.RequiredSpawns > 0 && msg.SpawnCount < msg.RequiredSpawns)
                warnings.Add(string.Format("Only {0}/{1} spawn points.", msg.SpawnCount, msg.RequiredSpawns));
            if (!zoneSet) warnings.Add("Zone not set - no storm, matches never resolve on time.");
            if (spotTiers == null || spotTiers.Length == 0)
                warnings.Add("No loot spots - tributes fight with only the starting loadout.");
            else if (bestTier != null && !msg.UsesCornucopiaTier)
                warnings.Add("No '" + bestTier + "' loot spot - nothing pulls tributes to the centre.");

            // Spawns starting outside the storm take damage almost immediately.
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

            // Config-level pitfalls read from the event itself.
            if (attrs != null)
            {
                float grace = attrs["gracePeriodSeconds"].AsFloat(0f);
                if (grace > 0f && grace > FirstStormDamageTime(attrs))
                    warnings.Add(string.Format(
                        "Grace period ({0}s) outlasts the first calm zone phase - tributes take storm damage during the truce.",
                        (int)grace));

                if (!HgConfigHasFood(attrs))
                    warnings.Add(string.Format(
                        "No food in the loadout or loot for a {0}s match - tributes may starve.",
                        (int)attrs["timeLimitSeconds"].AsFloat(900f)));
            }

            msg.Warnings = warnings.ToArray();
        }

        // Earliest elapsed second at which standing outside deals damage; float.MaxValue if the storm
        // never hurts. Used to catch a grace window that runs into the storm.
        private static float FirstStormDamageTime(JsonObject attrs)
        {
            if (!(attrs?["zonePhases"]?.Token is JArray phases)) return float.MaxValue;

            var list = new List<(float end, float dps)>();
            foreach (var p in phases)
                if (p is JObject po)
                    list.Add((po["endsAtSeconds"]?.Value<float>() ?? 0f, po["outsideDps"]?.Value<float>() ?? 0f));
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

        // Deliberately conservative heuristic: only fires when nothing edible is found anywhere.
        private static bool HgConfigHasFood(JsonObject attrs)
        {
            if (attrs == null) return true; // unknown → don't warn
            foreach (var code in HgAllItemCodes(attrs))
            {
                string c = code.ToLowerInvariant();
                foreach (var w in FoodWords)
                    if (c.Contains(w)) return true;
            }
            return false;
        }

        private static IEnumerable<string> HgAllItemCodes(JsonObject attrs)
        {
            if (attrs["loadoutSets"]?.Token is JArray sets)
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

        /// <summary>
        /// Every HG event that could run on this place, ordered by code so the panel's list and default
        /// pick are stable across restarts (event files load in directory order). Falls back to all HG
        /// events when none match the place's tags.
        /// </summary>
        private List<CANEvent> HgEventsForPlace(EventPlace place)
        {
            var hgEvents = eventManager.Events
                .Where(e => e.GetBehavior<HungerGamesEB>() != null)
                .OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (place?.placeTypes == null) return hgEvents;

            var matching = hgEvents.Where(e => e.EventTypes != null && e.EventTypes.Count > 0
                && e.EventTypes.All(t => place.placeTypes.Contains(t))).ToList();
            return matching.Count > 0 ? matching : hgEvents;
        }

        // The HG event applicable to this place: preferredCode wins when it is a real HG event, else the
        // first applicable one (each variant keeps its OWN loot tables). Not IsAvailableFor, which also
        // demands the place be free/enabled/spawned - often false mid-setup.
        private CANEvent ResolveHgEvent(EventPlace place, string preferredCode = null)
        {
            var candidates = HgEventsForPlace(place);
            if (!string.IsNullOrEmpty(preferredCode))
            {
                var picked = candidates.FirstOrDefault(e => e.Code.Equals(preferredCode, StringComparison.OrdinalIgnoreCase));
                if (picked == null)
                {
                    // Outside this place's tags but still an HG event: honour the variant the admin
                    // named rather than silently editing another one's config.
                    var byCode = eventManager.FindEventByCode(preferredCode);
                    if (byCode?.GetBehavior<HungerGamesEB>() != null) picked = byCode;
                }
                if (picked != null) return picked;
            }
            return candidates.FirstOrDefault();
        }


        private static void ReadHgTiers(JsonObject attrs, out string[] tiers, out string[] summaries)
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

        private static string BuildHgOverview(CANEvent ev, JsonObject attrs)
        {
            var a = ev.AssemblingEB;
            int minP = a?.MinPartiesCount ?? 0;
            int maxP = a?.MaxPartiesCount ?? 0;
            int partySize = a?.MaxPartySize ?? 1;
            string who = partySize <= 1 ? "tributes" : "teams";
            string comp = partySize <= 1 ? "One-life FFA" : "One-life, teams of up to " + partySize;

            int time = (int)attrs["timeLimitSeconds"].AsFloat(900f);
            int grace = (int)attrs["gracePeriodSeconds"].AsFloat(0f);

            string zone = "no shrinking zone";
            var phases = attrs["zonePhases"];
            if (phases != null && phases.IsArray())
            {
                var radii = phases.AsArray().Select(p => ((int)p["radius"].AsFloat(0f)).ToString());
                zone = "zone shrinks " + string.Join("→", radii) + " over " + time + "s";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(comp).Append(", ").Append(minP).Append('-').Append(maxP).Append(' ').Append(who).Append(". ");
            sb.Append(char.ToUpper(zone[0])).Append(zone.Substring(1)).Append(". ");
            if (grace > 0) sb.Append("Grace ").Append(grace).Append("s (no PvP). ");
            sb.Append("Loot from the chests you place below. Winner = last standing (time-up → most kills, tie → draw).");
            return sb.ToString();
        }

        // One entry per raw array element, in order, so panel indices line up with the stored array for
        // remove / re-tier. Malformed entries map to 0,0,0 / "?" rather than being dropped, which would
        // shift every later index.
        private static void ReadHgSpots(EventPlace place, string defaultTier,
            out int[] xs, out int[] ys, out int[] zs, out string[] tiers)
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

            xs = lx.ToArray(); ys = ly.ToArray(); zs = lz.ToArray(); tiers = lt.ToArray();
        }
    }
}
