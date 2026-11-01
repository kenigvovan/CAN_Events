using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    // A dimension reserved for arena packs. Compat/ManifoldArenaSystem provides it when Manifold is installed.
    public interface IArenaDimensionHost
    {
        int DimId { get; }
        // Creates the columns around the arrival point, also on a hop inside the dimension.
        void Send(IServerPlayer player, int x, int y, int z);
        void SendBack(IServerPlayer player);
    }

    // config/arenapacks/*.json in the canevents domain, so a content mod can ship one.
    public class ArenaPack
    {
        public string Code;
        public string Description;
        public string[] Templates;
    }

    // Stamps a pack's templates side by side. In the arena dimension every template has a fixed slot
    // (its index in the pack), so re-running skips built ones and a longer pack never moves the others.
    // Without the dimension the row starts next to the admin.
    public class ArenaPackService
    {
        public const int FirstSlot = 512;
        public const int SlotStep = 224;
        // Manifold generates 5 chunks around the arrival just outside a slot's corner.
        public const int MaxArenaSize = 160;
        private const int FloorMargin = 6;
        private const int LoadPollMs = 500;
        private const int LoadTries = 40;

        public static IArenaDimensionHost Host;

        private readonly ICoreServerAPI sapi;
        private bool busy;

        public ArenaPackService(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        public static bool HasDimension => Host != null && Host.DimId != 0;

        public List<ArenaPack> LoadPacks()
        {
            var packs = new List<ArenaPack>();
            foreach (var entry in sapi.Assets.GetMany<JObject>(sapi.World.Logger, "config/arenapacks/", "canevents"))
            {
                try
                {
                    var pack = entry.Value.ToObject<ArenaPack>();
                    if (pack == null) continue;
                    if (string.IsNullOrEmpty(pack.Code))
                        pack.Code = System.IO.Path.GetFileNameWithoutExtension(entry.Key.Path);
                    pack.Templates ??= new string[0];
                    packs.Add(pack);
                }
                catch (Exception e)
                {
                    sapi.World.Logger.Error("Arena pack {0} could not be loaded: {1}", entry.Key, e.Message);
                }
            }
            return packs;
        }

        public (bool ok, string message) Setup(IServerPlayer player, CANEventManager manager, string packCode, string[] only)
        {
            if (busy) return (false, Lang.Get("canevents:pack-busy"));

            var pack = LoadPacks().Find(p => string.Equals(p.Code, packCode, StringComparison.OrdinalIgnoreCase));
            if (pack == null) return (false, Lang.Get("canevents:pack-not-found", packCode));

            bool inDim = HasDimension;
            var start = player.Entity.Pos.AsBlockPos;
            var queue = new Queue<(BlockPos origin, ArenaTemplate template)>();
            var lines = new List<string>();

            for (int slot = 0; slot < pack.Templates.Length; slot++)
            {
                string code = pack.Templates[slot];
                if (only != null && only.Length > 0 && !only.Contains(code, StringComparer.OrdinalIgnoreCase)) continue;

                var template = manager.ArenaTemplates.Find(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
                if (template == null) { lines.Add(Lang.Get("canevents:pack-no-template", code)); continue; }

                var size = manager.SchematicPlacer.GetSchematicSize(template);
                if (size == null || size.X <= 0) { lines.Add(Lang.Get("canevents:pack-no-schematic", code, template.SchematicPath)); continue; }
                if (inDim && (size.X > MaxArenaSize || size.Z > MaxArenaSize))
                {
                    lines.Add(Lang.Get("canevents:pack-too-big", code, size.X, size.Z, MaxArenaSize));
                    continue;
                }

                if (inDim)
                {
                    var built = PlaceInSlot(manager, slot);
                    if (built != null) { lines.Add(Lang.Get("canevents:pack-exists", code, built.Id)); continue; }
                    queue.Enqueue((SlotOrigin(slot), template));
                }
                else
                {
                    queue.Enqueue((new BlockPos(start.X + 4 + queue.Count * SlotStep, start.Y, start.Z, start.dimension), template));
                }
            }

            if (queue.Count == 0)
            {
                if (lines.Count == 0) lines.Add(Lang.Get("canevents:pack-nothing"));
                return (true, string.Join("\n", lines));
            }

            busy = true;
            lines.Add(Lang.Get(inDim ? "canevents:pack-started-dim" : "canevents:pack-started-here", queue.Count, pack.Code));
            StampNext(player, manager, queue, inDim);
            return (true, string.Join("\n", lines));
        }

        public void SendToDimension(IServerPlayer player)
        {
            var origin = SlotOrigin(0);
            Host.Send(player, origin.X - 2, origin.Y, origin.Z - 2);
            // Before the first setup the dimension is void.
            Floor(new BlockPos(origin.X - 4, origin.Y, origin.Z - 4, origin.dimension), 5, 5);
        }

        private BlockPos SlotOrigin(int slot)
            => new BlockPos(FirstSlot + slot * SlotStep, sapi.World.SeaLevel, FirstSlot, Host.DimId);

        private void StampNext(IServerPlayer player, CANEventManager manager,
            Queue<(BlockPos origin, ArenaTemplate template)> queue, bool inDim)
        {
            if (queue.Count == 0)
            {
                busy = false;
                Tell(player, Lang.Get("canevents:pack-done"));
                return;
            }

            var (origin, template) = queue.Dequeue();
            var size = manager.SchematicPlacer.GetSchematicSize(template);
            int x1 = origin.X - FloorMargin, z1 = origin.Z - FloorMargin;
            int x2 = origin.X + size.X - 1 + FloorMargin, z2 = origin.Z + size.Z - 1 + FloorMargin;

            void Stamp()
            {
                try
                {
                    if (inDim) Floor(new BlockPos(x1, origin.Y, z1, origin.dimension), x2 - x1 + 1, z2 - z1 + 1);
                    var place = TemplateStamper.Stamp(template, origin, placeBlocks: true,
                        manager.eventPlacesManager, manager.SchematicPlacer, out var error);
                    Tell(player, place != null
                        ? Lang.Get("canevents:pack-stamped", template.Code, place.Id)
                        : Lang.Get("canevents:pack-failed", template.Code, error));
                }
                catch (Exception e)
                {
                    sapi.World.Logger.Error("Arena pack: stamping {0} failed: {1}", template.Code, e);
                    Tell(player, Lang.Get("canevents:pack-failed", template.Code, e.Message));
                }
                StampNext(player, manager, queue, inDim);
            }

            if (!inDim)
            {
                DimensionChunks.LoadArea(sapi, DimensionChunks.ColumnOf(x1), DimensionChunks.ColumnOf(z1),
                    DimensionChunks.ColumnOf(x2), DimensionChunks.ColumnOf(z2), origin.dimension, Stamp);
                return;
            }

            Host.Send(player, origin.X - 2, origin.Y, origin.Z - 2);
            int tries = 0;
            void Poll(float _)
            {
                if (player.ConnectionState != EnumClientState.Playing) { busy = false; return; }
                if (AreaLoaded(x1, z1, x2, z2, origin.dimension)) { Stamp(); return; }
                if (++tries < LoadTries) { sapi.Event.RegisterCallback(Poll, LoadPollMs); return; }

                Tell(player, Lang.Get("canevents:pack-not-loaded", template.Code));
                StampNext(player, manager, queue, inDim);
            }
            sapi.Event.RegisterCallback(Poll, LoadPollMs);
        }

        private bool AreaLoaded(int x1, int z1, int x2, int z2, int dim)
        {
            for (int cx = DimensionChunks.ColumnOf(x1); cx <= DimensionChunks.ColumnOf(x2); cx++)
                for (int cz = DimensionChunks.ColumnOf(z1); cz <= DimensionChunks.ColumnOf(z2); cz++)
                    if (!DimensionChunks.IsColumnLoaded(sapi, cx, cz, dim)) return false;
            return true;
        }

        // One layer under the given y; the dimension has no terrain of its own.
        private void Floor(BlockPos corner, int sizeX, int sizeZ)
        {
            var block = sapi.World.GetBlock(new AssetLocation("game:rock-granite"));
            if (block == null) return;
            var ba = sapi.World.BlockAccessor;
            var pos = new BlockPos(0, 0, 0, corner.dimension);
            for (int x = 0; x < sizeX; x++)
                for (int z = 0; z < sizeZ; z++)
                {
                    pos.Set(corner.X + x, corner.Y - 1, corner.Z + z);
                    if (ba.GetBlock(pos).Id == 0) ba.SetBlock(block.Id, pos);
                }
        }

        // By position rather than name, so a renamed place still blocks a second stamp on top of it.
        private EventPlace PlaceInSlot(CANEventManager manager, int slot)
        {
            int dim = Host.DimId;
            int from = FirstSlot + slot * SlotStep - GlobalConstants.ChunkSize;
            int to = from + SlotStep;
            return manager.eventPlacesManager.EventPlaces.FirstOrDefault(p =>
            {
                if (PlaceSummaryFormatter.Dimension(p) != dim) return false;
                var at = p.BoundsMin ?? p.spawnPositions?.FirstOrDefault();
                return at != null && at.X >= from && at.X < to;
            });
        }

        private static void Tell(IServerPlayer player, string message)
            => player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
    }
}
