using System;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// "Your queued match is ready - are you?"
    ///
    /// The third prompt of this shape, after the tournament ready-check and the duel invitation, and
    /// they differ in what silence means. Here silence costs only this match: you are left out and it
    /// starts without you. That is why dismissing the window counts as declining - the point of the
    /// whole check is to find out quickly that somebody is not at the keyboard, and a dismissed
    /// window is as clear an answer as a clicked button.
    ///
    /// (Worth folding the three into one prompt with a reason attached, once there is a build to
    /// verify the refactor against.)
    /// </summary>
    public class MatchReadyGui : GuiDialog
    {
        private readonly ClientNetworkHandler networkHandler;
        private readonly Action onChanged;

        private MatchReadyRequestMsg request;
        private double shownAt;

        private const double Width = 420;

        public MatchReadyGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;

            onChanged = () =>
            {
                var pending = networkHandler.PendingReady;
                if (pending == null)
                {
                    request = null;
                    if (IsOpened()) TryClose();
                    return;
                }

                // Only on a prompt that is actually new: a re-send for the same match (a reconnect,
                // say) should not ring a second time.
                bool isNew = request == null || request.EventId != pending.EventId;

                request = pending;
                shownAt = capi.ElapsedMilliseconds / 1000.0;
                Compose();
                if (!IsOpened()) TryOpen();

                // A silent deadline is worse than no deadline: missing this costs the match, and the
                // player may well be looking somewhere else. The registration toast already rings for
                // far less (EventInviteHud), so this one has to.
                if (isNew) capi.Gui.PlaySound("effect/receptionbell");
            };
            networkHandler.OnReadyPromptChanged += onChanged;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.OnReadyPromptChanged -= onChanged;
        }

        // Opened by a server packet only.
        public override string ToggleKeyCombinationCode => null;

        private void Compose()
        {
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            var composer = capi.Gui
                .CreateCompo("canevents-matchready", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("canevents:gui-ready-title"), OnDismiss)
                .BeginChildElements(bgBounds);

            double y = 14;

            composer.AddStaticText(
                Lang.Get("canevents:gui-ready-body",
                    (request.EventCode ?? "").ToUpperInvariant(), request.TotalPlayers),
                CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, Width, 50));
            y += 56;

            composer.AddDynamicText(CountdownText(), CairoFont.WhiteSmallishText()
                    .WithColor(GuiStyle.ErrorTextColor),
                ElementBounds.Fixed(0, y, Width, 25), "countdown");
            y += 34;

            composer.AddSmallButton(Lang.Get("canevents:gui-ready-accept"), OnReady,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-ready-decline"), OnDecline,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 170, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();
        }

        private bool OnReady()
        {
            networkHandler.SendMatchReady(true);
            request = null;
            TryClose();
            return true;
        }

        private bool OnDecline()
        {
            networkHandler.SendMatchReady(false);
            request = null;
            TryClose();
            return true;
        }

        // Closing is declining: see the class comment. Saying so at once frees the others to start
        // rather than making them wait out the clock.
        private void OnDismiss()
        {
            networkHandler.SendMatchReady(false);
            request = null;
            TryClose();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (request == null) return;

            if (Remaining() <= -0.5f) { TryClose(); return; }

            SingleComposer?.GetDynamicText("countdown")?.SetNewText(CountdownText());
        }

        private float Remaining()
            => request.TimeoutSeconds - (float)(capi.ElapsedMilliseconds / 1000.0 - shownAt);

        private string CountdownText()
            => Lang.Get("canevents:gui-ready-countdown", Math.Max(0, (int)Remaining()));
    }
}
