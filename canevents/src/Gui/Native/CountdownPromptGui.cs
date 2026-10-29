using System;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Separate from CountdownPromptGui so the class pick (not a yes/no prompt) can use it too.
    public sealed class CountdownClock
    {
        private readonly ICoreClientAPI capi;
        private double shownAt;
        private float timeoutSeconds;

        public CountdownClock(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void Restart(float timeoutSeconds)
        {
            this.timeoutSeconds = timeoutSeconds;
            shownAt = capi.ElapsedMilliseconds / 1000.0;
        }

        public float Remaining => timeoutSeconds - (float)(capi.ElapsedMilliseconds / 1000.0 - shownAt);

        public int SecondsLeft => Math.Max(0, (int)Remaining);

        // 0.5s margin so a click on the last frame still lands.
        public bool Expired => Remaining <= -0.5f;
    }

    // Code closes clear Request before TryClose, so a non-null Request in OnGuiClosed means the player dismissed it.
    public abstract class CountdownPromptGui<TReq> : GuiDialog where TReq : class
    {
        protected readonly ClientNetworkHandler networkHandler;
        protected readonly CountdownClock clock;

        // Null once answered, withdrawn or expired.
        protected TReq Request { get; private set; }

        protected const double Width = 420;

        protected CountdownPromptGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
            clock = new CountdownClock(capi);
        }

        public override string ToggleKeyCombinationCode => null;

        protected abstract string ComposerKey { get; }

        protected abstract string TitleText { get; }

        // Takes seconds left as its only argument.
        protected abstract string CountdownLangKey { get; }

        protected abstract float TimeoutOf(TReq request);

        // True: closing the window sends a decline right away.
        protected abstract bool DismissIsDecline { get; }

        protected virtual void SendDecline(TReq request) { }

        // Above the countdown line. Returns the next free y.
        protected abstract double ComposeBody(GuiComposer composer, double y);

        // Below the countdown line. Returns the next free y.
        protected abstract double ComposeButtons(GuiComposer composer, double y);

        protected void Show(TReq request, bool ring)
        {
            Request = request;
            clock.Restart(TimeoutOf(request));
            Compose();
            if (!IsOpened()) TryOpen();

            if (ring) capi.Gui.PlaySound("effect/receptionbell");
        }

        // Close without implying an answer.
        protected void CloseQuietly()
        {
            Request = null;
            if (IsOpened()) TryClose();
        }

        protected void Compose()
        {
            // Cross acts like Escape; OnGuiClosed decides what it means.
            var composer = DialogShell.Begin(capi, ComposerKey, TitleText, () => TryClose());

            double y = ComposeBody(composer, DialogShell.BodyTop);

            composer.AddDynamicText(CountdownText(), CairoFont.WhiteSmallishText()
                    .WithColor(GuiStyle.ErrorTextColor),
                ElementBounds.Fixed(0, y, Width, 25), "countdown");
            y += 34;

            ComposeButtons(composer, y);

            SingleComposer = composer.EndChildElements().Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            var dismissed = Request;
            Request = null;

            // Null means closed by code.
            if (dismissed != null && DismissIsDecline) SendDecline(dismissed);
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (Request == null) return;

            if (clock.Expired) { CloseQuietly(); return; }

            SingleComposer?.GetDynamicText("countdown")?.SetNewText(CountdownText());
        }

        private string CountdownText() => Lang.Get(CountdownLangKey, clock.SecondsLeft);
    }
}
