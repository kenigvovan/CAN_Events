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
    // Step bodies, built inside the ScrollPane (y = 0 is the top of the scrolled content).
    public sealed partial class WizardPage
    {
        private double BuildBody(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            y += 4;
            switch (step)
            {
                case Step.ChooseEvent: return BuildChooseEvent(ctx, column, y);
                case Step.CreatePlace: return BuildCreatePlace(ctx, column, y);
            }

            // Could have been deleted meanwhile.
            if (place == null) return BuildNoPlace(ctx, column, y);

            switch (step)
            {
                case Step.Bounds: return BuildBoundsStep(ctx, column, place, y);
                case Step.Spawns: return BuildSpawnsStep(ctx, column, place, y);
                case Step.Special: return BuildSpecialStep(ctx, column, place, y);
                case Step.Activate: return BuildActivateStep(ctx, column, place, y);
                default: return BuildDoneStep(ctx, column, place, y);
            }
        }

        private double BuildNoPlace(PageBuildContext ctx, ElementBounds column, double y)
        {
            var c = new CardContent(column, "wiz-noplace");
            c.Text(Lang.Get(Net.PlaceList.Value == null ? "canevents:admin-arenas-loading" : "canevents:admin-wizard-no-place"),
                AdminColors.Warning);
            c.Buttons(
                new WizButton(Lang.Get("canevents:admin-arenas-refresh"), () => Net.RequestPlaceList()),
                new WizButton(Lang.Get("canevents:admin-wizard-step-create"), () =>
                {
                    placeId = -1;
                    GoStep(Step.CreatePlace);
                    Rebuild();
                }));
            return c.Draw(ctx, y, null);
        }

        private double BuildChooseEvent(PageBuildContext ctx, ElementBounds column, double y)
        {
            var events = Net.AdminEventList.Value?.Events?.Where(e => e != null).ToArray();
            var c = new CardContent(column, "wiz-ev");

            if (events == null || events.Length == 0)
            {
                c.Text(Lang.Get(events == null ? "canevents:admin-arenas-loading" : "canevents:admin-wizard-no-events"), null, AdminFonts.Hint);
                c.Buttons(new WizButton(Lang.Get("canevents:admin-arenas-refresh"), () => Net.RequestAdminEventList()));
                return c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-event-title"));
            }

            c.Text(Lang.Get("canevents:admin-wizard-event-hint"), null, AdminFonts.Hint);
            c.Gap(4);

            double listHeight = Math.Min(events.Length, 10) * ColumnTable.RowPitch + 10;
            double tableHeight = ColumnTable.HeaderHeight + 4 + listHeight + 10;
            c.Custom(tableHeight, (x, cy, width) =>
            {
                var spec = new ColumnSpec(
                    new ColumnDef(Lang.Get("canevents:admin-wizard-col-event"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-wizard-col-tags"), 220),
                    new ColumnDef(Lang.Get("canevents:admin-wizard-col-spawns"), 60, EnumTextOrientation.Right));

                var rows = new List<ColumnRow>();
                foreach (var e in events)
                {
                    var entry = e;
                    string tags = entry.EventPlaceTypes != null ? string.Join("+", entry.EventPlaceTypes) : "";
                    var row = new ColumnRow(entry.Code ?? "", tags, Math.Max(1, entry.RequiredSpawns).ToString())
                        .WithColor(0, entry.Code == eventCode ? AdminColors.Value : AdminColors.Label)
                        .WithColor(1, AdminColors.Muted)
                        .WithColor(2, AdminColors.Muted);
                    row.Selected = entry.Code == eventCode;
                    row.OnClick = () => ChooseEvent(entry);
                    rows.Add(row);
                }

                ColumnTable.Add(ctx, ElementBounds.Fixed(x, cy, width, 0), spec, rows, listHeight,
                    new ScrollableListOptions { Key = "wiz-events" });
            });
            return c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-event-title"));
        }

        private void ChooseEvent(AdminEventListEntryMsg e)
        {
            eventCode = e.Code;
            types = e.EventPlaceTypes ?? Array.Empty<string>();
            requiredSpawns = Math.Max(1, e.RequiredSpawns);
            needsWaitingRoom = e.NeedsWaitingRoom;
            placeId = -1;
            placeName = e.Code + "_arena";
            State.Drafts.Remove("wiz-name");
            boundsSkipped = false;
            GoStep(Step.CreatePlace);
            Rebuild();
        }

        private double BuildCreatePlace(PageBuildContext ctx, ElementBounds column, double y)
        {
            if (types == null) return BuildChooseEvent(ctx, column, y);

            var c = new CardContent(column, "wiz-create");
            c.Text(Lang.Get("canevents:admin-wizard-tags", types.Length > 0 ? string.Join(" + ", types) : "-"), AdminColors.Muted);
            c.Text(Lang.Get(IsHungerGames ? "canevents:admin-wizard-needs-hg" : "canevents:admin-wizard-needs", requiredSpawns)
                   + Lang.Get(NeedsBounds ? "canevents:admin-wizard-needs-bounds" : "canevents:admin-wizard-needs-bounds-optional"),
                AdminColors.Muted);
            c.Gap(6);

            c.Custom(InputHeight + 8, (x, cy, width) =>
            {
                string label = Lang.Get("canevents:admin-wizard-name");
                double lw = AdminFonts.Body.GetTextExtents(label).Width + 8;
                ctx.Compo.AddStaticText(label, AdminFonts.Body, ElementBounds.Fixed(x, cy + 4, lw, LineHeight), "wiz-name-label");
                NumberField.AddText(ctx, ElementBounds.Fixed(x + lw, cy, Math.Min(240, width - lw), InputHeight), "wiz-name",
                    () => placeName, v => placeName = v ?? "");
            });

            c.Buttons(new WizButton(Lang.Get("canevents:admin-wizard-create"), CreatePlace,
                Lang.Get("canevents:admin-wizard-create-tip")));
            if (string.IsNullOrWhiteSpace(placeName))
                c.Text(Lang.Get("canevents:admin-wizard-name-first"), null, AdminFonts.Hint);

            y = c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-create-title", eventCode ?? "?"));
            return BuildPlacePicker(ctx, column, y);
        }

        private void CreatePlace()
        {
            string name = placeName;
            if (string.IsNullOrWhiteSpace(name))
            {
                Gui.SetStatus(false, Lang.Get("canevents:admin-wizard-name-first"));
                return;
            }
            var t = types;
            // No auto spawns, they're a separate step.
            Act(Lang.Get("canevents:admin-wizard-log-create", name), () => Net.SendCreatePlace(name, t, noAutoSpawns: true));
        }

        // Reuse an existing place that has all the tags.
        private double BuildPlacePicker(PageBuildContext ctx, ElementBounds column, double y)
        {
            var places = Net.PlaceList.Value?.Places;
            var matching = places == null ? new PlaceListEntryMsg[0]
                : places.Where(p => p != null && (types.Length == 0
                    || types.All(t => p.Types != null && Array.IndexOf(p.Types, t) >= 0))).ToArray();

            var c = new CardContent(column, "wiz-pick");
            if (matching.Length == 0)
            {
                c.Text(Lang.Get(places == null || places.Length == 0 ? "canevents:admin-wizard-pick-none" : "canevents:admin-wizard-pick-nomatch"),
                    null, AdminFonts.Hint);
                return c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-pick-title"));
            }

            double listHeight = Math.Min(matching.Length, 6) * ColumnTable.RowPitch + 10;
            double tableHeight = ColumnTable.HeaderHeight + 4 + listHeight + 10;
            c.Custom(tableHeight, (x, cy, width) =>
            {
                var spec = new ColumnSpec(
                    new ColumnDef(Lang.Get("canevents:admin-arenas-col-name"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-arenas-col-types"), 160),
                    new ColumnDef(Lang.Get("canevents:admin-arenas-col-spawns"), 40, EnumTextOrientation.Right),
                    new ColumnDef(null, 90));

                var rows = new List<ColumnRow>();
                foreach (var p in matching)
                {
                    long id = p.Id;
                    var row = new ColumnRow((p.Name ?? "?") + " (#" + p.Id + ")",
                            p.Types != null ? string.Join(", ", p.Types) : "", p.SpawnCount.ToString(), null)
                        .WithColor(0, p.Enabled ? AdminColors.Value : AdminColors.Muted)
                        .WithColor(1, AdminColors.Muted)
                        .WithColor(2, AdminColors.Label);
                    row.WithButton(3, Lang.Get("canevents:admin-wizard-pick-use"), () =>
                    {
                        placeId = id;
                        AdvanceFromPlaces();
                        Rebuild();
                    }, Lang.Get("canevents:admin-wizard-pick-use-tip"));
                    rows.Add(row);
                }
                ColumnTable.Add(ctx, ElementBounds.Fixed(x, cy, width, 0), spec, rows, listHeight,
                    new ScrollableListOptions { Key = "wiz-pick-list" });
            });
            return c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-pick-title"));
        }

        private double BuildBoundsStep(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            var c = new CardContent(column, "wiz-bounds");
            c.Text(Lang.Get("canevents:admin-wizard-bounds-hint"), null, AdminFonts.Hint);
            if (!NeedsBounds) c.Text(Lang.Get("canevents:admin-wizard-bounds-optional"), null, AdminFonts.Hint);

            if (place.HasBounds)
            {
                c.Gap(4);
                c.Text(Lang.Get("canevents:admin-wizard-bounds-set", ArenasPage.FormatBoundsSize(place)), AdminColors.Success);
                c.Text(Lang.Get("canevents:admin-wizard-bounds-replace"), null, AdminFonts.Hint);
            }

            if (!NeedsBounds && !place.HasBounds)
            {
                c.Gap(4);
                c.Buttons(new WizButton(Lang.Get("canevents:admin-wizard-bounds-skip"), () =>
                {
                    boundsSkipped = true;
                    GoStep(Step.Spawns);
                    Rebuild();
                }));
            }

            y = c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-bounds-title"));
            return editors.BuildBounds(ctx, column, place, y);
        }

        private double BuildSpawnsStep(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            bool done = StepDone(Step.Spawns);
            bool hgPlace = HungerGamesSection.AppliesTo(place);
            long id = place.Id;
            string name = place.Name ?? "?";

            var c = new CardContent(column, "wiz-spawns");
            c.Text(Lang.Get("canevents:admin-wizard-spawns-count", place.SpawnCount, requiredSpawns),
                done ? AdminColors.Success : AdminColors.Warning);
            c.Text(Lang.Get(IsHungerGames ? "canevents:admin-wizard-spawns-hint-hg" : "canevents:admin-wizard-spawns-hint"),
                null, AdminFonts.Hint);

            if (place.HasBounds && !Highlighter.PlayerInsideBounds(place))
                c.Text(Lang.Get("canevents:admin-wizard-outside"), AdminColors.Warning);
            if (!IsHungerGames && place.SpawnCount > requiredSpawns)
                c.Text(Lang.Get("canevents:admin-wizard-spawns-extra"), AdminColors.Warning);
            c.Gap(4);

            c.Buttons(
                new WizButton(Lang.Get("canevents:admin-wizard-add-spawn"),
                    () => Act(Lang.Get("canevents:admin-arenas-log-add-spawn", name), () => Net.SendAddSpawnPoint(id))),
                new WizButton(Lang.Get("canevents:admin-arenas-refresh"), () => Net.RequestPlaceList()),
                new WizButton(Lang.Get("canevents:admin-wizard-continue"), () =>
                {
                    GoStep(Step.Special);
                    Rebuild();
                }, null, done));
            if (!done) c.Text(Lang.Get("canevents:admin-wizard-spawns-todo"), null, AdminFonts.Hint);
            if (hgPlace) c.Text(Lang.Get("canevents:admin-wizard-spawns-hg-gen"), null, AdminFonts.Hint);

            y = c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-spawns-title"));

            return hgPlace
                ? hg.BuildPedestals(ctx, place, y, column)
                : editors.BuildSpawns(ctx, column, place, y);
        }

        private double BuildSpecialStep(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            bool done = SpecialDone(place);
            bool anyMode = ArenasPage.ModeTypes.Any(t => PlaceHighlighter.HasType(place, t));

            var c = new CardContent(column, "wiz-special");
            c.Text(Lang.Get("canevents:admin-wizard-special-hint"), null, AdminFonts.Hint);
            if (types != null && types.Contains(PlaceTags.Ctf))
            {
                int posts = ArenasPage.CountCtfPosts(place);
                c.Text(Lang.Get("canevents:admin-wizard-ctf-posts", posts, requiredSpawns),
                    posts >= requiredSpawns ? AdminColors.Success : AdminColors.Warning);
            }
            if (needsWaitingRoom)
            {
                bool hasRoom = !string.IsNullOrEmpty(place.WaitingRoom);
                c.Text(hasRoom
                        ? Lang.Get("canevents:admin-wizard-waitroom-set", place.WaitingRoom)
                        : Lang.Get("canevents:admin-wizard-waitroom-todo"),
                    hasRoom ? AdminColors.Success : AdminColors.Warning);
                c.Text(Lang.Get("canevents:admin-wizard-waitroom-hint"), null, AdminFonts.Hint);
                long roomPlaceId = place.Id;
                string roomPlaceName = place.Name ?? "?";
                c.Buttons(new WizButton(Lang.Get("canevents:admin-wizard-waitroom-here"),
                    () => Act(Lang.Get("canevents:admin-wizard-log-waitroom", roomPlaceName),
                        () => Net.SendSetWaitingRoom(roomPlaceId))));
            }
            // Zone cards (side zones, open-match pads) follow below this card.
            else if (!anyMode && WizardEvent()?.IsOpenMatch != true)
                c.Text(Lang.Get("canevents:admin-wizard-special-none"), AdminColors.Success);
            c.Gap(4);

            bool enabled = place.Enabled;
            c.Buttons(
                new WizButton(Lang.Get("canevents:admin-wizard-continue"), () =>
                {
                    GoStep(enabled ? Step.Done : Step.Activate);
                    Rebuild();
                }, null, done),
                new WizButton(Lang.Get("canevents:admin-arenas-refresh"), () => Net.RequestPlaceList()));
            if (!done) c.Text(Lang.Get("canevents:admin-wizard-special-todo"), null, AdminFonts.Hint);

            y = c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-special-title"));

            var ev = WizardEvent();
            if (ev != null && needsWaitingRoom) y = editors.BuildZoneCard(ctx, column, y, ArenasPage.SideZonesSpec(ev, place));
            if (ev != null && ev.IsOpenMatch) y = editors.BuildZoneCard(ctx, column, y, ArenasPage.OpenPadsSpec(ev));
            return anyMode ? editors.BuildModeCards(ctx, column, place, y, hg, mm, includeWaitingRoom: false) : y;
        }

        private AdminEventListEntryMsg WizardEvent()
            => Net.AdminEventList.Value?.Events?.FirstOrDefault(e => e != null && e.Code == eventCode);

        private double BuildActivateStep(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            long id = place.Id;
            var c = new CardContent(column, "wiz-enable");
            c.Text(Lang.Get("canevents:admin-wizard-enable-hint"), AdminColors.Label);
            c.Gap(4);
            // Enable, not toggle, so a double click can't turn it back off.
            string name = place.Name ?? "?";
            c.Buttons(new WizButton(Lang.Get("canevents:admin-wizard-enable", name),
                () => Act(Lang.Get("canevents:admin-arenas-log-enable", name),
                    () => Net.SendBulkEditPlaces(new[] { id }, BulkEnabledMode.Enable))));
            return c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-enable-title"));
        }

        private double BuildDoneStep(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            var entry = Net.AdminEventList.Value?.Events?.FirstOrDefault(e => e != null && e.Code == eventCode);
            string code = eventCode;

            var c = new CardContent(column, "wiz-done");
            c.Text(Lang.Get("canevents:admin-wizard-done"), AdminColors.Success);
            c.Text((place.Name ?? "?") + " (#" + place.Id + ")", AdminColors.Value);

            if (entry != null)
            {
                EventsPage.Readiness(entry, out string label, out double[] color, out string help);
                c.Gap(4);
                c.Custom(LineHeight, (x, cy, width) =>
                {
                    string head = (code ?? "?") + ":";
                    var headFont = CairoFont.WhiteSmallText().WithColor(AdminColors.Label);
                    double hw = headFont.GetTextExtents(head).Width + 8;
                    ctx.Compo.AddStaticText(head, headFont, ElementBounds.Fixed(x, cy, hw, LineHeight), "wiz-done-code");
                    var font = CairoFont.WhiteSmallText().WithColor(color);
                    var b = ElementBounds.Fixed(x + hw, cy, Math.Max(20, width - hw), LineHeight);
                    ctx.Compo.AddStaticText(Card.Fit(label, font, b.fixedWidth), font, b, "wiz-done-ready");
                    if (help != null) Tooltip.Add(ctx.Compo, help, b, "wiz-done-ready-tip");
                });
            }
            c.Gap(6);

            bool canStart = entry != null && entry.Readiness == EventReadiness.Ready;
            c.Buttons(
                new WizButton(Lang.Get("canevents:admin-wizard-recheck"), () =>
                {
                    Net.RequestPlaceList();
                    Net.RequestAdminEventList();
                }),
                new WizButton(Lang.Get("canevents:admin-wizard-start-now", code ?? "?"),
                    () => PanelActions.ForceStart(Gui, code),
                    Lang.Get("canevents:admin-wizard-start-now-tip"), canStart));

            return c.Draw(ctx, y, Lang.Get("canevents:admin-wizard-done-title"));
        }

        private sealed class WizButton
        {
            public readonly string Caption;
            public readonly Action OnClick;
            public readonly string Tooltip;
            public readonly bool Enabled;

            public WizButton(string caption, Action onClick, string tooltip = null, bool enabled = true)
            {
                Caption = caption;
                OnClick = onClick;
                Tooltip = tooltip;
                Enabled = enabled;
            }
        }

        // Items are queued with their heights so the card can be framed before drawing.
        private sealed class CardContent
        {
            private readonly List<(double height, Action<double, double, double> draw)> items
                = new List<(double, Action<double, double, double>)>();
            private readonly ElementBounds column;
            private readonly double width;
            private readonly string keyPrefix;
            private PageBuildContext ctx;
            private int seq;

            public CardContent(ElementBounds column, string keyPrefix)
            {
                this.column = column;
                this.keyPrefix = keyPrefix;
                width = column.fixedWidth - Card.Padding * 2;
            }

            public void Text(string text, double[] color = null, CairoFont font = null)
            {
                var f = font ?? CairoFont.WhiteSmallText().WithColor(color ?? AdminColors.Value);
                double h = ButtonRow.MultiLineHeight(text, width);
                string key = keyPrefix + "-t" + (seq++);
                items.Add((h + 2, (x, y, w) => ctx.Compo.AddStaticText(text ?? "", f, ElementBounds.Fixed(x, y, w, h), key)));
            }

            public void Gap(double height) => items.Add((height, (x, y, w) => { }));

            // draw(x, y, innerWidth)
            public void Custom(double height, Action<double, double, double> draw) => items.Add((height, draw));

            public void Buttons(params WizButton[] buttons)
            {
                double h = ButtonRow.HeightFor(width, 0, buttons.Select(b => b.Caption).ToArray());
                string prefix = keyPrefix + "-b" + (seq++);
                items.Add((h, (x, y, w) =>
                {
                    var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, y, w, 0), w, 0, prefix);
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        var b = buttons[i];
                        var bounds = row.Next(ButtonRow.ButtonWidth(b.Caption));
                        string key = prefix + "-" + i;
                        var click = b.OnClick;
                        ctx.Compo.AddButton(b.Caption, new ActionConsumable(() => { click(); return true; }),
                            bounds, EnumButtonStyle.Normal, key);
                        if (!b.Enabled) ctx.Compo.GetButton(key).Enabled = false;
                        if (b.Tooltip != null) Widgets.Tooltip.Add(ctx.Compo, b.Tooltip, bounds, "tip-" + key);
                    }
                }));
            }

            // Returns y of the next card. Null title = no heading.
            public double Draw(PageBuildContext buildCtx, double y, string title)
            {
                ctx = buildCtx;
                double content = 0;
                foreach (var item in items) content += item.height;

                double height = Card.HeightAround(content, title != null);
                var inner = Card.Frame(ctx.Compo, column, y, height, title);
                double cy = inner.fixedY;
                foreach (var item in items)
                {
                    item.draw(inner.fixedX, cy, width);
                    cy += item.height;
                }
                return y + height + Card.Gap;
            }
        }
    }
}
