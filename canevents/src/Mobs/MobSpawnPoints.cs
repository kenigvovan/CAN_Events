using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace canevents.src.Mobs
{
    // The place's mob spawn points (/caneventplace mobspawn), shared by every mode with mobs.
    // Picks spread over the points instead of one corner, leaning to those behind the fighters,
    // never right next to one.
    public sealed class MobSpawnPoints
    {
        private const double KeepAwayFromFightersSq = 8 * 8;

        private sealed class Point
        {
            public BlockPos Pos;
            public string Tag;
            public int Uses;
        }

        private readonly List<Point> points = new List<Point>();

        public int Count => points.Count;

        public IEnumerable<BlockPos> All => points.Select(p => p.Pos);

        public void Load(RunningEvent match, Log log)
        {
            points.Clear();
            var place = match.Place;
            if (place != null && place.TryGetBehaviorData(BehaviorKeys.Mobs, out var data)
                && data[PlaceConfigService.MobSpawnsKey] is JArray arr)
            {
                foreach (var token in arr)
                    if (JsonPos.TryRead(token, out var pos))
                        points.Add(new Point { Pos = pos, Tag = (token as JObject)?["Tag"]?.Value<string>() });
            }
            if (points.Count > 0 || place?.spawnPositions == null) return;

            log.Warning("Place '{0}' has no mob spawns (/caneventplace mobspawn), mobs use the player spawns.", place.Name);
            points.AddRange(place.spawnPositions.Select(p => new Point { Pos = p }));
        }

        public BlockPos Pick(string tag, List<Entity> fighters, Random rand)
        {
            var pool = points.Where(p => tag == null || string.Equals(p.Tag, tag, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.Count == 0) pool = points;
            if (pool.Count == 0) return null;

            var clear = pool.Where(p => fighters.All(f => f.Pos.SquareDistanceTo(p.Pos.X + 0.5, p.Pos.Y, p.Pos.Z + 0.5) > KeepAwayFromFightersSq)).ToList();
            var from = clear.Count > 0 ? clear : pool;

            var pick = from.OrderBy(p => p.Uses * 2.0 - Behind(p, fighters) + rand.NextDouble()).First();
            pick.Uses++;
            return pick.Pos;
        }

        // 1 = straight behind every fighter, -1 = right in front of them.
        private static double Behind(Point point, List<Entity> fighters)
        {
            if (fighters.Count == 0) return 0;
            double sum = 0;
            foreach (var f in fighters)
            {
                var view = f.Pos.GetViewVector();
                double dx = point.Pos.X + 0.5 - f.Pos.X, dz = point.Pos.Z + 0.5 - f.Pos.Z;
                double len = Math.Sqrt(dx * dx + dz * dz);
                double flat = Math.Sqrt(view.X * view.X + view.Z * view.Z);
                if (len < 0.01 || flat < 0.01) continue;
                sum -= (dx * view.X + dz * view.Z) / (len * flat);
            }
            return sum / fighters.Count;
        }
    }
}
