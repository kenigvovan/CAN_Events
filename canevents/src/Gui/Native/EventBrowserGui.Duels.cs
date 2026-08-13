using System.Text;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Duels tab: who is free to fight. Clicking one opens the challenge dialog, which is where the
    // terms of that fight are set - they belong to a challenge, not to the tab.
    public partial class EventBrowserGui
    {
        /// <summary>The challenge dialog this tab hands off to. Set from the mod system, like
        /// <see cref="LoadoutPick"/>.</summary>
        public DuelChallengeGui DuelChallenge { get; set; }

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
                AddDuelCard(i, list.PlayerNames[i], list.YourSideSize);

            ComposeCards();
            ComposeRefreshButton(() => networkHandler.RequestDuelTargets());
        }

        private void AddDuelCard(int index, string name, int yourSize)
        {
            var list = networkHandler.DuelTargets.Value;
            int At(int[] values) => values != null && index < values.Length ? values[index] : 0;

            int theirSize = list?.SideSizes != null && index < list.SideSizes.Length
                ? list.SideSizes[index] : 1;

            // Uneven sides cannot be played, and the server refuses them. Saying so on the card is
            // kinder than letting the click fail: the reason is about their party, not about them.
            bool playable = theirSize == yourSize;

            var detail = new StringBuilder();
            detail.Append(theirSize > 1
                ? Lang.Get("canevents:gui-duel-party-of", theirSize)
                : Lang.Get("canevents:gui-duel-solo"));

            if (!playable)
            {
                detail.Append('\n').Append(Lang.Get("canevents:gui-duel-uneven", yourSize, theirSize));
            }
            else
            {
                // The standing score against this one player, on their own row: the first question
                // anyone asks before picking a fight is whether they usually win it.
                int wins = At(list?.WinsVs), losses = At(list?.LossesVs), draws = At(list?.DrawsVs);
                if (wins + losses + draws > 0)
                    detail.Append('\n').Append(
                        Lang.Get("canevents:gui-duel-headtohead", wins, losses, draws));
            }

            AddCard(
                name,
                detail.ToString(),
                playable ? Lang.Get("canevents:gui-duel-challenge") : null,
                playable ? Palette.Go : Palette.Idle,
                playable ? DuelClickHandler(index, name) : null);
        }

        // Captured per row: the card keeps the action, and a shared lambda would challenge whoever
        // happened to be last in the loop.
        private System.Action DuelClickHandler(int index, string name) => () =>
        {
            var list = networkHandler.DuelTargets.Value;
            int At(int[] values) => values != null && index < values.Length ? values[index] : 0;

            if (DuelChallenge == null) return;

            // The browser steps aside for the dialog and comes back if it is dismissed, the same
            // hand-off the queue-time class pick uses.
            TryClose();
            DuelChallenge.OpenFor(name, At(list?.WinsVs), At(list?.LossesVs), At(list?.DrawsVs),
                list?.Modes, list?.DisabledMutators);
        };
    }
}
