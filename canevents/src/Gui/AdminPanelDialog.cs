using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSImGui;
using VSImGui.API;
using Newtonsoft.Json.Linq;

namespace canevents.src.Gui
{
    /// <summary>
    /// Server admin GUI, split across AdminPanelDialog.*.cs by tab. This file holds the shared
    /// state, the window frame and the tab bar.
    /// </summary>
    public partial class AdminPanelDialog
    {
        private ICoreClientAPI capi;
        private ClientNetworkHandler networkHandler;
        private ImGuiHost imgui;
        private bool isOpen = false;
        private int selectedTab = 0;

        /// <summary>Everything one item-set editor remembers. One instance per kind (loadouts,
        /// outfits), so switching sub-tabs does not drag the other editor's half-built set along.</summary>
        private sealed class ItemSetEditorState
        {
            public string EventCode = "";
            public string SetName = "";
            public string SelectedEvent = "";
            public string SelectedSet = "";
            public readonly System.Collections.Generic.List<(string code, int qty)> Pending
                = new System.Collections.Generic.List<(string, int)>();
        }

        private readonly ItemSetEditorState loadoutEditor = new ItemSetEditorState();
        private readonly ItemSetEditorState outfitEditor = new ItemSetEditorState();
        private ItemIconAtlas _atlas;

        // Held so Dispose can detach them.
        private Action<PlaceOperationResultMsg> _onPlaceOperationResult;
        private Action _onPlatformEditStateChanged;
        private Action _onLoadoutListReceived;
        private Action _onScheduleStateReceived;
        private Action _onPlaceListReceived;
        private Action _onEventRewardConfigReceived;
        private Action _onRewardLimitsReceived;

        // Single source of truth for place tags; client and server share the assembly.
        private static readonly string[] typeNames = PlaceTags.Known;

        /// <summary>
        /// Place-type checkboxes wrapped on actual available width: a fixed count per row pushed
        /// long names past the panel edge, where they rendered but could not be clicked.
        /// </summary>
        /// <param name="flags">One bool per entry in PlaceTags.Known; mutated in place.</param>
        /// <param name="idSuffix">ImGui id suffix, so two forms can show the list at once.</param>
        private static void DrawTypeCheckboxes(bool[] flags, string idSuffix = "")
        {
            float avail = ImGui.GetContentRegionAvail().X;
            float rowX = 0f;
            for (int i = 0; i < typeNames.Length; i++)
            {
                float itemW = ImGui.CalcTextSize(typeNames[i]).X + ImGui.GetFrameHeight()
                    + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.GetStyle().ItemSpacing.X;
                if (rowX > 0f && rowX + itemW > avail)
                {
                    rowX = 0f;
                }
                else if (i > 0)
                {
                    ImGui.SameLine();
                }
                ImGui.Checkbox(typeNames[i] + idSuffix, ref flags[i]);
                rowX += itemW;
            }
        }

        // Sub-tab selection in the grouping tabs, so each requests its data once.
        private int contentSubTab = -1;
        private int economySubTab = -1;

        // Dashboard countdown, ticked locally between scheduler packets.
        private float dashSecondsUntilNext = -1f;

        // How long the "Open" button keeps registration open, in minutes. 0 = until "Start now".
        private int signupMinutes = 5;

        // Open registration windows from the last scheduler packet, their countdowns ticked locally
        // the same way as the auto-scheduler's. A negative value means the window has no deadline.
        private readonly System.Collections.Generic.List<(string code, float left)> dashSignups
            = new System.Collections.Generic.List<(string, float)>();

        // New Arena wizard state
        private enum WizardStep { ChooseEvent, CreatePlace, Bounds, Spawns, Special, Activate, Done }
        private WizardStep wizStep = WizardStep.ChooseEvent;
        private string wizEventCode;
        private string[] wizTypes;
        private int wizRequiredSpawns = 1;
        private long wizPlaceId = -1;
        private string wizPlaceName = "";
        // Off by default: bounds silently replacing deliberately placed spawns is worse than none.
        private bool wizGenerateSpawns = false;
        private bool wizBoundsSkipped = false;

        // Per-type setup section state (Hunger Games keeps its own in AdminPanelDialog.HungerGames.cs).
        private int parkourNextIndex = 0;

        // InputFloat commits on Enter/defocus only, so a button clicked in the same frame fires
        // with the pre-edit value. These flags defer each send by one frame.
        private bool kothSetHillPending;
        private bool ctfSetPostPending;
        private bool spleefSetCagePending;
        private bool parkourApplyIndexPending;
        private bool parkourApplyLivesPending;

        private string statusMessage = "";
        private float statusTimer = 0f;

        // Rolling log: /canep commands answer in chat only, so the panel would otherwise
        // silently forget every action.
        private readonly List<(string time, bool ok, string text)> actionLog
            = new List<(string, bool, string)>();

        // Every panel-fired chat command goes through here so it lands in the log. Chat commands
        // send nothing back, so the place list is re-requested - delayed, since an immediate
        // request would race the command still travelling to the server.
        private void SendAdminChat(string command)
        {
            capi.SendChatMessage(command);
            LogAction(true, "> " + command);
            capi.Event.RegisterCallback(_ => networkHandler.RequestPlaceList(), 250);
        }

        private void LogAction(bool ok, string text)
        {
            actionLog.Add((System.DateTime.Now.ToString("HH:mm:ss"), ok, text));
            if (actionLog.Count > 20) actionLog.RemoveAt(0);
        }


        // Cage settings (+Cage on a place row)
        private int cageWidth = 5;
        private int cageDepth = 5;
        private int cageHeight = 4;
        private string cageBlock = "game:glass-plain";

        // Platform edit session state
        private long platformEditPlaceId = -1;
        private int platformFillRadius = 5;
        private string platformFillBlock = "game:stickslayer-wooden-ew-free";

        // Selected place
        private long selectedPlaceId = -1;
        private long pendingDeletePlaceId = -1;
        private bool deleteModalOpen = true;
        private bool deleteSetModalOpen = true;

        public AdminPanelDialog(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
        {
            this.capi = capi;
            this.networkHandler = networkHandler;
            imgui = new ImGuiHost(capi, Draw,
                onLevelReady: () => _atlas = SharedItemAtlas.Acquire(capi),
                // Stay in sync when the shared ImGui dialog is closed (Esc / toggle hotkey).
                onClosed: () => isOpen = false);

            capi.Input.RegisterHotKey("canevents_admin", "Events Admin Panel", GlKeys.F7, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("canevents_admin", new ActionConsumable<KeyCombination>(OnHotkey));

            // All handlers are detached in Dispose: the engine event API and VSImGui outlive this
            // dialog, and the sibling dialogs (browser, HUD) detach too.
            _onPlaceOperationResult = (msg) =>
            {
                statusMessage = (msg.Success ? "[OK] " : "[Error] ") + msg.Message;
                statusTimer = 4f;
                LogAction(msg.Success, msg.Message);
                // The wizard waits for the id of the place it just created.
                if (msg.Success && msg.PlaceId > 0 && wizPlaceId < 0 && wizStep == WizardStep.CreatePlace)
                    wizPlaceId = msg.PlaceId;
            };

            _onPlatformEditStateChanged = () =>
            {
                var s = networkHandler.PlatformEditState.Value;
                if (s != null)
                {
                    platformEditPlaceId = s.Active ? s.PlaceId : -1;
                }
            };

            _onLoadoutListReceived = () =>
            {
                if (string.IsNullOrEmpty(loadoutEditor.EventCode))
                {
                    var list = networkHandler.LoadoutList.Value;
                    if (list?.Events != null && list.Events.Length > 0)
                        loadoutEditor.EventCode = list.Events[0].EventCode;
                }
            };

            _onScheduleStateReceived = () =>
            {
                var state = networkHandler.ScheduleState.Value;
                dashSecondsUntilNext = state?.SecondsUntilNext ?? -1f;

                dashSignups.Clear();
                var codes = state?.OpenSignupCodes;
                if (codes == null) return;
                for (int i = 0; i < codes.Length; i++)
                {
                    if (string.IsNullOrEmpty(codes[i])) continue;
                    // Protobuf omits an all-default array, so the seconds can arrive short or missing;
                    // -1 is also the real value for a window with no deadline.
                    float left = state.OpenSignupSeconds != null && i < state.OpenSignupSeconds.Length
                        ? state.OpenSignupSeconds[i] : -1f;
                    dashSignups.Add((codes[i], left));
                }
            };

            // The highlight is keyed by place id, so editing the place already shown would not
            // repaint it. Fresh place data always means the drawing is stale.
            _onPlaceListReceived = () => highlightedPlaceId = -2;

            _onEventRewardConfigReceived = () => LoadRewardEditor(networkHandler.EventRewardConfig.Value);
            _onRewardLimitsReceived = () => LoadLimitsEditor(networkHandler.RewardLimits.Value);

            networkHandler.OnPlaceOperationResult      += _onPlaceOperationResult;
            networkHandler.PlatformEditState.Changed   += _onPlatformEditStateChanged;
            networkHandler.LoadoutList.Changed         += _onLoadoutListReceived;
            networkHandler.ScheduleState.Changed       += _onScheduleStateReceived;
            networkHandler.PlaceList.Changed           += OnPlaceListForWizard;
            networkHandler.PlaceList.Changed           += _onPlaceListReceived;
            networkHandler.EventRewardConfig.Changed   += _onEventRewardConfigReceived;
            networkHandler.RewardLimits.Changed        += _onRewardLimitsReceived;

        }

        // VS can fire a GUIOrOtherControls hotkey handler twice for one key press.
        private const long ToggleDebounceMs = 200;
        // VSImGui only feeds ImGui input while the cursor is free, so an Escape pressed while the
        // panel is closed sits unread and would close the panel on its first frame.
        private const long EscapeGuardMs = 250;

        private long lastToggleMs;
        private long openedAtMs;

        private bool OnHotkey(KeyCombination comb)
        {
            long now = capi.World.ElapsedMilliseconds;
            if (now - lastToggleMs < ToggleDebounceMs) return true;
            lastToggleMs = now;

            if (isOpen)
            {
                // The outline deliberately stays up; the status bar toggle drops it.
                isOpen = false;
            }
            else
            {
                isOpen = true;
                openedAtMs = now;
                imgui.Show();
                RequestDataFor(selectedTab);
            }
            return true;
        }

        private CallbackGUIStatus Draw(float deltaSeconds)
        {
            // The HG section re-arms this every frame it is drawn; anything else clears the marker.
            hgPlacementMarkerWanted = false;

            if (!isOpen) { UpdateHgPlacementMarker(); return CallbackGUIStatus.Closed; }

            // See EscapeGuardMs: a stale Escape arrives on the first frame after opening.
            if (capi.World.ElapsedMilliseconds - openedAtMs > EscapeGuardMs && ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                isOpen = false;
                UpdateHgPlacementMarker();
                return CallbackGUIStatus.Closed;
            }

            if (dashSecondsUntilNext > 0) dashSecondsUntilNext -= deltaSeconds;
            for (int i = dashSignups.Count - 1; i >= 0; i--)
            {
                var w = dashSignups[i];
                if (w.left < 0f) continue;             // no deadline: only the admin ends it
                float left = w.left - deltaSeconds;
                // At zero the server has already started the match; dropping the row here keeps the
                // panel from offering a Cancel that would answer "no window is open".
                if (left <= 0f) dashSignups.RemoveAt(i);
                else dashSignups[i] = (w.code, left);
            }

            var (styleVars, styleColors) = PushTheme();
            ImGui.SetNextWindowSize(new Vector2(860, 620), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(620, 420), new Vector2(float.MaxValue, float.MaxValue));

            ImGui.Begin("C&N Events - Admin", ref isOpen);

            if (ImGui.BeginTabBar("##AdminTabs", ImGuiTabBarFlags.None))
            {
                if (BeginTab("Dashboard", 0)) { DrawDashboardTab(); ImGui.EndTabItem(); }
                if (BeginTab("Events", 1)) { DrawEventsTab(); ImGui.EndTabItem(); }
                if (BeginTab("Arenas", 2)) { DrawArenasTab(); ImGui.EndTabItem(); }
                if (BeginTab("New Arena", 3)) { DrawWizardTab(); ImGui.EndTabItem(); }
                if (BeginTab("Content", 4)) { DrawContentTab(); ImGui.EndTabItem(); }
                if (BeginTab("Economy", 5)) { DrawEconomyTab(); ImGui.EndTabItem(); }
                if (BeginTab("Tournaments", 7)) { DrawTournamentTab(); ImGui.EndTabItem(); }
                if (BeginTab("Players", 9)) { DrawPlayersTab(); ImGui.EndTabItem(); }
                if (BeginTab("Debug", 8)) { DrawDebugTab(); ImGui.EndTabItem(); }
                if (BeginTab("Help", 6)) { DrawHelpTab(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }

            // Persistent status bar, visible from any tab
            ImGui.Separator();

            // The outline survives closing the panel, so the way to drop it must stay reachable
            // from every tab.
            bool anythingDrawn = highlightedPlaceId >= 0 || hasMark1 || hasMark2;
            if (anythingDrawn || !highlightEnabled)
            {
                if (highlightEnabled)
                {
                    if (ImGui.SmallButton("Hide outline"))
                    {
                        hasMark1 = false; hasMark2 = false;
                        UpdateMarkPreview();
                        SetHighlightEnabled(false);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Clears the bounds, spawn and mark highlights from the world.\nThe place itself is not changed.");
                }
                else
                {
                    if (ImGui.SmallButton("Show outline")) SetHighlightEnabled(true);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Draws the selected place's bounds and spawn points again.");
                }
                ImGui.SameLine();
            }

            if (statusTimer > 0)
            {
                ImGui.TextColored(statusMessage.StartsWith("[OK]") ? ColOk : ColBad, statusMessage);
                statusTimer -= ImGui.GetIO().DeltaTime;
            }
            else if (selectedPlaceId >= 0)
            {
                ImGui.TextDisabled("Selected place: #" + selectedPlaceId);
            }
            else
            {
                ImGui.TextDisabled("F7 closes this panel. Hover anything with (?) or a highlight for details.");
            }

            ImGui.End();
            PopTheme((styleVars, styleColors));

            UpdateHgPlacementMarker();

            return isOpen ? CallbackGUIStatus.GrabMouse : CallbackGUIStatus.Closed;
        }

        // Fires the tab's data request once, on the frame it becomes active, so lists are
        // fresh on arrival instead of waiting for a manual Refresh.
        private bool BeginTab(string label, int tabId)
        {
            if (!ImGui.BeginTabItem(label)) return false;
            if (selectedTab != tabId)
            {
                selectedTab = tabId;
                RequestDataFor(tabId);
            }
            return true;
        }

        private void RequestDataFor(int tabId)
        {
            switch (tabId)
            {
                case 0: networkHandler.RequestScheduleState(); networkHandler.RequestAdminEventList(); break;
                // The events table needs the scheduler state too: it is where the open registration
                // windows come from, and their rows show Cancel instead of Open.
                case 1:
                    networkHandler.RequestAdminEventList();
                    networkHandler.RequestPlaceList();
                    networkHandler.RequestScheduleState();
                    break;
                case 2: networkHandler.RequestPlaceList(); break;
                case 3: networkHandler.RequestAdminEventList(); networkHandler.RequestPlaceList(); break;
                case 4: networkHandler.RequestLoadoutList(); break;
                case 5: networkHandler.RequestAdminEventList(); break;
                case 7:
                    networkHandler.RequestTournamentList();
                    networkHandler.RequestAdminEventList();
                    networkHandler.RequestPlaceList();
                    // The player-facing list is the one that carries each mode's class names.
                    networkHandler.RequestEventList();
                    break;
                case 8:
                    networkHandler.RequestAdminEventList();
                    networkHandler.RequestTournamentList();
                    networkHandler.RequestScheduleState();
                    break;
                case 9: networkHandler.RequestPlayerAccess(); break;
            }
        }

        // Dashboard

        private void DrawDashboardTab()
        {
            var s = networkHandler.ScheduleState.Value;

            ImGui.TextColored(ColAccent, "Auto-scheduler");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##dash")) networkHandler.RequestScheduleState();

            if (s == null)
            {
                ImGui.TextDisabled("Waiting for scheduler state...");
            }
            else
            {
                if (s.Enabled)
                {
                    ImGui.TextColored(ColOk, "ON");
                    ImGui.SameLine();
                    ImGui.Text(string.Format("every {0} min, next attempt in {1}",
                        s.IntervalMinutes, FormatDuration(dashSecondsUntilNext)));
                    ImGui.Text("Next in rotation: " + (string.IsNullOrEmpty(s.NextEventCode) ? "(nothing runnable)" : s.NextEventCode));
                }
                else
                {
                    ImGui.TextColored(ColMuted, "OFF");
                    ImGui.SameLine();
                    ImGui.TextDisabled("- events only start when you announce them by hand.");
                }

                // The scheduler skips a turn unless both hold; showing them explains an
                // "enabled but nothing happens" scheduler without reading logs.
                bool enoughPlayers = s.OnlinePlayers >= s.MinPlayersToStart;
                bool roomToRun = s.RunningEvents < s.MaxConcurrentEvents;
                ImGui.TextColored(enoughPlayers ? new Vector4(0.7f, 0.7f, 0.7f, 1f) : new Vector4(1f, 0.6f, 0.2f, 1f),
                    string.Format("Players online: {0} (needs {1})", s.OnlinePlayers, s.MinPlayersToStart));
                ImGui.SameLine();
                ImGui.TextColored(roomToRun ? new Vector4(0.7f, 0.7f, 0.7f, 1f) : new Vector4(1f, 0.6f, 0.2f, 1f),
                    string.Format("| Running: {0} / {1}", s.RunningEvents, s.MaxConcurrentEvents));

                ImGui.Spacing();
                if (s.Enabled)
                {
                    if (ImGui.Button("Turn OFF")) networkHandler.SendAdminAction(AdminAction.ScheduleOff);
                }
                else
                {
                    if (GoButton("Turn ON")) networkHandler.SendAdminAction(AdminAction.ScheduleOn);
                }
                ImGui.SameLine();
                ImGui.TextDisabled("Interval:");
                int[] intervals = { 5, 10, 15, 30, 60 };
                foreach (int m in intervals)
                {
                    ImGui.SameLine();
                    bool current = s.IntervalMinutes == m;
                    if (current) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.52f, 0.42f, 0.24f, 1f));
                    if (ImGui.SmallButton(m + "m"))
                        networkHandler.SendAdminAction(AdminAction.ScheduleInterval, null, m);
                    if (current) ImGui.PopStyleColor();
                }

                if (s.RotationCodes != null && s.RotationCodes.Length > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled("Rotation: " + string.Join(" > ", s.RotationCodes));
                }

                DrawOpenSignupWindows();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColAccent, "Running matches");
            var running = networkHandler.EventList.Value?.Events?.Where(e => e.EventId != 0).ToArray();
            if (running == null || running.Length == 0)
            {
                ImGui.TextDisabled("Nothing running right now.");
            }
            else
            {
                foreach (var re in running)
                {
                    ImGui.BulletText(string.Format("{0} on {1} ({2} players)",
                        re.GameModeCode, string.IsNullOrEmpty(re.ArenaName) ? "no arena yet" : re.ArenaName, re.CurrentPlayers));
                }
            }
            if (StopButton("Stop all matches")) networkHandler.SendAdminAction(AdminAction.StopAll);

            ImGui.Spacing();
            ImGui.Separator();
            DrawActionLog();
        }

        private void DrawActionLog()
        {
            ImGui.TextColored(ColAccent, "Recent actions");
            if (actionLog.Count == 0)
            {
                ImGui.TextDisabled("Nothing yet. Actions you take here are recorded, including chat commands.");
                return;
            }
            int shown = 0;
            for (int i = actionLog.Count - 1; i >= 0 && shown < 8; i--, shown++)
            {
                var entry = actionLog[i];
                ImGui.TextDisabled(entry.time);
                ImGui.SameLine();
                ImGui.TextColored(entry.ok ? new Vector4(0.6f, 0.9f, 0.6f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                    entry.text ?? "");
            }
        }

        /// <summary>Registration windows currently open, with the two ways out of one: start it now,
        /// or close it without starting anything.</summary>
        private void DrawOpenSignupWindows()
        {
            if (dashSignups.Count == 0) return;

            ImGui.Spacing();
            ImGui.TextColored(ColAccent, "Registration open");
            foreach (var (code, left) in dashSignups)
            {
                ImGui.PushID("signup_" + code);
                // TimeText, not FormatDuration: this is the same countdown players read on the HUD
                // and in chat, and it should not be spelled "5m 00s" here and "5:00" there.
                ImGui.Text(left < 0f
                    ? code + " - until you start it"
                    : code + " - " + TimeText.Short(left) + " left");
                ImGui.SameLine();
                StartNowButton(code);
                ImGui.SameLine();
                CancelSignupButton(code);
                ImGui.PopID();
            }
        }

        // The two ways out of an open window, offered both on the dashboard and in the events table.

        /// <summary>Starts a match now from whoever is queued. "Announce" is its name on the wire only.</summary>
        private void StartNowButton(string code)
        {
            if (!GoSmallButton("Start now")) return;
            networkHandler.SendAdminAction(AdminAction.AnnounceEvent, code);
            networkHandler.RequestScheduleState();
            LogAction(true, "> start now: " + code);
        }

        private void CancelSignupButton(string code)
        {
            if (!StopSmallButton("Cancel")) return;
            networkHandler.SendAdminAction(AdminAction.CancelSignup, code);
            networkHandler.RequestScheduleState();
            LogAction(true, "> cancel signup: " + code);
        }

        private static string FormatDuration(float seconds) => GuiItemUtil.FormatShortDuration(seconds);

        // Events

        private void DrawEventsTab()
        {
            ImGui.TextColored(ColAccent, "Events");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##adminev")) networkHandler.RequestAdminEventList();
            ImGui.TextDisabled("Ready tells you whether a match could be placed right now. Disabled events are hidden from the J browser.");
            ImGui.Spacing();

            IntField("Signup window (min)", ref signupMinutes, 0, 1440);
            HelpMarker("How long \"Open\" keeps registration open. Everyone online gets a popup with a "
                + "Join button and the countdown, and the match starts when the time is up - or earlier "
                + "if the lobby fills.\n\n"
                + "0 keeps it open until you press \"Start now\" yourself.");
            ImGui.Spacing();

            var adminList = networkHandler.AdminEventList.Value;
            if (adminList?.Events == null || adminList.Events.Length == 0)
            {
                ImGui.TextColored(ColMuted, "No events loaded.");
                return;
            }

            float tableHeight = System.Math.Min(adminList.Events.Length * 26f + 26f, 320f);
            if (ImGui.BeginTable("##AdminEventsTable", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                new Vector2(ImGui.GetContentRegionAvail().X, tableHeight)))
            {
                ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Players", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Ready", ImGuiTableColumnFlags.WidthFixed, 170f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 230f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var e in adminList.Events)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (e.Enabled) ImGui.Text(e.Code);
                    else ImGui.TextColored(ColMuted, e.Code);
                    if (ImGui.IsItemHovered() && e.EventPlaceTypes != null && e.EventPlaceTypes.Length > 0)
                    {
                        ImGui.SetTooltip("Needs a place tagged: " + string.Join(" + ", e.EventPlaceTypes)
                            + "\nSpawn points wanted: " + e.RequiredSpawns
                            + (e.Rated ? "\nRated: results affect Elo." : ""));
                    }

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextColored(ColMuted, e.Category ?? "");

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextColored(ColMuted,
                        e.MaxPlayers > 0 ? e.MinPlayers + "-" + e.MaxPlayers : "-");

                    ImGui.TableSetColumnIndex(3);
                    // While registration is open, the time left is what the admin is watching for;
                    // readiness was already checked when the window opened.
                    int openIdx = dashSignups.FindIndex(w => w.code == e.Code);
                    if (openIdx >= 0)
                    {
                        float left = dashSignups[openIdx].left;
                        ImGui.TextColored(ColAccent, left < 0f
                            ? "Signup open - manual start"
                            : "Signup - starts in " + TimeText.Short(left));
                    }
                    else DrawReadinessCell(e);

                    ImGui.TableSetColumnIndex(4);
                    ImGui.PushID("row_" + e.Code);
                    bool canRun = e.Enabled && e.Readiness == EventReadiness.Ready;
                    bool signupOpen = dashSignups.Any(w => w.code == e.Code);

                    // The same button position is the way in and the way out, so a window opened by
                    // mistake is closed where it was opened rather than on another tab.
                    if (signupOpen)
                    {
                        CancelSignupButton(e.Code);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Closes the open registration window without starting anything.");
                    }
                    else
                    {
                        if (!canRun) ImGui.BeginDisabled();
                        if (GoSmallButton("Open"))
                        {
                            networkHandler.SendAdminAction(AdminAction.OpenSignup, e.Code, signupMinutes);
                            networkHandler.RequestScheduleState();
                            LogAction(true, "> open signup: " + e.Code + " (" + signupMinutes + "m)");
                        }
                        if (!canRun) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Opens registration for the window set above.\nEveryone online gets a popup with a Join button; the match starts when the time is up.");
                    }
                    ImGui.SameLine();

                    if (!canRun) ImGui.BeginDisabled();
                    StartNowButton(e.Code);
                    if (!canRun) ImGui.EndDisabled();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Starts a match right now from whoever is already queued.\nFails if the queue is empty.");
                    ImGui.SameLine();
                    if (e.Enabled)
                    {
                        if (StopSmallButton("Disable")) networkHandler.SendToggleEventEnabled(e.Code);
                    }
                    else
                    {
                        if (GoButton("Enable")) networkHandler.SendToggleEventEnabled(e.Code);
                    }
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        private void DrawReadinessCell(AdminEventListEntryMsg e)
        {
            var green = new Vector4(0.3f, 1f, 0.3f, 1f);
            var amber = new Vector4(1f, 0.75f, 0.2f, 1f);
            var red = new Vector4(1f, 0.4f, 0.4f, 1f);
            var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);

            string label;
            Vector4 color;
            string help;

            switch (e.Readiness)
            {
                case EventReadiness.Ready:
                    color = green; label = "ready"; help = "Will run on: " + e.ReadinessDetail;
                    break;
                case EventReadiness.AllBusy:
                    color = amber; label = "arena busy"; help = e.ReadinessDetail + ". It will be free once those matches end.";
                    break;
                case EventReadiness.NoSpawns:
                    color = red; label = "no spawns"; help = e.ReadinessDetail + ". Select it in Arenas and add spawn points.";
                    break;
                case EventReadiness.AllDisabled:
                    color = red; label = "arena off"; help = e.ReadinessDetail + ". Enable one in Arenas.";
                    break;
                case EventReadiness.EventDisabled:
                    color = grey; label = "event off"; help = "This event is disabled. Enable it to let players queue.";
                    break;
                default:
                    color = red; label = "no arena";
                    help = "No place carries the tags " + (e.ReadinessDetail ?? "?")
                         + ".\nBuild one in the New Arena tab.";
                    break;
            }

            ImGui.TextColored(color, label);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(help);
        }

        private static string FormatBoundsSize(PlaceListEntryMsg place)
        {
            if (!place.HasBounds) return "-";
            return string.Format("{0}x{1}x{2}",
                place.BoundsMaxX - place.BoundsMinX + 1,
                place.BoundsMaxY - place.BoundsMinY + 1,
                place.BoundsMaxZ - place.BoundsMinZ + 1);
        }

        /// <summary>
        /// One-glance "is this place finished?": a letter per requirement, red when a type
        /// needs it but it is missing.
        /// </summary>
        private void DrawSetupBadges(PlaceListEntryMsg place)
        {
            var ok = new Vector4(0.3f, 1f, 0.3f, 1f);
            var missing = new Vector4(1f, 0.4f, 0.4f, 1f);
            bool first = true;

            void Badge(string letter, bool satisfied, string tip)
            {
                if (!first) ImGui.SameLine(0, 3);
                first = false;
                ImGui.TextColored(satisfied ? ok : missing, letter);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
            }

            Badge("S", place.SpawnCount > 0, place.SpawnCount + " spawn point(s)");

            bool needsBounds = place.Types != null && place.Types.Any(t => BoundsRequiringTypes.Contains(t));
            if (needsBounds) Badge("B", place.HasBounds, place.HasBounds ? "Bounds set" : "No bounds: /canep commands cannot target this place");
            if (HasType(place, "KOTH")) Badge("H", !string.IsNullOrEmpty(place.KothHill), string.IsNullOrEmpty(place.KothHill) ? "No hill set" : place.KothHill);
            if (HasType(place, "PARKOUR")) Badge("C", HasSummary(place, "checkpoints"), "Parkour checkpoints");
            if (HasType(place, "HUNGERGAMES")) Badge("Z", HasSummary(place, "zone"), "Hunger Games zone");
            if (HasType(place, "SPLEEF")) Badge("P", HasSummary(place, "platform"), "Spleef platform blocks");
        }

        private string BuildPlaceTooltip(PlaceListEntryMsg place)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(place.Name).Append(" (#").Append(place.Id).Append(')');
            if (!string.IsNullOrWhiteSpace(place.Description)) sb.Append('\n').Append(place.Description);

            sb.Append('\n').Append(place.HasBounds
                ? string.Format("Bounds {0},{1},{2} .. {3},{4},{5}  ({6})",
                    place.BoundsMinX, place.BoundsMinY, place.BoundsMinZ,
                    place.BoundsMaxX, place.BoundsMaxY, place.BoundsMaxZ, FormatBoundsSize(place))
                : "No bounds");

            sb.Append('\n').Append(string.IsNullOrEmpty(place.SchematicTemplateCode)
                ? "Built in world"
                : "From template '" + place.SchematicTemplateCode + "'" + (place.SchematicOnDemand ? " (on demand)" : ""));

            if (place.BehaviorSummary != null && place.BehaviorSummary.Length > 0)
            {
                foreach (var line in place.BehaviorSummary) sb.Append('\n').Append(line);
            }

            // Which events could land here: the reverse of the Events tab's readiness.
            var serves = networkHandler.AdminEventList.Value?.Events?
                .Where(e => e.EventPlaceTypes != null && e.EventPlaceTypes.Length > 0
                            && e.EventPlaceTypes.All(t => place.Types != null && System.Array.IndexOf(place.Types, t) >= 0))
                .Select(e => e.Code)
                .ToArray();
            if (serves != null && serves.Length > 0)
                sb.Append("\nServes: ").Append(string.Join(", ", serves));

            return sb.ToString();
        }

        /// <summary>
        /// Set or replace a place's bounds after the fact; without bounds every /canep command
        /// silently ignores the place.
        /// </summary>
        private void DrawBoundsEditor(PlaceListEntryMsg place)
        {
            ImGui.Spacing();

            // Size in the header: a collapsed section saying only "Bounds" reads as having none.
            // The id is per-place - ImGui keys collapsed state by id, so a shared one let one
            // arena's open/closed state follow onto every other arena.
            // ASCII only: the ImGui font has no em dash glyph, it renders as "?".
            string header = place.HasBounds
                ? string.Format("Bounds: {0}##bnd{1}", FormatBoundsSize(place), place.Id)
                : string.Format("Bounds: NOT SET##bnd{0}", place.Id);

            if (!ImGui.CollapsingHeader(header, place.HasBounds ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Collapsed: still show the corners, so the data is never hidden behind a click.
                if (place.HasBounds)
                {
                    ImGui.TextDisabled(string.Format("   {0},{1},{2} .. {3},{4},{5}",
                        place.BoundsMinX, place.BoundsMinY, place.BoundsMinZ,
                        place.BoundsMaxX, place.BoundsMaxY, place.BoundsMaxZ));
                }
                return;
            }

            ImGui.TextDisabled("Bounds are the arena's footprint. They are not a barrier; players can walk out.");
            ImGui.TextDisabled("What they do: let /canep commands find this place by where you stand, give");
            ImGui.TextDisabled("Spleef its platform size, and provide the fallback centre for KOTH / Hunger Games.");
            ImGui.Spacing();
            ImGui.TextDisabled("Look at opposite floor corners and mark them.");
            DrawMarkButtons();

            DrawMarkedAreaSize();

            ImGui.Checkbox("Replace spawn points with 2 on opposite sides##bndsp", ref wizGenerateSpawns);

            DrawApplyBoundsButton(place, place.HasBounds ? "Replace bounds" : "Set bounds");

            if (place.HasBounds)
            {
                ImGui.SameLine();
                if (StopSmallButton("Clear bounds"))
                {
                    networkHandler.SendClearPlaceBounds(place.Id);
                    LogAction(true, "> clear bounds on '" + place.Name + "'");
                    highlightedPlaceId = -2;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Removes the footprint from the place itself.\n/canep commands will no longer be able to target it.");
            }
        }


        // Shown by every /canep-driven section: those commands act on the block under your feet,
        // so standing in the right place is a precondition.
        private bool DrawStandingCheck(PlaceListEntryMsg place, string whatWillBeOrphan)
        {
            if (!place.HasBounds)
            {
                ImGui.TextColored(ColBad,
                    "This place has no bounds, so /canep commands cannot find it. Set them in the Bounds section.");
                return false;
            }
            bool inside = PlayerInsideBounds(place);
            if (inside)
                ImGui.TextColored(ColOk, "You are inside this place's bounds.");
            else
                ImGui.TextColored(ColBad,
                    "You are NOT inside this place's bounds: " + whatWillBeOrphan);
            return inside;
        }

        private void DrawParkourSection(PlaceListEntryMsg place)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColAccent, "Parkour");

            if (ImGui.CollapsingHeader("How parkour setup works##pkhelp"))
            {
                ImGui.BulletText("Stand on the surface block where the checkpoint should be.");
                ImGui.BulletText("Mark CP N / Mark Finish: no blocks are placed, only positions saved.");
                ImGui.BulletText("During an event an invisible trigger is spawned on each saved block.");
            }

            DrawStandingCheck(place, "the checkpoint would be written to another place or rejected.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Next index", ref parkourNextIndex, 1);
            if (parkourNextIndex < 0) parkourNextIndex = 0;
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##pk")) parkourNextIndex = 0;

            ImGui.Spacing();
            if (ImGui.Button(string.Format("Mark CP {0} (block under my feet)", parkourNextIndex)))
            {
                SendAdminChat("/canep cp " + parkourNextIndex);
                parkourNextIndex++;
            }
            ImGui.SameLine();
            if (GoButton("Mark Finish")) SendAdminChat("/canep cpfinish");

            ImGui.Spacing();
            if (ImGui.Button("List checkpoints")) SendAdminChat("/canep cplist");
            ImGui.SameLine();
            if (StopButton("Remove CP under my feet")) SendAdminChat("/canep cpremove");

            ImGui.Spacing();
            ImGui.TextDisabled("Re-index: stand on a marked checkpoint, set the index, Apply.");
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Index##cpedit", ref parkourEditIndex, 1);
            if (parkourEditIndex < 0) parkourEditIndex = 0;
            ImGui.SameLine();
            // Deferred a frame so an index still being typed has committed before sending.
            if (parkourApplyIndexPending)
            {
                parkourApplyIndexPending = false;
                SendAdminChat("/canep cpedit " + parkourEditIndex);
            }
            if (ImGui.Button("Apply index")) parkourApplyIndexPending = true;

            ImGui.Spacing();
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Lives##cplives", ref parkourLivesValue, 1);
            if (parkourLivesValue < 1) parkourLivesValue = 1;
            ImGui.SameLine();
            if (parkourApplyLivesPending)
            {
                parkourApplyLivesPending = false;
                SendAdminChat("/canep cplives " + parkourLivesValue);
            }
            if (ImGui.Button("Apply lives")) parkourApplyLivesPending = true;
        }

        private float kothRadius = 5f;
        private float kothHeight = 5f;

        private void DrawKothSection(PlaceListEntryMsg place)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColAccent, "King of the Hill");

            if (ImGui.CollapsingHeader("How KOTH scoring works##kothhelp"))
            {
                ImGui.BulletText("The zone is a cylinder: radius in XZ, from 1 block below the center up to its height.");
                ImGui.BulletText("The team that outnumbers everyone else inside scores; equal numbers score nothing.");
                ImGui.BulletText("Add one spawn point per team (2-4 teams supported), not one per player:");
                ImGui.BulletText("  with as many spawns as players they are handed out per player and teams get split.");
            }

            if (string.IsNullOrEmpty(place.KothHill))
            {
                ImGui.TextColored(ColBad,
                    "NO HILL SET: the match would fall back to the bounds center, which may float off the floor.");
            }
            else
            {
                var f = place.KothHill.Split(',');
                ImGui.TextColored(ColOk,
                    f.Length >= 5
                        ? string.Format("Hill @ {0},{1},{2}  r={3}  h={4}", f[0], f[1], f[2], f[3], f[4])
                        : "Hill set");
            }

            DrawStandingCheck(place, "the hill would be written to another place or rejected.");

            ImGui.Spacing();
            ImGui.SetNextItemWidth(120);
            ImGui.InputFloat("Radius##koth", ref kothRadius, 1f, 5f, "%.1f");
            if (kothRadius <= 0f) kothRadius = 1f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputFloat("Height##koth", ref kothHeight, 1f, 5f, "%.1f");
            if (kothHeight <= 0f) kothHeight = 1f;

            // Deferred one frame from the click so a value still being typed has committed.
            if (kothSetHillPending)
            {
                kothSetHillPending = false;
                // Invariant culture: a comma decimal separator fails the Float parser.
                SendAdminChat("/canep kothhill "
                    + kothRadius.ToString(CultureInfo.InvariantCulture) + " "
                    + kothHeight.ToString(CultureInfo.InvariantCulture));
            }
            if (GoButton("Set hill at the block under my feet")) kothSetHillPending = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stand on top of the hill first: the block under your feet becomes the center,\nand the zone reaches from 1 below it up to the height you set.");
        }

        private int parkourEditIndex = 0;
        private int parkourLivesValue = 3;

        private void DrawItemIcon(string code, float size = 28f)
            => GuiItemUtil.DrawIcon(_atlas, capi, code, size);


        // Grouping tabs: Loadouts/Templates/Schematics and Rewards/Limits are each one topic,
        // nested so the top bar stays at 7 entries.

        private void DrawContentTab()
        {
            if (ImGui.BeginTabBar("##ContentTabs"))
            {
                if (ImGui.BeginTabItem("Loadouts"))
                {
                    if (contentSubTab != 0)
                    {
                        contentSubTab = 0;
                        networkHandler.RequestLoadoutList(EventManagers.ItemSetKinds.Loadout);
                    }
                    DrawLoadoutsTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Outfits"))
                {
                    if (contentSubTab != 1)
                    {
                        contentSubTab = 1;
                        networkHandler.RequestLoadoutList(EventManagers.ItemSetKinds.Outfit);
                    }
                    DrawOutfitsTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Templates"))
                {
                    if (contentSubTab != 2) { contentSubTab = 2; networkHandler.RequestTemplateList(); }
                    DrawTemplatesTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Schematics"))
                {
                    if (contentSubTab != 3) { contentSubTab = 3; networkHandler.RequestSchematicList(); }
                    DrawSchematicsTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }

        private void DrawEconomyTab()
        {
            if (ImGui.BeginTabBar("##EconomyTabs"))
            {
                if (ImGui.BeginTabItem("Rewards"))
                {
                    if (economySubTab != 0)
                    {
                        economySubTab = 0;
                        networkHandler.RequestAdminEventList();
                        // The tab shows what each "inherit" resolves to, so the global limits
                        // are needed too.
                        networkHandler.SendRequestRewardLimits();
                    }
                    DrawRewardsTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Limits"))
                {
                    if (economySubTab != 1) { economySubTab = 1; networkHandler.SendRequestRewardLimits(); }
                    DrawLimitsTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }

        public void Dispose()
        {
            imgui.Dispose();

            networkHandler.OnPlaceOperationResult      -= _onPlaceOperationResult;
            networkHandler.PlatformEditState.Changed   -= _onPlatformEditStateChanged;
            networkHandler.LoadoutList.Changed         -= _onLoadoutListReceived;
            networkHandler.ScheduleState.Changed       -= _onScheduleStateReceived;
            networkHandler.PlaceList.Changed           -= OnPlaceListForWizard;
            networkHandler.PlaceList.Changed           -= _onPlaceListReceived;
            networkHandler.EventRewardConfig.Changed   -= _onEventRewardConfigReceived;
            networkHandler.RewardLimits.Changed        -= _onRewardLimitsReceived;

            if (_atlas != null) { SharedItemAtlas.Release(); _atlas = null; }
            // Or a hot-reload with the HG section open leaves the block painted until relog.
            hgPlacementMarkerWanted = false;
            try { UpdateHgPlacementMarker(); } catch { }
        }
    }
}
