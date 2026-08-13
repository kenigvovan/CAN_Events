using System.Text;
using canevents.src.Duels;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Duels tab: who is free to fight, and one click to challenge them.
    public partial class EventBrowserGui
    {
        // Stake for the next challenge, packed "code:qty;code:qty". Empty means a friendly duel -
        // which is what most challenges are, so it is the default rather than a mode to select.
        private string duelStake = "";

        private void ComposeDuelsTab()
        {
            var list = networkHandler.DuelTargets.Value;

            if (list?.PlayerNames == null || list.PlayerNames.Length == 0)
            {
                AddCard(Lang.Get("canevents:gui-duel-nobody"),
                    Lang.Get("canevents:gui-duel-nobody-detail"), accent: Palette.Idle);
                ComposeCards();
                ComposeRefreshButton(() => networkHandler.RequestDuelTargets());
                return;
            }

            AddHeader(Lang.Get("canevents:gui-duel-section", list.YourSideSize));

            for (int i = 0; i < list.PlayerNames.Length; i++)
            {
                string name = list.PlayerNames[i];
                int theirSize = list.SideSizes != null && i < list.SideSizes.Length ? list.SideSizes[i] : 1;

                AddDuelCard(name, theirSize, list.YourSideSize);
            }

            ComposeCards();
            ComposeRefreshButton(() => networkHandler.RequestDuelTargets());
        }

        private void AddDuelCard(string name, int theirSize, int yourSize)
        {
            // Uneven sides cannot be played, and the server refuses them. Saying so on the card is
            // kinder than letting the click fail: the reason is about their party, not about them.
            bool playable = theirSize == yourSize;

            var detail = new StringBuilder();
            detail.Append(theirSize > 1
                ? Lang.Get("canevents:gui-duel-party-of", theirSize)
                : Lang.Get("canevents:gui-duel-solo"));

            if (!playable)
                detail.Append('\n').Append(Lang.Get("canevents:gui-duel-uneven", yourSize, theirSize));
            else if (!string.IsNullOrWhiteSpace(duelStake))
                detail.Append('\n').Append(Lang.Get("canevents:gui-duel-will-stake",
                    GuiItemUtil.DescribePacked(capi, duelStake)));

            AddCard(
                name,
                detail.ToString(),
                playable ? Lang.Get("canevents:gui-duel-challenge") : null,
                playable ? Palette.Go : Palette.Idle,
                playable ? DuelClickHandler(name) : null);
        }

        // Captured per row: the card keeps the action, and a shared lambda would challenge whoever
        // happened to be last in the loop.
        private System.Action DuelClickHandler(string name) => () =>
        {
            bool wager = !string.IsNullOrWhiteSpace(duelStake);
            networkHandler.SendDuelChallenge(name,
                wager ? DuelKind.Wager : DuelKind.Friendly,
                wager ? duelStake : null);

            // Closing is the honest response: the answer arrives as a chat line and, if accepted, as
            // a match. Leaving the list open would invite a second challenge that only replaces the
            // first.
            TryClose();
        };
    }
}
