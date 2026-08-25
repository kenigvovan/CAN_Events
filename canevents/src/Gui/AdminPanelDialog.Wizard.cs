using System.Linq;
using System.Numerics;
using ImGuiNET;
using canevents.src.Network;

namespace canevents.src.Gui
{
    // New Arena wizard: walks the dependency chain (tags, bounds, spawns, mode geometry)
    // and reads each step's completion out of the place list, so it reflects the server.
    public partial class AdminPanelDialog
    {
        // CTF is in here because /canep ctfpost finds the place by the caller's position, which
        // only works on a place that has bounds.
        private static readonly string[] BoundsRequiringTypes = { "SPLEEF", "PARKOUR", "KOTH", "HUNGERGAMES", "CTF" };

        private bool WizNeedsBounds =>
            wizTypes != null && wizTypes.Any(t => BoundsRequiringTypes.Contains(t));

        private PlaceListEntryMsg WizPlace =>
            wizPlaceId >= 0 && networkHandler.PlaceList.Value?.Places != null
                ? System.Array.Find(networkHandler.PlaceList.Value.Places, p => p.Id == wizPlaceId)
                : null;

        private bool WizStepDone(WizardStep step)
        {
            var place = WizPlace;
            switch (step)
            {
                case WizardStep.ChooseEvent: return wizTypes != null;
                case WizardStep.CreatePlace: return place != null;
                // Optional-bounds modes still stop here once, rather than skipping a promised step.
                case WizardStep.Bounds: return place != null && (place.HasBounds || wizBoundsSkipped);
                case WizardStep.Spawns: return place != null && place.SpawnCount >= wizRequiredSpawns;
                case WizardStep.Special: return place != null && WizSpecialDone(place);
                case WizardStep.Activate: return place != null && place.Enabled;
                default: return false;
            }
        }

        // Does the place carry the mode-specific data its type needs to actually play?
        private bool WizSpecialDone(PlaceListEntryMsg place)
        {
            if (wizTypes == null) return true;
            if (wizTypes.Contains("KOTH") && string.IsNullOrEmpty(place.KothHill)) return false;
            if (wizTypes.Contains("PARKOUR") && !HasSummary(place, "checkpoints")) return false;
            if (wizTypes.Contains("HUNGERGAMES") && !HasSummary(place, "zone")) return false;
            if (wizTypes.Contains("SPLEEF") && !HasSummary(place, "platform")) return false;
            // One post per team: a team without a post can neither capture nor be captured from.
            if (wizTypes.Contains("CTF") && CtfPostCount(place) < wizRequiredSpawns) return false;
            return true;
        }

        private static bool HasSummary(PlaceListEntryMsg place, string prefix)
            => place.BehaviorSummary != null
               && place.BehaviorSummary.Any(s => s != null && s.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Flag posts that actually carry coordinates, counted from the server's per-post summary
        /// lines ("ctf post 1 @ x,y,z" / "ctf post 2: NOT SET"). An empty slot is not a post.
        /// </summary>
        internal static int CtfPostCount(PlaceListEntryMsg place)
        {
            if (place?.BehaviorSummary == null) return 0;

            int n = 0;
            foreach (var line in place.BehaviorSummary)
            {
                if (line == null) continue;
                if (!line.StartsWith("ctf post ", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (line.IndexOf('@') >= 0) n++;
            }
            return n;
        }

        // Fresh place data arrived: advance to whatever is still unfinished.
        private void OnPlaceListForWizard()
        {
            if (wizStep == WizardStep.ChooseEvent || wizStep == WizardStep.Done) return;
            for (var s = WizardStep.CreatePlace; s <= WizardStep.Activate; s++)
            {
                if (!WizStepDone(s))
                {
                    // Never yank the user off the mode-setup screen: every added checkpoint or loot
                    // spot refreshes the list. Its own Continue button is the way forward.
                    if (wizStep == WizardStep.Special && s > WizardStep.Special) return;
                    wizStep = s;
                    return;
                }
            }
            if (wizStep == WizardStep.Special) return;
            wizStep = WizardStep.Done;
        }

        private void DrawWizardTab()
        {
            UpdatePlaceHighlight(WizPlace);

            ImGui.TextColored(ColAccent, "New Arena");
            ImGui.TextDisabled("Pick what you want to run; this walks through everything that event needs.");
            ImGui.Spacing();

            // Checklist on the left, current step on the right.
            if (ImGui.BeginTable("##WizardLayout", 2, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("steps", ImGuiTableColumnFlags.WidthFixed, 170f);
                ImGui.TableSetupColumn("body", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                DrawWizardChecklist();

                ImGui.TableSetColumnIndex(1);
                DrawWizardBody();

                ImGui.EndTable();
            }

            // Every step past the first acts on your position, which may be far from the place.
            var wizPlace = WizPlace;
            if (wizPlace != null)
            {
                ImGui.Spacing();
                if (GoSmallButton("Go to " + wizPlace.Name))
                    networkHandler.SendTeleportToPlace(wizPlace.Id);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Teleports you to this place so the next step acts where you expect.");
            }

            ImGui.Spacing();
            ImGui.Separator();
            if (ImGui.SmallButton("Start over"))
            {
                wizStep = WizardStep.ChooseEvent;
                wizEventCode = null; wizTypes = null; wizPlaceId = -1; wizPlaceName = "";
                hasMark1 = false; hasMark2 = false; wizBoundsSkipped = false;
                UpdateMarkPreview();
            }
        }

        // Names the mode-specific step after what it asks for ("Set the hill", not a generic label).
        private string WizModeSetupLabel()
        {
            if (wizTypes == null) return "Mode setup";
            if (wizTypes.Contains("KOTH")) return "Set the hill";
            if (wizTypes.Contains("PARKOUR")) return "Checkpoints";
            if (wizTypes.Contains("HUNGERGAMES")) return "Zone & loot";
            if (wizTypes.Contains("SPLEEF")) return "Platform";
            if (wizTypes.Contains("CTF")) return "Flag posts";
            return "Mode setup (none)";
        }

        private void DrawWizardChecklist()
        {
            var place = WizPlace;
            (WizardStep step, string label)[] steps =
            {
                (WizardStep.ChooseEvent, "Choose event"),
                (WizardStep.CreatePlace, "Create place"),
                (WizardStep.Bounds,      WizNeedsBounds ? "Mark bounds" : "Bounds (optional)"),
                (WizardStep.Spawns,      place != null && wizRequiredSpawns > 1
                                            ? "Spawns " + System.Math.Min(place.SpawnCount, wizRequiredSpawns) + "/" + wizRequiredSpawns
                                            : "Spawn points"),
                (WizardStep.Special,     WizModeSetupLabel()),
                (WizardStep.Activate,    "Enable"),
            };

            foreach (var (step, label) in steps)
            {
                bool done = WizStepDone(step);
                bool current = wizStep == step;
                string mark = done ? "[x] " : (current ? "[>] " : "[ ] ");
                var color = done ? ColOk : current ? ColAccent : ColMuted;

                ImGui.PushID("wizstep_" + (int)step);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                // Steps stay clickable so you can go back and fix an earlier one.
                if (ImGui.Selectable(mark + label, current) && wizTypes != null) wizStep = step;
                ImGui.PopStyleColor();
                ImGui.PopID();
            }
        }

        private void DrawWizardBody()
        {
            switch (wizStep)
            {
                case WizardStep.ChooseEvent: DrawWizChooseEvent(); break;
                case WizardStep.CreatePlace: DrawWizCreatePlace(); break;
                case WizardStep.Bounds: DrawWizBounds(); break;
                case WizardStep.Spawns: DrawWizSpawns(); break;
                case WizardStep.Special: DrawWizSpecial(); break;
                case WizardStep.Activate: DrawWizActivate(); break;
                case WizardStep.Done: DrawWizDone(); break;
            }
        }

        private void DrawWizChooseEvent()
        {
            ImGui.Text("Which event should this arena host?");
            ImGui.Spacing();

            var list = networkHandler.AdminEventList.Value;
            if (list?.Events == null || list.Events.Length == 0)
            {
                ImGui.TextDisabled("No events loaded.");
                if (ImGui.SmallButton("Refresh##wizev")) networkHandler.RequestAdminEventList();
                return;
            }

            if (ImGui.BeginChild("##WizEventList", new Vector2(0, 220), true))
            {
                foreach (var e in list.Events)
                {
                    ImGui.PushID("wizev_" + e.Code);
                    if (ImGui.Selectable(e.Code, wizEventCode == e.Code))
                    {
                        wizEventCode = e.Code;
                        wizTypes = e.EventPlaceTypes ?? System.Array.Empty<string>();
                        wizRequiredSpawns = System.Math.Max(1, e.RequiredSpawns);
                        wizPlaceId = -1;
                        wizPlaceName = e.Code + "_arena";
                        wizBoundsSkipped = false;
                        wizStep = WizardStep.CreatePlace;
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(e.EventPlaceTypes != null ? string.Join("+", e.EventPlaceTypes) : "");
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }

        private void DrawWizCreatePlace()
        {
            ImGui.Text("Creating a place for: " + wizEventCode);
            ImGui.TextDisabled("Tags: " + string.Join(" + ", wizTypes));
            ImGui.TextDisabled("Needs " + wizRequiredSpawns + " spawn point(s)"
                + (WizNeedsBounds ? " and bounds." : "; bounds optional but recommended."));
            ImGui.Spacing();

            ImGui.SetNextItemWidth(240);
            ImGui.InputText("Name##wizname", ref wizPlaceName, 64);

            ImGui.Spacing();
            bool named = !string.IsNullOrWhiteSpace(wizPlaceName);
            if (!named) ImGui.BeginDisabled();
            if (GoButton("Create the place"))
            {
                // Bounds and spawns are their own steps, so this only registers the place.
                networkHandler.SendCreatePlace(wizPlaceName, wizTypes, noAutoSpawns: true);
                LogAction(true, "> create place '" + wizPlaceName + "'");
            }
            if (!named) ImGui.EndDisabled();
            HelpMarker("Registers the place with no bounds and no spawn points yet; "
                + "the next steps set those where you actually want them.");
            if (!named) ImGui.TextDisabled("Name the place first.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("Already built it? Pick it from the list:");
            DrawWizPlacePicker();
        }

        // Lets the wizard adopt an existing place instead of forcing a new one.
        private void DrawWizPlacePicker()
        {
            var places = networkHandler.PlaceList.Value?.Places;
            if (places == null || places.Length == 0) { ImGui.TextDisabled("(no places yet)"); return; }

            foreach (var p in places)
            {
                if (wizTypes != null && wizTypes.Length > 0
                    && !wizTypes.All(t => p.Types != null && System.Array.IndexOf(p.Types, t) >= 0)) continue;
                ImGui.PushID("wizpick_" + p.Id);
                if (ImGui.SmallButton("Use '" + p.Name + "' (#" + p.Id + ")"))
                {
                    wizPlaceId = p.Id;
                    OnPlaceListForWizard();
                }
                ImGui.PopID();
            }
        }

        private void DrawWizBounds()
        {
            var place = WizPlace;
            if (place == null) return;

            ImGui.Text("Mark the arena's footprint.");
            ImGui.TextDisabled("Bounds are how /canep commands find the place you are standing in,");
            ImGui.TextDisabled("and how this mode's geometry gets attached to it.");
            if (!WizNeedsBounds)
                ImGui.TextDisabled("Optional for this mode, but it also lays down two spawn points for you.");
            ImGui.Spacing();

            if (place.HasBounds)
            {
                ImGui.TextColored(ColOk, "Bounds set: " + FormatBoundsSize(place));
                ImGui.TextDisabled("Mark two new corners below to replace them.");
                ImGui.Spacing();
            }

            DrawMarkButtons();

            DrawMarkedAreaSize();

            ImGui.Spacing();
            ImGui.Checkbox("Also lay down 2 spawn points on opposite sides", ref wizGenerateSpawns);
            HelpMarker("Replaces the current spawn points with one on each side of the area. "
                + "Turn this off if you have already placed spawns by hand.");

            ImGui.Spacing();
            DrawApplyBoundsButton(place, "Apply bounds");
            if (!hasMark1 || !hasMark2) ImGui.TextDisabled("Mark both corners first.");

            if (!WizNeedsBounds && !place.HasBounds)
            {
                ImGui.Spacing();
                if (ImGui.SmallButton("Skip: this mode does not need bounds"))
                {
                    wizBoundsSkipped = true;
                    wizStep = WizardStep.Spawns;
                }
            }
        }

        private void DrawWizSpawns()
        {
            var place = WizPlace;
            if (place == null) return;

            bool enough = place.SpawnCount >= wizRequiredSpawns;
            ImGui.TextColored(enough ? ColOk : ColWarn,
                string.Format("Spawn points: {0} / {1}", place.SpawnCount, wizRequiredSpawns));
            ImGui.TextDisabled("One per team, not one per player: with as many spawns as players");
            ImGui.TextDisabled("they get handed out per player and teams end up split across corners.");
            ImGui.Spacing();

            // Warn before the click: a spawn placed outside the box looks fine in the list.
            bool insideNow = PlayerInsideBounds(place);
            if (place.HasBounds && !insideNow)
                ImGui.TextColored(ColWarn, "You are standing OUTSIDE this place's bounds.");

            if (GoButton("Add spawn point at my position"))
            {
                networkHandler.SendAddSpawnPoint(place.Id);
                LogAction(true, "> add spawn point");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##wizsp")) networkHandler.RequestPlaceList();

            // Hunger Games can fill spawns, zone and loot in one shot.
            if (HasType(place, "HUNGERGAMES"))
            {
                ImGui.Separator();
                ImGui.TextDisabled("...or generate the whole HG layout at once (stand at the arena centre):");
                DrawHgGenerateButton(place);
            }

            DrawSpawnPointList(place, "wizspawn_", null, wizRequiredSpawns, warnOnExtra: true);
        }

        private void DrawWizSpecial()
        {
            var place = WizPlace;
            if (place == null) return;

            ImGui.Text("Mode-specific setup");
            ImGui.TextDisabled("Same controls as the Arenas tab, shown here so you don't have to go looking.");

            if (HasType(place, "KOTH")) DrawKothSection(place);
            if (HasType(place, "PARKOUR")) DrawParkourSection(place);
            if (HasType(place, "HUNGERGAMES")) DrawHungerGamesSection(place);
            if (HasType(place, "CTF")) DrawCtfSection(place);
            if (HasType(place, "MURDERMYSTERY")) DrawMurderMysterySection(place);
            if (HasType(place, "SPLEEF"))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColWarn,
                    "Spleef platforms are painted in the Arenas tab: select this place there and use Edit Platform.");
            }

            ImGui.Spacing();
            if (ImGui.SmallButton("Refresh##wizspecial")) networkHandler.RequestPlaceList();

            // Leaving this step is explicit, so adding another checkpoint doesn't bounce the
            // user to the next screen mid-work.
            if (WizSpecialDone(place))
            {
                ImGui.Spacing();
                if (GoButton("Continue"))
                    wizStep = place.Enabled ? WizardStep.Done : WizardStep.Activate;
            }
            else
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Finish the setup above to continue.");
            }
        }

        private void DrawWizActivate()
        {
            var place = WizPlace;
            if (place == null) return;

            ImGui.Text("Everything is set up. Enable the place so events can use it.");
            ImGui.Spacing();
            if (GoButton("Enable '" + place.Name + "'")) networkHandler.SendTogglePlace(place.Id);
        }

        private void DrawWizDone()
        {
            var place = WizPlace;
            var entry = networkHandler.AdminEventList.Value?.Events?
                .FirstOrDefault(e => e.Code == wizEventCode);

            ImGui.TextColored(ColOk, "Arena ready.");
            if (place != null) ImGui.Text(place.Name + " (#" + place.Id + ")");

            if (entry != null)
            {
                ImGui.Spacing();
                ImGui.Text(wizEventCode + ":");
                ImGui.SameLine();
                DrawReadinessCell(entry);
            }

            ImGui.Spacing();
            if (ImGui.Button("Re-check")) { networkHandler.RequestPlaceList(); networkHandler.RequestAdminEventList(); }
            ImGui.SameLine();
            bool canAnnounce = entry != null && entry.Readiness == EventReadiness.Ready;
            if (!canAnnounce) ImGui.BeginDisabled();
            if (ImGui.Button("Announce " + wizEventCode))
                networkHandler.SendAdminAction(AdminAction.AnnounceEvent, wizEventCode);
            if (!canAnnounce) ImGui.EndDisabled();
        }
    }
}
