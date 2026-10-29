using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages.Sections
{
    public sealed partial class HungerGamesSection
    {
        private double BuildConfig(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                   HgSetupMsg s, double y)
        {
            var gui = ctx.Gui;
            var state = ctx.State;
            double width = column.fixedWidth - Card.Padding * 2;
            const string sectionKey = "hg-cfg";
            string title = Lang.Get("canevents:admin-hg-cfg-title");

            if (!state.IsSectionOpen(sectionKey, false))
            {
                Section.Begin(ctx, column, y, sectionKey, title, 0, out double foldedNext, defaultOpen: false);
                return foldedNext;
            }

            if (string.IsNullOrEmpty(buf.CfgEventCode)) buf.CfgEventCode = s.EventCode;

            // Loot tables live on the event, not the arena: each variant has its own pool.
            var codes = s.HgEventCodes != null && s.HgEventCodes.Length > 0 ? s.HgEventCodes : new[] { s.EventCode };

            // Server answered for another variant: re-ask, once per packet.
            bool waiting = buf.CfgEventCode != s.EventCode;
            if (waiting)
            {
                if (!ReferenceEquals(buf.CfgReqAfterMsg, s))
                {
                    buf.CfgReqAfterMsg = s;
                    gui.Net.SendRequestHgSetup(place.Id, buf.CfgEventCode);
                }
            }
            else if (buf.EditLoadedFor != s.EventCode)
            {
                buf.Load(s);
            }

            string variantLabel = Lang.Get("canevents:admin-hg-variant");
            double labelWidth = CairoFont.WhiteSmallText().GetTextExtents(variantLabel).Width + 12;
            var variantCaptions = new string[codes.Length];
            for (int i = 0; i < codes.Length; i++)
                variantCaptions[i] = codes[i] == buf.CfgEventCode ? "> " + codes[i] + " <" : codes[i];
            double variantHeight = ButtonRow.HeightFor(width, labelWidth, variantCaptions);

            string allCaption = Lang.Get(buf.CfgApplyToAll ? "canevents:admin-hg-apply-all-on" : "canevents:admin-hg-apply-all-off");
            string saveCaption = Lang.Get("canevents:admin-hg-save");
            string reloadCaption = Lang.Get("canevents:admin-hg-reload");
            string reloadArmed = Lang.Get("canevents:admin-hg-reload-armed");
            string reloadMeasure = ArmedTextButton.WiderCaption(reloadCaption, reloadArmed);
            double buttonsHeight = ButtonRow.HeightFor(width, 0, allCaption, saveCaption, reloadMeasure);

            string target = buf.CfgApplyToAll
                ? Lang.Get("canevents:admin-hg-target-all", string.Join(", ", codes))
                : Lang.Get("canevents:admin-hg-target-one", s.EventCode);
            double targetHeight = ButtonRow.TextHeight(target, width);
            string hint = Lang.Get("canevents:admin-hg-cfg-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);

            string scoreLabel = Lang.Get("canevents:admin-hg-scoreboard");
            double scoreLabelWidth = CairoFont.WhiteSmallText().GetTextExtents(scoreLabel).Width + 12;
            var scoreValues = BehaviorConfigKeys.ScoreboardVisibilities;
            var scoreCaptions = new string[scoreValues.Length];
            for (int i = 0; i < scoreValues.Length; i++)
            {
                string caption = Lang.Get("canevents:admin-hg-scoreboard-" + scoreValues[i]);
                scoreCaptions[i] = scoreValues[i] == buf.ScoreboardVisibility ? "> " + caption + " <" : caption;
            }
            double scoreHeight = ButtonRow.HeightFor(width, scoreLabelWidth, scoreCaptions);

            double contentHeight = waiting
                ? variantHeight + 4 + LineHeight
                : variantHeight + 4 + targetHeight + 2 + hintHeight + 4 + scoreHeight + 6
                  + LineHeight + 4 + buttonsHeight;

            var inner = Section.Begin(ctx, column, y, sectionKey, title, contentHeight, out double nextY, defaultOpen: false);
            if (inner == null) return nextY;

            double cy = inner.fixedY;

            // Switching variant reloads the buffers, so dirty edits need a confirm click.
            ctx.Compo.AddStaticText(variantLabel, CairoFont.WhiteSmallText().WithColor(AdminColors.Label),
                ElementBounds.Fixed(inner.fixedX, cy + 6, labelWidth, LineHeight), "hg-variant-label");
            var variants = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, labelWidth, "hg-variant");
            bool dirty = buf.IsDirty;
            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                bool current = code == buf.CfgEventCode;
                Action pick = () =>
                {
                    if (current) return;
                    buf.CfgEventCode = code;
                    buf.CfgReqAfterMsg = null; // re-ask for the newly picked variant
                    gui.LogAction(true, Lang.Get("canevents:admin-hg-log-variant", code));
                    gui.RequestRebuild();
                };
                if (dirty && !current)
                {
                    ArmedTextButton.Add(ctx, variants.Next(ArmedTextButton.Width(variantCaptions[i])), "hg-variant-" + i,
                        variantCaptions[i], pick, null, Lang.Get("canevents:admin-hg-variant-dirty"));
                }
                else
                {
                    variants.Add(variantCaptions[i], pick,
                        Lang.Get(current ? "canevents:admin-hg-variant-current" : "canevents:admin-hg-variant-hint"));
                }
            }
            cy += variantHeight + 4;

            if (waiting)
            {
                ctx.Compo.AddStaticText(Lang.Get("canevents:admin-hg-cfg-loading", buf.CfgEventCode), AdminFonts.Hint,
                    ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-cfg-loading");
                return nextY;
            }

            ctx.Compo.AddStaticText(target, CairoFont.WhiteSmallText().WithColor(buf.CfgApplyToAll ? AdminColors.Warning : AdminColors.Muted),
                ElementBounds.Fixed(inner.fixedX, cy, width, targetHeight), "hg-cfg-target");
            cy += targetHeight + 2;
            ctx.Compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), "hg-cfg-hint");
            cy += hintHeight + 4;

            ctx.Compo.AddStaticText(scoreLabel, CairoFont.WhiteSmallText().WithColor(AdminColors.Label),
                ElementBounds.Fixed(inner.fixedX, cy + 6, scoreLabelWidth, LineHeight), "hg-scoreboard-label");
            var scoreRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, scoreLabelWidth, "hg-scoreboard");
            for (int i = 0; i < scoreValues.Length; i++)
            {
                string value = scoreValues[i];
                scoreRow.Add(scoreCaptions[i], () =>
                {
                    buf.ScoreboardVisibility = value;
                    gui.RequestRebuild();
                }, Lang.Get("canevents:admin-hg-scoreboard-" + value + "-hint"));
            }
            cy += scoreHeight + 6;

            Card.AddDirtyLine(ctx, inner.fixedX, cy, width, "hg-cfg-dirty", buf.IsDirty, "canevents:admin-hg-");
            cy += LineHeight + 4;

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "hg-cfg-btn");
            buttons.Add(allCaption, () =>
            {
                buf.CfgApplyToAll = !buf.CfgApplyToAll;
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-apply-all-hint", string.Join(", ", codes), s.EventCode));
            buttons.Add(saveCaption, () => Save(gui, place, s, codes), Lang.Get("canevents:admin-hg-save-hint"));
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(reloadCaption, reloadArmed)), "hg-cfg-reload",
                reloadCaption, () =>
                {
                    buf.Load(s);
                    gui.LogAction(true, Lang.Get("canevents:admin-hg-log-reload", s.EventCode ?? "?"));
                }, reloadArmed, Lang.Get("canevents:admin-hg-reload-hint"));

            double next = BuildPhases(ctx, column, place, s, codes, nextY);
            return BuildTiers(ctx, column, place, s, codes, next);
        }
    }
}
