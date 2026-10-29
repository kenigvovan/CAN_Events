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
    // Sign-up toast in the corner. No close button; /canhud invite full|mini|off controls it.
    public class EventInviteHud : HudElement
    {
        private readonly ClientNetworkHandler networkHandler;
        private readonly HudLayout layout;

        private EventInviteMsg invite;
        private GuiComposer panel;

        private readonly Dictionary<string, string> lastText = new Dictionary<string, string>();

        private int lastWrittenSecond = int.MinValue;

        private string hotkeyLabel = "?";
        private string dismissLabel = "?";

        // Countdown lives in ClientNetworkHandler so the HUD and the browser show the same number.
        private float SecondsLeft => networkHandler.SecondsLeftFor(invite?.EventCode);
        private float WindowSeconds => Math.Max(1f, networkHandler.WindowSecondsFor(invite?.EventCode));

        // Any change here triggers a recompose.
        private bool composedUrgent;
        private bool composedJoined;
        private bool composedTimed;
        private HudLayout.InviteDisplay composedMode;
        private int composedFrameW, composedFrameH;

        private const double Width = 300, Height = 96;
        private const double MiniHeight = 30;
        private const float UrgentSeconds = 15f;

        // Regular VS hotkeys, rebindable in Settings -> Controls.
        public const string JoinHotkeyCode = "caneventsjoin";
        public const string DismissHotkeyCode = "caneventsdismiss";

        // Cleared when that sign-up window closes.
        private readonly HashSet<string> dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public LoadoutPickGui LoadoutPick { get; set; }

        private readonly Action<EventInviteMsg> onInvited;
        private readonly Action<EventInviteClosedMsg> onClosed;

        public EventInviteHud(ICoreClientAPI capi, ClientNetworkHandler networkHandler, HudLayout layout)
            : base(capi)
        {
            this.networkHandler = networkHandler;
            this.layout = layout;

            // A closed HUD gets no render calls, so open on the packet. Always show the soonest window.
            onInvited = _ => Refresh(playSoundIfNew: true);

            onClosed = msg =>
            {
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

        private void Refresh(bool playSoundIfNew)
        {
            var next = networkHandler.SoonestInvite(IsDismissed);
            bool isNewEvent = next != null && (invite == null || invite.EventCode != next.EventCode);
            invite = next;

            if (invite == null) { TryClose(); return; }

            // Off also mutes the bell; the invite is still tracked for the hotkey and browser.
            if (Mode == HudLayout.InviteDisplay.Off)
            {
                if (IsOpened()) TryClose();
                return;
            }

            if (playSoundIfNew && isNewEvent) capi.Gui.PlaySound("effect/receptionbell");

            Compose();
            if (!IsOpened()) TryOpen();
        }

        public void OnLayoutChanged() => Refresh(playSoundIfNew: false);

        // Join or leave. False lets the key fall through to other bindings.
        public bool TryToggleQueue()
        {
            // Read live: with the panel off or dismissed, invite is not kept up to date.
            var target = networkHandler.SoonestInvite();
            if (target == null) return false;
            string code = target.EventCode;

            if (target.YouAreIn)
            {
                networkHandler.SendLeaveRequest(code);
                networkHandler.RequestEventList();
                return true;
            }

            // Same as the browser: several classes means pick one first.
            var entry = networkHandler.EventList.Value?.Events?
                .FirstOrDefault(e => e != null && e.GameModeCode == code);
            if (LoadoutPick != null && entry?.LoadoutSets != null && entry.LoadoutSets.Length > 1)
            {
                LoadoutPick.OpenForQueue(entry);
                return true;
            }

            networkHandler.SendJoinRequest(code);
            networkHandler.RequestEventList();
            // Joined state comes from the server's refreshed invite (fee, blacklist, party rules).
            return true;
        }

        // Hides this window only; does not touch the queue.
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

            if (ShapeChanged()) Compose();

            // Texts only change once a second; avoid rebuilding strings every frame.
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

            // No bar for a manual-start window.
            if (composedTimed)
                composer.AddStatbar(ElementBounds.Fixed(10, 48, Width - 20, 6),
                    composedUrgent ? Palette.Stop : Palette.Go, "timebar");

            composer
                    .AddDynamicText(SignupsText(), detailFont, ElementBounds.Fixed(10, 58, Width - 20, 18), "signups")
                    .AddDynamicText(ActionText(), actionFont, ElementBounds.Fixed(10, 76, Width - 20, 18), "action")
                .EndChildElements();

            SetPanel(composer.Compose());
        }

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

        // Kept in a field too; looking up a removed key in Composers is not safe per frame.
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

            // Only one form is composed; missing keys are skipped.
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

        // SetNewText redraws the texture, so skip unchanged text.
        private void SetIfChanged(string key, string text)
        {
            if (lastText.TryGetValue(key, out var previous) && previous == text) return;
            var element = panel.GetDynamicText(key);
            if (element == null) return;      // the other form's key, not composed here
            element.SetNewText(text);
            lastText[key] = text;
        }

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

        private string ActionText()
            => invite.YouAreIn
                ? Lang.Get("canevents:hud-invite-action-leave", hotkeyLabel, dismissLabel)
                : Lang.Get("canevents:hud-invite-action", hotkeyLabel, dismissLabel);

        private string ReadHotkeyLabel(string hotkeyCode)
        {
            var hotkey = capi.Input.GetHotKeyByCode(hotkeyCode);
            string key = hotkey?.CurrentMapping?.ToString();
            return string.IsNullOrEmpty(key) ? "?" : key;
        }
    }
}
