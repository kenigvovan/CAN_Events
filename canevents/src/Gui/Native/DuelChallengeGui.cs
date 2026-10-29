using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.Duels;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Sender side of a duel challenge; mirrors DuelPromptGui. Stake items are taken from the hand.
    public class DuelChallengeGui : GuiDialog
    {
        private readonly ClientNetworkHandler networkHandler;

        private string targetName;
        private int winsVs, lossesVs, drawsVs;
        private DuelModeMsg[] modes = Array.Empty<DuelModeMsg>();
        private string[] disabledMutators = Array.Empty<string>();

        // Reset on every open so old terms never carry over to a new opponent.
        private string modeCode;          // null = let the server pick a fitting mode
        private string forcedLoadout;     // null = each player picks their own set
        private bool noArmor, noShields, friendlyFire, randomLoadout;
        private int healthIdx, speedIdx;
        private readonly List<RewardItem> stake = new List<RewardItem>();
        private string qtyInput = "1";

        // Value "1" means off in both, so index 0 is "no mutator" and nothing is sent.
        private static readonly string[] HealthValues = { "1", "0.75", "0.5", "0.25" };
        private static readonly string[] SpeedValues = { "1", "0.75", "1.25", "1.5" };

        private const double Width = 470;

        // Stake card height from the previous compose; 86 is a first-pass guess.
        private double measuredListHeight = 86;
        private bool measuring;

        private bool challengeSent;

        private string lastSummary;

        // Reopens the browser after a cancel.
        public Action OnCancelled;

        public DuelChallengeGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
        }

        public override string ToggleKeyCombinationCode => null;

        public void OpenFor(string name, int wins, int losses, int draws,
            DuelModeMsg[] availableModes, string[] disabled)
        {
            targetName = name;
            winsVs = wins; lossesVs = losses; drawsVs = draws;
            modes = availableModes ?? Array.Empty<DuelModeMsg>();
            disabledMutators = disabled ?? Array.Empty<string>();

            modeCode = null;
            forcedLoadout = null;
            noArmor = noShields = friendlyFire = randomLoadout = false;
            healthIdx = speedIdx = 0;
            stake.Clear();
            qtyInput = "1";
            challengeSent = false;

            Compose();
            TryOpen();
        }

        private void Compose()
        {
            var composer = DialogShell.Begin(capi, "canevents-duelchallenge",
                Lang.Get("canevents:gui-challenge-title", targetName), OnDismiss);

            var font = CairoFont.WhiteSmallishText();
            double y = DialogShell.BodyTop;

            // Head-to-head record; hidden on a first meeting.
            if (winsVs + lossesVs + drawsVs > 0)
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-duel-headtohead", winsVs, lossesVs, drawsVs),
                    font.Clone().WithColor(Palette.Info), ElementBounds.Fixed(0, y, Width, 25));
                y += 30;
            }

            y = ComposeModeRow(composer, font, y);
            y = ComposeStakeRow(composer, y);
            y = ComposeMutatorRows(composer, y);

            composer.AddDynamicText(SummaryText(), CairoFont.WhiteSmallText().WithColor(Palette.Detail),
                ElementBounds.Fixed(0, y, Width, 40), "summary");
            lastSummary = SummaryText();
            y += 46;

            composer.AddSmallButton(Lang.Get("canevents:gui-duel-challenge"), OnChallenge,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-cancel"), () => { OnDismiss(); return true; },
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 150, y, 0, 0).WithFixedPadding(12, 4),
                EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();

            // A fresh composer loses input state; Compose() reruns on every stake or mode change.
            SingleComposer.GetTextInput("stakeqty")?.SetValue(qtyInput);
            SingleComposer.GetSwitch("noarmor")?.SetValue(noArmor);
            SingleComposer.GetSwitch("noshields")?.SetValue(noShields);
            SingleComposer.GetSwitch("friendlyfire")?.SetValue(friendlyFire);
            SingleComposer.GetSwitch("randomloadout")?.SetValue(randomLoadout);

            // Second pass measures the same card, so this settles after one recompose.
            if (!measuring && MeasureList())
            {
                measuring = true;
                Compose();
                measuring = false;
            }
        }

        private double ComposeModeRow(GuiComposer composer, CairoFont font, double y)
        {
            composer.AddStaticText(Lang.Get("canevents:gui-duel-mode"), font,
                ElementBounds.Fixed(0, y + 3, 80, 25));

            var codes = modes.Select(m => m.Code).ToArray();
            var values = new[] { "" }.Concat(codes).ToArray();
            var names = new[] { Lang.Get("canevents:gui-duel-mode-auto") }
                .Concat(codes.Select(c => (c ?? "").ToUpperInvariant())).ToArray();
            int idx = Math.Max(0, Array.FindIndex(values,
                v => string.Equals(v, modeCode ?? "", StringComparison.OrdinalIgnoreCase)));

            composer.AddDropDown(values, names, idx, OnModeChanged,
                ElementBounds.Fixed(84, y, 200, 25), "mode");
            y += 34;

            var sets = SetsOfSelectedMode();
            if (sets.Length > 1 && MutatorAllowed(EventMutators.Loadout))
            {
                composer.AddStaticText(Lang.Get("canevents:gui-duel-loadout"), font,
                    ElementBounds.Fixed(0, y + 3, 80, 25));

                var setValues = new[] { "" }.Concat(sets).ToArray();
                var setNames = new[] { Lang.Get("canevents:gui-duel-loadout-any") }.Concat(sets).ToArray();
                int setIdx = Math.Max(0, Array.FindIndex(setValues,
                    v => string.Equals(v, forcedLoadout ?? "", StringComparison.OrdinalIgnoreCase)));

                composer.AddDropDown(setValues, setNames, setIdx,
                    (value, _) => forcedLoadout = value == "" ? null : value,
                    ElementBounds.Fixed(84, y, 200, 25), "loadout");
                y += 34;
            }

            return y;
        }

        private double ComposeStakeRow(GuiComposer composer, double y)
        {
            string packed = ItemSetUtil.Pack(stake);
            bool empty = string.IsNullOrEmpty(packed);

            var card = EventCardRow.Single(new EventCard
            {
                Title = empty ? Lang.Get("canevents:gui-stake-none") : Lang.Get("canevents:gui-stake-title"),
                Detail = empty ? Lang.Get("canevents:gui-stake-none-detail") : null,
                Icons = empty ? null : GuiItemUtil.ResolvePacked(capi, packed),
                BadgeText = empty ? null : Lang.Get("canevents:gui-stake-badge"),
                Accent = empty ? Palette.Idle : Palette.Title
            });

            var listBounds = ElementBounds.Fixed(0, y, Width, measuredListHeight);
            composer.AddCellList(listBounds,
                (row, bounds) => new EventTileRow(capi, row, bounds), new[] { card }, "stake");

            // Padding must be set before Compose(), when the list builds its cell bounds.
            var list = composer.GetCellList<EventCardRow>("stake");
            list.unscaledCellSpacing = 6;
            list.UnscaledCellVerPadding = 0;
            list.UnscaledCellHorPadding = 4;

            y += measuredListHeight + 8;

            composer.AddStaticText("x", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(0, y + 4, 12, 25));
            composer.AddTextInput(ElementBounds.Fixed(14, y, 50, 25),
                text => qtyInput = text, CairoFont.WhiteSmallText(), "stakeqty");

            composer.AddSmallButton(Lang.Get("canevents:gui-stake-add"), OnAddFromHand,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 74, y, 0, 0).WithFixedPadding(10, 3),
                EnumButtonStyle.Normal);

            if (!empty)
            {
                composer.AddSmallButton(Lang.Get("canevents:gui-stake-clear"),
                    () => { stake.Clear(); Compose(); return true; },
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, 250, y, 0, 0).WithFixedPadding(10, 3),
                    EnumButtonStyle.Normal);
            }

            return y + 36;
        }

        // Two columns; mutators the server disables are skipped.
        private double ComposeMutatorRows(GuiComposer composer, double y)
        {
            var pending = new List<(string key, string langKey, Action<bool> set, string name)>
            {
                (EventMutators.NoArmor,       "canevents:mutator-noarmor",       on => noArmor = on,       "noarmor"),
                (EventMutators.NoShields,     "canevents:mutator-noshields",     on => noShields = on,     "noshields"),
                (EventMutators.FriendlyFire,  "canevents:mutator-friendlyfire",  on => friendlyFire = on,  "friendlyfire"),
                (EventMutators.RandomLoadout, "canevents:mutator-randomloadout", on => randomLoadout = on, "randomloadout"),
            }.Where(m => MutatorAllowed(m.key)).ToList();

            for (int i = 0; i < pending.Count; i++)
            {
                double x = (i % 2) * 235;
                double rowY = y + (i / 2) * 32;

                composer.AddSwitch(pending[i].set, ElementBounds.Fixed(x, rowY, 25, 25), pending[i].name, 25, 3);
                composer.AddStaticText(Lang.Get(pending[i].langKey), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(x + 32, rowY + 4, 195, 25));
            }
            if (pending.Count > 0) y += ((pending.Count + 1) / 2) * 32 + 6;

            var font = CairoFont.WhiteSmallText();
            if (MutatorAllowed(EventMutators.Health))
            {
                composer.AddStaticText(Lang.Get("canevents:gui-duel-health"), font,
                    ElementBounds.Fixed(0, y + 4, 80, 25));
                composer.AddDropDown(HealthValues, PercentNames(HealthValues), healthIdx,
                    (value, _) => healthIdx = Math.Max(0, Array.IndexOf(HealthValues, value)),
                    ElementBounds.Fixed(84, y, 90, 25), "health");
            }
            if (MutatorAllowed(EventMutators.Speed))
            {
                composer.AddStaticText(Lang.Get("canevents:gui-duel-speed"), font,
                    ElementBounds.Fixed(200, y + 4, 80, 25));
                composer.AddDropDown(SpeedValues, PercentNames(SpeedValues), speedIdx,
                    (value, _) => speedIdx = Math.Max(0, Array.IndexOf(SpeedValues, value)),
                    ElementBounds.Fixed(284, y, 90, 25), "speed");
            }
            if (MutatorAllowed(EventMutators.Health) || MutatorAllowed(EventMutators.Speed)) y += 36;

            return y;
        }

        // Held stack gives the full "game:item" code the server's inventory check expects.
        private bool OnAddFromHand()
        {
            if (!GuiItemUtil.TryReadHeldItem(capi, out string code, out _))
            {
                capi.TriggerIngameError(this, "emptyhand", Lang.Get("canevents:gui-stake-hold-something"));
                return true;
            }

            if (!int.TryParse(qtyInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qty)
                || qty < 1) qty = 1;

            // Not clamped to the held stack; the server checks the whole inventory.
            stake.Add(new RewardItem { Code = code, Quantity = qty });

            Compose();
            return true;
        }

        private void OnModeChanged(string value, bool selected)
        {
            modeCode = value == "" ? null : value;
            // Loadout sets differ per mode.
            forcedLoadout = null;
            // Deferred: recomposing inside the dropdown's own callback crashes the GUI.
            capi.Event.EnqueueMainThreadTask(() => { if (IsOpened()) Compose(); }, "canevents-duelterms");
        }

        private bool OnChallenge()
        {
            string packed = ItemSetUtil.Pack(stake);
            bool wager = !string.IsNullOrEmpty(packed);
            var mutators = BuildMutators();

            networkHandler.SendDuelChallenge(targetName,
                wager ? DuelKind.Wager : DuelKind.Friendly,
                wager ? packed : null,
                modeCode,
                mutators.Length == 0 ? null : mutators);

            challengeSent = true;
            TryClose();
            return true;
        }

        private void OnDismiss() => TryClose();

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            // Escape and the cross skip OnDismiss.
            if (!challengeSent) OnCancelled?.Invoke();
            challengeSent = false;
        }

        // Layout composed before TryOpen is not always the one the open dialog ends up with.
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);

            // Switches don't recompose, so the summary is refreshed here.
            string text = SummaryText();
            if (text == lastSummary) return;
            SingleComposer?.GetDynamicText("summary")?.SetNewText(text);
            lastSummary = text;
        }

        private string[] BuildMutators()
        {
            var mutators = new List<string>();
            if (noArmor) mutators.Add(EventMutators.NoArmor);
            if (noShields) mutators.Add(EventMutators.NoShields);
            if (friendlyFire) mutators.Add(EventMutators.FriendlyFire);

            // Random wins over a forced set.
            if (randomLoadout) mutators.Add(EventMutators.RandomLoadout);
            else if (forcedLoadout != null) mutators.Add(EventMutators.Loadout + ":" + forcedLoadout);

            if (healthIdx > 0) mutators.Add(EventMutators.Health + ":" + HealthValues[healthIdx]);
            if (speedIdx > 0) mutators.Add(EventMutators.Speed + ":" + SpeedValues[speedIdx]);

            // Drop anything the server disabled, even if the field is still set.
            return mutators.Where(m => MutatorAllowed(m.Split(':')[0])).ToArray();
        }

        private string SummaryText()
        {
            var parts = new List<string>
            {
                modeCode != null ? modeCode.ToUpperInvariant() : Lang.Get("canevents:gui-duel-mode-auto")
            };

            string mutators = EventMutators.Describe(BuildMutators());
            if (mutators != null) parts.Add(mutators);

            string packed = ItemSetUtil.Pack(stake);
            parts.Add(string.IsNullOrEmpty(packed)
                ? Lang.Get("canevents:gui-stake-none")
                : Lang.Get("canevents:gui-duel-will-stake", GuiItemUtil.DescribePacked(capi, packed)));

            return Lang.Get("canevents:gui-challenge-summary", string.Join(", ", parts));
        }

        private string[] SetsOfSelectedMode()
        {
            if (modeCode == null) return Array.Empty<string>();
            return modes.FirstOrDefault(m
                    => string.Equals(m.Code, modeCode, StringComparison.OrdinalIgnoreCase))
                ?.LoadoutSetNames ?? Array.Empty<string>();
        }

        private bool MutatorAllowed(string key)
            => !disabledMutators.Contains(key, StringComparer.OrdinalIgnoreCase);

        private static string[] PercentNames(string[] values)
            => values.Select(v => (int)Math.Round(
                float.Parse(v, CultureInfo.InvariantCulture) * 100) + "%").ToArray();

        // True when the real height differs and the dialog needs another compose.
        private bool MeasureList()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("stake");
            if (list == null) return false;

            double actual = list.Bounds.fixedHeight;
            if (actual <= 0 || Math.Abs(actual - measuredListHeight) < 0.5) return false;

            measuredListHeight = actual;
            return true;
        }
    }
}
