using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using canevents.src.Tournaments;
using Newtonsoft.Json;

namespace canevents.src.Gui
{
    // Tournaments tab: configure a bracket, then watch it run and step in when needed.
    //
    // The bracket is drawn with ImDrawList in its own child window so scroll and clipping stay
    // local; each node gets an InvisibleButton for hover and the right-click menu.
    // The ImGui font has no Cyrillic, so Russian names render as boxes - hence the seeds next to
    // every name; the players' in-game bracket shows the real names.
    public partial class AdminPanelDialog
    {
        private long tourSelectedId;
        private long tourLoadedFor = -1;
        private bool tourBracketRequested;

        // Editor buffers: loaded from the server snapshot on selection change, sent back whole.
        private string tourName = "Tournament";
        private readonly HashSet<string> tourEventCodes = new HashSet<string>();
        private readonly HashSet<long> tourPlaces = new HashSet<long>();
        private readonly List<string> tourClasses = new List<string>();   // empty = every class
        private int tourTeamSize = 1;
        private int tourMaxEntrants = 16;
        private int tourReadySeconds = 60;
        private int tourConcurrent = 1;
        private int tourRake;
        private int tourSides;      // 0 = derive from the event
        private int tourAdvance = 1;
        private int tourAutoStart;              // 0 = off
        private int tourAutoStartDelay = 15;
        private int tourDeadlineMinutes;
        // Scheduling. Times of day are edited as text ("19:00;21:30"): one line to type versus
        // three widgets to click.
        private int tourOpenInMinutes;
        private int tourSignupWindow;
        private int tourRepeatMinutes;
        private string tourRepeatAt = "";
        private bool tourShowBeforeSignup = true;
        private int tourSeedMode;
        private int tourRatedMode;
        private bool tourThirdPlace = true;
        private bool tourLockClass;
        private bool tourOwnGear;
        private bool tourManualStart;
        private bool tourPauseScheduler = true;
        private readonly List<RewardItem> tourFee = new List<RewardItem>();
        private readonly List<int> tourSplit = new List<int> { 60, 30, 10 };
        private readonly List<List<RewardItem>> tourPrizes = new List<List<RewardItem>>();

        private bool tourCancelArmed;
        private bool tourCreating;
        private static readonly string[] TourSeedLabels = { "random", "by Elo" };
        private static readonly string[] TourRatedLabels = { "as the event", "unrated" };

        private static readonly string[] TourStateLabels =
            { "draft", "signup", "seeded", "running", "finished", "cancelled" };

        private void DrawTournamentTab()
        {
            var list = networkHandler.TournamentList.Value;

            Heading("Tournaments");
            if (ImGui.SmallButton("Refresh")) networkHandler.RequestTournamentList();
            ImGui.SameLine();
            if (GoSmallButton("New tournament")) TourNewDraft();

            // A selection can outlive what it pointed at (deleted by chat or another admin);
            // left alone, the next Save goes out as "configure #1" and fails confusingly.
            if (tourSelectedId > 0 && !ListContains(list, tourSelectedId))
            {
                tourSelectedId = 0;
                tourLoadedFor = -1;
                tourBracketRequested = false;
            }

            DrawTournamentList(list);
            ImGui.Separator();

            // A new tournament is edited in place with id 0: Save sends "create", and the server
            // answers with the id.
            if (tourSelectedId <= 0 && !tourCreating)
            {
                ImGui.TextDisabled("Select a tournament above, or create one.");
                return;
            }

            if (tourSelectedId > 0) EnsureTournamentLoaded();

            if (tourCreating)
            {
                ImGui.TextColored(ColAccent, "New tournament (not saved yet)");
                ImGui.SameLine();
                if (ImGui.SmallButton("Discard")) tourCreating = false;
                DrawTournamentSetup();
                return;
            }

            if (ImGui.BeginTabBar("##TourSub"))
            {
                if (ImGui.BeginTabItem("Setup")) { DrawTournamentSetup(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Bracket")) { DrawTournamentBracket(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }

        // List

        private void DrawTournamentList(TournamentListMsg list)
        {
            if (list == null || list.Count == 0)
            {
                ImGui.TextDisabled("No tournaments yet.");
                return;
            }

            if (!ImGui.BeginTable("##tourlist", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;

            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 36f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Entrants", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Events", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableHeadersRow();

            // Read through the accessors, not the arrays: protobuf omits an all-default array,
            // so those fields arrive null.
            for (int i = 0; i < list.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.PushID((int)list.Ids[i]);

                ImGui.TableSetColumnIndex(0); ImGui.Text(list.Ids[i].ToString());
                ImGui.TableSetColumnIndex(1); ImGui.Text(list.NameAt(i));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(TourStateColor(list.StateAt(i)), StateLabel(list.StateAt(i)));
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(list.EntrantsAt(i) + "/" + list.MaxEntrantsAt(i));
                ImGui.TableSetColumnIndex(4); ImGui.Text(list.EventsAt(i).Replace(";", ", "));
                ImGui.TableSetColumnIndex(5);
                if (ImGui.SmallButton(list.Ids[i] == tourSelectedId ? "shown" : "select"))
                {
                    tourSelectedId = list.Ids[i];
                    tourLoadedFor = -1;
                    tourBracketRequested = false;
                    tourCancelArmed = false;
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        private static bool ListContains(TournamentListMsg list, long id)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list.Ids[i] == id) return true;
            return false;
        }

        private static string StateLabel(int state)
            => state >= 0 && state < TourStateLabels.Length ? TourStateLabels[state] : "?";

        private Vector4 TourStateColor(int state)
        {
            switch ((TournamentState)state)
            {
                case TournamentState.Running: return ColOk;
                case TournamentState.Signup: return ColAccent;
                case TournamentState.Cancelled: return ColBad;
                case TournamentState.Finished: return ColMuted;
                default: return ColWarn;
            }
        }

        // Setup

        private void TourNewDraft()
        {
            tourSelectedId = 0;
            tourLoadedFor = -1;
            tourName = "Tournament";
            tourEventCodes.Clear();
            tourPlaces.Clear();
            tourClasses.Clear();
            tourTeamSize = 1;
            tourMaxEntrants = 16;
            tourReadySeconds = 60;
            tourConcurrent = 1;
            tourRake = 0;
            tourSides = 0;
            tourAdvance = 1;
            tourDeadlineMinutes = 0;
            tourOpenInMinutes = 0;
            tourSignupWindow = 0;
            tourRepeatMinutes = 0;
            tourRepeatAt = "";
            tourShowBeforeSignup = true;
            tourSeedMode = 0;
            tourRatedMode = 0;
            tourThirdPlace = true;
            tourPauseScheduler = true;
            tourFee.Clear();
            tourSplit.Clear(); tourSplit.AddRange(new[] { 60, 30, 10 });
            tourPrizes.Clear();
            tourCreating = true;
        }

        /// <summary>
        /// Pulls the editor buffers from the bracket snapshot once per selection. Only the fields
        /// that message carries; the rest keep their current values, hence the partial draft wire
        /// shape (nullable everywhere).
        /// </summary>
        private void EnsureTournamentLoaded()
        {
            if (tourLoadedFor == tourSelectedId) return;

            if (!tourBracketRequested)
            {
                networkHandler.RequestTournamentBracket(tourSelectedId);
                tourBracketRequested = true;
            }

            var b = networkHandler.TournamentBracket.Value;
            if (b == null || b.TournamentId != tourSelectedId) return;

            tourName = b.Name ?? "";
            tourRake = b.RakePercent;
            tourSides = b.PartiesPerMatch <= 2 ? 0 : b.PartiesPerMatch;
            tourAdvance = Math.Max(1, b.AdvancePerMatch);
            tourFee.Clear();
            tourFee.AddRange(ItemSetUtil.Parse(b.EntryFeePacked));
            tourSplit.Clear();
            tourSplit.AddRange(b.PotSplitPercents ?? new[] { 60, 30, 10 });
            tourPrizes.Clear();
            foreach (var packed in b.PlacementPrizesPacked ?? new string[0])
                tourPrizes.Add(ItemSetUtil.Parse(packed));

            // Scheduling comes back as a moment and goes out as "minutes from now", so a panel
            // left open does not push the opening later on save.
            tourOpenInMinutes = b.OpenAtUnix <= 0
                ? 0 : (int)Math.Max(0, (b.OpenAtUnix - UnixTime.Now + 59) / 60);
            tourSignupWindow = b.SignupWindowMinutes;
            tourRepeatMinutes = b.RepeatEveryMinutes;
            tourRepeatAt = b.RepeatAtPacked ?? "";
            tourShowBeforeSignup = b.ShowBeforeSignup;

            var list = networkHandler.TournamentList.Value;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list.Ids[i] != tourSelectedId) continue;
                    tourMaxEntrants = Math.Max(2, list.MaxEntrantsAt(i));
                    tourTeamSize = list.TeamSizeAt(i);
                    tourEventCodes.Clear();
                    foreach (var code in list.EventsAt(i).Split(';'))
                        if (!string.IsNullOrWhiteSpace(code)) tourEventCodes.Add(code);
                    break;
                }
            }

            tourLoadedFor = tourSelectedId;
        }

        private void DrawTournamentSetup()
        {
            int state = tourCreating ? (int)TournamentState.Draft : TourStateOf(tourSelectedId);
            bool editable = state <= (int)TournamentState.Seeded;

            if (!editable)
                ImGui.TextColored(ColWarn, "Running or finished - configuration is locked.");

            ImGui.BeginDisabled(!editable);

            ImGui.SetNextItemWidth(240);
            ImGui.InputText("Name", ref tourName, 64);

            DrawTourEventPicker();
            DrawTourClassPicker();
            DrawTourArenaPicker();

            ImGui.Separator();
            IntField("Team size", ref tourTeamSize, 1, 16);
            HelpMarker("1 = solo bracket. Above 1, players sign up as a party of exactly this size.");
            IntField("Max entrants", ref tourMaxEntrants, 2, 128);
            IntField("Ready check (s)", ref tourReadySeconds, 0, 600);
            HelpMarker("Seconds each side has to accept a called match. 0 = start without asking.");
            IntField("Parallel matches", ref tourConcurrent, 1, 16);

            ImGui.Checkbox("Start each match by hand", ref tourManualStart);
            HelpMarker("Nothing is called on its own: you press 'Call next match now' (Debug tab, or the "
                + "button below while it runs) when the stage is ready. For a paced, commentated event.");
            IntField("Signup deadline (min)", ref tourDeadlineMinutes, 0, 24 * 60);
            HelpMarker("Minutes from Save until signup closes and the bracket starts by itself. 0 = manual.");

            ImGui.Separator();
            Heading("Schedule");
            IntField("Open signup in (min)", ref tourOpenInMinutes, 0, 30 * 24 * 60);
            HelpMarker("Minutes from Save until signup opens by itself. The tournament sits in the list as a "
                + "draft until then, so players can see it coming. 0 = you open it by hand.");

            IntField("Signup stays open (min)", ref tourSignupWindow, 0, 7 * 24 * 60);
            HelpMarker("How long signup stays open once it opens. Unlike the deadline above this is a "
                + "duration, so a repeating tournament gets the same window every time. 0 = no window.");

            IntField("Repeat every (min)", ref tourRepeatMinutes, 0, 30 * 24 * 60);
            HelpMarker("After this one ends, a fresh copy is queued with the same settings. Measured from the "
                + "END, so a long bracket never overlaps its successor. 0 = one-off.");

            ImGui.Checkbox("Show to players before signup opens", ref tourShowBeforeSignup);
            HelpMarker("On: the scheduled tournament sits in the players' list as a draft with a countdown - "
                + "an announcement they can plan around. Off: only admins see it until signup opens, for a "
                + "half-configured draft or a surprise event.");

            ImGui.SetNextItemWidth(160);
            ImGui.InputText("Repeat daily at", ref tourRepeatAt, 64);
            HelpMarker("Server-local clock times, e.g. \"19:00;21:30\". Takes precedence over the interval "
                + "above - a fixed hour is something players can remember. Empty = no daily times.");
            if (!string.IsNullOrWhiteSpace(tourRepeatAt)
                && TournamentManager.ParseTimesOfDay(tourRepeatAt) == null)
                ImGui.TextColored(ColBad, "Times must look like 19:00, separated by ';'.");

            IntField("Auto-start at N entrants", ref tourAutoStart, 0, 128);
            HelpMarker("Starts the bracket by itself once this many have signed up. 0 = wait for an admin "
                + "or the deadline. Set it to the max for the usual 'start when full'.");
            if (tourAutoStart > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("= max")) tourAutoStart = tourMaxEntrants;
                IntField("Start delay (s)", ref tourAutoStartDelay, 0, 600);
                HelpMarker("Breathing room between the field filling up and the first ready check. "
                    + "Cancelled if somebody withdraws in the meantime.");
            }

            IntField("Sides per match", ref tourSides, 0, 32);
            HelpMarker("How many sides meet in one bracket match. 0 = take it from the event: 2 for a duel, "
                + "a whole heat for Hunger Games.");

            IntField("Advance per match", ref tourAdvance, 1, 16);
            HelpMarker("How many of them go through. 1 is a straight knockout; more turns each match into a "
                + "qualifying heat, which is what a big Hunger Games field needs. Requires a mode that ranks "
                + "its players - Hunger Games ranks by survival.");
            if (tourAdvance > 1 && tourSides > 0 && tourAdvance >= tourSides)
                ImGui.TextColored(ColBad, "Must be fewer than the sides, or nobody is ever knocked out.");

            ImGui.Checkbox("Third-place match", ref tourThirdPlace);
            if (tourSides > 2)
                ImGui.TextColored(ColMuted, "Third-place match only applies to two-sided brackets.");
            ImGui.Checkbox("Players bring their own gear", ref tourOwnGear);
            HelpMarker("No arena kit: everyone fights with what they walked in with - and REALLY owns it. "
                + "Dying drops it like anywhere else, and nothing is handed back at the end.\n\n"
                + "The kit's death protection cannot apply here: the snapshot is taken at the start, so "
                + "returning it would also copy anything the player dropped on purpose. Either the gear is "
                + "issued (protected, returned) or it is theirs (theirs to lose).");
            if (tourOwnGear) ImGui.TextColored(ColWarn, "Real stakes: gear lost in a match is gone.");

            ImGui.Checkbox("Lock class for the whole tournament", ref tourLockClass);
            HelpMarker("On: the class picked at signup is fixed once the bracket starts - nobody counter-picks "
                + "after seeing who they drew. Off: players may switch between rounds.");

            ImGui.Checkbox("Pause auto-scheduler while running", ref tourPauseScheduler);
            HelpMarker("Keeps the scheduler from taking the arenas or the players mid-bracket.");

            ImGui.SetNextItemWidth(130);
            ImGui.Combo("Seeding", ref tourSeedMode, TourSeedLabels, TourSeedLabels.Length);
            ImGui.SetNextItemWidth(130);
            ImGui.Combo("Rating", ref tourRatedMode, TourRatedLabels, TourRatedLabels.Length);

            ImGui.Separator();
            Heading("Entry fee");
            DrawItemListEditor("tourFee", tourFee);
            IntField("Rake %", ref tourRake, 0, 100);
            HelpMarker("Burned from the pot before it is split. 0 pays everything out.");

            Heading("Pot split by place");
            DrawTourSplitEditor();

            Heading("Item prizes by place");
            DrawTourPrizeEditor();

            ImGui.Separator();
            if (GoButton("Save")) SaveTournamentConfig();

            ImGui.EndDisabled();

            if (tourCreating) return;   // nothing to drive until it exists on the server

            ImGui.Separator();
            DrawTournamentControls(state);
        }

        private void DrawTourEventPicker()
        {
            var events = networkHandler.AdminEventList.Value?.Events;
            ImGui.Text("Events");
            HelpMarker("The bracket rotates over the ticked events by round. Untick everything but one for a single-mode tournament.");
            if (events == null || events.Length == 0)
            {
                ImGui.TextDisabled("(event list not loaded)");
                return;
            }

            if (!ImGui.BeginChild("##tourevents", new Vector2(0, 92), true)) { ImGui.EndChild(); return; }
            foreach (var ev in events)
            {
                bool ticked = tourEventCodes.Contains(ev.Code);
                if (ImGui.Checkbox(ev.Code + (ev.Enabled ? "" : " (switched off)"), ref ticked))
                {
                    if (ticked) tourEventCodes.Add(ev.Code); else tourEventCodes.Remove(ev.Code);
                }
            }
            ImGui.EndChild();

            // A switched-off event can never be called, so the bracket would wait forever. Warn
            // here rather than letting Begin fail after seeding.
            var offPicks = events.Where(e => !e.Enabled && tourEventCodes.Contains(e.Code))
                .Select(e => e.Code).ToList();
            if (offPicks.Count > 0)
                ImGui.TextColored(ColBad, "Switched off, turn them on in the Events tab: "
                    + string.Join(", ", offPicks));
        }

        /// <summary>
        /// Which classes are legal here. Empty means every class the mode has - the usual case -
        /// so the picker starts collapsed.
        /// </summary>
        private void DrawTourClassPicker()
        {
            // Class names come from the player-facing event list; the admin list does not carry them.
            var ev = networkHandler.EventList.Value?.Events?
                .FirstOrDefault(e => tourEventCodes.Contains(e.GameModeCode));
            var sets = ev?.LoadoutSets;

            if (sets == null || sets.Length <= 1)
            {
                if (tourClasses.Count > 0) tourClasses.Clear();
                return;   // one class or none: nothing to restrict
            }

            string summary = tourClasses.Count == 0
                ? "Allowed classes: all"
                : "Allowed classes: " + string.Join(", ", tourClasses);

            if (!ImGui.CollapsingHeader(summary + "###tourclasses")) return;

            if (ImGui.SmallButton("All")) tourClasses.Clear();
            ImGui.SameLine();
            ImGui.TextDisabled("Untick to ban a class for this tournament.");

            foreach (var set in sets)
            {
                bool allowed = tourClasses.Count == 0 || tourClasses.Contains(set.Name);
                if (!ImGui.Checkbox(set.Name + "##cls", ref allowed)) continue;

                if (tourClasses.Count == 0)
                {
                    // First untick turns "all" into an explicit list of everything else.
                    foreach (var s in sets) if (s.Name != set.Name) tourClasses.Add(s.Name);
                }
                else if (allowed) tourClasses.Add(set.Name);
                else tourClasses.Remove(set.Name);

                // Everything ticked again means "all", which is how it is stored.
                if (tourClasses.Count == sets.Length) tourClasses.Clear();
            }

            if (tourClasses.Count == 1)
                ImGui.TextColored(ColWarn, "Only one class left - everybody plays it.");
        }

        private void DrawTourArenaPicker()
        {
            ImGui.Text("Arenas");
            HelpMarker("Empty = any suitable arena (the normal random pick). Ticking some pins the tournament to them.");

            var places = networkHandler.PlaceList.Value?.Places;
            if (places == null || places.Length == 0)
            {
                ImGui.TextDisabled("(place list not loaded)");
                return;
            }

            // Tags the chosen events need, so an arena that cannot host them is flagged here
            // instead of stalling the bracket later.
            var needed = new List<string[]>();
            foreach (var ev in networkHandler.AdminEventList.Value?.Events ?? new AdminEventListEntryMsg[0])
                if (tourEventCodes.Contains(ev.Code)) needed.Add(ev.EventPlaceTypes ?? new string[0]);

            if (!ImGui.BeginChild("##tourplaces", new Vector2(0, 110), true)) { ImGui.EndChild(); return; }
            foreach (var p in places)
            {
                bool ticked = tourPlaces.Contains(p.Id);
                bool usable = needed.Count == 0 || needed.Any(tags => tags.All(t => (p.Types ?? new string[0]).Contains(t)));

                if (ImGui.Checkbox("#" + p.Id + " " + p.Name, ref ticked))
                {
                    if (ticked) tourPlaces.Add(p.Id); else tourPlaces.Remove(p.Id);
                }
                if (!usable)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColBad, "- not usable by the chosen events");
                }
            }
            ImGui.EndChild();
        }

        private void DrawTourSplitEditor()
        {
            int total = tourSplit.Sum();
            int removeAt = -1;
            for (int i = 0; i < tourSplit.Count; i++)
            {
                ImGui.PushID(i);
                // No +/- steppers (the 0): they eat most of an 80px field and clip the number.
                ImGui.SetNextItemWidth(70);
                int v = tourSplit[i];
                if (ImGui.InputInt(GuiItemUtil.Ordinal(i + 1) + " %", ref v, 0)) tourSplit[i] = Math.Max(0, v);
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) removeAt = i;
                ImGui.PopID();
            }
            if (removeAt >= 0) tourSplit.RemoveAt(removeAt);
            if (ImGui.SmallButton("+ place")) tourSplit.Add(0);

            ImGui.SameLine();
            if (total == 100) ImGui.TextColored(ColOk, "sums to 100%");
            else ImGui.TextColored(ColWarn, "sums to " + total + "% (will be normalized)");
        }

        private void DrawTourPrizeEditor()
        {
            for (int i = 0; i < tourPrizes.Count; i++)
            {
                ImGui.PushID(1000 + i);
                if (ImGui.CollapsingHeader(GuiItemUtil.Ordinal(i + 1) + " place prize"))
                    DrawItemListEditor("prize" + i, tourPrizes[i]);
                ImGui.PopID();
            }
            if (ImGui.SmallButton("+ place prize")) tourPrizes.Add(new List<RewardItem>());
            if (tourPrizes.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("- last")) tourPrizes.RemoveAt(tourPrizes.Count - 1);
            }
        }

        private void DrawTournamentControls(int state)
        {
            switch ((TournamentState)state)
            {
                case TournamentState.Draft:
                    if (GoButton("Open signup")) TourAction("opensignup");
                    break;
                case TournamentState.Signup:
                    if (ImGui.Button("Close signup")) TourAction("closesignup");
                    ImGui.SameLine();
                    if (GoButton("Begin now")) TourAction("begin");
                    break;
                case TournamentState.Seeded:
                    if (GoButton("Begin")) TourAction("begin");
                    break;
                case TournamentState.Running:
                    ImGui.TextColored(ColOk, "Running - see the Bracket tab.");
                    ImGui.SameLine();
                    if (GoButton("Call next match")) TourAction("forcecall");
                    break;
            }

            if (state <= (int)TournamentState.Running)
            {
                ImGui.SameLine();
                if (!tourCancelArmed)
                {
                    if (StopButton("Cancel tournament")) tourCancelArmed = true;
                }
                else
                {
                    ImGui.TextColored(ColWarn, "Refund the stakes?");
                    if (StopSmallButton("Cancel + refund")) { TourAction("cancel", boolArg: true); tourCancelArmed = false; }
                    ImGui.SameLine();
                    if (StopSmallButton("Cancel, keep stakes")) { TourAction("cancel", boolArg: false); tourCancelArmed = false; }
                    ImGui.SameLine();
                    // Delete leaves no history row, for ones created by mistake. Stakes are
                    // refunded first either way.
                    if (StopSmallButton("Delete outright"))
                    {
                        TourAction("delete");
                        tourCancelArmed = false;
                        tourSelectedId = 0;
                        tourLoadedFor = -1;
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Never mind")) tourCancelArmed = false;
                }
            }
        }

        // Bracket

        private void DrawTournamentBracket()
        {
            var msg = networkHandler.TournamentBracket.Value;
            if (msg == null || msg.TournamentId != tourSelectedId)
            {
                ImGui.TextDisabled("Loading bracket...");
                if (!tourBracketRequested)
                {
                    networkHandler.RequestTournamentBracket(tourSelectedId);
                    tourBracketRequested = true;
                }
                return;
            }

            var view = BracketView.FromMsg(msg);
            if (view.Matches.Count == 0)
            {
                ImGui.TextDisabled("Needs at least two entrants before a bracket can be shown.");
                if (view.Entrants.Count > 0)
                {
                    ImGui.Text("Entrants:");
                    foreach (var e in view.Entrants.Values) ImGui.BulletText(e.Name);
                }
                return;
            }

            if (ImGui.SmallButton("Refresh")) networkHandler.RequestTournamentBracket(tourSelectedId);
            ImGui.SameLine();
            if (view.IsPreview)
                ImGui.TextColored(ColWarn, "Preview - seeding at the start will move people around.");
            else
                ImGui.TextDisabled("Right-click a match to declare a winner.");

            if (!ImGui.BeginChild("##bracketcanvas", new Vector2(0, 440), true,
                ImGuiWindowFlags.HorizontalScrollbar)) { ImGui.EndChild(); return; }

            // Shrink the tree to the canvas rather than scrolling a picture; past the scale floor
            // the child's own scrollbars take over.
            var avail = ImGui.GetContentRegionAvail();
            var layout = BracketLayout.ComputeFitted(view, avail.X - 8, avail.Y - 8, 0.55, out _);

            var origin = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();

            // Reserves the scroll area; the drawing itself is absolute, in screen space.
            ImGui.Dummy(new Vector2(layout.Width + 8, layout.Height + 8));

            uint lineCol = ImGui.ColorConvertFloat4ToU32(ColMuted);
            foreach (var e in layout.Edges)
            {
                var p0 = new Vector2(origin.X + e.X0, origin.Y + e.Y0);
                var pm0 = new Vector2(origin.X + e.XMid, origin.Y + e.Y0);
                var pm1 = new Vector2(origin.X + e.XMid, origin.Y + e.Y1);
                var p1 = new Vector2(origin.X + e.X1, origin.Y + e.Y1);
                draw.AddLine(p0, pm0, lineCol, 2f);
                draw.AddLine(pm0, pm1, lineCol, 2f);
                draw.AddLine(pm1, p1, lineCol, 2f);
            }

            foreach (var node in layout.Nodes)
            {
                var m = view.Matches.FirstOrDefault(x => x.Id == node.MatchId);
                if (m == null) continue;
                DrawBracketNode(view, m, node, origin, draw);
            }

            ImGui.EndChild();

            if (view.IsPreview)
            {
                ImGui.Separator();
                ImGui.Text("Signed up (" + view.Entrants.Count + "): " + (view.Entrants.Count == 0
                    ? "nobody yet"
                    : string.Join(", ", view.Entrants.Values.OrderBy(e => e.Name).Select(e => e.Name))));
                ImGui.TextDisabled("Slots stay empty until the tournament begins - that is when seeding decides who plays whom.");
            }

            if (view.FinalPlacement.Count > 0)
            {
                ImGui.Separator();
                ImGui.Text("Final standings: " + string.Join(", ",
                    view.FinalPlacement.Select((id, i) => (i + 1) + ". " + view.NameOf(id))));
            }
        }

        private void DrawBracketNode(BracketView view, BracketView.Match m, BracketLayout.Node node,
            Vector2 origin, ImDrawListPtr draw)
        {
            var min = new Vector2(origin.X + node.X, origin.Y + node.Y);
            var max = new Vector2(min.X + node.W, min.Y + node.H);

            Vector4 accent = MatchStateColor(m);
            draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.13f, 0.16f, 0.95f)), 3f);
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(accent), 3f, ImDrawFlags.None, 1.5f);
            // Accent stripe: state readable at a glance without reading text.
            draw.AddRectFilled(min, new Vector2(min.X + 4, max.Y), ImGui.ColorConvertFloat4ToU32(accent), 3f);

            uint white = ImGui.ColorConvertFloat4ToU32(new Vector4(0.92f, 0.92f, 0.94f, 1f));
            uint dim = ImGui.ColorConvertFloat4ToU32(ColMuted);

            // One row per slot - a Hunger Games node lists all its tributes.
            int rows = Math.Max(2, m.Slots.Count);
            float rowH = node.H / rows;
            for (int i = 0; i < rows; i++)
            {
                string side = i < m.Slots.Count ? m.Slots[i] : null;
                draw.AddText(new Vector2(min.X + 9, min.Y + rowH * i + 2),
                    SideIsWinner(m, side) ? white : dim, SideText(view, m, side));
            }

            // Hit area for hover + the per-match menu.
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##m" + m.Id, new Vector2(node.W, node.H));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    BracketLayout.RoundLabel(view, m) + " (match #" + m.Id + ")\n"
                    + "State: " + MatchStateText(m)
                    + (string.IsNullOrEmpty(m.EventCode) ? "" : "\nMode: " + m.EventCode)
                    + (string.IsNullOrEmpty(m.ResultKind) ? "" : "\nResult: " + m.ResultKind));
            }

            // No result menu on a preview: those matches do not exist on the server yet.
            if (!view.IsPreview && ImGui.BeginPopupContextItem("##ctx" + m.Id))
            {
                ImGui.TextDisabled("Match #" + m.Id);
                foreach (var side in m.Filled)
                    if (ImGui.MenuItem("Winner: " + view.NameOf(side)))
                        TourAction("result", matchId: m.Id, stringArg: side);
                ImGui.Separator();
                foreach (var side in m.Filled)
                    if (ImGui.MenuItem("Disqualify " + view.NameOf(side)))
                        TourAction("dq", stringArg: side);
                ImGui.EndPopup();
            }
        }

        private static bool SideIsWinner(BracketView.Match m, string side)
            => !m.IsAbsent(side) && (string.IsNullOrEmpty(m.Winner) || m.Winner == side);

        private static string SideText(BracketView view, BracketView.Match m, string entrantId)
        {
            if (string.IsNullOrEmpty(entrantId)) return "- TBD -";
            int seed = view.SeedOf(entrantId);
            string name = view.NameOf(entrantId) ?? "?";
            if (name.Length > 18) name = name.Substring(0, 17) + "~";
            return (seed > 0 ? "[" + seed + "] " : "") + name + (m.IsAbsent(entrantId) ? " (no-show)" : "");
        }

        /// <summary>
        /// The shared state-to-colour table in ImGui's vector form. A second switch here is what
        /// once made the two bracket views disagree about a walkover.
        /// </summary>
        private static Vector4 MatchStateColor(BracketView.Match m)
        {
            var c = BracketPalette.ColorOf(m.State);
            return new Vector4((float)c[0], (float)c[1], (float)c[2], (float)(c.Length > 3 ? c[3] : 1));
        }

        private static string MatchStateText(BracketView.Match m)
        {
            switch ((BracketMatchState)m.State)
            {
                case BracketMatchState.Pending: return "waiting to be called";
                case BracketMatchState.AwaitingReady: return "ready check";
                case BracketMatchState.Starting: return "starting";
                case BracketMatchState.Live: return "live";
                case BracketMatchState.Done: return "finished";
                case BracketMatchState.Bye: return "bye";
                case BracketMatchState.Void: return "void";
                default: return "?";
            }
        }

        // Sending

        private int TourStateOf(long id)
        {
            var list = networkHandler.TournamentList.Value;
            if (list == null) return (int)TournamentState.Draft;
            for (int i = 0; i < list.Count; i++)
                if (list.Ids[i] == id) return list.StateAt(i);
            return (int)TournamentState.Draft;
        }

        private void TourAction(string action, int matchId = 0, string stringArg = null, bool boolArg = false)
        {
            networkHandler.SendTournamentAdminAction(new TournamentAdminActionMsg
            {
                Action = action,
                TournamentId = tourSelectedId,
                MatchId = matchId,
                StringArg = stringArg,
                BoolArg = boolArg
            });
            networkHandler.RequestTournamentList();
        }

        private void SaveTournamentConfig()
        {
            // Saving an editor that points at nothing means "make a new one", not "configure #0".
            if (!tourCreating && tourSelectedId <= 0) tourCreating = true;

            var draft = BuildTournamentDraft();
            networkHandler.SendTournamentAdminAction(new TournamentAdminActionMsg
            {
                Action = tourCreating ? "create" : "configure",
                TournamentId = tourSelectedId,
                ConfigJson = JsonConvert.SerializeObject(draft)
            });
            networkHandler.RequestTournamentList();
            tourCreating = false;
            tourLoadedFor = -1;
        }

        private TournamentDraft BuildTournamentDraft() => new TournamentDraft
        {
            Name = tourName,
            EventCodes = tourEventCodes.ToList(),
            PlaceWhitelist = tourPlaces.ToList(),
            TeamSize = tourTeamSize,
            MaxEntrants = tourMaxEntrants,
            EntryFee = tourFee.Select(i => new RewardItem { Code = i.Code, Quantity = i.Quantity }).ToList(),
            RakePercent = tourRake,
            PotSplitPercents = tourSplit.ToList(),
            PlacementPrizes = tourPrizes.Select(items => new RewardTier { Items = items.ToList() }).ToList(),
            ThirdPlaceMatch = tourThirdPlace,
            LockLoadout = tourLockClass,
            AllowedLoadouts = tourClasses.ToList(),
            BringOwnGear = tourOwnGear,
            ManualMatchStart = tourManualStart,
            PartiesPerMatch = tourSides,
            AdvancePerMatch = Math.Max(1, tourAdvance),
            AutoStartEntrants = Math.Max(0, tourAutoStart),
            AutoStartDelaySeconds = Math.Max(0, tourAutoStartDelay),
            ReadyCheckSeconds = tourReadySeconds,
            MaxConcurrentMatches = tourConcurrent,
            SignupDeadlineMinutes = tourDeadlineMinutes,
            SeedMode = tourSeedMode,
            RatedMode = tourRatedMode,
            PauseScheduler = tourPauseScheduler,
            OpenInMinutes = Math.Max(0, tourOpenInMinutes),
            SignupWindowMinutes = Math.Max(0, tourSignupWindow),
            RepeatEveryMinutes = Math.Max(0, tourRepeatMinutes),
            RepeatAtPacked = tourRepeatAt ?? "",
            ShowBeforeSignup = tourShowBeforeSignup
        };
    }
}
