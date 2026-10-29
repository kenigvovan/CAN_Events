using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages.Sections;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // New Arena tab. Step completion is read from the place list, not from clicks.
    // Cards are borrowed from a hidden ArenasPage instance.
    public sealed partial class WizardPage : CANGuiPage
    {
        private enum Step { ChooseEvent, CreatePlace, Bounds, Spawns, Special, Activate, Done }

        private const string BodyPaneKey = "wiz-body-pane";
        private const double ChecklistWidth = 220;
        private const double LineHeight = 20;
        private const double InputHeight = 26;

        private Step step = Step.ChooseEvent;
        private string eventCode;
        private string[] types;
        private int requiredSpawns = 1;
        private long placeId = -1;
        private string placeName = "";
        private bool boundsSkipped;
        private bool needsWaitingRoom;

        private readonly HungerGamesSection hg = new HungerGamesSection();
        private readonly MurderMysterySection mm = new MurderMysterySection();

        private readonly ArenasPage editors = new ArenasPage();

        private Action<PlaceOperationResultMsg> onOperationResult;
        private Action onPlaceList;

        private int keySeq;
        private string K(string prefix) => prefix + "-" + (keySeq++);

        private bool NeedsBounds => types != null && types.Any(t => ArenasPage.BoundsRequiringTypes.Contains(t));

        // HG can have fewer pedestals than tributes, they get shared.
        private bool IsHungerGames => types != null && types.Contains(PlaceTags.HungerGames);

        private PlaceListEntryMsg WizPlace => Highlighter.FindPlace(placeId);

        private bool StepDone(Step s)
        {
            var place = WizPlace;
            switch (s)
            {
                case Step.ChooseEvent: return types != null;
                case Step.CreatePlace: return place != null;
                case Step.Bounds: return place != null && (place.HasBounds || boundsSkipped);
                // For HG any spawn is enough, the section warns about the shortfall.
                case Step.Spawns: return place != null && (IsHungerGames
                    ? place.SpawnCount > 0
                    : place.SpawnCount >= requiredSpawns);
                case Step.Special: return place != null && SpecialDone(place);
                case Step.Activate: return place != null && place.Enabled;
                default: return false;
            }
        }

        private bool SpecialDone(PlaceListEntryMsg place)
        {
            if (types == null) return true;
            // Server setup gaps first, then the stricter wizard requirements.
            if (ArenasPage.HasSetupGaps(place)) return false;
            if (types.Contains(PlaceTags.Koth) && !ArenasPage.HasKothHill(place)) return false;
            if (types.Contains(PlaceTags.Parkour) && !ArenasPage.HasParkourCheckpoints(place)) return false;
            if (types.Contains(PlaceTags.HungerGames) && !ArenasPage.HasHgZone(place)) return false;
            if (types.Contains(PlaceTags.Spleef) && !ArenasPage.HasSpleefPlatform(place)) return false;
            if (types.Contains(PlaceTags.Ctf) && ArenasPage.CountCtfPosts(place) < requiredSpawns) return false;
            if (needsWaitingRoom && string.IsNullOrEmpty(place.WaitingRoom)) return false;
            return true;
        }

        private static bool IsEditStep(Step s) => s == Step.Spawns || s == Step.Special;

        private void AdvanceFromPlaces()
        {
            if (step == Step.ChooseEvent || step == Step.Done) return;
            for (var s = Step.CreatePlace; s <= Step.Activate; s++)
            {
                if (!StepDone(s))
                {
                    // Don't auto-advance off edit steps, every edit refreshes the list. They have a Continue button.
                    if (IsEditStep(step) && s > step) return;
                    GoStep(s);
                    return;
                }
            }
            if (IsEditStep(step)) return;
            GoStep(Step.Done);
        }

        private void GoStep(Step s)
        {
            if (s == step) return;
            step = s;
            State.Scroll.Remove(BodyPaneKey);
            if (Gui != null) ScrollPane.Unfocus(Gui, BodyPaneKey);
        }

        public override void Watch(PageWatchList w)
        {
            w.Add(Net.PlaceList).Add(Net.AdminEventList).Add(Net.PlatformEditState).Add(Net.PadList);
            hg.Watch(w, Net);
            mm.Watch(w, Net);
        }

        public override void OnShown()
        {
            // New place id only comes with the operation result.
            if (onOperationResult == null) onOperationResult = OnOperationResult;
            if (onPlaceList == null) onPlaceList = AdvanceFromPlaces;

            Net.OnPlaceOperationResult += onOperationResult;
            Net.PlaceList.Changed += onPlaceList;

            hg.OnShown();
            mm.OnShown();
            Net.SendPadEdit("list");
            AdvanceFromPlaces();
        }

        private void OnOperationResult(PlaceOperationResultMsg msg)
        {
            if (msg == null || !msg.Success || msg.PlaceId <= 0) return;
            if (placeId >= 0 || step != Step.CreatePlace) return;
            placeId = msg.PlaceId;
            // The place list may have arrived before this result.
            AdvanceFromPlaces();
            Rebuild();
        }

        public override void OnHidden()
        {
            Net.OnPlaceOperationResult -= onOperationResult;
            Net.PlaceList.Changed -= onPlaceList;
            hg.OnHidden();
            ScrollPane.Remove(Gui, BodyPaneKey);
        }

        public override bool WantsPlacementMarker(out int tierIdx)
        {
            tierIdx = -1;
            if (step != Step.Spawns && step != Step.Special) return false;
            if (!HungerGamesSection.AppliesTo(WizPlace)) return false;
            return hg.WantsPlacementMarker(out tierIdx);
        }

        protected override void BuildContent(PageBuildContext ctx)
        {
            keySeq = 0;
            editors.Attach(Gui);
            ScrollPane.Remove(Gui, BodyPaneKey);

            var place = WizPlace;
            Highlighter.Update(place);

            // +12: the taller title font overlaps the tab strip otherwise.
            double y = ctx.Current.fixedY + 12;
            ctx.Compo.AddStaticText(Lang.Get("canevents:admin-wizard-title"),
                CairoFont.WhiteSmallishText().WithColor(AdminColors.Section),
                ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, 26), "wiz-title");
            y += 26;
            string intro = Lang.Get("canevents:admin-wizard-intro");
            ctx.Compo.AddStaticText(Card.Fit(intro, AdminFonts.Hint, ctx.Width), AdminFonts.Hint,
                ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, LineHeight), "wiz-intro");
            y += LineHeight + 6;

            BuildLeftColumn(ctx, place, y);

            double bodyX = ctx.Main.fixedX + ChecklistWidth + Card.ColumnGap;
            var viewport = ElementBounds.Fixed(bodyX, y, ctx.Main.fixedX + ctx.Width - bodyX, ctx.Bottom - y);
            var pane = ScrollPane.Begin(ctx, viewport, BodyPaneKey);
            var pctx = pane.Ctx;
            var column = pctx.Column(0, pane.Width);
            double end = BuildBody(pctx, column, place, 0);
            pane.End(end);
        }

        private void BuildLeftColumn(PageBuildContext ctx, PlaceListEntryMsg place, double y)
        {
            var compo = ctx.Compo;
            var column = ElementBounds.Fixed(ctx.Main.fixedX, ctx.Main.fixedY, ChecklistWidth, 0);
            double width = ChecklistWidth - Card.Padding * 2;

            var steps = new List<(Step step, string label)>
            {
                (Step.ChooseEvent, Lang.Get("canevents:admin-wizard-step-event")),
                (Step.CreatePlace, Lang.Get("canevents:admin-wizard-step-create")),
                (Step.Bounds, Lang.Get(NeedsBounds ? "canevents:admin-wizard-step-bounds" : "canevents:admin-wizard-step-bounds-optional")),
                (Step.Spawns, place != null && requiredSpawns > 1
                    ? Lang.Get("canevents:admin-wizard-step-spawns-n", Math.Min(place.SpawnCount, requiredSpawns), requiredSpawns)
                    : Lang.Get("canevents:admin-wizard-step-spawns")),
                (Step.Special, ModeSetupLabel()),
                (Step.Activate, Lang.Get("canevents:admin-wizard-step-enable")),
            };

            // Plain buttons: a scroll list here stole the mouse wheel from the body pane.
            // Buttons draw their border outside bounds, hence ButtonRow spacing.
            const double stepPitch = ButtonRow.LineStep;
            double cardHeight = Card.HeightAround(steps.Count * stepPitch - (stepPitch - ButtonRow.ButtonExtent));
            var inner = Card.Frame(compo, column, y, cardHeight, Lang.Get("canevents:admin-wizard-steps-title"));

            double sy = inner.fixedY;
            foreach (var (s, label) in steps)
            {
                bool done = StepDone(s);
                bool current = step == s;
                // No '>' here, VTML parses it as a tag and spams errors.
                string mark = done ? "[x] " : current ? "[*] " : "[ ] ";
                var target = s;
                var bounds = ElementBounds.Fixed(inner.fixedX, sy, width, ButtonRow.ButtonHeight);
                // GuiElementTextButton directly: AddButton always uses the big ButtonText font regardless of style.
                string text = mark + label;
                var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
                var pressed = CairoFont.SmallButtonText(EnumButtonStyle.Small);
                pressed.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();
                string shown = Card.Fit(text, font, width - 16);
                string key = K("wiz-step");
                compo.AddInteractiveElement(new GuiElementTextButton(compo.Api, shown, font, pressed,
                    new ActionConsumable(() =>
                    {
                        if (types == null) return true;
                        GoStep(target);
                        Rebuild();
                        return true;
                    }), bounds, EnumButtonStyle.Small), key);
                if (shown != text) Tooltip.Add(compo, text, bounds, "tip-" + key);
                sy += stepPitch;
            }
            y += cardHeight + Card.Gap;

            var buttons = new ButtonRow(compo, ElementBounds.Fixed(column.fixedX, y, ChecklistWidth, 0), ChecklistWidth, 0, "wiz-left");
            if (place != null)
            {
                string goCap = Lang.Get("canevents:admin-wizard-goto",
                    Card.Fit(place.Name ?? "?", CairoFont.ButtonText(), ChecklistWidth - ButtonRow.ButtonWidth(Lang.Get("canevents:admin-wizard-goto", ""))));
                long id = place.Id;
                var gb = buttons.Next(Math.Min(ChecklistWidth, ButtonRow.ButtonWidth(goCap)));
                string goKey = K("wiz-goto");
                string goName = place.Name ?? "?";
                compo.AddButton(goCap, new ActionConsumable(() =>
                {
                    Act(Lang.Get("canevents:admin-arenas-log-goto", goName), () => Net.SendTeleportToPlace(id));
                    return true;
                }), gb, EnumButtonStyle.Normal, goKey);
                Tooltip.Add(compo, Lang.Get("canevents:admin-wizard-goto-tip"), gb, "tip-" + goKey);
            }

            // Resets wizard progress only, the place is kept.
            string overCap = Lang.Get("canevents:admin-wizard-start-over");
            string overArmed = Lang.Get("canevents:admin-wizard-start-over-armed");
            ArmedTextButton.Add(ctx, buttons.Next(Math.Min(ChecklistWidth, ArmedTextButton.Width(overCap, overArmed))),
                "wiz-start-over", overCap, StartOver, overArmed, Lang.Get("canevents:admin-wizard-start-over-tip"));
        }

        private void StartOver()
        {
            GoStep(Step.ChooseEvent);
            eventCode = null;
            types = null;
            placeId = -1;
            placeName = "";
            boundsSkipped = false;
            needsWaitingRoom = false;
            Highlighter.ClearMarks();
            State.Drafts.Remove("wiz-name");
            Rebuild();
        }

        private string ModeSetupLabel()
        {
            if (types == null) return Lang.Get("canevents:admin-wizard-step-mode");
            if (types.Contains(PlaceTags.Koth)) return Lang.Get("canevents:admin-wizard-step-koth");
            if (types.Contains(PlaceTags.Parkour)) return Lang.Get("canevents:admin-wizard-step-parkour");
            if (types.Contains(PlaceTags.HungerGames)) return Lang.Get("canevents:admin-wizard-step-hg");
            if (types.Contains(PlaceTags.Spleef)) return Lang.Get("canevents:admin-wizard-step-spleef");
            if (types.Contains(PlaceTags.Ctf)) return Lang.Get("canevents:admin-wizard-step-ctf");
            if (needsWaitingRoom) return Lang.Get("canevents:admin-wizard-step-waitroom");
            return Lang.Get("canevents:admin-wizard-step-mode-none");
        }
    }
}
