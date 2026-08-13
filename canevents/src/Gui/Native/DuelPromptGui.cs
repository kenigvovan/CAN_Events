using System;
using canevents.src.Duels;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// "So-and-so challenges you." Built on the same shape as <see cref="TournamentReadyGui"/>, with
    /// one difference that matters: ignoring this one costs nothing. A tournament ready-check forfeits
    /// the match if unanswered; a duel invitation simply lapses, so dismissing the dialog is a
    /// perfectly good way to say no.
    /// </summary>
    public class DuelPromptGui : GuiDialog
    {
        private readonly ClientNetworkHandler networkHandler;
        private readonly Action onChanged;

        private DuelPromptMsg request;
        private double shownAt;

        private const double Width = 420;

        public DuelPromptGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;

            onChanged = () =>
            {
                var pending = networkHandler.PendingDuel;
                if (pending == null)
                {
                    request = null;
                    if (IsOpened()) TryClose();
                    return;
                }

                // New challenge only, keyed on its id: a resend for the same one must not ring twice.
                bool isNew = request == null || request.ChallengeId != pending.ChallengeId;

                request = pending;
                shownAt = capi.ElapsedMilliseconds / 1000.0;
                Compose();
                if (!IsOpened()) TryOpen();

                // Someone is waiting on an answer with a clock running. Unheard, the challenge just
                // lapses and both players think the other ignored them.
                if (isNew) capi.Gui.PlaySound("effect/receptionbell");
            };
            networkHandler.OnDuelPromptChanged += onChanged;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.OnDuelPromptChanged -= onChanged;
        }

        // Opened by a server packet only.
        public override string ToggleKeyCombinationCode => null;

        private void Compose()
        {
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            var composer = capi.Gui
                .CreateCompo("canevents-duelprompt", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("canevents:gui-duel-title", request.ChallengerName), OnDismiss)
                .BeginChildElements(bgBounds);

            double y = 14;

            composer.AddStaticText(BodyText(), CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(0, y, Width, 50));
            y += 56;

            // The lifetime score, when there is one: "am I usually the one winning this?" is the
            // first question the prompt raises, so the answer belongs on it.
            if (request.YourWinsVsChallenger + request.ChallengerWinsVsYou + request.DrawsVsChallenger > 0)
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-duel-headtohead", request.YourWinsVsChallenger,
                        request.ChallengerWinsVsYou, request.DrawsVsChallenger),
                    CairoFont.WhiteSmallishText().WithColor(Palette.Info),
                    ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }

            if (!string.IsNullOrEmpty(request.WagerItems))
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-duel-wager", GuiItemUtil.DescribePacked(capi, request.WagerItems)),
                    CairoFont.WhiteSmallishText().WithColor(Palette.Title),
                    ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }

            composer.AddDynamicText(CountdownText(), CairoFont.WhiteSmallishText()
                    .WithColor(GuiStyle.ErrorTextColor),
                ElementBounds.Fixed(0, y, Width, 25), "countdown");
            y += 34;

            composer.AddSmallButton(Lang.Get("canevents:gui-duel-accept"), OnAccept,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-duel-decline"), OnDecline,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 170, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();
        }

        // Says what is actually being proposed: a 3v3 must not arrive looking like a duel of two.
        private string BodyText()
        {
            string mode = (request.EventCode ?? "").ToUpperInvariant();

            return request.SideSize > 1
                ? Lang.Get("canevents:gui-duel-body-team", request.ChallengerName,
                    request.SideSize, mode)
                : Lang.Get("canevents:gui-duel-body", request.ChallengerName, mode);
        }

        private bool OnAccept()
        {
            networkHandler.SendDuelAnswer(true);
            request = null;
            TryClose();
            return true;
        }

        private bool OnDecline()
        {
            networkHandler.SendDuelAnswer(false);
            request = null;
            TryClose();
            return true;
        }

        // Closing the window is a real answer here, unlike the tournament prompt: nothing is lost by
        // declining, and leaving the challenger waiting out the full minute for a decision already
        // made is worse than saying no.
        private void OnDismiss()
        {
            networkHandler.SendDuelAnswer(false);
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
            => Lang.Get("canevents:gui-duel-countdown", Math.Max(0, (int)Remaining()));
    }
}
