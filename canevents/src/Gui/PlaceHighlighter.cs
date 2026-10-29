using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui
{
    // One instance per client: the engine keys highlights by slot id, so two owners would erase each other.
    public sealed class PlaceHighlighter : IDisposable
    {
        // Reusing a slot id replaces the previous drawing.
        public const int SlotBounds = 4180;
        public const int SlotSpawns = 4181;
        public const int SlotMarks = 4182;
        public const int SlotMmPickups = 4183;
        public const int SlotHgLoot = 4184;
        public const int SlotHgPlacement = 7802; // must not collide with other mods' slots
        private const int MaxBlocks = 4000;

        private readonly ICoreClientAPI capi;
        private readonly ClientNetworkHandler networkHandler;
        private readonly Action onPlaceList;
        private readonly Action onHgSetup;

        // -1 none, -2 forces the next Update to redraw.
        public long HighlightedPlaceId { get; set; } = -1;

        private int highlightedRevision = -1;

        private long focusPlaceId = -1;

        private int highlightedDim;

        private int watchedDim;
        private readonly long dimWatchId;

        // Survives closing the GUI so the admin can walk the arena.
        public bool Enabled { get; private set; } = true;

        // Bounds marking, shared by the Arenas editor and the wizard.
        public bool HasMark1;
        public bool HasMark2;
        public int Mark1X, Mark1Y, Mark1Z;
        public int Mark2X, Mark2Y, Mark2Z;
        // Dimension each corner was taken in; the server sets bounds in the admin's dimension.
        private int mark1Dim, mark2Dim;

        // Crosshair block at panel open; used by one Mark, then the feet.
        public BlockPos AimAtOpen;

        // Admin's choice in the bounds section: ignore the aim and always mark the block under the feet.
        public bool MarkAtFeet;

        public PlaceHighlighter(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
        {
            this.capi = capi;
            this.networkHandler = networkHandler;

            // Keyed by place id, so an edit to the shown place would not repaint it otherwise.
            onPlaceList = () => { HighlightedPlaceId = -2; RedrawFocus(); };
            onHgSetup = RedrawFocus;
            networkHandler.PlaceList.Changed += onPlaceList;
            networkHandler.HgSetup.Changed += onHgSetup;

            // Nothing calls Update once the panel is closed, so poll for dimension hops (Manifold).
            dimWatchId = capi.Event.RegisterGameTickListener(_ => WatchDimension(), 500);
        }

        private int PlayerDim() => capi.World?.Player?.Entity?.Pos?.Dimension ?? 0;

        private void WatchDimension()
        {
            int dim = PlayerDim();
            if (dim == watchedDim) return;
            watchedDim = dim;
            RedrawFocus();
            UpdateMarkPreview();
        }

        // Vanilla BlockHighlight offsets blocks by InternalY, so dim N draws N*32768 up; send as dim 0.
        // Callers only pass blocks in the viewer's own dimension.
        private void Send(IClientPlayer player, int slot, List<BlockPos> blocks, List<int> colors)
        {
            var flat = new List<BlockPos>(blocks.Count);
            foreach (var b in blocks) flat.Add(new BlockPos(b.X, b.Y, b.Z, 0));
            capi.World.HighlightBlocks(player, slot, flat, colors);
        }

        public bool AnythingDrawn => HighlightedPlaceId >= 0 || HasMark1 || HasMark2;

        // Call while the mouse is captured; with a free cursor the engine picks under the cursor.
        public void CaptureAim()
        {
            AimAtOpen = capi.World.Player?.CurrentBlockSelection?.Position?.Copy();
        }

        public static bool HasType(PlaceListEntryMsg place, string tag)
            => place?.Types != null && Array.IndexOf(place.Types, tag) >= 0;

        // X/Z only, matching the server's BoundsContain.
        public static bool PositionInsideBounds(PlaceListEntryMsg place, int x, int z)
            => PositionInsideBounds(place, x, z, place?.Dim ?? 0); // the place's own points share its dimension

        public static bool PositionInsideBounds(PlaceListEntryMsg place, int x, int z, int dim)
        {
            if (place == null || !place.HasBounds) return true; // nothing to be outside of
            return PlaceBounds.Contains(place.BoundsMinX, place.BoundsMaxX,
                place.BoundsMinZ, place.BoundsMaxZ, place.Dim, x, z, dim);
        }

        public bool PlayerInsideBounds(PlaceListEntryMsg place)
        {
            var pp = capi.World.Player?.Entity?.Pos;
            if (pp == null) return true;
            return PositionInsideBounds(place, (int)Math.Floor(pp.X), (int)Math.Floor(pp.Z), pp.Dimension);
        }

        public PlaceListEntryMsg FindPlace(long id)
        {
            var places = networkHandler.PlaceList.Value?.Places;
            if (places == null || id < 0) return null;
            foreach (var p in places)
                if (p != null && p.Id == id) return p;
            return null;
        }

        // HG setup arrives separately; null unless it is for this place.
        public HgSetupMsg HgSetupFor(PlaceListEntryMsg place)
        {
            var s = networkHandler.HgSetup.Value;
            return place != null && s != null && s.PlaceId == place.Id ? s : null;
        }

        // Case-insensitive like the server's lookup; -1 if absent.
        public static int TierIndexOf(string[] tiers, string tier)
            => tiers == null || tier == null ? -1
                : Array.FindIndex(tiers, t => string.Equals(t, tier, StringComparison.OrdinalIgnoreCase));

        // The three arrays are index-aligned; take the shortest.
        public static int MmPickupCount(PlaceListEntryMsg place)
        {
            if (place?.MmPickupX == null || place.MmPickupY == null || place.MmPickupZ == null) return 0;
            return Math.Min(place.MmPickupX.Length, Math.Min(place.MmPickupY.Length, place.MmPickupZ.Length));
        }

        public static List<BlockPos> MmPickupBlocks(PlaceListEntryMsg place)
        {
            var blocks = new List<BlockPos>();
            int count = MmPickupCount(place);
            for (int i = 0; i < count; i++)
                blocks.Add(new BlockPos(place.MmPickupX[i], place.MmPickupY[i], place.MmPickupZ[i], place.Dim));
            return blocks;
        }

        public static int PlaceFingerprint(PlaceListEntryMsg place)
        {
            if (place == null) return 0;
            unchecked
            {
                int h = 17;
                h = h * 31 + place.Dim;
                h = h * 31 + (place.HasBounds ? 1 : 0);
                h = h * 31 + place.BoundsMinX; h = h * 31 + place.BoundsMinY; h = h * 31 + place.BoundsMinZ;
                h = h * 31 + place.BoundsMaxX; h = h * 31 + place.BoundsMaxY; h = h * 31 + place.BoundsMaxZ;
                if (place.SpawnX != null)
                    for (int i = 0; i < place.SpawnX.Length; i++)
                        h = ((h * 31 + place.SpawnX[i]) * 31 + place.SpawnY[i]) * 31 + place.SpawnZ[i];
                if (place.Types != null)
                    foreach (var t in place.Types) h = h * 31 + (t?.GetHashCode() ?? 0);
                return h;
            }
        }

        // Safe per frame: only re-sends when place, revision or dimension changed.
        public void Update(PlaceListEntryMsg place)
        {
            focusPlaceId = place?.Id ?? -1;

            long id = Enabled ? (place?.Id ?? -1) : -1;
            // Spawn/pickup edits keep the id, so compare the revision too.
            int revision = HighlightRevision(place);
            int dim = PlayerDim();
            if (id == HighlightedPlaceId && revision == highlightedRevision && dim == highlightedDim) return;
            HighlightedPlaceId = id;
            highlightedRevision = revision;
            highlightedDim = dim;

            var player = capi.World.Player;
            if (player == null) return;

            if (place == null || !Enabled)
            {
                Clear();
                return;
            }

            // Another dimension's arena may share these X/Z; hide it until the admin goes there.
            if (place.Dim != dim)
            {
                ClearPlaceSlots(player);
                return;
            }

            // Spawns are stored at foot level; also mark the block below so it is visible.
            var spawnBlocks = new List<BlockPos>();
            if (place.SpawnX != null)
            {
                for (int i = 0; i < place.SpawnX.Length; i++)
                {
                    spawnBlocks.Add(new BlockPos(place.SpawnX[i], place.SpawnY[i], place.SpawnZ[i], place.Dim));
                    spawnBlocks.Add(new BlockPos(place.SpawnX[i], place.SpawnY[i] - 1, place.SpawnZ[i], place.Dim));
                }
            }
            // Colours are packed red in the low byte: ToRgba(a, b, g, r).
            int spawnColor = (ZoneVisualizer.SpawnMarkerColor & 0x00FFFFFF) | (170 << 24);
            var spawnColors = spawnBlocks.Select(_ => spawnColor).ToList();
            Send(player, SlotSpawns, spawnBlocks, spawnColors);

            // Murder Mystery weapon spawns, yellow.
            var pickupBlocks = MmPickupBlocks(place);
            var pickupColors = pickupBlocks.Select(_ => ColorUtil.ToRgba(190, 40, 220, 240)).ToList();
            Send(player, SlotMmPickups, pickupBlocks, pickupColors);

            // Hunger Games chests, in their tier colour.
            var hgLootBlocks = new List<BlockPos>();
            var hgLootColors = new List<int>();
            var hg = HgSetupFor(place);
            if (hg?.SpotX != null)
            {
                for (int i = 0; i < hg.SpotX.Length; i++)
                {
                    hgLootBlocks.Add(new BlockPos(hg.SpotX[i], hg.SpotY[i], hg.SpotZ[i], place.Dim));
                    int c = ZoneVisualizer.LootTierColor(TierIndexOf(hg.Tiers, hg.SpotTier?[i]));
                    hgLootColors.Add((c & 0x00FFFFFF) | (190 << 24));
                }
            }
            Send(player, SlotHgLoot, hgLootBlocks, hgLootColors);

            if (!place.HasBounds)
            {
                capi.World.HighlightBlocks(player, SlotBounds, new List<BlockPos>(), new List<int>());
                return;
            }

            var outline = BuildBoxOutline(place.BoundsMinX, place.BoundsMinY, place.BoundsMinZ,
                                          place.BoundsMaxX, place.BoundsMaxY, place.BoundsMaxZ, place.Dim);
            var colors = outline.Select(_ => ColorUtil.ToRgba(110, 60, 200, 255)).ToList(); // amber
            Send(player, SlotBounds, outline, colors);
        }

        private void RedrawFocus()
        {
            if (focusPlaceId < 0) return;
            Update(FindPlace(focusPlaceId));
        }

        // Hash of everything drawn; recomputed only when a new place list or HG setup arrives.
        private object revisionListRef, revisionHgRef;
        private long revisionPlaceId = long.MinValue;
        private int revisionCached;

        private int HighlightRevision(PlaceListEntryMsg place)
        {
            if (place == null) return 0;
            var listRef = networkHandler.PlaceList.Value;
            var hg = HgSetupFor(place);
            if (ReferenceEquals(listRef, revisionListRef) && ReferenceEquals(hg, revisionHgRef)
                && place.Id == revisionPlaceId)
                return revisionCached;

            unchecked
            {
                int h = PlaceFingerprint(place);
                if (hg?.SpotX != null)
                    for (int i = 0; i < hg.SpotX.Length; i++)
                        h = (((h * 31 + hg.SpotX[i]) * 31 + hg.SpotY[i]) * 31 + hg.SpotZ[i]) * 31
                            + (hg.SpotTier?[i]?.GetHashCode() ?? 0);
                // Positions, not a count: moving a pickup spot must redraw it too.
                foreach (var p in MmPickupBlocks(place))
                    h = ((h * 31 + p.X) * 31 + p.Y) * 31 + p.Z;
                revisionCached = h;
            }
            revisionListRef = listRef;
            revisionHgRef = hg;
            revisionPlaceId = place.Id;
            return revisionCached;
        }

        // HG world preview is server particles; it follows Enabled so "hide outlines" hides it too.
        public bool HgPreviewOn { get; private set; }
        private long hgPreviewPlace = -1;

        public void SetHgPreview(long placeId, bool on)
        {
            HgPreviewOn = on;
            hgPreviewPlace = placeId;
            if (Enabled || !on) networkHandler.SendHgPreview(placeId, on);
        }

        public void SetEnabled(bool enabled)
        {
            if (HgPreviewOn && hgPreviewPlace >= 0 && enabled != Enabled)
                networkHandler.SendHgPreview(hgPreviewPlace, enabled);
            Enabled = enabled;
            // Force the next Update to redraw rather than early-out.
            HighlightedPlaceId = -2;
            if (!enabled) Clear();
            else
            {
                RedrawFocus();
                UpdateMarkPreview();
            }
        }

        public void Clear()
        {
            var player = capi.World?.Player;
            if (player == null) return;
            ClearPlaceSlots(player);
            capi.World.HighlightBlocks(player, SlotMarks, new List<BlockPos>(), new List<int>());
            HighlightedPlaceId = -1;
        }

        private void ClearPlaceSlots(IClientPlayer player)
        {
            capi.World.HighlightBlocks(player, SlotBounds, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(player, SlotSpawns, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(player, SlotMmPickups, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(player, SlotHgLoot, new List<BlockPos>(), new List<int>());
        }

        // 12 edges only; long edges are sampled to stay under MaxBlocks.
        public static List<BlockPos> BuildBoxOutline(int x0, int y0, int z0, int x1, int y1, int z1, int dim)
        {
            long spanX = x1 - x0 + 1, spanY = y1 - y0 + 1, spanZ = z1 - z0 + 1;
            long edgeBlocks = 4 * (spanX + spanY + spanZ);
            int step = edgeBlocks <= MaxBlocks ? 1 : (int)(edgeBlocks / MaxBlocks) + 1;

            var list = new List<BlockPos>();
            void AddX(int y, int z) { for (int x = x0; x <= x1; x += step) list.Add(new BlockPos(x, y, z, dim)); list.Add(new BlockPos(x1, y, z, dim)); }
            void AddZ(int x, int y) { for (int z = z0; z <= z1; z += step) list.Add(new BlockPos(x, y, z, dim)); list.Add(new BlockPos(x, y, z1, dim)); }
            void AddY(int x, int z) { for (int y = y0; y <= y1; y += step) list.Add(new BlockPos(x, y, z, dim)); list.Add(new BlockPos(x, y1, z, dim)); }

            AddX(y0, z0); AddX(y0, z1); AddX(y1, z0); AddX(y1, z1);
            AddZ(x0, y0); AddZ(x1, y0); AddZ(x0, y1); AddZ(x1, y1);
            AddY(x0, z0); AddY(x1, z0); AddY(x0, z1); AddY(x1, z1);
            return list;
        }

        // Crosshair block from panel open (one Mark per aim), else the block under the feet.
        public bool TryGetMarkTarget(out BlockPos pos, out bool fromLook)
        {
            var player = capi.World.Player;
            // Not CurrentBlockSelection: with the panel open the engine picks under the cursor.
            if (AimAtOpen != null && !MarkAtFeet)
            {
                pos = AimAtOpen.Copy();
                fromLook = true;
                return true;
            }

            var entity = player?.Entity;
            if (entity != null)
            {
                pos = entity.Pos.AsBlockPos.DownCopy();
                fromLook = false;
                return true;
            }

            pos = null;
            fromLook = false;
            return false;
        }

        public bool TakeMark(int which)
        {
            if (!TryGetMarkTarget(out var target, out bool fromLook)) return false;
            if (which == 1)
            {
                Mark1X = target.X; Mark1Y = target.Y; Mark1Z = target.Z;
                mark1Dim = target.dimension;
                HasMark1 = true;
            }
            else
            {
                Mark2X = target.X; Mark2Y = target.Y; Mark2Z = target.Z;
                mark2Dim = target.dimension;
                HasMark2 = true;
            }
            if (fromLook) AimAtOpen = null; // one aim, one corner: the next Mark needs a fresh aim or your feet
            UpdateMarkPreview();
            return true;
        }

        // Starts the marks from the saved bounds, so they can be nudged instead of walked again.
        public void MarksFromBounds(PlaceListEntryMsg place)
        {
            Mark1X = place.BoundsMinX; Mark1Y = place.BoundsMinY; Mark1Z = place.BoundsMinZ;
            Mark2X = place.BoundsMaxX; Mark2Y = place.BoundsMaxY; Mark2Z = place.BoundsMaxZ;
            mark1Dim = mark2Dim = place.Dim;
            HasMark1 = HasMark2 = true;
        }

        // Moves the box face on that side by delta (negative shrinks). False if it would turn inside out.
        public bool NudgeMarks(BlockFacing face, int delta)
        {
            if (!HasMark1 || !HasMark2) return false;
            var n = face.Normali;
            int[] min = { Math.Min(Mark1X, Mark2X), Math.Min(Mark1Y, Mark2Y), Math.Min(Mark1Z, Mark2Z) };
            int[] max = { Math.Max(Mark1X, Mark2X), Math.Max(Mark1Y, Mark2Y), Math.Max(Mark1Z, Mark2Z) };
            int[] dir = { n.X, n.Y, n.Z };
            for (int i = 0; i < 3; i++)
            {
                if (dir[i] > 0) max[i] += delta;
                else if (dir[i] < 0) min[i] -= delta;
            }
            if (min[0] > max[0] || min[1] > max[1] || min[2] > max[2]) return false;

            Mark1X = min[0]; Mark1Y = min[1]; Mark1Z = min[2];
            Mark2X = max[0]; Mark2Y = max[1]; Mark2Z = max[2];
            mark1Dim = mark2Dim;
            UpdateMarkPreview();
            return true;
        }

        public void ClearMarks()
        {
            HasMark1 = false;
            HasMark2 = false;
            UpdateMarkPreview();
        }

        public void UpdateMarkPreview()
        {
            var player = capi.World.Player;
            if (player == null) return;

            // Box uses mark 2's dimension, as the server stores it; hidden in other dimensions.
            int markDim = HasMark2 ? mark2Dim : mark1Dim;
            if (!Enabled || (!HasMark1 && !HasMark2) || markDim != PlayerDim())
            {
                capi.World.HighlightBlocks(player, SlotMarks, new List<BlockPos>(), new List<int>());
                return;
            }

            List<BlockPos> blocks;
            if (HasMark1 && HasMark2)
            {
                blocks = BuildBoxOutline(
                    Math.Min(Mark1X, Mark2X), Math.Min(Mark1Y, Mark2Y), Math.Min(Mark1Z, Mark2Z),
                    Math.Max(Mark1X, Mark2X), Math.Max(Mark1Y, Mark2Y), Math.Max(Mark1Z, Mark2Z), mark2Dim);
            }
            else
            {
                blocks = new List<BlockPos> { HasMark1
                    ? new BlockPos(Mark1X, Mark1Y, Mark1Z, mark1Dim)
                    : new BlockPos(Mark2X, Mark2Y, Mark2Z, mark2Dim) };
            }

            var colors = blocks.Select(_ => ColorUtil.ToRgba(170, 40, 170, 255)).ToList(); // orange
            Send(player, SlotMarks, blocks, colors);
        }

        private object placementOwner;
        private bool placementShown;
        private BlockPos placementPos;
        private int placementColor;

        // Called per frame; owner stops one panel from clearing another panel's marker.
        public void UpdatePlacementMarker(object owner, bool wanted, int tierIdx)
        {
            var plr = capi.World?.Player;

            if (!wanted || plr?.Entity == null)
            {
                if (placementOwner != null && placementOwner != owner) return;
                if (placementShown && plr != null)
                {
                    capi.World.HighlightBlocks(plr, SlotHgPlacement, new List<BlockPos>(), new List<int>());
                    placementShown = false;
                    placementPos = null;
                }
                placementOwner = null;
                return;
            }

            placementOwner = owner;
            var pos = plr.Entity.Pos.AsBlockPos.DownCopy();
            // Preview-marker palette, made translucent (highlight ints are ABGR; alpha in top byte).
            int color = (ZoneVisualizer.LootTierColor(tierIdx) & 0x00FFFFFF) | (110 << 24);

            if (placementShown && pos.Equals(placementPos) && color == placementColor)
                return;

            // Under the player's own feet, so always the viewer's dimension; Send fixes the render Y.
            Send(plr, SlotHgPlacement, new List<BlockPos> { pos }, new List<int> { color });
            placementShown = true;
            placementPos = pos;
            placementColor = color;
        }

        public void Dispose()
        {
            networkHandler.PlaceList.Changed -= onPlaceList;
            networkHandler.HgSetup.Changed -= onHgSetup;
            capi.Event.UnregisterGameTickListener(dimWatchId);
            // The engine keeps highlight slots by id; without this they stay painted until relog.
            try
            {
                placementOwner = null;
                UpdatePlacementMarker(null, false, -1);
                HasMark1 = false;
                HasMark2 = false;
                Clear();
            }
            catch { }
        }
    }
}
