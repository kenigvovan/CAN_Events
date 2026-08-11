using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Gui;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// "Registration is open" as a corner toast rather than a dialog in the middle of the screen.
    /// A modal prompt was the wrong shape for this: it takes the mouse off the player mid-game, and
    /// closing it took the countdown with it - after which nothing on screen said when the event
    /// started. A HUD panel keeps the clock visible for the whole window, costs no clicks, and is
    /// answered with one key.
    ///
    /// There is no close button: it exists precisely to still be there in five minutes. What there is
    /// instead is a setting - /canhud invite full|mini|off - so a player who finds it in the way turns
    /// it down once rather than dismissing it every time. The events browser carries the same
    /// countdown either way.
    /// </summary>
    public class EventInviteHud : HudElement
    {
        private readonly ClientNetworkHandler networkHandler;
        private readonly HudLayout layout;

        private EventInviteMsg invite;
        /// <summary>The composed panel, or null while nothing is drawn.</summary>
        private GuiComposer panel;

        /// <summary>Last string written into each dynamic text; see <see cref="SetIfChanged"/>.</summary>
        private readonly Dictionary<string, string> lastText = new Dictionary<string, string>();

        /// <summary>Whole second the panel was last written for; the texts only change with it.</summary>
        private int lastWrittenSecond = int.MinValue;

        // Printed key names, worked out at compose time rather than per frame.
        private string hotkeyLabel = "?";
        private string dismissLabel = "?";

        // The countdown is not kept here: ClientNetworkHandler works it out from the last packet and
        // the client clock, so the panel, the browser and anything else read the same number.
        private float SecondsLeft => networkHandler.SecondsLeftFor(invite?.EventCode);
        private float WindowSeconds => Math.Max(1f, networkHandler.WindowSecondsFor(invite?.EventCode));

        // What the current composition was built for; a change of any of these is a recompose.
        private bool composedUrgent;
        private bool composedJoined;
        private bool composedTimed;
        private HudLayout.InviteDisplay composedMode;
        private int composedFrameW, composedFrameH;

        private const double Width = 300, Height = 96;
        private const double MiniHeight = 30;
        private const float UrgentSeconds = 15f;

        // Both are ordinary Vintage Story hotkeys: they show up in Settings -> Controls and can be
        // rebound there, and the panel prints whatever binding is in force rather than a fixed letter.
        /// <summary>Joins the offered event, or leaves it again if already queued.</summary>
        public const string JoinHotkeyCode = "caneventsjoin";
        /// <summary>Hides the panel for the window currently shown.</summary>
        public const string DismissHotkeyCode = "caneventsdismiss";

        /// <summary>Windows this player waved away; cleared when the window itself closes.</summary>
        private readonly HashSet<string> dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The class picker, when the mode offers more than one set.</summary>
        public LoadoutPickGui LoadoutPick { get; set; }

        private readonly Action<EventInviteMsg> onInvited;
        private readonly Action<EventInviteClosedMsg> onClosed;

        public EventInviteHud(ICoreClientAPI capi, ClientNetworkHandler networkHandler, HudLayout layout)
            : base(capi)
        {
            this.networkHandler = networkHandler;
            this.layout = layout;

            // A closed HUD element gets no render calls, so opening is decided here, on the packet.
            // Which window is shown is never decided by which packet arrived last: with two events
            // taking sign-ups, that made the panel jump between them on every update. The panel always
            // shows the one closing soonest.
            onInvited = _ => Refresh(playSoundIfNew: true);

            onClosed = msg =>
            {
                // The window is gone, so waving it away means nothing any more; forgetting it here is
                // what makes the next event on the same code show up again.
                if (msg?.EventCode != null) dismissed.Remove(msg.EventCode);
                Refresh(playSoundIfNew: false);
            };

            networkHandler.OnEventInvited += onInvited;
            networkHandler.OnEventInviteClosed += onClosed;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.OnEventInvited -= onInvited;
            networkHandler.OnEventInviteClosed -= onClosed;
        }

        public override bool Focusable => false;
        public override bool ShouldReceiveKeyboardEvents() => false;

        private HudLayout.InviteDisplay Mode => layout?.InviteMode ?? HudLayout.InviteDisplay.Full;

        /// <summary>
        /// Re-reads which window to show and rebuilds the panel. The bell rings for an event this
        /// panel was not already showing - a fresh call, not a count going up on the same one.
        /// </summary>
        private void Refresh(bool playSoundIfNew)
        {
            var next = networkHandler.SoonestInvite(IsDismissed);
            bool isNewEvent = next != null && (invite == null || invite.EventCode != next.EventCode);
            invite = next;

            if (invite == null) { TryClose(); return; }

            // Off means off, including the sound: someone who turned the panel away is not asking to
            // be pinged either. The invite is still tracked - the hotkey and the browser use it.
            if (Mode == HudLayout.InviteDisplay.Off)
            {
                if (IsOpened()) TryClose();
                return;
            }

            // A desk bell, not the menu click the browser uses for its own buttons: this is the one
            // sound the player did not cause, and it should read as "you are being called".
            if (playSoundIfNew && isNewEvent) capi.Gui.PlaySound("effect/receptionbell");

            Compose();
            if (!IsOpened()) TryOpen();
        }

        /// <summary>Rebuild after the layout editor moved the panel, or /canhud invite changed it.</summary>
        public void OnLayoutChanged() => Refresh(playSoundIfNew: false);

        /// <summary>
        /// The hotkey answer: join the offered event, or leave it again if already queued. One key
        /// both ways, because the panel is the only thing on screen at that moment - changing your
        /// mind should not mean finding the browser. Returns false when there is nothing to answer,
        /// which lets the key fall through to whatever else is bound to it.
        /// </summary>
        public bool TryToggleQueue()
        {
            // Read live rather than from the panel's copy: with the panel switched off or dismissed
            // nothing recomposes, and the key must still answer whatever window is actually open.
            var target = networkHandler.SoonestInvite();
            if (target == null) return false;
            string code = target.EventCode;

            if (target.YouAreIn)
            {
                networkHandler.SendLeaveRequest(code);
                networkHandler.RequestEventList();
                return true;
            }

            // Same route the browser takes: a mode with several classes asks which one first. That
            // dialog does take the mouse, but by then the player has asked for it.
            var entry = networkHandler.EventList.Value?.Events?
                .FirstOrDefault(e => e != null && e.GameModeCode == code);
            if (LoadoutPick != null && entry?.LoadoutSets != null && entry.LoadoutSets.Length > 1)
            {
                LoadoutPick.OpenForQueue(entry);
                return true;
            }

            networkHandler.SendJoinRequest(code);
            networkHandler.RequestEventList();
            // Not marked joined here: the server answers with a refreshed invite, and only it knows
            // whether the sign-up was accepted (fee, blacklist, party rules).
            return true;
        }

        /// <summary>
        /// Hides the panel for the window being shown, until that window closes. Distinct from
        /// /canhud invite off, which is a standing preference: this is "not this one, thanks", and
        /// the next event opens a panel again. Queue state is untouched - a player who dismisses
        /// after joining stays in the queue.
        /// </summary>
        public bool TryDismissCurrent()
        {
            var target = networkHandler.SoonestInvite(IsDismissed);
            if (target == null) return false;

            dismissed.Add(target.EventCode);
            Refresh(playSoundIfNew: false);   // moves to the next open window, or closes the panel
            return true;
        }

        private bool IsDismissed(string eventCode)
            => eventCode != null && dismissed.Contains(eventCode);

        public override void OnRenderGUI(float deltaTime)
        {
            if (invite == null)
            {
                if (IsOpened()) TryClose();
                return;
            }

            // The clock is read, not ticked: it comes from the last packet plus elapsed client time,
            // and stops at zero on its own while the close packet is on its way.
            if (ShapeChanged()) Compose();

            // Every line on this panel is derived from a whole second of the countdown, so building
            // them 60 times a second would allocate strings nobody reads. Compose() writes them once
            // itself, hence no update on the frame it ran.
            int second = (int)SecondsLeft;
            if (second != lastWrittenSecond)
            {
                lastWrittenSecond = second;
                UpdateTexts();
            }

            base.OnRenderGUI(deltaTime);
        }

        private bool ShapeChanged()
            => IsUrgent() != composedUrgent
            || invite.YouAreIn != composedJoined
            || (invite.SecondsLeft >= 0f) != composedTimed
            || Mode != composedMode
            || capi.Render.FrameWidth != composedFrameW
            || capi.Render.FrameHeight != composedFrameH;

        private bool IsUrgent() => invite.SecondsLeft >= 0f && SecondsLeft <= UrgentSeconds;

        private void Compose()
        {
            if (invite == null) return;

            composedUrgent = IsUrgent();
            composedJoined = invite.YouAreIn;
            composedTimed = invite.SecondsLeft >= 0f;
            composedMode = Mode;
            composedFrameW = capi.Render.FrameWidth;
            composedFrameH = capi.Render.FrameHeight;
            // Looked up here, not per frame: the binding only changes in the controls settings, and
            // every recompose (and every packet) passes through this method.
            hotkeyLabel = ReadHotkeyLabel(JoinHotkeyCode);
            dismissLabel = ReadHotkeyLabel(DismissHotkeyCode);

            if (composedMode == HudLayout.InviteDisplay.Off) { ClearPanel(); return; }
            if (composedMode == HudLayout.InviteDisplay.Mini) { ComposeMini(); return; }

            var pos = PanelPosition(Width, Height);

            var bounds = ElementBounds.Fixed(pos.X, pos.Y, Width, Height);
            var bg = ElementBounds.Fixed(0, 0, Width, Height);

            var headFont = CairoFont.WhiteSmallText().WithColor(Palette.Title);
            var clockFont = CairoFont.WhiteSmallishText()
                .WithColor(composedUrgent ? Palette.Stop : Palette.Title);
            var detailFont = CairoFont.WhiteDetailText().WithColor(Palette.Detail);
            var actionFont = CairoFont.WhiteDetailText()
                .WithColor(composedJoined ? Palette.Go : Palette.Header);

            var composer = capi.Gui
                .CreateCompo("canevents-hud-invite", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddStaticText(Lang.Get("canevents:hud-invite-head",
                            (invite.EventCode ?? "").ToUpperInvariant()),
                        headFont, ElementBounds.Fixed(10, 6, Width - 20, 18))
                    .AddDynamicText(ClockText(), clockFont, ElementBounds.Fixed(10, 26, Width - 20, 20), "clock");

            // No bar for a window with no deadline: an empty gauge under "starts when the admin says
            // so" would read as a stalled countdown.
            if (composedTimed)
                composer.AddStatbar(ElementBounds.Fixed(10, 48, Width - 20, 6),
                    composedUrgent ? Palette.Stop : Palette.Go, "timebar");

            composer
                    .AddDynamicText(SignupsText(), detailFont, ElementBounds.Fixed(10, 58, Width - 20, 18), "signups")
                    .AddDynamicText(ActionText(), actionFont, ElementBounds.Fixed(10, 76, Width - 20, 18), "action")
                .EndChildElements();

            SetPanel(composer.Compose());
        }

        /// <summary>
        /// The one-line form: the same clock, none of the room. For players who want to know an event
        /// is open without a panel on their screen; the details are a keypress away in the browser.
        /// </summary>
        private void ComposeMini()
        {
            var pos = PanelPosition(Width, MiniHeight);
            var bounds = ElementBounds.Fixed(pos.X, pos.Y, Width, MiniHeight);
            var bg = ElementBounds.Fixed(0, 0, Width, MiniHeight);

            var font = CairoFont.WhiteDetailText()
                .WithColor(composedJoined ? Palette.Go : composedUrgent ? Palette.Stop : Palette.Title);

            SetPanel(capi.Gui
                .CreateCompo("canevents-hud-invite", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddDynamicText(MiniText(), font, ElementBounds.Fixed(8, 6, Width - 16, 18), "mini")
                .EndChildElements()
                .Compose());
        }

        /// <summary>
        /// Installs the freshly built panel as the element's only composer. The composer is also kept
        /// in a field: the per-frame text update reads it every frame, and looking it up by key there
        /// depends on how the manager answers for a key it no longer has.
        /// </summary>
        private void SetPanel(GuiComposer composer)
        {
            Composers.ClearComposers();
            lastText.Clear();          // the elements are new; what the old ones said means nothing
            lastWrittenSecond = int.MinValue;
            panel = composer;
            Composers["invite"] = composer;
            UpdateTexts();
        }

        private void ClearPanel()
        {
            Composers.ClearComposers();
            panel = null;
        }

        /// <summary>Where the panel goes: the player's stored spot, or the default top-right corner.</summary>
        private (double X, double Y) PanelPosition(double width, double height)
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            var pos = HudLayout.Resolve(layout?.Invite,
                new System.Numerics.Vector2((float)screenW, (float)screenH),
                new System.Numerics.Vector2((float)width, (float)height),
                new System.Numerics.Vector2((float)(screenW - width - 10), 110f));

            return (GameMath.Clamp(pos.X, 0, Math.Max(0, screenW - width)),
                    GameMath.Clamp(pos.Y, 0, Math.Max(0, screenH - height)));
        }

        private void UpdateTexts()
        {
            if (panel == null || invite == null) return;

            // Whichever form is composed answers; the other keys are simply not there.
            SetIfChanged("mini", MiniText());
            SetIfChanged("clock", ClockText());
            SetIfChanged("signups", SignupsText());
            SetIfChanged("action", ActionText());

            var bar = panel.GetStatbar("timebar");
            if (bar != null)
            {
                bar.SetLineInterval(1);
                bar.SetValues(Math.Max(0f, SecondsLeft), 0, WindowSeconds);
            }
        }

        /// <summary>
        /// Writes a line only when it actually reads differently. This runs every frame; the clock it
        /// prints changes once a second, and a dynamic text redraws its texture when it is set.
        /// </summary>
        private void SetIfChanged(string key, string text)
        {
            if (lastText.TryGetValue(key, out var previous) && previous == text) return;
            var element = panel.GetDynamicText(key);
            if (element == null) return;      // the other form's key, not composed here
            element.SetNewText(text);
            lastText[key] = text;
        }

        /// <summary>
        /// The one-line form. A window with no deadline gets its own phrasing rather than a countdown
        /// with words where the clock goes ("HUNGERGAMES in registration open").
        /// </summary>
        private string MiniText()
        {
            string code = (invite.EventCode ?? "").ToUpperInvariant();

            if (invite.SecondsLeft < 0f)
                return invite.YouAreIn
                    ? Lang.Get("canevents:hud-invite-mini-open-joined", code, hotkeyLabel)
                    : Lang.Get("canevents:hud-invite-mini-open", code, hotkeyLabel);

            return invite.YouAreIn
                ? Lang.Get("canevents:hud-invite-mini-joined", code, TimeText.Short(SecondsLeft), hotkeyLabel)
                : Lang.Get("canevents:hud-invite-mini", code, TimeText.Short(SecondsLeft), hotkeyLabel);
        }

        private string ClockText()
            => invite.SecondsLeft < 0f
                ? Lang.Get("canevents:gui-invite-manual")
                : Lang.Get("canevents:gui-invite-countdown", TimeText.Short(SecondsLeft));

        private string SignupsText()
            => Lang.Get("canevents:gui-invite-signups", invite.MaxParties > 0
                ? invite.Registered + "/" + invite.MaxParties
                : invite.Registered.ToString());

        /// <summary>
        /// The line that says what the keys do right now: joining and leaving share one key, so the
        /// verb changes with the state, and hiding the panel is always offered next to it.
        /// </summary>
        private string ActionText()
            => invite.YouAreIn
                ? Lang.Get("canevents:hud-invite-action-leave", hotkeyLabel, dismissLabel)
                : Lang.Get("canevents:hud-invite-action", hotkeyLabel, dismissLabel);

        /// <summary>A key's printed name, read from the live binding so a rebind shows the player's
        /// own key instead of the default this mod shipped with.</summary>
        private string ReadHotkeyLabel(string hotkeyCode)
        {
            var hotkey = capi.Input.GetHotKeyByCode(hotkeyCode);
            string key = hotkey?.CurrentMapping?.ToString();
            return string.IsNullOrEmpty(key) ? "?" : key;
        }
    }
}
