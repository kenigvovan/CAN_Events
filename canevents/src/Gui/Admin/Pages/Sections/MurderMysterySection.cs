using System;
using System.Collections.Generic;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages.Sections
{
    // MM setup card under the selected place. Spots come with the place list, settings via MmConfig.
    public sealed class MurderMysterySection
    {
        private const double LineHeight = 20;
        private const double InputHeight = 26;
        private const double SettingsTableHeight = 300;
        private const double MaxVisibleRows = 4;

        private readonly MmSettingsBuffer buf = new MmSettingsBuffer();

        public MmSettingsBuffer Buffer => buf;

        public static bool AppliesTo(PlaceListEntryMsg place) => PlaceHighlighter.HasType(place, PlaceTags.MurderMystery);

        // Host must also watch the place list (spots live there).
        public void Watch(PageWatchList watch, ClientNetworkHandler net) => watch.Add(net.MmConfig);

        // Drops unsaved edits.
        public void Invalidate() => buf.Invalidate();

        // The other host page may have replaced the shared MmConfig snapshot meanwhile.
        public void OnShown() => buf.Invalidate();

        // column null = full content width.
        public double Build(PageBuildContext ctx, PlaceListEntryMsg place, double y, ElementBounds column = null)
        {
            if (place == null) return y;
            column = column ?? ctx.Column(0, ctx.Width);

            // Cheap when nothing changed.
            ctx.Gui.Highlighter.Update(place);

            // Snapshot is shared with the other host page; an answer for another place means ours was overwritten.
            var cfg = ctx.Gui.Net.MmConfig.Value;
            bool overwritten = buf.CfgReqPlace == place.Id && cfg != null && cfg.PlaceId != place.Id
                               && !ReferenceEquals(cfg, buf.CfgAtRequest);
            if (buf.CfgReqPlace != place.Id || overwritten)
            {
                buf.CfgReqPlace = place.Id;
                buf.CfgLoadedFrom = null;
                buf.CfgAtRequest = cfg;
                ctx.Gui.Net.SendRequestMmConfig(place.Id);
            }

            y = BuildHelp(ctx, column, y);
            y = BuildSpots(ctx, column, place, y);

            bool loaded = cfg != null && cfg.PlaceId == place.Id
                          && cfg.MmEventCodes != null && cfg.MmEventCodes.Length > 0;
            if (loaded && !ReferenceEquals(buf.CfgLoadedFrom, cfg)) buf.Load(cfg);

            y = BuildSettings(ctx, column, place, cfg, loaded, y);
            if (loaded) y = BuildReward(ctx, column, place, cfg, y);
            return y;
        }

        private static double BuildHelp(PageBuildContext ctx, ElementBounds column, double y)
        {
            string text = Lang.Get("canevents:admin-mm-help-text");
            double width = column.fixedWidth - Card.Padding * 2;
            double height = ButtonRow.MultiLineHeight(text, width) + 4;

            var inner = Section.Begin(ctx, column, y, "mm-help", Lang.Get("canevents:admin-mm-help-title"),
                height, out double nextY, defaultOpen: false);
            if (inner != null)
            {
                ctx.Compo.AddStaticText(text, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(height), "mm-help-text");
            }
            return nextY;
        }

        private static double BuildSpots(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            var gui = ctx.Gui;
            var net = gui.Net;
            double width = column.fixedWidth - Card.Padding * 2;

            int count = PlaceHighlighter.MmPickupCount(place);

            // Spots are placed at the admin's feet.
            string outsideText = place.HasBounds && !gui.Highlighter.PlayerInsideBounds(place)
                ? Lang.Get("canevents:admin-mm-outside-bounds") : null;
            double outsideHeight = outsideText != null ? ButtonRow.TextHeight(outsideText, width) + 4 : 0;

            double listHeight = Math.Min(count, MaxVisibleRows) * ColumnTable.RowPitch + 10;
            double tableHeight = count > 0 ? ColumnTable.HeaderHeight + 4 + listHeight + 10 : 0;

            string markCaption = Lang.Get("canevents:admin-mm-mark");
            string clearCaption = Lang.Get("canevents:admin-mm-clear");
            string clearArmed = Lang.Get("canevents:admin-mm-clear-armed");
            string refreshCaption = Lang.Get("canevents:admin-mm-refresh");
            string clearMeasure = ArmedTextButton.WiderCaption(clearCaption, clearArmed);
            double buttonsHeight = ButtonRow.HeightFor(width, 0, markCaption, clearMeasure, refreshCaption);

            double contentHeight = LineHeight + 4 + outsideHeight + tableHeight + buttonsHeight;

            string title = Lang.Get("canevents:admin-mm-spots-title", place.Name ?? "?", place.Id);
            var inner = Section.Begin(ctx, column, y, "mm-spots", title, contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;

            string status = count == 0
                ? Lang.Get("canevents:admin-mm-spots-none")
                : Lang.Get("canevents:admin-mm-spots-count", count);
            var statusFont = CairoFont.WhiteSmallText().WithColor(count == 0 ? AdminColors.Warning : AdminColors.Success);
            ctx.Compo.AddStaticText(Card.Fit(status, statusFont, width), statusFont,
                ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "mm-spots-status");
            cy += LineHeight + 4;

            if (outsideText != null)
            {
                ctx.Compo.AddStaticText(outsideText, CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, outsideHeight - 4), "mm-spots-outside");
                cy += outsideHeight;
            }

            if (count > 0)
            {
                var spec = new ColumnSpec(
                    new ColumnDef("#", 34),
                    new ColumnDef(Lang.Get("canevents:admin-mm-col-position"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-mm-col-where"), 150),
                    new ColumnDef(null, 150));

                var rows = new List<ColumnRow>();
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    int x = place.MmPickupX[i], py = place.MmPickupY[i], z = place.MmPickupZ[i];
                    bool inside = PlaceHighlighter.PositionInsideBounds(place, x, z);

                    var row = new ColumnRow("#" + i, "(" + x + ", " + py + ", " + z + ")",
                        inside ? Lang.Get("canevents:admin-mm-spot-inside") : Lang.Get("canevents:admin-mm-spot-outside"), null);
                    row.WithColor(1, inside ? AdminColors.Value : AdminColors.Danger);
                    row.WithColor(2, inside ? AdminColors.Muted : AdminColors.Danger);

                    ArmedTextButton.AddToRow(ctx, row, 3, "mm-rm-" + place.Id + "-" + i,
                        Lang.Get("canevents:admin-mm-remove"), Lang.Get("canevents:admin-mm-remove-armed"), null,
                        () => gui.Act(Lang.Get("canevents:admin-mm-log-remove", index, place.Name ?? "?"),
                            () => net.SendRemoveMmPickup(place.Id, index)));
                    rows.Add(row);
                }

                var anchor = ElementBounds.Fixed(inner.fixedX, cy, width, 0);
                ColumnTable.Add(ctx, anchor, spec, rows, listHeight,
                    new ScrollableListOptions { Key = "mm-spots-list" });
                cy += tableHeight;
            }

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "mm-spots-btn");
            buttons.Add(markCaption, () =>
                gui.Act(Lang.Get("canevents:admin-mm-log-add", place.Name ?? "?"), () => net.SendAddMmPickup(place.Id)),
                Lang.Get("canevents:admin-mm-mark-hint"));
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(clearCaption, clearArmed)), "mm-spots-clear",
                clearCaption,
                () => gui.Act(Lang.Get("canevents:admin-mm-log-clear", place.Name ?? "?"), () => net.SendClearMmPickups(place.Id)),
                clearArmed, Lang.Get("canevents:admin-mm-clear-hint"));
            buttons.Add(refreshCaption, () => net.RequestPlaceList(), Lang.Get("canevents:admin-mm-refresh-hint"));

            return nextY;
        }

        private double BuildSettings(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                     MmConfigMsg cfg, bool loaded, double y)
        {
            var gui = ctx.Gui;
            double width = column.fixedWidth - Card.Padding * 2;
            string title = Lang.Get("canevents:admin-mm-settings-title");

            if (!loaded)
            {
                // Still loading, or the server has no MM event.
                bool none = cfg != null && cfg.PlaceId == place.Id;
                var inner0 = Section.Begin(ctx, column, y, "mm-settings", title, LineHeight, out double next0);
                if (inner0 != null)
                {
                    var font = CairoFont.WhiteSmallText().WithColor(none ? AdminColors.Danger : AdminColors.Muted);
                    ctx.Compo.AddStaticText(Lang.Get(none ? "canevents:admin-mm-no-event" : "canevents:admin-mm-loading"),
                        font, ElementBounds.Fixed(inner0.fixedX, inner0.fixedY, width, LineHeight), "mm-settings-wait");
                }
                return next0;
            }

            var codes = cfg.MmEventCodes;
            string variantLabel = Lang.Get("canevents:admin-mm-variant");
            double labelWidth = CairoFont.WhiteSmallText().GetTextExtents(variantLabel).Width + 12;
            var variantCaptions = new string[codes.Length];
            for (int i = 0; i < codes.Length; i++)
                variantCaptions[i] = codes[i] == cfg.EventCode ? "> " + codes[i] + " <" : codes[i];
            double variantHeight = ButtonRow.HeightFor(width, labelWidth, variantCaptions);

            string hint = Lang.Get("canevents:admin-mm-settings-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);

            string saveCaption = Lang.Get("canevents:admin-mm-save");
            string reloadCaption = Lang.Get("canevents:admin-mm-reload");
            string reloadArmed = Lang.Get("canevents:admin-mm-reload-armed");
            string heldCaption = Lang.Get("canevents:admin-mm-token-held");
            string reloadMeasure = ArmedTextButton.WiderCaption(reloadCaption, reloadArmed);
            double buttonsHeight = ButtonRow.HeightFor(width, 0, saveCaption, reloadMeasure, heldCaption);

            double contentHeight = variantHeight + 4 + hintHeight + 4 + SettingsTableHeight + 8
                                   + LineHeight + 4 + buttonsHeight;

            var inner = Section.Begin(ctx, column, y, "mm-settings", title, contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;

            // Switching variant reloads the buffers, so dirty edits need a confirm click.
            ctx.Compo.AddStaticText(variantLabel, CairoFont.WhiteSmallText().WithColor(AdminColors.Label),
                ElementBounds.Fixed(inner.fixedX, cy + 6, labelWidth, LineHeight), "mm-variant-label");
            var variants = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, labelWidth, "mm-variant");
            bool dirty = buf.IsDirty;
            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                bool current = code == cfg.EventCode;
                Action pick = () =>
                {
                    if (current) return;
                    gui.Act(Lang.Get("canevents:admin-mm-log-variant", code),
                        () => gui.Net.SendRequestMmConfig(place.Id, code));
                };
                string tip = current ? Lang.Get("canevents:admin-mm-variant-current") : Lang.Get("canevents:admin-mm-variant-hint");
                if (dirty && !current)
                {
                    ArmedTextButton.Add(ctx, variants.Next(ArmedTextButton.Width(variantCaptions[i])), "mm-variant-" + i,
                        variantCaptions[i], pick, null, Lang.Get("canevents:admin-mm-variant-dirty"));
                }
                else
                {
                    variants.Add(variantCaptions[i], pick, tip);
                }
            }
            cy += variantHeight + 4;

            ctx.Compo.AddStaticText(hint, AdminFonts.Hint,
                ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), "mm-settings-hint");
            cy += hintHeight + 4;

            var tableBounds = ElementBounds.Fixed(inner.fixedX, cy, width, SettingsTableHeight);
            SettingsTable.Add(ctx, tableBounds, "mm-cfg", SettingsRows());
            cy += SettingsTableHeight + 8;

            Card.AddDirtyLine(ctx, inner.fixedX, cy, width, "mm-settings-dirty", buf.IsDirty, "canevents:admin-mm-");
            cy += LineHeight + 4;

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "mm-settings-btn");
            buttons.Add(saveCaption, () => Save(gui, place, cfg), Lang.Get("canevents:admin-mm-save-hint"));
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(reloadCaption, reloadArmed)), "mm-settings-reload",
                reloadCaption, () =>
                {
                    buf.Load(cfg);
                    // Drop half-typed values too.
                    ctx.State.SettingsEditKey.Remove("mm-cfg");
                    ctx.State.SettingsEditValue.Remove("mm-cfg");
                    ctx.State.Drafts.Remove("mm-cfg-value");
                    gui.LogAction(true, Lang.Get("canevents:admin-mm-log-reload", cfg.EventCode ?? "?"));
                }, reloadArmed, Lang.Get("canevents:admin-mm-reload-hint"));
            buttons.Add(heldCaption, () =>
            {
                if (GuiItemUtil.TryReadHeldItem(gui.Capi, out var heldCode, out _))
                {
                    buf.PickupItem = heldCode;
                    gui.LogAction(true, Lang.Get("canevents:admin-mm-row-item") + " = " + heldCode);
                }
                else
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-mm-hold-first"));
                }
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-mm-token-held-hint"));

            return nextY;
        }

        // Lower bounds match the clamps in MmSettingsBuffer.ToJson.
        private List<SettingRow> SettingsRows()
        {
            var b = buf;
            string zeroKeeps = Lang.Get("canevents:admin-mm-tip-zero-keeps");

            var penalty = SettingRow.Choice("penalty", Lang.Get("canevents:admin-mm-row-penalty"),
                () => b.Penalty, v => b.Penalty = v,
                new SettingOption("death", Lang.Get("canevents:admin-mm-penalty-death")),
                new SettingOption("disarm", Lang.Get("canevents:admin-mm-penalty-disarm")),
                new SettingOption("none", Lang.Get("canevents:admin-mm-penalty-none")));
            penalty.Tooltip = Lang.Get("canevents:admin-mm-tip-penalty");

            return new List<SettingRow>
            {
                SettingRow.Group(Lang.Get("canevents:admin-mm-group-roles")),
                SettingRow.Dbl("timeLimitSeconds", Lang.Get("canevents:admin-mm-row-timelimit"),
                    () => b.TimeLimit, v => b.TimeLimit = v, 1),
                SettingRow.Int("murdererCount", Lang.Get("canevents:admin-mm-row-murderers"),
                    () => b.MurdererCount, v => b.MurdererCount = v, 0),
                SettingRow.Int("sheriffCount", Lang.Get("canevents:admin-mm-row-sheriffs"),
                    () => b.SheriffCount, v => b.SheriffCount = v, 0),
                SettingRow.Int("extraMurdererPerPlayers", Lang.Get("canevents:admin-mm-row-extra"),
                    () => b.ExtraMurdererPer, v => b.ExtraMurdererPer = v, 0,
                    tooltip: Lang.Get("canevents:admin-mm-tip-extra")),
                SettingRow.Dbl("murdererSpeed", Lang.Get("canevents:admin-mm-row-speed"),
                    () => b.MurdererSpeed, v => b.MurdererSpeed = v, 0.1,
                    tooltip: Lang.Get("canevents:admin-mm-tip-speed")),
                SettingRow.Flag("innocentsCanFight", Lang.Get("canevents:admin-mm-row-innocents-fight"),
                    () => b.InnocentsCanFight, v => b.InnocentsCanFight = v),
                SettingRow.Flag("concealWeapons", Lang.Get("canevents:admin-mm-row-conceal"),
                    () => b.ConcealWeapons, v => b.ConcealWeapons = v),
                SettingRow.Flag("lockOutfit", Lang.Get("canevents:admin-mm-row-lock-outfit"),
                    () => b.LockOutfit, v => b.LockOutfit = v),
                SettingRow.Dbl("stalemateSeconds", Lang.Get("canevents:admin-mm-row-stalemate"),
                    () => b.Stalemate, v => b.Stalemate = v, 0,
                    tooltip: Lang.Get("canevents:admin-mm-tip-stalemate")),
                SettingRow.Dbl("stalemateGraceSeconds", Lang.Get("canevents:admin-mm-row-stalemate-grace"),
                    () => b.StalemateGrace, v => b.StalemateGrace = v, 0),

                SettingRow.Group(Lang.Get("canevents:admin-mm-group-damage")),
                SettingRow.Dbl("sheriffBowDamage", Lang.Get("canevents:admin-mm-row-sheriff-bow"),
                    () => b.SheriffBowDamage, v => b.SheriffBowDamage = v, 0, tooltip: zeroKeeps),
                SettingRow.Dbl("murdererBowDamage", Lang.Get("canevents:admin-mm-row-murderer-bow"),
                    () => b.MurdererBowDamage, v => b.MurdererBowDamage = v, 0, tooltip: zeroKeeps),
                SettingRow.Dbl("bowDamage", Lang.Get("canevents:admin-mm-row-any-bow"),
                    () => b.BowDamage, v => b.BowDamage = v, 0,
                    tooltip: Lang.Get("canevents:admin-mm-tip-any-bow") + "\n" + zeroKeeps),
                SettingRow.Dbl("murdererMeleeDamage", Lang.Get("canevents:admin-mm-row-murderer-melee"),
                    () => b.MurdererMeleeDamage, v => b.MurdererMeleeDamage = v, 0, tooltip: zeroKeeps),
                penalty,
                SettingRow.Dbl("sheriffDisarmSeconds", Lang.Get("canevents:admin-mm-row-disarm-seconds"),
                    () => b.DisarmSeconds, v => b.DisarmSeconds = v, 0),
                SettingRow.Dbl("sheriffDisarmSlowness", Lang.Get("canevents:admin-mm-row-disarm-slowness"),
                    () => b.DisarmSlowness, v => b.DisarmSlowness = v, 0, 1,
                    tooltip: Lang.Get("canevents:admin-mm-tip-disarm-slowness")),
                SettingRow.Flag("sheriffDropsWeapon", Lang.Get("canevents:admin-mm-row-sheriff-drops"),
                    () => b.SheriffDropsWeapon, v => b.SheriffDropsWeapon = v),
                SettingRow.Flag("murdererDropsWeapon", Lang.Get("canevents:admin-mm-row-murderer-drops"),
                    () => b.MurdererDropsWeapon, v => b.MurdererDropsWeapon = v),

                SettingRow.Group(Lang.Get("canevents:admin-mm-group-spawns")),
                SettingRow.Text("bowPickupItem", Lang.Get("canevents:admin-mm-row-item"),
                    () => b.PickupItem, v => b.PickupItem = (v ?? "").Trim(),
                    tooltip: Lang.Get("canevents:admin-mm-tip-item")),
                SettingRow.Int("bowPickupQuantity", Lang.Get("canevents:admin-mm-row-per-spawn"),
                    () => b.PickupQuantity, v => b.PickupQuantity = v, 1,
                    tooltip: Lang.Get("canevents:admin-mm-tip-per-spawn")),
                SettingRow.Int("bowPickupRequired", Lang.Get("canevents:admin-mm-row-required"),
                    () => b.PickupRequired, v => b.PickupRequired = v, 1,
                    tooltip: Lang.Get("canevents:admin-mm-tip-required")),
                SettingRow.Dbl("bowPickupFirstSeconds", Lang.Get("canevents:admin-mm-row-first"),
                    () => b.PickupFirst, v => b.PickupFirst = v, 0),
                SettingRow.Dbl("bowPickupIntervalSeconds", Lang.Get("canevents:admin-mm-row-interval"),
                    () => b.PickupInterval, v => b.PickupInterval = v, 0),
                SettingRow.Int("bowPickupMaxActive", Lang.Get("canevents:admin-mm-row-max-active"),
                    () => b.PickupMaxActive, v => b.PickupMaxActive = v, 1),
                SettingRow.Dbl("bowPickupMinDistance", Lang.Get("canevents:admin-mm-row-min-distance"),
                    () => b.PickupMinDistance, v => b.PickupMinDistance = v, 0,
                    tooltip: Lang.Get("canevents:admin-mm-tip-min-distance")),
                SettingRow.Dbl("bowPickupLifetimeSeconds", Lang.Get("canevents:admin-mm-row-lifetime"),
                    () => b.PickupLifetime, v => b.PickupLifetime = v, 0,
                    tooltip: Lang.Get("canevents:admin-mm-tip-lifetime")),
                SettingRow.Flag("bowPickupMakesSheriff", Lang.Get("canevents:admin-mm-row-makes-sheriff"),
                    () => b.PickupMakesSheriff, v => b.PickupMakesSheriff = v)
            };
        }

        private double BuildReward(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                   MmConfigMsg cfg, double y)
        {
            var gui = ctx.Gui;
            double width = column.fixedWidth - Card.Padding * 2;

            string hint = Lang.Get("canevents:admin-mm-reward-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);

            var opts = new ItemListEditorOptions
            {
                Key = "mm-reward",
                LogName = Lang.Get("canevents:admin-mm-reward-title"),
                ShowHotbar = false,
                ShowClear = false,
                HeldAppends = true,
                MaxQuantity = 9999,
                VisibleRows = (int)MaxVisibleRows,
                EmptyText = Lang.Get("canevents:admin-mm-reward-empty"),
                EmptyColor = AdminColors.Warning
            };
            // Same payload as the settings: this Save saves everything.
            opts.Extra.Add(new ItemListButton
            {
                Caption = Lang.Get("canevents:admin-mm-save"),
                Tooltip = Lang.Get("canevents:admin-mm-save-hint"),
                OnClick = () => Save(gui, place, cfg)
            });
            double editorHeight = ItemListEditor.Height(width, buf.Reward, opts);

            double contentHeight = hintHeight + 4 + editorHeight + LineHeight + 4;

            var inner = Section.Begin(ctx, column, y, "mm-reward", Lang.Get("canevents:admin-mm-reward-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            ctx.Compo.AddStaticText(hint, AdminFonts.Hint,
                ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), "mm-reward-hint");
            cy += hintHeight + 4;

            ItemListEditor.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, editorHeight), buf.Reward, buf.RewardInput, opts);
            cy += editorHeight;

            Card.AddDirtyLine(ctx, inner.fixedX, cy, width, "mm-reward-dirty", buf.IsDirty, "canevents:admin-mm-");
            return nextY;
        }

        private void Save(AdminPanelGui gui, PlaceListEntryMsg place, MmConfigMsg cfg)
        {
            string json = buf.ToJson().ToString();
            gui.Act(Lang.Get("canevents:admin-mm-log-save", cfg.EventCode ?? "?"),
                () => gui.Net.SendSaveMmConfig(place.Id, cfg.EventCode, json));
        }

    }
}
