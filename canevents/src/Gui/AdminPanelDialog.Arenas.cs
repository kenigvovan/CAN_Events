using System.Linq;
using System.Numerics;
using canevents.src.Network;
using ImGuiNET;

namespace canevents.src.Gui
{
    // The Arenas tab, split into the sections it draws (one per mode).
    public partial class AdminPanelDialog
    {
        private void DrawArenasTab()
        {
            Heading("Arena Management");
            ImGui.TextDisabled("Setup column: S spawns, B bounds, H hill, C checkpoints, Z zone, P platform. Red means missing.");
            ImGui.Spacing();

            var placeList = networkHandler.PlaceList.Value;

            if (ImGui.Button("Refresh")) networkHandler.RequestPlaceList();

            DrawPlacesTable();

            var selectedPlace = placeList?.Places != null
                ? System.Array.Find(placeList.Places, p => p.Id == selectedPlaceId)
                : null;

            // Outline it in the world so "bounds" stops being an abstract number pair.
            UpdatePlaceHighlight(selectedPlace);

            if (selectedPlace != null)
            {
                ImGui.Spacing();
                ImGui.Separator();
                DrawPlaceHeader(selectedPlace);

                ImGui.PushID("detail_" + selectedPlace.Id);

                bool openDeleteModal = DrawPlaceActions(selectedPlace);
                DrawSpawnPointList(selectedPlace, "spawn_",
                    "Spawn points (order assigns them to teams):");
                if (openDeleteModal) pendingDeletePlaceId = selectedPlace.Id;

                DrawBoundsEditor(selectedPlace);

                // Per-type setup lives next to the place it configures.
                if (HasType(selectedPlace, "KOTH")) DrawKothSection(selectedPlace);
                if (HasType(selectedPlace, "PARKOUR")) DrawParkourSection(selectedPlace);
                if (HasType(selectedPlace, "HUNGERGAMES")) DrawHungerGamesSection(selectedPlace);
                if (HasType(selectedPlace, "CTF")) DrawCtfSection(selectedPlace);
                if (HasType(selectedPlace, "SPLEEF")) DrawSpleefSection(selectedPlace);

                ImGui.PopID();
                if (openDeleteModal)
                {
                    deleteModalOpen = true;
                    ImGui.OpenPopup("##ConfirmDelete");
                }
            }

            DrawDeleteModal();

            // This tab only edits existing places; creation lives in the New Arena tab.
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("To build a new arena, use the New Arena tab.");
        }

        private void DrawPlacesTable()
        {
            var placeList = networkHandler.PlaceList.Value;
            if (placeList?.Places == null || placeList.Places.Length == 0)
            {
                ImGui.TextColored(ColMuted, "No places found.");
                return;
            }

            float tableHeight = System.Math.Min(placeList.Places.Length * 22f + 22f, 180f);
            if (!ImGui.BeginTable("##PlacesTable", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                new Vector2(ImGui.GetContentRegionAvail().X, tableHeight)))
                return;

            ImGui.TableSetupColumn("ID",     ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableSetupColumn("Name",   ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Types",  ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Size",   ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Sp.",    ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Setup",  ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var place in placeList.Places)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);

                bool isSelected = selectedPlaceId == place.Id;
                ImGui.PushID((int)place.Id);
                if (ImGui.Selectable(place.Id.ToString(), isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    selectedPlaceId = place.Id;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(BuildPlaceTooltip(place));
                ImGui.PopID();

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(place.Name ?? "");

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(place.Types != null ? string.Join(", ", place.Types) : "");

                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(ColMuted, FormatBoundsSize(place));

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(place.SpawnCount.ToString());

                ImGui.TableSetColumnIndex(5);
                DrawSetupBadges(place);

                ImGui.TableSetColumnIndex(6);
                if (place.Occupied)
                    ImGui.TextColored(ColBad, "In use");
                else if (!place.Enabled)
                    ImGui.TextColored(ColMuted, "Off");
                else
                    ImGui.TextColored(ColOk, "Free");
            }

            ImGui.EndTable();
        }

        private void DrawPlaceHeader(PlaceListEntryMsg place)
        {
            Heading(place.Name + " (#" + place.Id + ")");

            if (!string.IsNullOrWhiteSpace(place.Description))
                ImGui.TextWrapped(place.Description);

            if (place.HasBounds)
            {
                ImGui.TextDisabled(string.Format("Bounds {0},{1},{2} .. {3},{4},{5}   ({6})",
                    place.BoundsMinX, place.BoundsMinY, place.BoundsMinZ,
                    place.BoundsMaxX, place.BoundsMaxY, place.BoundsMaxZ,
                    FormatBoundsSize(place)));
            }
            else
            {
                ImGui.TextColored(ColBad, "No bounds: /canep commands cannot target this place.");
            }

            ImGui.TextDisabled(string.IsNullOrEmpty(place.SchematicTemplateCode)
                ? "Source: built in world"
                : "Source: template '" + place.SchematicTemplateCode + "'"
                  + (place.SchematicOnDemand ? " (placed on demand)" : ""));

            if (place.BehaviorSummary != null && place.BehaviorSummary.Length > 0)
            {
                foreach (var line in place.BehaviorSummary)
                    ImGui.TextColored(ColOk, line);
            }

            ImGui.Spacing();
        }

        /// <summary>Action row for the selected place. Returns true when Delete was clicked.</summary>
        private bool DrawPlaceActions(PlaceListEntryMsg place)
        {
            // Other controls act on where you stand, so "Go to" comes first.
            if (GoSmallButton("Go to")) networkHandler.SendTeleportToPlace(place.Id);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Teleports you to this place: its first spawn point,\nor the bounds centre if it has no spawns yet.");
            ImGui.SameLine();

            // Outline survives closing the panel so you can walk the arena.
            if (highlightEnabled)
            {
                if (ImGui.SmallButton("Hide outline")) SetHighlightEnabled(false);
            }
            else
            {
                if (GoSmallButton("Show outline")) SetHighlightEnabled(true);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Draws this place's bounds and spawn points in the world.\nStays visible after you close this panel: press F7 and walk around.");
            ImGui.SameLine();

            if (ImGui.SmallButton("+Spawn")) networkHandler.SendAddSpawnPoint(place.Id);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add a spawn point at your current position.\nTeam-based events use spawn points 1:1 to teams.");
            ImGui.SameLine();

            if (place.Enabled)
            {
                if (ImGui.SmallButton("Disable")) networkHandler.SendTogglePlace(place.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Take this place out of rotation without deleting it.");
            }
            else
            {
                if (GoButton("Enable")) networkHandler.SendTogglePlace(place.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-add this place to rotation.");
            }
            ImGui.SameLine();

            bool openDeleteModal = ImGui.SmallButton("Del");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this place permanently. Asks for confirmation.");
            return openDeleteModal;
        }

        /// <summary>
        /// Editable spawn-point list, shared by the Arenas tab and the New Arena wizard.
        /// <paramref name="requiredSpawns"/> dims spawns beyond what the mode needs; pass 0 when
        /// there is no team count to compare against.
        /// </summary>
        private void DrawSpawnPointList(PlaceListEntryMsg place, string idPrefix, string caption,
            int requiredSpawns = 0, bool warnOnExtra = false)
        {
            if (place.SpawnX == null || place.SpawnX.Length == 0)
            {
                if (warnOnExtra)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled("No spawn points yet. Stand where a team should start and add one.");
                }
                return;
            }

            ImGui.Spacing();
            if (!string.IsNullOrEmpty(caption)) ImGui.TextColored(ColMuted, caption);

            int removeIdx = -1;
            for (int i = 0; i < place.SpawnX.Length; i++)
            {
                ImGui.PushID(idPrefix + i);

                bool extra = requiredSpawns > 0 && i >= requiredSpawns;
                bool outside = !PositionInsideBounds(place, place.SpawnX[i], place.SpawnZ[i]);

                ImGui.TextColored(outside ? ColBad : (extra ? ColMuted : ColOk),
                    string.Format("team {0}", i + 1));
                ImGui.SameLine();
                ImGui.TextDisabled(string.Format("({0}, {1}, {2})", place.SpawnX[i], place.SpawnY[i], place.SpawnZ[i]));
                if (outside)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColBad, "outside bounds");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("This spawn sits outside the place's footprint.\nIt will still be used, but players start off the arena.");
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Move here"))
                {
                    // Keep the index: re-adding at the end would reassign this team's corner.
                    networkHandler.SendMoveSpawnPoint(place.Id, i);
                    LogAction(true, "> move spawn " + (i + 1));
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Moves this team's spawn to where you are standing, keeping its number.");

                ImGui.SameLine();
                if (StopSmallButton("x")) removeIdx = i;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Removes it. The spawns after it shift up a team.");

                ImGui.PopID();
            }
            if (removeIdx >= 0)
            {
                networkHandler.SendRemoveSpawnPoint(place.Id, removeIdx);
                LogAction(true, "> remove spawn " + (removeIdx + 1));
            }

            if (warnOnExtra && requiredSpawns > 0 && place.SpawnX.Length > requiredSpawns)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColWarn, "More spawns than teams: they will be handed out per player,");
                ImGui.TextColored(ColWarn, "which splits teams across separate corners.");
            }
        }

        /// <summary>Size readout for the two marked corners, when both are set.</summary>
        private void DrawMarkedAreaSize()
        {
            if (!hasMark1 || !hasMark2) return;
            int sx = System.Math.Abs(mark2X - mark1X) + 1;
            int sz = System.Math.Abs(mark2Z - mark1Z) + 1;
            ImGui.TextColored(ColOk, string.Format("Area {0}x{1}", sx, sz));
        }

        /// <summary>
        /// The apply-bounds button, disabled until both corners are marked. Shared by the Arenas
        /// tab and the wizard, which differ only in the label.
        /// </summary>
        private void DrawApplyBoundsButton(PlaceListEntryMsg place, string label)
        {
            bool ready = hasMark1 && hasMark2;
            if (!ready) ImGui.BeginDisabled();
            if (GoButton(label))
            {
                networkHandler.SendSetPlaceBounds(place.Id,
                    mark1X, mark1Y, mark1Z, mark2X, mark2Y, mark2Z, wizGenerateSpawns);
                LogAction(true, "> set bounds on '" + place.Name + "'");
                // Marks stay put: clearing them made the outline blink out until the list came back.
                highlightedPlaceId = -2;
            }
            if (!ready) ImGui.EndDisabled();
        }

        // Team whose post the "set post here" button will write. 1-based, like the command.
        private int ctfPostTeam = 1;

        private void DrawCtfSection(PlaceListEntryMsg place)
        {
            ImGui.Spacing();
            Heading("Capture the Flag - " + (place.Name ?? "?") + " (#" + place.Id + ")");
            // Buttons below send this place's id, unlike /canep ctfpost, which resolves the place
            // from the caller's position and picks the wrong one when arenas overlap.

            if (ImGui.CollapsingHeader("How CTF works##ctfhelp"))
            {
                ImGui.BulletText("Each team needs one flag post. Post 1 belongs to team 1, post 2 to team 2.");
                ImGui.BulletText("You score by carrying an enemy flag to your own post - but only while");
                ImGui.BulletText("  your own flag is still home, so a mutual steal is a stand-off.");
                ImGui.BulletText("Killing a carrier sends the flag straight back to its post.");
                ImGui.BulletText("Players respawn at their own team's spawn point, so add one spawn per team.");
            }

            // The wizard knows the team count; on the Arenas tab a place is not tied to one event,
            // so spawn points stand in for "how many teams".
            int postCount = CtfPostCount(place);
            int wantPosts = wizStep != WizardStep.Done && wizTypes != null && wizTypes.Contains("CTF")
                ? wizRequiredSpawns
                : System.Math.Max(2, place.SpawnCount);

            if (postCount == 0)
                ImGui.TextColored(ColBad, "NO FLAG POSTS SET: nobody can capture. Add one per team below.");
            else if (postCount < wantPosts)
                ImGui.TextColored(ColBad, string.Format(
                    "Only {0} of {1} posts set - a team without a post cannot capture or be captured from.",
                    postCount, wantPosts));
            else
                ImGui.TextColored(ColOk, string.Format("{0} flag post(s) set - one per team.", postCount));

            // Post coordinates: "2 posts set" alone is unverifiable.
            if (place.BehaviorSummary != null)
            {
                foreach (var line in place.BehaviorSummary)
                {
                    if (line == null || !line.StartsWith("ctf post ", System.StringComparison.OrdinalIgnoreCase)) continue;
                    ImGui.TextColored(line.IndexOf('@') >= 0 ? ColMuted : ColBad, "  " + line);
                }
            }

            ImGui.Spacing();
            if (ImGui.SmallButton("Refresh##ctf")) networkHandler.RequestPlaceList();

            // The post lands at your feet, so standing outside the bounds puts it off the arena.
            if (place.HasBounds && !PlayerInsideBounds(place))
            {
                ImGui.TextColored(ColWarn, "You are OUTSIDE this place's bounds:");
                ImGui.TextColored(ColWarn, "the post would be placed off the arena.");
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(100);
            ImGui.InputInt("Team##ctfteam", ref ctfPostTeam, 1);
            if (ctfPostTeam < 1) ctfPostTeam = 1;
            HelpMarker("Which team this post belongs to. Team 1 is the first party in the match, and so on.");

            // Deferred one frame so a Team value still being typed has committed; otherwise the
            // pre-edit team is sent and overwrites the wrong team's post.
            if (ctfSetPostPending)
            {
                ctfSetPostPending = false;
                networkHandler.SendSetCtfPost(place.Id, ctfPostTeam);
                LogAction(true, "> set CTF post " + ctfPostTeam + " on '" + place.Name + "'");
            }
            if (GoButton("Set team " + ctfPostTeam + "'s post at the block under my feet"))
                ctfSetPostPending = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Writes to this place by id - standing inside another arena's bounds does not matter.");
            ImGui.SameLine();
            if (StopButton("Clear all posts"))
            {
                networkHandler.SendClearCtfPosts(place.Id);
                LogAction(true, "> clear CTF posts on '" + place.Name + "'");
            }
        }

        private void DrawSpleefSection(PlaceListEntryMsg place)
        {
            ImGui.Spacing();
            Heading("Spleef");
            ImGui.TextColored(ColMuted, "Platform:");

            bool inEdit = platformEditPlaceId == place.Id;
            if (inEdit)
            {
                var state = networkHandler.PlatformEditState.Value;
                ImGui.TextColored(ColOk, "Editing - " + (state?.Count ?? 0) + " blocks");
                ImGui.SetNextItemWidth(60); ImGui.InputInt("Radius##pfr", ref platformFillRadius, 0);
                platformFillRadius = System.Math.Max(1, platformFillRadius);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(180); ImGui.InputText("Block##pfb", ref platformFillBlock, 64);
                if (ImGui.SmallButton("Fill Sq.")) networkHandler.SendPlatformFillShape(place.Id, false, platformFillRadius, platformFillBlock);
                ImGui.SameLine();
                if (ImGui.SmallButton("Fill Circ.")) networkHandler.SendPlatformFillShape(place.Id, true, platformFillRadius, platformFillBlock);
                ImGui.SameLine();
                if (ImGui.SmallButton("Fill Flood")) networkHandler.SendPlatformFloodFill(place.Id, platformFillRadius, platformFillBlock);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flood fill air blocks from your position\nuntil hitting terrain or max radius.");
                ImGui.SameLine();
                if (GoSmallButton("Save")) networkHandler.SendPlatformEditStop(place.Id, true);
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel")) networkHandler.SendPlatformEditStop(place.Id, false);
            }
            else
            {
                if (ImGui.SmallButton("Edit Platform")) networkHandler.SendPlatformEditStart(place.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Enter platform-edit mode: paint destructible blocks\ninside the place's bounds. Save to commit.");
            }

            ImGui.Spacing();
            ImGui.TextColored(ColMuted, "Cage:");
            ImGui.SetNextItemWidth(50); ImGui.InputInt("W##cw", ref cageWidth, 0); cageWidth = System.Math.Max(3, cageWidth);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50); ImGui.InputInt("D##cd", ref cageDepth, 0); cageDepth = System.Math.Max(3, cageDepth);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50); ImGui.InputInt("H##ch", ref cageHeight, 0); cageHeight = System.Math.Max(4, cageHeight);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140); ImGui.InputText("Block##cb", ref cageBlock, 64);
            ImGui.TextColored(ColMuted, "Stand in the center of the desired cage, then:");
            // Deferred a frame so cage W/D/H values still being typed have committed before the send.
            if (spleefSetCagePending)
            {
                spleefSetCagePending = false;
                networkHandler.SendSetCagePos(place.Id, cageWidth, cageDepth, cageHeight, cageBlock);
            }
            if (ImGui.SmallButton("+Cage")) spleefSetCagePending = true;
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear Cage")) networkHandler.SendClearCagePos(place.Id);
        }

        private void DrawDeleteModal()
        {
            if (!ImGui.BeginPopupModal("##ConfirmDelete", ref deleteModalOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
                return;

            var placeList = networkHandler.PlaceList.Value;
            var delPlace = placeList?.Places != null
                ? System.Array.Find(placeList.Places, p => p.Id == pendingDeletePlaceId)
                : null;

            ImGui.TextColored(ColBad, "Delete place?");
            ImGui.Text(delPlace?.Name ?? "#" + pendingDeletePlaceId);
            ImGui.Spacing();
            if (ImGui.Button("Yes, delete", new Vector2(100, 0)))
            {
                networkHandler.SendRemovePlaceWithRestore(pendingDeletePlaceId);
                if (selectedPlaceId == pendingDeletePlaceId) selectedPlaceId = -1;
                pendingDeletePlaceId = -1;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(70, 0)))
            {
                pendingDeletePlaceId = -1;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }
}
