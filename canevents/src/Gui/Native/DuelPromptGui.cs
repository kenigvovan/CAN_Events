using canevents.src.Duels;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Closing the window sends a decline right away.
    public class DuelPromptGui : CountdownPromptGui<DuelPromptMsg>
    {
        public DuelPromptGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
            : base(capi, networkHandler)
        {
        }

        protected override string ComposerKey => "canevents-duelprompt";
        protected override string TitleText => Lang.Get("canevents:gui-duel-title", Request.ChallengerName);
        protected override string CountdownLangKey => "canevents:gui-duel-countdown";
        protected override float TimeoutOf(DuelPromptMsg request) => request.TimeoutSeconds;
        protected override bool DismissIsDecline => true;

        protected override void SendDecline(DuelPromptMsg request) => networkHandler.SendDuelAnswer(false);

        // Wired to ClientNetworkHandler.OnDuelPromptChanged.
        public void OnPromptChanged()
        {
            var pending = networkHandler.PendingDuel;
            if (pending == null)
            {
                CloseQuietly();
                return;
            }

            // Keyed on id so a resend doesn't ring twice.
            bool isNew = Request == null || Request.ChallengeId != pending.ChallengeId;
            Show(pending, isNew);
        }

        protected override double ComposeBody(GuiComposer composer, double y)
        {
            composer.AddStaticText(BodyText(), CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(0, y, Width, 50));
            y += 56;

            if (Request.YourWinsVsChallenger + Request.ChallengerWinsVsYou + Request.DrawsVsChallenger > 0)
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-duel-headtohead", Request.YourWinsVsChallenger,
                        Request.ChallengerWinsVsYou, Request.DrawsVsChallenger),
                    CairoFont.WhiteSmallishText().WithColor(Palette.Info),
                    ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }

            string terms = EventMutators.Describe(Request.Mutators);
            if (terms != null)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-duel-terms", terms),
                    CairoFont.WhiteSmallishText().WithColor(Palette.Live),
                    ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }

            if (!string.IsNullOrEmpty(Request.WagerItems))
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-duel-wager", GuiItemUtil.DescribePacked(capi, Request.WagerItems)),
                    CairoFont.WhiteSmallishText().WithColor(Palette.Title),
                    ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }

            return y;
        }

        protected override double ComposeButtons(GuiComposer composer, double y)
        {
            composer.AddSmallButton(Lang.Get("canevents:gui-duel-accept"), () => Answer(true),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-duel-decline"), () => Answer(false),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 170, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            return y + 34;
        }

        private string BodyText()
        {
            string mode = (Request.EventCode ?? "").ToUpperInvariant();

            return Request.SideSize > 1
                ? Lang.Get("canevents:gui-duel-body-team", Request.ChallengerName,
                    Request.SideSize, mode)
                : Lang.Get("canevents:gui-duel-body", Request.ChallengerName, mode);
        }

        private bool Answer(bool accept)
        {
            networkHandler.SendDuelAnswer(accept);
            CloseQuietly();
            return true;
        }
    }
}
