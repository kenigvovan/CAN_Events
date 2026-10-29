using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using canevents.src.Tournaments;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    public sealed partial class TournamentsPage
    {
        private const string SettingsTableKey = "tour-settings";
        private const double SplitLabelWidth = 26;
        private const double SplitFieldWidth = 48;
        private const double SmallButtonHeight = 22;

        // -1 = entry fee, 0.. = place prize.
        private int payoutSet = -1;

        private readonly ItemListInput payoutInput = new ItemListInput();

        private const string PayoutItemsKey = "tour-items";

        private void BuildSettingsPane(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, null);

            string nameLabel = Lang.Get("canevents:admin-tour-name");
            var labelBounds = ElementBounds.Fixed(inner.fixedX, inner.fixedY + 4, 70, 20);
            compo.AddStaticText(nameLabel, AdminFonts.Body, labelBounds, "tour-name-label");
            var nameBounds = ElementBounds.Fixed(inner.fixedX + 70, inner.fixedY, 220, FieldHeight);
            NumberField.AddText(ctx, nameBounds, "tour-name", () => tour.Name, v => tour.Name = v,
                Lang.Get("canevents:admin-tour-name-tip"));

            if (tour.AutoStart > 0 && tour.AutoStart != tour.MaxEntrants)
            {
                string caption = Lang.Get("canevents:admin-tour-autostart-max", tour.MaxEntrants);
                double w = ButtonRow.ButtonWidth(caption) - 10;
                var b = ElementBounds.Fixed(nameBounds.fixedX + nameBounds.fixedWidth + 10, inner.fixedY + 2, w, SmallButtonHeight);
                compo.AddSmallButton(caption, new ActionConsumable(() =>
                {
                    tour.AutoStart = tour.MaxEntrants;
                    Rebuild();
                    return true;
                }), b, EnumButtonStyle.Small, "tour-autostart-max");
            }

            var warnings = SettingsWarnings();
            double warnHeight = warnings.Count * LineHeight;
            double tableTop = inner.fixedY + FieldHeight + 8;
            double tableHeight = inner.fixedY + inner.fixedHeight - tableTop - warnHeight - (warnings.Count > 0 ? 4 : 0);

            var tableInner = ElementBounds.Fixed(inner.fixedX, tableTop, inner.fixedWidth, Math.Max(80, tableHeight));
            SettingsTable.Add(ctx, tableInner, SettingsTableKey, SettingsRows());

            double wy = tableTop + tableInner.fixedHeight + 4;
            for (int i = 0; i < warnings.Count; i++)
            {
                Line(ctx, inner.fixedX, wy, inner.fixedWidth, "tour-warn-" + i, warnings[i].text, warnings[i].color);
                wy += LineHeight;
            }
        }

        // Ranges match TournamentDraft.ApplyTo; values the server only floors at 0 get an upper bound here.
        private List<SettingRow> SettingsRows()
        {
            string T(string key) => Lang.Get("canevents:admin-tour-set-" + key);
            string Tip(string key) => Lang.Get("canevents:admin-tour-set-" + key + "-tip");

            return new List<SettingRow>
            {
                SettingRow.Group(Lang.Get("canevents:admin-tour-group-field")),
                SettingRow.Int("teamsize", T("teamsize"), () => tour.TeamSize, v => tour.TeamSize = v,
                    1, TournamentDraft.MaxTeamSize, Tip("teamsize")),
                SettingRow.Int("maxentrants", T("maxentrants"), () => tour.MaxEntrants, v => tour.MaxEntrants = v,
                    TournamentDraft.MinEntrantCount, TournamentDraft.MaxEntrantCount),
                SettingRow.Int("ready", T("ready"), () => tour.ReadySeconds, v => tour.ReadySeconds = v,
                    0, 600, Tip("ready")),
                SettingRow.Int("concurrent", T("concurrent"), () => tour.Concurrent, v => tour.Concurrent = v,
                    1, TournamentDraft.MaxConcurrent),
                SettingRow.Flag("manualstart", T("manualstart"), () => tour.ManualStart, v => tour.ManualStart = v,
                    Tip("manualstart")),
                SettingRow.Int("deadline", T("deadline"), () => tour.DeadlineMinutes, v => tour.DeadlineMinutes = v,
                    0, 24 * 60, Tip("deadline")),

                SettingRow.Group(Lang.Get("canevents:admin-tour-group-schedule")),
                SettingRow.Int("openin", T("openin"), () => tour.OpenInMinutes, v => tour.OpenInMinutes = v,
                    0, TournamentDraft.MaxRepeatMinutes, Tip("openin")),
                SettingRow.Int("window", T("window"), () => tour.SignupWindow, v => tour.SignupWindow = v,
                    0, TournamentDraft.MaxSignupWindowMinutes, Tip("window")),
                SettingRow.Int("repeat", T("repeat"), () => tour.RepeatMinutes, v => tour.RepeatMinutes = v,
                    0, TournamentDraft.MaxRepeatMinutes, Tip("repeat")),
                SettingRow.Flag("showbefore", T("showbefore"), () => tour.ShowBeforeSignup, v => tour.ShowBeforeSignup = v,
                    Tip("showbefore")),
                SettingRow.Text("repeatat", T("repeatat"), () => tour.RepeatAt, v => tour.RepeatAt = (v ?? "").Trim(),
                    tooltip: Tip("repeatat")),
                SettingRow.Int("autostart", T("autostart"), () => tour.AutoStart, v => tour.AutoStart = v,
                    0, TournamentDraft.MaxEntrantCount, Tip("autostart")),
                SettingRow.Int("autostartdelay", T("autostartdelay"), () => tour.AutoStartDelay, v => tour.AutoStartDelay = v,
                    0, 600, Tip("autostartdelay")),

                SettingRow.Group(Lang.Get("canevents:admin-tour-group-match")),
                SettingRow.Int("sides", T("sides"), () => tour.Sides, v => tour.Sides = v,
                    0, TournamentDraft.MaxSides, Tip("sides")),
                SettingRow.Int("advance", T("advance"), () => tour.Advance, v => tour.Advance = v,
                    1, TournamentDraft.MaxAdvance, Tip("advance")),
                SettingRow.Flag("thirdplace", T("thirdplace"), () => tour.ThirdPlace, v => tour.ThirdPlace = v),
                SettingRow.Flag("owngear", T("owngear"), () => tour.OwnGear, v => tour.OwnGear = v, Tip("owngear")),
                SettingRow.Flag("lockclass", T("lockclass"), () => tour.LockClass, v => tour.LockClass = v, Tip("lockclass")),
                SettingRow.Flag("pausesched", T("pausesched"), () => tour.PauseScheduler, v => tour.PauseScheduler = v,
                    Tip("pausesched")),
                SettingRow.Choice("seed", T("seed"), () => tour.SeedMode.ToString(), v => tour.SeedMode = v == "1" ? 1 : 0,
                    new SettingOption("0", Lang.Get("canevents:admin-tour-seed-random")),
                    new SettingOption("1", Lang.Get("canevents:admin-tour-seed-elo"))),
                SettingRow.Choice("rated", T("rated"), () => tour.RatedMode.ToString(), v => tour.RatedMode = v == "1" ? 1 : 0,
                    new SettingOption("0", Lang.Get("canevents:admin-tour-rated-event")),
                    new SettingOption("1", Lang.Get("canevents:admin-tour-rated-off"))),
            };
        }

        private List<(string text, double[] color)> SettingsWarnings()
        {
            var result = new List<(string, double[])>();
            if (!string.IsNullOrWhiteSpace(tour.RepeatAt) && TournamentManager.ParseTimesOfDay(tour.RepeatAt) == null)
                result.Add((Lang.Get("canevents:admin-tour-warn-repeatat"), AdminColors.Danger));
            if (tour.Advance > 1 && tour.Sides > 0 && tour.Advance >= tour.Sides)
                result.Add((Lang.Get("canevents:admin-tour-warn-advance"), AdminColors.Danger));
            if (tour.ThirdPlace && tour.Sides > 2)
                result.Add((Lang.Get("canevents:admin-tour-warn-thirdplace"), AdminColors.Muted));
            if (tour.OwnGear)
                result.Add((Lang.Get("canevents:admin-tour-warn-owngear"), AdminColors.Warning));
            return result;
        }

        private void BuildEventsPane(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            double colWidth = (column.fixedWidth - Card.ColumnGap) / 2;
            var left = ElementBounds.Fixed(column.fixedX, column.fixedY, colWidth, 0);
            var right = ElementBounds.Fixed(column.fixedX + colWidth + Card.ColumnGap, column.fixedY, colWidth, 0);

            // Only the player-facing event list carries class names.
            var sets = Net.EventList.Value?.Events?
                .FirstOrDefault(e => tour.EventCodes.Contains(e.GameModeCode))?.LoadoutSets;
            if (sets == null || sets.Length <= 1)
            {
                if (tour.Classes.Count > 0) tour.Classes.Clear();
                BuildEventPicker(ctx, left, y, height);
            }
            else
            {
                double eventsHeight = Math.Floor(height * 0.55);
                BuildEventPicker(ctx, left, y, eventsHeight);
                BuildClassPicker(ctx, left, y + eventsHeight + Card.Gap, height - eventsHeight - Card.Gap, sets);
            }

            BuildArenaPicker(ctx, right, y, height);
        }

        private void BuildEventPicker(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-tour-events-title"));
            var events = Net.AdminEventList.Value?.Events;

            string hint = Lang.Get("canevents:admin-tour-events-hint");
            double hintHeight = ButtonRow.TextHeight(hint, inner.fixedWidth);
            compo.AddStaticText(hint, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(hintHeight), "tour-events-hint");
            double top = inner.fixedY + hintHeight + 4;

            if (events == null || events.Length == 0)
            {
                Line(ctx, inner.fixedX, top, inner.fixedWidth, "tour-events-empty",
                    Lang.Get("canevents:admin-tour-events-missing"), AdminColors.Muted);
                return;
            }

            // A disabled event is never called, so the bracket would stall.
            var offPicks = events.Where(e => !e.Enabled && tour.EventCodes.Contains(e.Code)).Select(e => e.Code).ToList();
            double warnHeight = offPicks.Count > 0 ? LineHeight + 4 : 0;

            var rows = new List<ColumnRow>(events.Length);
            foreach (var ev in events)
            {
                string code = ev.Code;
                bool ticked = tour.EventCodes.Contains(code);
                var row = new ColumnRow(Tick(ticked), code ?? "", ev.Enabled ? "" : Lang.Get("canevents:admin-tour-event-off"))
                    .WithColor(1, ev.Enabled ? AdminColors.Value : AdminColors.Muted)
                    .WithColor(2, AdminColors.Danger);
                row.Selected = ticked;
                row.OnClick = () =>
                {
                    if (!tour.EventCodes.Remove(code)) tour.EventCodes.Add(code);
                    Rebuild();
                };
                rows.Add(row);
            }

            var spec = new ColumnSpec(
                new ColumnDef(null, 22),
                new ColumnDef(Lang.Get("canevents:admin-tour-col-event"), 0),
                new ColumnDef(null, 80));

            AddTable(ctx, inner, top, inner.fixedY + inner.fixedHeight - warnHeight, spec, rows, "tour-events");

            if (offPicks.Count > 0)
            {
                Line(ctx, inner.fixedX, inner.fixedY + inner.fixedHeight - LineHeight, inner.fixedWidth, "tour-events-off",
                    Lang.Get("canevents:admin-tour-events-off-warn", string.Join(", ", offPicks)), AdminColors.Danger);
            }
        }

        // Empty list = all classes; ticking everything collapses back to empty.
        private void BuildClassPicker(PageBuildContext ctx, ElementBounds column, double y, double height,
                                      LoadoutSetInfoMsg[] sets)
        {
            var compo = ctx.Compo;
            var classes = tour.Classes;

            string summary = classes.Count == 0
                ? Lang.Get("canevents:admin-tour-classes-all")
                : Lang.Get("canevents:admin-tour-classes-some", string.Join(", ", classes));
            string allCaption = Lang.Get("canevents:admin-tour-classes-reset");
            double allWidth = ButtonRow.ButtonWidth(allCaption) - 10;

            var titleFont = CairoFont.WhiteSmallishText();
            string title = Card.Fit(summary, titleFont, column.fixedWidth - Card.Padding * 2 - allWidth - 8);
            ElementBounds inner = Card.Frame(compo, column, y, height, title);
            if (title != summary)
                Tooltip.Add(compo, summary, ElementBounds.Fixed(inner.fixedX, y + Card.Padding, inner.fixedWidth - allWidth, 20),
                    "tip-tour-classes-title");

            var allBounds = ElementBounds.Fixed(column.fixedX + column.fixedWidth - Card.Padding - allWidth, y + Card.Padding, allWidth, 20);
            compo.AddSmallButton(allCaption, new ActionConsumable(() =>
            {
                classes.Clear();
                Rebuild();
                return true;
            }), allBounds, EnumButtonStyle.Small, "tour-classes-all");

            string hint = Lang.Get("canevents:admin-tour-classes-hint");
            double hintHeight = ButtonRow.TextHeight(hint, inner.fixedWidth);
            compo.AddStaticText(hint, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(hintHeight), "tour-classes-hint");
            double top = inner.fixedY + hintHeight + 4;

            bool oneLeft = classes.Count == 1;
            double warnHeight = oneLeft ? LineHeight + 4 : 0;

            var rows = new List<ColumnRow>(sets.Length);
            foreach (var set in sets)
            {
                string name = set.Name;
                bool allowed = classes.Count == 0 || classes.Contains(name);
                var row = new ColumnRow(Tick(allowed), name ?? "")
                    .WithColor(1, allowed ? AdminColors.Value : AdminColors.Muted);
                row.Selected = allowed;
                row.OnClick = () =>
                {
                    bool nowAllowed = !(classes.Count == 0 || classes.Contains(name));
                    if (classes.Count == 0)
                    {
                        foreach (var s in sets) if (s.Name != name) classes.Add(s.Name);
                    }
                    else if (nowAllowed) classes.Add(name);
                    else classes.Remove(name);

                    if (classes.Count == sets.Length) classes.Clear();
                    Rebuild();
                };
                rows.Add(row);
            }

            var spec = new ColumnSpec(
                new ColumnDef(null, 22),
                new ColumnDef(Lang.Get("canevents:admin-tour-col-class"), 0));
            AddTable(ctx, inner, top, inner.fixedY + inner.fixedHeight - warnHeight, spec, rows, "tour-classes");

            if (oneLeft)
            {
                Line(ctx, inner.fixedX, inner.fixedY + inner.fixedHeight - LineHeight, inner.fixedWidth, "tour-classes-one",
                    Lang.Get("canevents:admin-tour-classes-one"), AdminColors.Warning);
            }
        }

        private void BuildArenaPicker(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-tour-arenas-title"));

            string hint = Lang.Get("canevents:admin-tour-arenas-hint");
            double hintHeight = ButtonRow.TextHeight(hint, inner.fixedWidth);
            compo.AddStaticText(hint, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(hintHeight), "tour-arenas-hint");
            double top = inner.fixedY + hintHeight + 4;

            var places = Net.PlaceList.Value?.Places;
            if (places == null || places.Length == 0)
            {
                Line(ctx, inner.fixedX, top, inner.fixedWidth, "tour-arenas-empty",
                    Lang.Get("canevents:admin-tour-arenas-missing"), AdminColors.Muted);
                return;
            }

            // Place tags the chosen events need; arenas without them would stall the bracket.
            var needed = new List<string[]>();
            foreach (var ev in Net.AdminEventList.Value?.Events ?? new AdminEventListEntryMsg[0])
                if (tour.EventCodes.Contains(ev.Code)) needed.Add(ev.EventPlaceTypes ?? new string[0]);

            var rows = new List<ColumnRow>(places.Length);
            foreach (var p in places)
            {
                long placeId = p.Id;
                bool ticked = tour.Places.Contains(placeId);
                var types = p.Types ?? new string[0];
                bool usable = needed.Count == 0 || needed.Any(tags => tags.All(t => types.Contains(t)));

                var row = new ColumnRow(Tick(ticked), "#" + p.Id + " " + p.Name, usable ? "" : Lang.Get("canevents:admin-tour-arena-unusable"))
                    .WithColor(2, AdminColors.Danger);
                if (!usable) row.WithCellTooltip(2, Lang.Get("canevents:admin-tour-arena-unusable-tip"));
                if (types.Length > 0) row.WithCellTooltip(1, string.Join(", ", types));
                row.Selected = ticked;
                row.OnClick = () =>
                {
                    if (!tour.Places.Remove(placeId)) tour.Places.Add(placeId);
                    Rebuild();
                };
                rows.Add(row);
            }

            var spec = new ColumnSpec(
                new ColumnDef(null, 22),
                new ColumnDef(Lang.Get("canevents:admin-tour-col-arena"), 0),
                new ColumnDef(null, 80));
            AddTable(ctx, inner, top, inner.fixedY + inner.fixedHeight, spec, rows, "tour-arenas");
        }

        private void BuildPayoutsPane(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            double potHeight = BuildPotCard(ctx, column, y);
            BuildItemsCard(ctx, column, y + potHeight + Card.Gap, height - potHeight - Card.Gap);
        }

        // Returns the card height.
        private double BuildPotCard(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            var split = tour.Split;
            double innerWidth = column.fixedWidth - Card.Padding * 2;

            double entryWidth = SplitLabelWidth + SplitFieldWidth + 4 + SmallButtonHeight + 12;
            int perLine = Math.Max(1, (int)((innerWidth + 12) / entryWidth));
            int lines = split.Count == 0 ? 0 : (split.Count + perLine - 1) / perLine;
            double content = FieldHeight + lines * (FieldHeight + 6);
            double height = Card.HeightAround(content);

            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-tour-pot-title"));

            var rakeLabel = ElementBounds.Fixed(inner.fixedX, inner.fixedY + 4, 64, 20);
            compo.AddStaticText(Lang.Get("canevents:admin-tour-rake"), AdminFonts.Body, rakeLabel, "tour-rake-label");
            var rakeField = ElementBounds.Fixed(inner.fixedX + 64, inner.fixedY, 56, FieldHeight);
            NumberField.AddInt(ctx, rakeField, "tour-rake", () => tour.Rake, v => tour.Rake = v, 0, 100,
                Lang.Get("canevents:admin-tour-rake-tip"));

            int total = split.Sum();
            string sumText = total == 100
                ? Lang.Get("canevents:admin-tour-split-ok")
                : Lang.Get("canevents:admin-tour-split-off", total);
            double x = rakeField.fixedX + rakeField.fixedWidth + 20;

            string addCaption = Lang.Get("canevents:admin-tour-split-add");
            double addWidth = ButtonRow.ButtonWidth(addCaption) - 10;
            var addBounds = ElementBounds.Fixed(inner.fixedX + innerWidth - addWidth, inner.fixedY + 2, addWidth, SmallButtonHeight);
            compo.AddSmallButton(addCaption, new ActionConsumable(() =>
            {
                split.Add(0);
                Rebuild();
                return true;
            }), addBounds, EnumButtonStyle.Small, "tour-split-add");

            Line(ctx, x, inner.fixedY + 5, addBounds.fixedX - x - 8, "tour-split-sum",
                Lang.Get("canevents:admin-tour-split-title") + "  " + sumText,
                total == 100 ? AdminColors.Success : AdminColors.Warning);

            for (int i = 0; i < split.Count; i++)
            {
                int index = i;
                double ex = inner.fixedX + (i % perLine) * entryWidth;
                double ey = inner.fixedY + FieldHeight + 6 + (i / perLine) * (FieldHeight + 6);

                compo.AddStaticText(Place(i), AdminFonts.Body, ElementBounds.Fixed(ex, ey + 4, SplitLabelWidth, 20), "tour-split-label-" + i);
                var field = ElementBounds.Fixed(ex + SplitLabelWidth, ey, SplitFieldWidth, FieldHeight);
                NumberField.AddInt(ctx, field, "tour-split-" + i,
                    () => index < split.Count ? split[index] : 0,
                    v => { if (index < split.Count) split[index] = v; },
                    0, 100);

                var remove = ElementBounds.Fixed(field.fixedX + field.fixedWidth + 4, ey + 2, SmallButtonHeight, SmallButtonHeight);
                compo.AddSmallButton("x", new ActionConsumable(() =>
                {
                    if (index < split.Count) split.RemoveAt(index);
                    // Indices shift, so drop half-typed split drafts.
                    ClearDraftsWithPrefix("tour-split-");
                    Rebuild();
                    return true;
                }), remove, EnumButtonStyle.Small, "tour-split-x-" + i);
                Tooltip.Add(compo, Lang.Get("canevents:admin-tour-split-remove"), remove, "tip-tour-split-x-" + i);
            }

            return height;
        }

        private void BuildItemsCard(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            var compo = ctx.Compo;
            var prizes = tour.Prizes;
            if (payoutSet >= prizes.Count) payoutSet = -1;

            ElementBounds inner = Card.Frame(compo, column, y, Math.Max(120, height), Lang.Get("canevents:admin-tour-items-title"));
            double bottom = inner.fixedY + inner.fixedHeight;

            const double setsWidth = 190;
            string addCaption = Lang.Get("canevents:admin-tour-prize-add");
            string removeCaption = Lang.Get("canevents:admin-tour-prize-remove");
            double addWidth = ButtonRow.ButtonWidth(addCaption) - 10;
            var addBounds = ElementBounds.Fixed(inner.fixedX, inner.fixedY, addWidth, SmallButtonHeight);
            compo.AddSmallButton(addCaption, new ActionConsumable(() =>
            {
                prizes.Add(new List<RewardItem>());
                payoutSet = prizes.Count - 1;
                ItemListEditor.Deselect(State, payoutInput, PayoutItemsKey);
                Rebuild();
                return true;
            }), addBounds, EnumButtonStyle.Small, "tour-prize-add");

            if (prizes.Count > 0)
            {
                double removeWidth = ButtonRow.ButtonWidth(removeCaption) - 10;
                var removeBounds = ElementBounds.Fixed(addBounds.fixedX + addWidth + 6, inner.fixedY, removeWidth, SmallButtonHeight);
                compo.AddSmallButton(removeCaption, new ActionConsumable(() =>
                {
                    if (prizes.Count > 0) prizes.RemoveAt(prizes.Count - 1);
                    if (payoutSet >= prizes.Count)
                    {
                        payoutSet = -1;
                        ItemListEditor.Deselect(State, payoutInput, PayoutItemsKey);
                    }
                    Rebuild();
                    return true;
                }), removeBounds, EnumButtonStyle.Small, "tour-prize-remove");
            }

            var setRows = new List<ColumnRow>(prizes.Count + 1);
            setRows.Add(SetRow(-1, Lang.Get("canevents:admin-tour-fee"), tour.Fee));
            for (int i = 0; i < prizes.Count; i++)
                setRows.Add(SetRow(i, Lang.Get("canevents:admin-tour-prize-place", i + 1), prizes[i]));

            var setSpec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-tour-col-set"), 0),
                new ColumnDef(Lang.Get("canevents:admin-tour-col-items"), 36, EnumTextOrientation.Right));
            var setsBox = ElementBounds.Fixed(inner.fixedX, 0, setsWidth, 0);
            AddTable(ctx, setsBox, inner.fixedY + SmallButtonHeight + 8, bottom, setSpec, setRows, "tour-sets");

            var items = payoutSet < 0 ? tour.Fee : prizes[payoutSet];
            double ex = inner.fixedX + setsWidth + 12;
            var area = ElementBounds.Fixed(ex, inner.fixedY, inner.fixedX + inner.fixedWidth - ex, bottom - inner.fixedY);
            ItemListEditor.Add(ctx, area, items, payoutInput, new ItemListEditorOptions
            {
                Key = PayoutItemsKey,
                EditMode = ItemListEditMode.SelectRow,
                ShowHeld = false,
                EmptyText = Lang.Get("canevents:admin-tour-items-empty")
            });
        }

        private ColumnRow SetRow(int setIndex, string label, List<RewardItem> items)
        {
            var row = new ColumnRow(label, items.Count.ToString())
                .WithColor(1, items.Count > 0 ? AdminColors.Value : AdminColors.Muted);
            row.Selected = payoutSet == setIndex;
            row.OnClick = () =>
            {
                if (payoutSet == setIndex) return;
                payoutSet = setIndex;
                ItemListEditor.Deselect(State, payoutInput, PayoutItemsKey);
                Rebuild();
            };
            return row;
        }

        private static string Tick(bool on) => on ? "[x]" : "[  ]";

        // bottom includes the header.
        private static void AddTable(PageBuildContext ctx, ElementBounds box, double top, double bottom,
                                     ColumnSpec spec, List<ColumnRow> rows, string key)
        {
            var anchor = ElementBounds.Fixed(box.fixedX, top, box.fixedWidth, 0);
            double listTop = top + ColumnTable.HeaderHeight + 4;
            double listHeight = Math.Max(40, bottom - listTop - 6);
            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = key });
        }
    }
}
