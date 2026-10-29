using System;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Waiting-room side pick. Closing it changes nothing: whoever hasn't picked is placed at the start.
    public class TeamPickGui : CountdownPromptGui<TeamPickStateMsg>
    {
        private long lastEventId;

        public TeamPickGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
            : base(capi, networkHandler)
        {
        }

        protected override string ComposerKey => "canevents-teampick";
        protected override string TitleText
            => Lang.Get("canevents:gui-teampick-title", (Request?.EventCode ?? "").ToUpperInvariant());
        protected override string CountdownLangKey => "canevents:gui-lobby-starts-in";
        protected override float TimeoutOf(TeamPickStateMsg request) => request.SecondsLeft;
        protected override bool DismissIsDecline => false;

        // Wired to ClientNetworkHandler.OnTeamPickChanged.
        public void OnStateChanged()
        {
            var state = networkHandler.TeamPick;
            if (state == null)
            {
                CloseQuietly();
                return;
            }

            // A dismissed dialog comes back with the next change, so Escape can't lose the choice for good.
            bool ring = Request == null && !IsOpened() && lastEventId != state.EventId;
            lastEventId = state.EventId;
            Show(state, ring);
        }

        protected override double ComposeBody(GuiComposer composer, double y)
        {
            if (Request.MaxImbalance > 0)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-teampick-hint", Request.MaxImbalance),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, Width, 40));
                y += 44;
            }

            var members = Request.Members ?? new string[0];
            var counts = members.Select(CountNames).ToArray();
            for (int t = 0; t < members.Length; t++)
            {
                int team = t;
                var c = TeamPalette.Rgba(t);
                composer.AddStaticText(TeamPalette.NameOf(t) + " (" + counts[t] + ")",
                    CairoFont.WhiteSmallishText().WithColor(new double[] { c.X, c.Y, c.Z, 1 }),
                    ElementBounds.Fixed(0, y, 240, 25));

                if (Request.YourTeam == t)
                    composer.AddStaticText(Lang.Get("canevents:gui-teampick-yours"), CairoFont.WhiteDetailText(),
                        ElementBounds.Fixed(260, y + 3, 160, 25));
                else if (IsFull(counts, t))
                    composer.AddStaticText(Lang.Get("canevents:gui-teampick-full"),
                        CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor),
                        ElementBounds.Fixed(260, y + 3, 160, 25));
                else
                    composer.AddSmallButton(Lang.Get("canevents:gui-teampick-join"), () => Pick(team),
                        ElementBounds.Fixed(EnumDialogArea.LeftFixed, 260, y, 0, 0).WithFixedPadding(10, 2),
                        EnumButtonStyle.Normal, "join" + t);
                y += 28;

                if (!string.IsNullOrEmpty(members[t]))
                {
                    string names = members[t].Replace("\n", ", ");
                    composer.AddStaticText(names, CairoFont.WhiteDetailText(), ElementBounds.Fixed(12, y, Width - 12, 22));
                    y += 24;
                }
                y += 6;
            }
            return y;
        }

        protected override double ComposeButtons(GuiComposer composer, double y) => y;

        private static int CountNames(string joined)
            => string.IsNullOrEmpty(joined) ? 0 : joined.Split('\n').Length;

        // Mirrors WaitingRoomEB.TryPick, which has the final word: counted without our own seat.
        private bool IsFull(int[] counts, int team)
        {
            if (Request.MaxImbalance <= 0) return false;
            var without = (int[])counts.Clone();
            if (Request.YourTeam >= 0 && Request.YourTeam < without.Length) without[Request.YourTeam]--;
            return without[team] - without.Min() >= Request.MaxImbalance;
        }

        private bool Pick(int team)
        {
            networkHandler.SendTeamPick(team);
            return true;
        }
    }
}
