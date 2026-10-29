using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    // All place edits, shared by /canep and the admin panel. Edits are refused while a match uses the place.
    public partial class PlaceConfigService
    {
        private readonly DefaultEventPlacesManager placeManager;
        // Getter because the placer is created after this service.
        private readonly Func<SchematicPlacer> placer;

        // Caps the per-team grow loop so a bad packet can't allocate unbounded slots.
        public const int MaxCtfTeams = 8;

        // Cage is built block by block on the main thread.
        public const int MinCageDim = 1;
        public const int MaxCageDim = 128;

        public const string DefaultCageBlock = "game:glass-plain";

        public PlaceConfigService(DefaultEventPlacesManager placeManager, Func<SchematicPlacer> placer = null)
        {
            this.placeManager = placeManager;
            this.placer = placer;
        }

        public const int MaxNameLength = 40;
        public const int MaxDescriptionLength = 200;

        // File name is sanitized separately in DefaultEventPlacesManager.FileNameFor.
        public static string CleanText(string raw, int maxLength)
        {
            if (raw == null) return null;
            var sb = new StringBuilder();
            foreach (char c in raw) sb.Append(char.IsControl(c) ? ' ' : c);
            string s = sb.ToString().Trim();
            if (s.Length > maxLength) s = s.Substring(0, maxLength).TrimEnd();
            return s.Length == 0 ? null : s;
        }

        public PlaceEditResult Rename(EventPlace place, string name)
        {
            if (!TryEdit(place, out var busy)) return busy;
            string clean = CleanText(name, MaxNameLength);
            if (clean == null) return PlaceEditResult.Fail(Lang.Get("canevents:place-name-empty"));

            string old = place.Name;
            place.Name = clean;
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:place-renamed", old ?? "?", clean));
        }

        public PlaceEditResult SetDescription(EventPlace place, string description)
        {
            if (!TryEdit(place, out var busy)) return busy;
            place.Description = CleanText(description, MaxDescriptionLength);
            PersistIfSaved(place);
            return PlaceEditResult.Success(place.Description == null
                ? Lang.Get("canevents:place-description-cleared", place.Name)
                : Lang.Get("canevents:place-description-set", place.Name));
        }

        public PlaceEditResult AddTag(EventPlace place, string tag)
        {
            if (!TryEdit(place, out var busy)) return busy;
            tag = PlaceTags.Normalize(tag);
            if (string.IsNullOrEmpty(tag)) return PlaceEditResult.Fail(Lang.Get("canevents:place-type-invalid"));

            place.AddType(tag);
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:place-tags-now", place.Name, TagText(place)));
        }

        public PlaceEditResult RemoveTag(EventPlace place, string tag)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!place.RemoveType(tag))
                return PlaceEditResult.Fail(Lang.Get("canevents:place-tag-missing", place.Name, PlaceTags.Normalize(tag)));

            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:place-tags-now", place.Name, TagText(place)));
        }

        public PlaceEditResult SetEnabled(EventPlace place, bool enabled)
        {
            if (!TryEdit(place, out var busy)) return busy;
            place.Enabled = enabled;
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get(enabled
                ? "canevents:place-toggled-enabled"
                : "canevents:place-toggled-disabled", place.Name));
        }

        // Bulk edit from the Arenas tab, saved once.
        public PlaceEditResult ApplyBulk(EventPlace place, bool? enabled, string addTag, string removeTag,
            out bool changed)
        {
            changed = false;
            if (!TryEdit(place, out var busy)) return busy;

            addTag = PlaceTags.Normalize(addTag);
            removeTag = PlaceTags.Normalize(removeTag);

            if (enabled is bool on && place.Enabled != on)
            {
                place.Enabled = on;
                changed = true;
            }
            if (!string.IsNullOrEmpty(addTag) && (place.placeTypes == null || !place.placeTypes.Contains(addTag)))
            {
                place.AddType(addTag);
                changed = true;
            }
            if (!string.IsNullOrEmpty(removeTag) && place.RemoveType(removeTag)) changed = true;

            if (changed) PersistIfSaved(place);
            return PlaceEditResult.Success(place.Name);
        }

        public PlaceEditResult UpdateInfo(EventPlace place, string name, string description, string[] tags)
        {
            if (!TryEdit(place, out var busy)) return busy;
            string cleanName = CleanText(name, MaxNameLength);
            if (cleanName == null) return PlaceEditResult.Fail(Lang.Get("canevents:place-name-empty"));

            place.Name = cleanName;
            place.Description = CleanText(description, MaxDescriptionLength);
            place.placeTypes = (tags ?? new string[0])
                .Select(PlaceTags.Normalize)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToArray();
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:place-info-saved", place.Name, TagText(place)));
        }

        private static string TagText(EventPlace place)
            => place.placeTypes == null || place.placeTypes.Length == 0 ? "-" : string.Join(", ", place.placeTypes);

        public static string Describe(EventPlace place)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("#{0} '{1}' [{2}]{3}{4}", place.Id, place.Name, TagText(place),
                place.Enabled ? "" : " (disabled)", place.Occupied ? " (in use)" : ""));
            if (!string.IsNullOrEmpty(place.Description)) sb.AppendLine("  " + place.Description);

            sb.AppendLine(PlaceBounds.Has(place)
                ? string.Format("  bounds: ({0}, {1}, {2}) - ({3}, {4}, {5})",
                    place.BoundsMin.X, place.BoundsMin.Y, place.BoundsMin.Z,
                    place.BoundsMax.X, place.BoundsMax.Y, place.BoundsMax.Z)
                : "  bounds: none");

            var spawns = place.spawnPositions;
            sb.AppendLine(string.Format("  spawns: {0}", spawns?.Count ?? 0));
            // 1-based, same as removesp/movesp.
            if (spawns != null)
                for (int i = 0; i < spawns.Count; i++)
                    sb.AppendLine(string.Format("    [{0}] ({1}, {2}, {3})", i + 1, spawns[i].X, spawns[i].Y, spawns[i].Z));

            if (!string.IsNullOrEmpty(place.SchematicTemplateCode))
                sb.AppendLine("  template: " + place.SchematicTemplateCode + (place.SchematicOnDemand ? " (on demand)" : ""));

            var gaps = PlaceSetupCheck.Gaps(place);
            if (gaps.Count == 0) sb.Append("  setup: complete");
            else sb.Append("  setup MISSING: " + string.Join("; ", gaps));
            return sb.ToString();
        }

        // Without blocks, generated spawns sit at the lower corner's Y instead of the floor.
        public PlaceEditResult SetBounds(EventPlace place, BlockPos cornerA, BlockPos cornerB, bool replaceSpawns,
            Vintagestory.API.Common.IBlockAccessor blocks = null)
        {
            if (!TryEdit(place, out var busy)) return busy;

            place.SetBounds(cornerA, cornerB);
            if (replaceSpawns) place.GenerateEdgeSpawns(blocks == null ? null : FloorFinder(place, blocks));
            PersistIfSaved(place);

            var min = place.BoundsMin;
            var max = place.BoundsMax;
            return PlaceEditResult.Success(string.Format("Bounds set on '{0}' ({1}x{2}x{3}){4}.",
                place.Name,
                max.X - min.X + 1, max.Y - min.Y + 1, max.Z - min.Z + 1,
                replaceSpawns ? ", 2 spawn points generated" : ""));
        }

        // Highest solid block in the column within bounds. Unloaded chunks read as air, so null.
        // Func<> from System: Vintagestory.API.Common has its own and would be ambiguous.
        private static Func<int, int, int?> FloorFinder(EventPlace place, Vintagestory.API.Common.IBlockAccessor blocks)
        {
            int minY = place.BoundsMin.Y, maxY = place.BoundsMax.Y, dim = place.BoundsMin.dimension;
            return (x, z) =>
            {
                var pos = new BlockPos(x, maxY, z, dim);
                for (int y = maxY; y >= minY; y--)
                {
                    pos.Y = y;
                    var block = blocks.GetBlock(pos);
                    if (block != null && block.Id != 0 && !block.IsLiquid()
                        && block.CollisionBoxes != null && block.CollisionBoxes.Length > 0)
                        return y;
                }
                return null;
            };
        }

        public PlaceEditResult ClearBounds(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            place.HasBounds = false;
            place.BoundsMin = null;
            place.BoundsMax = null;
            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format(
                "Bounds cleared on '{0}'. /canep commands can no longer target it.", place.Name));
        }

        public PlaceEditResult AddSpawn(EventPlace place, BlockPos pos)
        {
            if (!TryEdit(place, out var busy)) return busy;

            place.spawnPositions ??= new System.Collections.Generic.List<BlockPos>();
            place.spawnPositions.Add(pos.Copy());
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:spawn-added", place.Name, place.spawnPositions.Count)
                + OutsideBoundsNote(place, pos));
        }

        // Spawn edits take a 0-based index and reply with the 1-based number.
        // Moving keeps the index, so the spawn stays on its team.
        public PlaceEditResult MoveSpawn(EventPlace place, int index, BlockPos pos)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!HasSpawnIndex(place, index)) return InvalidSpawnIndex(place, index);

            place.spawnPositions[index] = pos.Copy();
            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format("Spawn #{0} on '{1}' moved to ({2}, {3}, {4}).",
                    index + 1, place.Name, pos.X, pos.Y, pos.Z)
                + OutsideBoundsNote(place, pos));
        }

        public PlaceEditResult RemoveSpawn(EventPlace place, int index)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!HasSpawnIndex(place, index)) return InvalidSpawnIndex(place, index);

            place.spawnPositions.RemoveAt(index);
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:spawn-removed", index + 1, place.spawnPositions.Count));
        }

        private static bool HasSpawnIndex(EventPlace place, int index)
            => place.spawnPositions != null && index >= 0 && index < place.spawnPositions.Count;

        private static PlaceEditResult InvalidSpawnIndex(EventPlace place, int index)
            => PlaceEditResult.Fail(Lang.Get("canevents:spawn-invalid-index", index + 1, place.spawnPositions?.Count ?? 0));

        // Only a warning: bounds aren't enforced during a match.
        public static string OutsideBoundsNote(EventPlace place, BlockPos pos)
        {
            if (!PlaceBounds.Has(place)) return "";
            return PlaceBounds.Contains(place, pos)
                ? ""
                : " WARNING: that position is outside the place's bounds.";
        }

        // Restores the template stamp first. Refused while occupied: RunningEvent teardown still uses the place.
        public PlaceEditResult DeletePlace(EventPlace place)
        {
            if (place == null) return PlaceEditResult.Fail(Lang.Get("canevents:place-delete-failed"));
            if (place.Occupied) return PlaceEditResult.Fail(Lang.Get("canevents:tpl-arena-in-use"));

            var schematics = placer?.Invoke();
            long id = place.Id;
            // After the block journal, or replaying it would put schematic-era blocks on restored terrain.
            if (schematics != null && schematics.HasSavedData(id))
                EventManagers.eventBehavior.Shared.ArenaBlockJournal.RunAfterRestore(id, () => schematics.Restore(id));
            placeManager.DeletePlace(id);

            string message = Lang.Get("canevents:tpl-place-removed-restored", id);
            var cleanup = PlaceDeleted?.Invoke(id);
            if (cleanup != null && cleanup.Count > 0) message += "\n" + string.Join("\n", cleanup);
            return PlaceEditResult.Success(message);
        }

        // Set at startup; tournament services are created after this one.
        public Func<long, System.Collections.Generic.List<string>> PlaceDeleted { get; set; }

        public static bool TryFindTeleportPoint(EventPlace place, out double x, out double y, out double z,
            out int dim, out string what)
        {
            x = y = z = 0; dim = 0; what = null;
            if (place == null) return false;

            if (place.spawnPositions != null && place.spawnPositions.Count > 0)
            {
                var sp = place.spawnPositions[0];
                x = sp.X + 0.5; y = sp.Y; z = sp.Z + 0.5; dim = sp.dimension;
                what = "spawn #1";
                return true;
            }
            if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                x = (place.BoundsMin.X + place.BoundsMax.X) / 2.0 + 0.5;
                y = place.BoundsMax.Y + 1;
                z = (place.BoundsMin.Z + place.BoundsMax.Z) / 2.0 + 0.5;
                dim = place.BoundsMin.dimension;
                what = "bounds centre";
                return true;
            }
            if (place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out var koth) && JsonPos.TryRead(koth, out var hill))
            {
                x = hill.X + 0.5; y = hill.Y + 1; z = hill.Z + 0.5; dim = hill.dimension;
                what = "the hill";
                return true;
            }
            return false;
        }

        // For the event wand: removes the spawn within 1.5 blocks of the clicked block's top.
        public PlaceEditResult RemoveSpawnNear(EventPlace place, BlockPos floorPos)
        {
            int best = -1;
            double bestDist = 1.5 * 1.5;
            var spawns = place?.spawnPositions;
            if (spawns != null)
                for (int i = 0; i < spawns.Count; i++)
                {
                    double d = spawns[i].DistanceSqTo(floorPos.X + 0.5, floorPos.Y + 1.0, floorPos.Z + 0.5);
                    if (spawns[i].dimension == floorPos.dimension && d <= bestDist) { best = i; bestDist = d; }
                }
            if (best < 0) return PlaceEditResult.Fail(Lang.Get("canevents:wand-nothing-here"));
            return RemoveSpawn(place, best);
        }

        // A running event reads this data at teardown, so occupied places are locked.
        private static bool TryEdit(EventPlace place, out PlaceEditResult error)
        {
            if (place == null)
            {
                error = PlaceEditResult.Fail(Lang.Get("canevents:no-place-at-position"));
                return false;
            }
            if (place.Occupied)
            {
                error = PlaceEditResult.Fail(Lang.Get("canevents:place-in-use"));
                return false;
            }
            error = default;
            return true;
        }

        // Places still being built live in the in-progress slot until "save".
        public void PersistIfSaved(EventPlace place)
        {
            if (placeManager != null && placeManager.EventPlaces.Contains(place)) placeManager.SavePlace(place);
        }

    }
}
