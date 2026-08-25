using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    /// <summary>Outcome of one place edit: what to tell the caller, and whether it happened.</summary>
    public readonly struct PlaceEditResult
    {
        public readonly bool Ok;
        public readonly string Message;

        private PlaceEditResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }

        public static PlaceEditResult Success(string message) => new PlaceEditResult(true, message);
        public static PlaceEditResult Fail(string message) => new PlaceEditResult(false, message);
    }

    /// <summary>
    /// The single definition of every per-behavior place edit (HG zone/loot, CTF posts, KOTH hill).
    /// The /canep commands and the admin panel's packets used to carry drifting copies of each edit;
    /// they now only decide WHICH place is edited and hand the work here. Deliberately front-end
    /// agnostic: no IServerPlayer, no packets, no TextCommandResult.
    /// </summary>
    public class PlaceConfigService
    {
        private readonly DefaultEventPlacesManager placeManager;

        /// <summary>Guard, not a rule: the grow-loop below allocates one slot per team, so an
        /// unbounded index is an OOM from a single bad packet.</summary>
        public const int MaxCtfTeams = 8;

        public PlaceConfigService(DefaultEventPlacesManager placeManager)
        {
            this.placeManager = placeManager;
        }

        // Hunger Games

        /// <summary>Centres the storm on <paramref name="floorPos"/> (the block under the admin's feet).</summary>
        public PlaceEditResult SetHungerGamesZone(EventPlace place, BlockPos floorPos, float radius)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (radius <= 0f) return PlaceEditResult.Fail(Lang.Get("canevents:hg-radius-min"));

            var data = place.GetOrCreateBehaviorData(BehaviorKeys.HungerGames);
            data["X"] = floorPos.X;
            data["Y"] = floorPos.Y;
            data["Z"] = floorPos.Z;
            data["InitialRadius"] = radius;
            Persist(place);

            return PlaceEditResult.Success(string.Format("HG zone set on '{0}' at ({1}, {2}, {3}) r={4}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, radius));
        }

        /// <summary>Marks a chest spot. Re-marking the same block re-tiers it instead of stacking a
        /// second spot there. A blank tier means "the event's default tier".</summary>
        public PlaceEditResult AddHungerGamesLoot(EventPlace place, BlockPos floorPos, string tier)
        {
            if (!TryEdit(place, out var busy)) return busy;

            var arr = GetOrCreateArray(place, BehaviorKeys.HungerGames, "LootSpots");

            for (int i = arr.Count - 1; i >= 0; i--)
                if (arr[i] is JObject e && JsonPos.Matches(e, floorPos)) arr.RemoveAt(i);

            tier = string.IsNullOrWhiteSpace(tier) ? null : tier.Trim();

            var entry = JsonPos.Write(floorPos);
            if (tier != null) entry["Tier"] = tier;
            arr.Add(entry);
            Persist(place);

            return PlaceEditResult.Success(string.Format(
                "HG loot spot added on '{0}' at ({1}, {2}, {3}) tier={4}. Total: {5}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, tier ?? "(default)", arr.Count));
        }

        public PlaceEditResult ClearHungerGamesLoot(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            if (place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data))
            {
                data["LootSpots"] = new JArray();
                Persist(place);
            }
            return PlaceEditResult.Success(string.Format("HG loot spots cleared on '{0}'.", place.Name));
        }

        /// <summary>Removes one spot by its index in the stored array - the same index the panel
        /// renders, so the two stay aligned.</summary>
        public PlaceEditResult RemoveHungerGamesLoot(EventPlace place, int index)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!TryGetLootArray(place, index, out var arr))
                return PlaceEditResult.Fail(Lang.Get("canevents:hg-loot-invalid-index"));

            // No JObject check: a malformed entry still occupies an index the panel shows, and
            // removing it is how an admin gets rid of one.
            arr.RemoveAt(index);
            Persist(place);
            return PlaceEditResult.Success(string.Format("Removed HG loot spot #{0} on '{1}'. Left: {2}.",
                index, place.Name, arr.Count));
        }

        public PlaceEditResult SetHungerGamesLootTier(EventPlace place, int index, string tier)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!TryGetLootArray(place, index, out var arr) || !(arr[index] is JObject entry))
                return PlaceEditResult.Fail(Lang.Get("canevents:hg-loot-invalid-index"));

            if (string.IsNullOrWhiteSpace(tier)) entry.Remove("Tier");
            else entry["Tier"] = tier.Trim();
            Persist(place);

            return PlaceEditResult.Success(string.Format("HG loot spot #{0} on '{1}' set to tier '{2}'.",
                index, place.Name, string.IsNullOrWhiteSpace(tier) ? "(default)" : tier.Trim()));
        }

        /// <summary>Human-readable loot listing. Read-only, so it works on an occupied place too.</summary>
        public string DescribeHungerGamesLoot(EventPlace place)
        {
            if (!place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)
                || !(data["LootSpots"] is JArray arr) || arr.Count == 0)
                return string.Format("No loot spots on '{0}'.", place.Name);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("'{0}' has {1} loot spot(s):", place.Name, arr.Count));
            foreach (var t in arr)
            {
                if (!(t is JObject e) || e["X"] == null) continue;
                string tier = e["Tier"]?.Value<string>() ?? "(default)";
                sb.AppendLine(string.Format("  ({0}, {1}, {2})  tier={3}",
                    e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z"), tier));
            }
            return sb.ToString().TrimEnd();
        }

        // Capture the Flag

        /// <summary>
        /// Sets team N's flag post, addressed by team index (post [0] is team 1), the same "index is the
        /// team" rule the spawns follow. Re-running moves that post; skipped slots stay empty and read
        /// as NOT SET rather than shifting the teams after them.
        /// </summary>
        public PlaceEditResult SetCtfPost(EventPlace place, BlockPos floorPos, int team)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (team < 1 || team > MaxCtfTeams) return PlaceEditResult.Fail(Lang.Get("canevents:ctf-team-min"));

            var arr = GetOrCreateArray(place, BehaviorKeys.CaptureTheFlag, "Posts");
            while (arr.Count < team) arr.Add(new JObject());

            arr[team - 1] = JsonPos.Write(floorPos);
            Persist(place);

            return PlaceEditResult.Success(string.Format(
                "CTF post for team {0} set on '{1}' at ({2}, {3}, {4}). Posts: {5}.",
                team, place.Name, floorPos.X, floorPos.Y, floorPos.Z, arr.Count));
        }

        public PlaceEditResult ClearCtfPosts(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            if (place.TryGetBehaviorData(BehaviorKeys.CaptureTheFlag, out var data))
            {
                data["Posts"] = new JArray();
                Persist(place);
            }
            return PlaceEditResult.Success(string.Format("CTF posts cleared on '{0}'.", place.Name));
        }

        public string DescribeCtfPosts(EventPlace place)
        {
            if (!place.TryGetBehaviorData(BehaviorKeys.CaptureTheFlag, out var data)
                || !(data["Posts"] is JArray arr) || arr.Count == 0)
                return string.Format("No CTF posts on '{0}'.", place.Name);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("'{0}' has {1} CTF post(s):", place.Name, arr.Count));
            for (int i = 0; i < arr.Count; i++)
            {
                var e = arr[i] as JObject;
                if (e == null || e["X"] == null)
                    sb.AppendLine(string.Format("  team {0}: (not set)", i + 1));
                else
                    sb.AppendLine(string.Format("  team {0}: ({1}, {2}, {3})",
                        i + 1, e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z")));
            }
            return sb.ToString().TrimEnd();
        }

        // Murder Mystery

        /// <summary>
        /// Marks a spot where the mode's weapon token can appear. Re-marking the same block is a no-op
        /// rather than a second spot there: two spots on one block would let the same corner hold two
        /// tokens, which is exactly what the minimum-distance rule exists to prevent.
        /// </summary>
        public PlaceEditResult AddMurderMysteryPickup(EventPlace place, BlockPos floorPos)
        {
            if (!TryEdit(place, out var busy)) return busy;

            var arr = GetOrCreateArray(place, BehaviorKeys.MurderMystery, "PickupSpots");
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] is JObject e && JsonPos.Matches(e, floorPos))
                    return PlaceEditResult.Fail(Lang.Get("canevents:mm-pickup-duplicate"));

            arr.Add(JsonPos.Write(floorPos));
            Persist(place);

            return PlaceEditResult.Success(string.Format(
                "MM pickup spot added on '{0}' at ({1}, {2}, {3}). Total: {4}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, arr.Count));
        }

        /// <summary>Removes one spot by its index in the stored array - the index the panel renders.</summary>
        public PlaceEditResult RemoveMurderMysteryPickup(EventPlace place, int index)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!TryGetPickupArray(place, index, out var arr))
                return PlaceEditResult.Fail(Lang.Get("canevents:mm-pickup-invalid-index"));

            arr.RemoveAt(index);
            Persist(place);
            return PlaceEditResult.Success(string.Format("Removed MM pickup spot #{0} on '{1}'. Left: {2}.",
                index, place.Name, arr.Count));
        }

        public PlaceEditResult ClearMurderMysteryPickups(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            if (place.TryGetBehaviorData(BehaviorKeys.MurderMystery, out var data))
            {
                data["PickupSpots"] = new JArray();
                Persist(place);
            }
            return PlaceEditResult.Success(string.Format("MM pickup spots cleared on '{0}'.", place.Name));
        }

        /// <summary>Human-readable listing. Read-only, so it works on an occupied place too.</summary>
        public string DescribeMurderMysteryPickups(EventPlace place)
        {
            if (!place.TryGetBehaviorData(BehaviorKeys.MurderMystery, out var data)
                || !(data["PickupSpots"] is JArray arr) || arr.Count == 0)
                return string.Format("No pickup spots on '{0}'.", place.Name);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("'{0}' has {1} pickup spot(s):", place.Name, arr.Count));
            for (int i = 0; i < arr.Count; i++)
            {
                if (!JsonPos.TryRead(arr[i], out var pos)) continue;
                sb.AppendLine(string.Format("  #{0}  ({1}, {2}, {3})", i, pos.X, pos.Y, pos.Z));
            }
            return sb.ToString().TrimEnd();
        }

        // King of the Hill

        /// <summary>Height &lt;= 0 means "unset" and falls back to the documented default of 5: an
        /// omitted OptionalFloat parser yields 0, not null.</summary>
        public PlaceEditResult SetKothHill(EventPlace place, BlockPos floorPos, float radius, float height)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (radius <= 0f) return PlaceEditResult.Fail(Lang.Get("canevents:koth-radius-min"));
            if (height <= 0f) height = 5f;

            var data = place.GetOrCreateBehaviorData(BehaviorKeys.KingOfTheHill);
            data["X"] = floorPos.X;
            data["Y"] = floorPos.Y;
            data["Z"] = floorPos.Z;
            data["Radius"] = radius;
            data["Height"] = height;
            Persist(place);

            return PlaceEditResult.Success(string.Format(
                "KOTH hill set on '{0}' at ({1}, {2}, {3}) r={4} h={5}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, radius, height));
        }

        // shared

        /// <summary>
        /// Refuses edits to a place hosting a match: the running event holds a reference to it and
        /// reads this data at teardown.
        /// </summary>
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

        /// <summary>Writes the place to disk only if it is already a saved one: /canep can edit a place
        /// still being built, which lives in the caller's in-progress slot until "save".</summary>
        private void Persist(EventPlace place)
        {
            if (placeManager != null && placeManager.EventPlaces.Contains(place)) placeManager.SavePlace(place);
        }

        private static JArray GetOrCreateArray(EventPlace place, string behaviorKey, string field)
        {
            var data = place.GetOrCreateBehaviorData(behaviorKey);
            var arr = data[field] as JArray;
            if (arr == null) { arr = new JArray(); data[field] = arr; }
            return arr;
        }

        private static bool TryGetPickupArray(EventPlace place, int index, out JArray arr)
        {
            arr = null;
            if (!place.TryGetBehaviorData(BehaviorKeys.MurderMystery, out var data)) return false;
            arr = data["PickupSpots"] as JArray;
            return arr != null && index >= 0 && index < arr.Count;
        }

        private static bool TryGetLootArray(EventPlace place, int index, out JArray arr)
        {
            arr = null;
            if (!place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)) return false;
            arr = data["LootSpots"] as JArray;
            return arr != null && index >= 0 && index < arr.Count;
        }

    }
}
