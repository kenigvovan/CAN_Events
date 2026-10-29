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
        private double BuildLayout(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                   HgSetupMsg s, double y)
        {
            var gui = ctx.Gui;
            var state = ctx.State;
            var net = gui.Net;
            double width = column.fixedWidth - Card.Padding * 2;

            if (buf.GenSpawnsFor != place.Id) { buf.GenSpawnsFor = place.Id; buf.GenSpawns = 0; }
            // Default to the server's tribute cap once known.
            if (buf.GenSpawns <= 0 && s != null && s.RequiredSpawns > 0) buf.GenSpawns = s.RequiredSpawns;

            string hint = Lang.Get("canevents:admin-hg-gen-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);

            bool hasData = s != null && (s.SpawnCount > 0 || (s.SpotX != null && s.SpotX.Length > 0));
            const string genKey = "hg-gen";
            bool armed = hasData && ArmedTextButton.IsArmed(state, gui, genKey);
            string armedWarn = armed
                ? Lang.Get("canevents:admin-hg-gen-armed-warn", s.SpawnCount, s.SpotX?.Length ?? 0) : null;
            double armedHeight = armedWarn != null ? ButtonRow.TextHeight(armedWarn, width) + 4 : 0;

            string genCaption = Lang.Get("canevents:admin-hg-gen");
            string genArmed = Lang.Get("canevents:admin-hg-gen-armed");
            string snapCaption = Lang.Get(buf.SnapTerrain ? "canevents:admin-hg-snap-on" : "canevents:admin-hg-snap-off");
            string previewCaption = Lang.Get(gui.Highlighter.HgPreviewOn ? "canevents:admin-hg-preview-on" : "canevents:admin-hg-preview-off");
            string genMeasure = hasData && ButtonRow.ButtonWidth(genArmed) > ButtonRow.ButtonWidth(genCaption) ? genArmed : genCaption;
            double buttonsHeight = ButtonRow.HeightFor(width, 0, genMeasure, snapCaption, previewCaption);

            double contentHeight = hintHeight + 4 + InputRowHeight + buttonsHeight + armedHeight;
            var inner = Section.Begin(ctx, column, y, "hg-layout", Lang.Get("canevents:admin-hg-layout-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            ctx.Compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), "hg-gen-hintline");
            cy += hintHeight + 4;

            double x = AddLabel(ctx, Lang.Get("canevents:admin-hg-gen-radius"), inner.fixedX, cy, "hg-gen-radius-label");
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 70, InputHeight), "hg-gen-radius",
                () => buf.Radius, v => { buf.Radius = (float)v; buf.RadiusTypedFor = place.Id; }, 4, 1000,
                Lang.Get("canevents:admin-hg-gen-radius-hint"));
            x += 70 + 16;
            x = AddLabel(ctx, Lang.Get("canevents:admin-hg-gen-spawns"), x, cy, "hg-gen-spawns-label");
            // Clamped only on edit: unseeded 0 means "event's cap" to the server.
            NumberField.AddInt(ctx, ElementBounds.Fixed(x, cy, 60, InputHeight), "hg-gen-spawns",
                () => buf.GenSpawns, v => buf.GenSpawns = v, 1, 48,
                Lang.Get("canevents:admin-hg-gen-spawns-hint"));
            cy += InputRowHeight;

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "hg-gen-btn");
            Action generate = () =>
            {
                // A rejected entry (e.g. "2" under the minimum) stays only as a draft; don't send the stale radius.
                if (state.Drafts.TryGetValue("hg-gen-radius", out string typed)
                    && !(NumberParse.TryDouble(typed, out double typedR) && typedR >= 4 && typedR <= 1000))
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-hg-gen-radius-bad", typed));
                    return;
                }
                float radius = Math.Max(4f, buf.Radius);
                int spawns = buf.GenSpawns;
                bool snap = buf.SnapTerrain;
                // Server radius is authoritative again after this.
                buf.RadiusTypedFor = -1;
                gui.Act(Lang.Get("canevents:admin-hg-log-generate", NumberParse.Format(Math.Round(radius)), spawns, place.Name ?? "?"),
                    () => net.SendGenerateHgLayout(place.Id, radius, snap, spawns));
            };
            string genTip = Lang.Get("canevents:admin-hg-gen-tip");
            if (hasData)
            {
                ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(genCaption, genArmed)), genKey,
                    genCaption, generate, genArmed, genTip);
            }
            else
            {
                buttons.Add(genCaption, generate, genTip);
            }
            buttons.Add(snapCaption, () =>
            {
                buf.SnapTerrain = !buf.SnapTerrain;
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-snap-hint"));
            buttons.Add(previewCaption, () =>
            {
                bool on = !gui.Highlighter.HgPreviewOn;
                gui.Act(Lang.Get(on ? "canevents:admin-hg-log-preview-on" : "canevents:admin-hg-log-preview-off", place.Name ?? "?"),
                    () => gui.Highlighter.SetHgPreview(place.Id, on));
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-preview-hint"));
            cy += buttonsHeight;

            if (armedWarn != null)
            {
                ctx.Compo.AddStaticText(armedWarn, CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, armedHeight - 4), "hg-gen-armed-warn");
            }
            return nextY;
        }

        private double BuildZone(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                 HgSetupMsg s, double y)
        {
            var gui = ctx.Gui;
            var net = gui.Net;
            double width = column.fixedWidth - Card.Padding * 2;
            bool zoneExists = s != null && s.ZoneSet;

            // Follow server radius changes, unless the admin is typing one.
            if (zoneExists && buf.RadiusTypedFor != place.Id
                && (buf.ZoneRadiusFor != place.Id || s.ZoneRadius != buf.ZoneRadiusSeen))
            {
                buf.ZoneRadiusFor = place.Id;
                buf.ZoneRadiusSeen = s.ZoneRadius;
                buf.Radius = s.ZoneRadius;
                ctx.State.Drafts.Remove("hg-zone-radius");
                ctx.State.Drafts.Remove("hg-gen-radius");
            }

            string status = s == null
                ? Lang.Get("canevents:admin-hg-loading")
                : zoneExists
                    ? Lang.Get("canevents:admin-hg-zone-status", NumberParse.Format(Math.Round(s.ZoneRadius, 1)))
                    : Lang.Get("canevents:admin-hg-zone-none");

            string radiusLabel = Lang.Get("canevents:admin-hg-zone-radius");
            double labelWidth = CairoFont.WhiteSmallText().GetTextExtents(radiusLabel).Width + 6;
            const double fieldWidth = 80;
            double startX = labelWidth + fieldWidth + 12;

            string applyCaption = Lang.Get("canevents:admin-hg-zone-apply");
            string hereCaption = Lang.Get("canevents:admin-hg-zone-here");
            string fitCaption = Lang.Get("canevents:admin-hg-zone-fit");
            double buttonsHeight = ButtonRow.HeightFor(width, startX, applyCaption, hereCaption, fitCaption);

            double contentHeight = LineHeight + 4 + buttonsHeight;
            var inner = Section.Begin(ctx, column, y, "hg-zone", Lang.Get("canevents:admin-hg-zone-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            var statusFont = CairoFont.WhiteSmallText().WithColor(zoneExists ? AdminColors.Success : AdminColors.Warning);
            ctx.Compo.AddStaticText(Card.Fit(status, statusFont, width), statusFont,
                ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-zone-status");
            cy += LineHeight + 4;

            AddLabel(ctx, radiusLabel, inner.fixedX, cy + 1, "hg-zone-radius-label");
            NumberField.Add(ctx, ElementBounds.Fixed(inner.fixedX + labelWidth, cy + 1, fieldWidth, InputHeight), "hg-zone-radius",
                () => buf.Radius, v => { buf.Radius = (float)v; buf.RadiusTypedFor = place.Id; }, 1, 1000,
                Lang.Get("canevents:admin-hg-zone-radius-hint"));

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, startX, "hg-zone-btn");
            buttons.Add(applyCaption, () =>
            {
                if (!zoneExists)
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-hg-zone-apply-nozone"));
                    return;
                }
                SetZone(gui, place, HgZoneMode.KeepCentre);
            }, Lang.Get(zoneExists ? "canevents:admin-hg-zone-apply-hint" : "canevents:admin-hg-zone-apply-nozone"));
            buttons.Add(hereCaption, () => SetZone(gui, place, HgZoneMode.AtFeet),
                Lang.Get("canevents:admin-hg-zone-here-hint"));
            buttons.Add(fitCaption, () =>
            {
                if (!place.HasBounds)
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-hg-zone-fit-nobounds"));
                    return;
                }
                SetZone(gui, place, HgZoneMode.FitBounds);
            }, Lang.Get(place.HasBounds ? "canevents:admin-hg-zone-fit-hint" : "canevents:admin-hg-zone-fit-nobounds"));

            return nextY;
        }

        private void SetZone(AdminPanelGui gui, PlaceListEntryMsg place, HgZoneMode mode)
        {
            float radius = Math.Max(1f, buf.Radius);
            buf.RadiusTypedFor = -1;
            string log = mode == HgZoneMode.FitBounds
                ? Lang.Get("canevents:admin-hg-log-zone-fit", place.Name ?? "?")
                : Lang.Get(mode == HgZoneMode.KeepCentre ? "canevents:admin-hg-log-zone-radius" : "canevents:admin-hg-log-zone-here",
                    NumberParse.Format(Math.Round(radius, 1)), place.Name ?? "?");
            gui.Act(log, () => gui.Net.SendSetHgZone(place.Id, radius, mode));
        }
    }
}
