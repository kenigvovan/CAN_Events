using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Dismissing the window counts as declining.
    public class MatchReadyGui : CountdownPromptGui<MatchReadyRequestMsg>
    {
        public MatchReadyGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
            : base(capi, networkHandler)
        {
        }

        protected override string ComposerKey => "canevents-matchready";
        protected override string TitleText => Lang.Get("canevents:gui-ready-title");
        protected override string CountdownLangKey => "canevents:gui-ready-countdown";
        protected override float TimeoutOf(MatchReadyRequestMsg request) => request.TimeoutSeconds;
        protected override bool DismissIsDecline => true;

        protected override void SendDecline(MatchReadyRequestMsg request) => networkHandler.SendMatchReady(false);

        // Wired to ClientNetworkHandler.OnReadyPromptChanged.
        public void OnPromptChanged()
        {
            var pending = networkHandler.PendingReady;
            if (pending == null)
            {
                CloseQuietly();
                return;
            }

            // Don't ring again on a re-send for the same match (reconnect).
            bool isNew = Request == null || Request.EventId != pending.EventId;
            Show(pending, isNew);
        }

        protected override double ComposeBody(GuiComposer composer, double y)
        {
            composer.AddStaticText(
                Lang.Get("canevents:gui-ready-body",
                    (Request.EventCode ?? "").ToUpperInvariant(), Request.TotalPlayers),
                CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, Width, 50));
            return y + 56;
        }

        protected override double ComposeButtons(GuiComposer composer, double y)
        {
            composer.AddSmallButton(Lang.Get("canevents:gui-ready-accept"), () => Answer(true),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-ready-decline"), () => Answer(false),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 170, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            return y + 34;
        }

        private bool Answer(bool ready)
        {
            networkHandler.SendMatchReady(ready);
            CloseQuietly();
            return true;
        }
    }
}
