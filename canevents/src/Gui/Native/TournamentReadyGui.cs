using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Closing the dialog is not an answer: the countdown is server-side and /tournament ready still works.
    public class TournamentReadyGui : CountdownPromptGui<TournamentReadyRequestMsg>
    {
        private bool answered;

        public TournamentReadyGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
            : base(capi, networkHandler)
        {
        }

        protected override string ComposerKey => "canevents-tournamentready";
        protected override string TitleText => Lang.Get("canevents:gui-tournament-ready-title", Request.TournamentName);
        protected override string CountdownLangKey => "canevents:gui-tournament-ready-countdown";
        protected override float TimeoutOf(TournamentReadyRequestMsg request) => request.TimeoutSeconds;
        protected override bool DismissIsDecline => false;

        public void OpenFor(TournamentReadyRequestMsg msg)
        {
            if (msg == null) return;

            // No sound on a reconnect re-send of an already confirmed match.
            bool isNew = !msg.AlreadyReady
                && (Request == null || Request.MatchId != msg.MatchId
                    || Request.TournamentId != msg.TournamentId);

            answered = msg.AlreadyReady;
            Show(msg, isNew);
        }

        public void CloseFor(long tournamentId, int matchId)
        {
            if (Request == null) return;
            if (Request.TournamentId != tournamentId || Request.MatchId != matchId) return;
            CloseQuietly();
        }

        protected override double ComposeBody(GuiComposer composer, double y)
        {
            composer.AddStaticText(
                Lang.Get("canevents:gui-tournament-ready-body", Request.RoundLabel, Request.OpponentName,
                    (Request.EventCode ?? "").ToUpperInvariant()),
                CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, Width, 50));
            return y + 56;
        }

        protected override double ComposeButtons(GuiComposer composer, double y)
        {
            if (answered)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-tournament-ready-waiting"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, Width, 25));
                return y + 30;
            }

            composer.AddSmallButton(Lang.Get("canevents:gui-tournament-ready-accept"), OnAccept,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-tournament-ready-forfeit"), OnForfeit,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 170, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            return y + 34;
        }

        private bool OnAccept()
        {
            if (Request == null) return true;
            networkHandler.SendTournamentReady(Request.TournamentId, Request.MatchId, true);
            answered = true;
            Compose();
            return true;
        }

        private bool OnForfeit()
        {
            if (Request == null) return true;
            networkHandler.SendTournamentReady(Request.TournamentId, Request.MatchId, false);
            CloseQuietly();
            return true;
        }
    }
}
