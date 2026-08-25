using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui
{
    // Geometry: what counts as "inside" a place, the in-world outline of the selected one,
    // and the Mark 1 / Mark 2 corner picking that feeds it. Bounds are invisible otherwise.
    public partial class AdminPanelDialog
    {
        private static bool HasType(PlaceListEntryMsg place, string tag)
            => place?.Types != null && System.Array.IndexOf(place.Types, tag) >= 0;

        /// <summary>
        /// Is this position inside the place's footprint? X/Z only, matching the server's
        /// BoundsContain. Outside bounds still works at runtime, but is flagged as likely a mistake.
        /// </summary>
        private static bool PositionInsideBounds(PlaceListEntryMsg place, int x, int z)
        {
            if (place == null || !place.HasBounds) return true; // nothing to be outside of
            // Same X/Z-only rule as the server, from the one place that defines it - the client
            // works off a DTO rather than an EventPlace, so it passes the numbers directly.
            return PlaceBounds.Contains(place.BoundsMinX, place.BoundsMaxX,
                place.BoundsMinZ, place.BoundsMaxZ, x, z);
        }

        /// <summary>
        /// Where the player currently stands, judged against the same footprint. Callers check
        /// HasBounds themselves, so the no-bounds case just defers to PositionInsideBounds.
        /// </summary>
        private bool PlayerInsideBounds(PlaceListEntryMsg place)
        {
            var pp = capi.World.Player?.Entity?.Pos;
            if (pp == null) return true;
            return PositionInsideBounds(place, (int)System.Math.Floor(pp.X), (int)System.Math.Floor(pp.Z));
        }

        // In-world highlight of the selected place. Slot ids must be stable and ours alone:
        // the engine keys the highlight set by them, so reusing one replaces the previous drawing.
        private const int HighlightSlotBounds = 4180;
        private const int HighlightSlotSpawns = 4181;
        private const int HighlightSlotMarks = 4182;
        private const int HighlightSlotMmPickups = 4183;
        private const int HighlightMaxBlocks = 4000;

        private long highlightedPlaceId = -1;
        /// <summary>Fingerprint of what the outline was last drawn from, so an edit to the selected
        /// place redraws it (see UpdatePlaceHighlight).</summary>
        private int highlightedRevision = -1;
        // Sticky on purpose: closing the GUI must not clear the outline, since walking the
        // arena to judge its size is the point.
        private bool highlightEnabled = true;

        // Bounds marking, shared by the Arenas editor and the wizard.
        private bool hasMark1 = false;
        private bool hasMark2 = false;
        private int mark1X, mark1Y, mark1Z;
        private int mark2X, mark2Y, mark2Z;

        /// <summary>
        /// Draws the selected place's outline and spawn points in the world.
        /// </summary>
        private void UpdatePlaceHighlight(PlaceListEntryMsg place)
        {
            long id = highlightEnabled ? (place?.Id ?? -1) : -1;
            // Selecting a different place is not the only reason to redraw: marking or removing a
            // spawn or a pickup spot leaves the id alone, and the overlay would keep showing the
            // arena as it was until the admin clicked away and back.
            int revision = place == null ? 0 : (place.SpawnCount * 397) ^ MmPickupCount(place);
            if (id == highlightedPlaceId && revision == highlightedRevision) return;
            highlightedPlaceId = id;
            highlightedRevision = revision;

            var player = capi.World.Player;
            if (player == null) return;

            if (place == null || !highlightEnabled)
            {
                ClearPlaceHighlight();
                return;
            }

            // A spawn is stored at foot level, invisible while you stand in it; marking the
            // block beneath gives a pad you can see from the ground.
            var spawnBlocks = new List<BlockPos>();
            if (place.SpawnX != null)
            {
                for (int i = 0; i < place.SpawnX.Length; i++)
                {
                    spawnBlocks.Add(new BlockPos(place.SpawnX[i], place.SpawnY[i], place.SpawnZ[i], 0));
                    spawnBlocks.Add(new BlockPos(place.SpawnX[i], place.SpawnY[i] - 1, place.SpawnZ[i], 0));
                }
            }
            // Green spawns / blue bounds / orange pending marks are drawn at once. (a, r, g, b)
            var spawnColors = spawnBlocks.Select(_ => ColorUtil.ToRgba(170, 80, 230, 80)).ToList();
            capi.World.HighlightBlocks(player, HighlightSlotSpawns, spawnBlocks, spawnColors);

            // Murder Mystery weapon spawns in yellow. Spacing them by eye is the whole job here -
            // the mode refuses to drop two tokens near each other, so spots the admin cannot see
            // silently turn into spots that never get used.
            var pickupBlocks = MmPickupBlocks(place);
            var pickupColors = pickupBlocks.Select(_ => ColorUtil.ToRgba(190, 40, 220, 240)).ToList();
            capi.World.HighlightBlocks(player, HighlightSlotMmPickups, pickupBlocks, pickupColors);

            if (!place.HasBounds)
            {
                capi.World.HighlightBlocks(player, HighlightSlotBounds, new List<BlockPos>(), new List<int>());
                return;
            }

            var outline = BuildBoundsOutline(place);
            var colors = outline.Select(_ => ColorUtil.ToRgba(110, 60, 200, 255)).ToList();
            capi.World.HighlightBlocks(player, HighlightSlotBounds, outline, colors);
        }

        private void SetHighlightEnabled(bool enabled)
        {
            highlightEnabled = enabled;
            // Force the next UpdatePlaceHighlight to redraw rather than early-out.
            highlightedPlaceId = -2;
            if (!enabled) ClearPlaceHighlight();
        }

        private void ClearPlaceHighlight()
        {
            var player = capi.World.Player;
            if (player == null) return;
            capi.World.HighlightBlocks(player, HighlightSlotBounds, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(player, HighlightSlotSpawns, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(player, HighlightSlotMarks, new List<BlockPos>(), new List<int>());
            capi.World.HighlightBlocks(player, HighlightSlotMmPickups, new List<BlockPos>(), new List<int>());
            highlightedPlaceId = -1;
        }

        /// <summary>
        /// The 12 edges of the bounds box: a filled volume would be thousands of blocks.
        /// Long edges are sampled so a huge arena still fits inside the block budget.
        /// </summary>
        private static List<BlockPos> BuildBoundsOutline(PlaceListEntryMsg p)
            => BuildBoxOutline(p.BoundsMinX, p.BoundsMinY, p.BoundsMinZ,
                               p.BoundsMaxX, p.BoundsMaxY, p.BoundsMaxZ);

        private static List<BlockPos> BuildBoxOutline(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            long spanX = x1 - x0 + 1, spanY = y1 - y0 + 1, spanZ = z1 - z0 + 1;
            long edgeBlocks = 4 * (spanX + spanY + spanZ);
            int step = edgeBlocks <= HighlightMaxBlocks ? 1 : (int)(edgeBlocks / HighlightMaxBlocks) + 1;

            var list = new List<BlockPos>();
            void AddX(int y, int z) { for (int x = x0; x <= x1; x += step) list.Add(new BlockPos(x, y, z, 0)); list.Add(new BlockPos(x1, y, z, 0)); }
            void AddZ(int x, int y) { for (int z = z0; z <= z1; z += step) list.Add(new BlockPos(x, y, z, 0)); list.Add(new BlockPos(x, y, z1, 0)); }
            void AddY(int x, int z) { for (int y = y0; y <= y1; y += step) list.Add(new BlockPos(x, y, z, 0)); list.Add(new BlockPos(x, y1, z, 0)); }

            AddX(y0, z0); AddX(y0, z1); AddX(y1, z0); AddX(y1, z1);
            AddZ(x0, y0); AddZ(x1, y0); AddZ(x0, y1); AddZ(x1, y1);
            AddY(x0, z0); AddY(x1, z0); AddY(x0, z1); AddY(x1, z1);
            return list;
        }

        // Mark 1 / Mark 2

        /// <summary>
        /// The corner the next Mark button will take: the block you are looking at, or the one
        /// under your feet. The panel grabs the mouse while open, so aiming is often impossible
        /// and the fallback is what makes these buttons usable.
        /// </summary>
        private bool TryGetMarkTarget(out BlockPos pos, out bool fromLook)
        {
            var player = capi.World.Player;
            var sel = player?.CurrentBlockSelection;
            if (sel != null)
            {
                pos = sel.Position.Copy();
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

        private void DrawMarkButtons()
        {
            bool haveTarget = TryGetMarkTarget(out var target, out bool fromLook);

            ImGui.TextDisabled(haveTarget
                ? (fromLook ? "Next mark: the block you are looking at " : "Next mark: the block under your feet ")
                  + string.Format("({0}, {1}, {2})", target.X, target.Y, target.Z)
                : "Cannot read a position right now.");

            if (!haveTarget) ImGui.BeginDisabled();

            if (ImGui.Button("Mark 1"))
            {
                mark1X = target.X; mark1Y = target.Y; mark1Z = target.Z;
                hasMark1 = true;
                UpdateMarkPreview();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes one corner. Aim at a block before opening the panel,\nor just stand on the corner: the block under your feet is used.");
            ImGui.SameLine();
            if (hasMark1) ImGui.TextColored(ColOk, string.Format("({0}, {1}, {2})", mark1X, mark1Y, mark1Z));
            else ImGui.TextColored(ColMuted, "not set");

            if (ImGui.Button("Mark 2"))
            {
                mark2X = target.X; mark2Y = target.Y; mark2Z = target.Z;
                hasMark2 = true;
                UpdateMarkPreview();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes the OPPOSITE corner from Mark 1.");
            ImGui.SameLine();
            if (hasMark2) ImGui.TextColored(ColOk, string.Format("({0}, {1}, {2})", mark2X, mark2Y, mark2Z));
            else ImGui.TextColored(ColMuted, "not set");

            if (!haveTarget) ImGui.EndDisabled();

            if (hasMark1 || hasMark2)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear marks"))
                {
                    hasMark1 = false; hasMark2 = false;
                    UpdateMarkPreview();
                }
                ImGui.TextDisabled("The marked box stays drawn after you close the panel (F7); walk it to check the size.");
            }
        }

        /// <summary>
        /// Draws the box the current marks describe, before anything is committed.
        /// </summary>
        private void UpdateMarkPreview()
        {
            var player = capi.World.Player;
            if (player == null) return;

            if (!hasMark1 && !hasMark2)
            {
                capi.World.HighlightBlocks(player, HighlightSlotMarks, new List<BlockPos>(), new List<int>());
                return;
            }

            List<BlockPos> blocks;
            if (hasMark1 && hasMark2)
            {
                blocks = BuildBoxOutline(
                    System.Math.Min(mark1X, mark2X), System.Math.Min(mark1Y, mark2Y), System.Math.Min(mark1Z, mark2Z),
                    System.Math.Max(mark1X, mark2X), System.Math.Max(mark1Y, mark2Y), System.Math.Max(mark1Z, mark2Z));
            }
            else
            {
                blocks = new List<BlockPos> { hasMark1
                    ? new BlockPos(mark1X, mark1Y, mark1Z, 0)
                    : new BlockPos(mark2X, mark2Y, mark2Z, 0) };
            }

            var colors = blocks.Select(_ => ColorUtil.ToRgba(170, 40, 170, 255)).ToList(); // orange
            capi.World.HighlightBlocks(player, HighlightSlotMarks, blocks, colors);
        }
    }
}
