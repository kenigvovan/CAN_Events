using System.Text;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Duels tab. Terms are set in the challenge dialog, not here.
    public partial class EventBrowserGui
    {
        // Set from the mod system, like LoadoutPick.
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

            // Server refuses uneven sides.
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

        // Per-row capture; a lambda built in the loop would bind the last row.
        private System.Action DuelClickHandler(int index, string name) => () =>
        {
            var list = networkHandler.DuelTargets.Value;
            int At(int[] values) => values != null && index < values.Length ? values[index] : 0;

            if (DuelChallenge == null) return;

            // Browser reopens if the dialog is dismissed, same as the class pick.
            TryClose();
            DuelChallenge.OpenFor(name, At(list?.WinsVs), At(list?.LossesVs), At(list?.DrawsVs),
                list?.Modes, list?.DisabledMutators);
        };
    }
}
