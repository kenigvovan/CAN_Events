using System;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// "Your tournament match is up - ready?" Like <see cref="LoadoutPickGui"/>, except ignoring this
    /// one loses the match: the countdown is the server's, and closing the dialog is NOT an answer -
    /// the player can still accept from chat (/tournament ready) until the deadline passes.
    /// </summary>
    public class TournamentReadyGui : GuiDialog
    {
        private readonly ClientNetworkHandler networkHandler;

        private TournamentReadyRequestMsg request;
        private double shownAt;
        private bool answered;

        private const double Width = 420;

        public TournamentReadyGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
        }

        // Opened by a server packet only.
        public override string ToggleKeyCombinationCode => null;

        public void OpenFor(TournamentReadyRequestMsg msg)
        {
            if (msg == null) return;

            // Rings for a match not already answered. The highest stakes of the three prompts -
            // ignoring this one forfeits a bracket match - so silence here is the least affordable.
            // Skipped on a reconnect re-send of a match already confirmed: nothing is being asked.
            bool isNew = !msg.AlreadyReady
                && (request == null || request.MatchId != msg.MatchId
                    || request.TournamentId != msg.TournamentId);

            request = msg;
            answered = msg.AlreadyReady;
            shownAt = capi.ElapsedMilliseconds / 1000.0;
            Compose();
            TryOpen();

            if (isNew) capi.Gui.PlaySound("effect/receptionbell");
        }

        /// <summary>Server says this prompt is over (answered elsewhere, expired, opponent forfeited).</summary>
        public void CloseFor(long tournamentId, int matchId)
        {
            if (request == null) return;
            if (request.TournamentId != tournamentId || request.MatchId != matchId) return;
            request = null;
            TryClose();
        }

        private void Compose()
        {
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            var composer = capi.Gui
                .CreateCompo("canevents-tournamentready", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("canevents:gui-tournament-ready-title", request.TournamentName), OnDismiss)
                .BeginChildElements(bgBounds);

            double y = 14;

            composer.AddStaticText(
                Lang.Get("canevents:gui-tournament-ready-body", request.RoundLabel, request.OpponentName,
                    (request.EventCode ?? "").ToUpperInvariant()),
                CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, Width, 50));
            y += 56;

            composer.AddDynamicText(CountdownText(), CairoFont.WhiteSmallishText()
                    .WithColor(GuiStyle.ErrorTextColor),
                ElementBounds.Fixed(0, y, Width, 25), "countdown");
            y += 34;

            if (answered)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-tournament-ready-waiting"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }
            else
            {
                composer.AddSmallButton(Lang.Get("canevents:gui-tournament-ready-accept"), OnAccept,
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                    EnumButtonStyle.Normal);
                composer.AddSmallButton(Lang.Get("canevents:gui-tournament-ready-forfeit"), OnForfeit,
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, 170, y, 0, 0).WithFixedPadding(12, 4),
                    EnumButtonStyle.Normal);
                y += 34;
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private bool OnAccept()
        {
            if (request == null) return true;
            networkHandler.SendTournamentReady(request.TournamentId, request.MatchId, true);
            answered = true;
            Compose();          // switches to "waiting for your opponent" in place
            return true;
        }

        private bool OnForfeit()
        {
            if (request == null) return true;
            networkHandler.SendTournamentReady(request.TournamentId, request.MatchId, false);
            request = null;     // cleared before closing, so OnGuiClosed can tell this from a dismissal
            TryClose();
            return true;
        }

        private void OnDismiss() => TryClose();

        // Dismissing this is NOT an answer: the server's deadline still runs. Only the client-side
        // cache is cleared, so a re-send after a reconnect can bring the dialog back.
        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            networkHandler.DismissTournamentReady();
            request = null;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (request == null) return;

            // Past the deadline the server has already decided; a late click would do nothing.
            if (Remaining() <= -0.5f) { TryClose(); return; }

            SingleComposer?.GetDynamicText("countdown")?.SetNewText(CountdownText());
        }

        private float Remaining()
            => request.TimeoutSeconds - (float)(capi.ElapsedMilliseconds / 1000.0 - shownAt);

        private string CountdownText()
            => Lang.Get("canevents:gui-tournament-ready-countdown", Math.Max(0, (int)Remaining()));
    }
}
