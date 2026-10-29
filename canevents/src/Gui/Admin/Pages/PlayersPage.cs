using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Players tab: who may watch matches, who is barred from the queue. No fetch of its own: the list
    // walks every known player, so it comes with the tab (AdminDataRequests) and on Refresh, never per rebuild.
    public sealed class PlayersPage : CANGuiPage
    {
        private const double RowHeight = 30;
        private const double InputHeight = 26;
        private const double LabelWidth = 90;
        private const double InputWidth = 240;

        private const string NameKey = "players-name";
        private const string GrantKey = "players-grant";
        private const string BlacklistKey = "players-blacklist";

        // Survives rebuilds; cleared after an action used it.
        private string nameInput = "";

        public override void Watch(PageWatchList watch) => watch.Add(Net.PlayerAccess);

        protected override void BuildContent(PageBuildContext ctx)
        {
            var column = ctx.Column(0, ctx.Width);
            double y = column.fixedY + 4;

            y = BuildGrantCard(ctx, column, y);
            BuildTableCard(ctx, column, y);
        }

        // ---- "Pick a player" ----

        private double BuildGrantCard(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double contentHeight = RowHeight * 2 + ButtonRow.ButtonExtent;
            double height = Card.HeightAround(contentHeight);
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-players-pick-title"));

            // The dropdown covers the common case in one click. The name box stays for the case it
            // cannot serve: someone offline who has never been granted anything, and so appears
            // neither here nor in the table below.
            AddLabel(compo, inner, inner.fixedY, Lang.Get("canevents:admin-players-online"), "players-online-label");

            string[] online = OnlineNames();
            var dropBounds = inner.FlatCopy().WithFixedSize(InputWidth, InputHeight);
            dropBounds.fixedX += LabelWidth;
            dropBounds.fixedY = inner.fixedY;
            if (online.Length > 0)
            {
                // -1 while the name box holds someone not in the list (or nothing): the dropdown
                // shows no pick rather than a stale one.
                int selected = Array.FindIndex(online, n => string.Equals(n, nameInput, StringComparison.OrdinalIgnoreCase));
                compo.AddDropDown(online, online, selected, (code, on) =>
                {
                    if (!on || string.IsNullOrEmpty(code)) return;
                    nameInput = code;
                    State.Drafts.Remove(NameKey);
                    Rebuild();
                }, dropBounds, "players-online");
                Tooltip.Add(compo, Lang.Get("canevents:admin-players-online-hint"), dropBounds, "players-online-tip");
            }
            else
            {
                var none = dropBounds.FlatCopy().WithFixedSize(inner.fixedWidth - LabelWidth, 20);
                none.fixedY += 4;
                compo.AddStaticText(Lang.Get("canevents:admin-players-nobody-online"), AdminFonts.Hint, none, "players-online-none");
            }

            double nameY = inner.fixedY + RowHeight;
            AddLabel(compo, inner, nameY, Lang.Get("canevents:admin-players-name"), "players-name-label");

            var nameBounds = inner.FlatCopy().WithFixedSize(InputWidth, InputHeight);
            nameBounds.fixedX += LabelWidth;
            nameBounds.fixedY = nameY;
            NumberField.AddText(ctx, nameBounds, NameKey, () => nameInput, v =>
            {
                nameInput = v ?? "";
                // In place, not by rebuilding: a rebuild waits for the field to lose focus, and the
                // buttons should follow the box while typing.
                UpdateNameButtons(compo);
            }, Lang.Get("canevents:admin-players-name-hint"));

            var rowAnchor = inner.FlatCopy().WithFixedHeight(0);
            rowAnchor.fixedY = inner.fixedY + RowHeight * 2 + 2;
            var buttons = new ButtonRow(compo, rowAnchor, inner.fixedWidth, 0, "players-grantrow");

            string grantCaption = Lang.Get("canevents:admin-players-grant");
            compo.AddButton(grantCaption, new ActionConsumable(() =>
            {
                string name = nameInput?.Trim();
                if (string.IsNullOrEmpty(name)) return true;
                Act(Lang.Get("canevents:admin-players-log-grant", name),
                    () => Net.SendPlayerAccessAction(PlayerAccessAction.GrantSpectate, name: name));
                ClearName();
                return true;
            }), buttons.Next(ButtonRow.ButtonWidth(grantCaption)), EnumButtonStyle.Normal, GrantKey);

            string blacklistCaption = Lang.Get("canevents:admin-players-blacklist-name");
            compo.AddButton(blacklistCaption, new ActionConsumable(() =>
            {
                string name = nameInput?.Trim();
                if (string.IsNullOrEmpty(name)) return true;
                Act(Lang.Get("canevents:admin-players-log-blacklist", name),
                    () => Net.SendPlayerAccessAction(PlayerAccessAction.BlacklistAdd, name: name));
                ClearName();
                return true;
            }), buttons.Next(ButtonRow.ButtonWidth(blacklistCaption)), EnumButtonStyle.Normal, BlacklistKey);

            UpdateNameButtons(compo);
            return y + height + Card.Gap;
        }

        private static void AddLabel(GuiComposer compo, ElementBounds inner, double y, string text, string key)
        {
            var b = inner.FlatCopy().WithFixedSize(LabelWidth - 6, 20);
            b.fixedY = y + 4;
            compo.AddStaticText(Card.Fit(text, AdminFonts.Body, LabelWidth - 6), AdminFonts.Body, b, key);
        }

        // Both buttons act on the typed name, so they are off while it is empty.
        private void UpdateNameButtons(GuiComposer compo)
        {
            bool hasName = !string.IsNullOrWhiteSpace(nameInput);
            var grant = compo.GetButton(GrantKey);
            if (grant != null) grant.Enabled = hasName;
            var blacklist = compo.GetButton(BlacklistKey);
            if (blacklist != null) blacklist.Enabled = hasName;
        }

        private void ClearName()
        {
            nameInput = "";
            State.Drafts.Remove(NameKey);
            Rebuild();
        }

        // Same list the table draws, so nothing goes stale. Sorted so the dropdown doesn't reshuffle.
        private string[] OnlineNames()
        {
            var list = Net.PlayerAccess.Value;
            if (list?.Players == null) return Array.Empty<string>();

            return list.Players
                .Where(p => p.Online && !string.IsNullOrEmpty(p.PlayerName))
                .Select(p => p.PlayerName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // ---- the table ----

        private void BuildTableCard(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double height = Math.Max(120, ctx.Bottom - y);
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-players-table-title"));

            // Refresh sits in the heading's right corner, where a section keeps its fold button.
            string refreshCaption = Lang.Get("canevents:admin-players-refresh");
            double refreshWidth = CairoFont.SmallButtonText(EnumButtonStyle.Small).GetTextExtents(refreshCaption).Width + 20;
            var refresh = column.FlatCopy().WithFixedSize(refreshWidth, 20);
            refresh.fixedX += column.fixedWidth - Card.Padding - refreshWidth;
            refresh.fixedY = y + Card.Padding;
            compo.AddSmallButton(refreshCaption, new ActionConsumable(() =>
            {
                Net.RequestPlayerAccess();
                return true;
            }), refresh, EnumButtonStyle.Small, "players-refresh");

            double lineY = inner.fixedY;
            double innerBottom = inner.fixedY + inner.fixedHeight;

            var list = Net.PlayerAccess.Value;
            if (list?.Players == null)
            {
                AddLine(compo, inner, ref lineY, Lang.Get("canevents:admin-players-not-loaded"), AdminColors.Muted, "players-state");
                return;
            }
            if (list.Players.Length == 0)
            {
                AddLine(compo, inner, ref lineY, Lang.Get("canevents:admin-players-empty"), AdminColors.Muted, "players-state");
                return;
            }

            AddLine(compo, inner, ref lineY, Lang.Get("canevents:admin-players-table-hint"), AdminColors.Muted, "players-hint");
            if (list.OmittedCount > 0)
            {
                AddLine(compo, inner, ref lineY, Lang.Get("canevents:admin-players-omitted", list.OmittedCount),
                    AdminColors.Warning, "players-omitted");
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-players-col-player"), 0),
                new ColumnDef(Lang.Get("canevents:admin-players-col-role"), 90),
                new ColumnDef(Lang.Get("canevents:admin-players-col-spectate"), 110),
                new ColumnDef(Lang.Get("canevents:admin-players-col-queue"), 100),
                new ColumnDef(null, 320));

            var rows = new List<ColumnRow>(list.Players.Length);
            foreach (var p in list.Players) rows.Add(BuildRow(p));

            var anchor = inner.FlatCopy().WithFixedHeight(0);
            anchor.fixedY = lineY + 4;
            // Header row, its gap and the inset's grow eat the rest of the card below the list.
            double listHeight = Math.Max(40, innerBottom - anchor.fixedY - ColumnTable.HeaderHeight - 4 - 8);
            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = "players-access" });
        }

        private ColumnRow BuildRow(PlayerAccessEntryMsg p)
        {
            string spectate = p.MaySpectate
                ? Lang.Get(p.SpectateFromRole ? "canevents:admin-players-spectate-role" : "canevents:admin-players-yes")
                : Lang.Get("canevents:admin-players-no");
            string queue = Lang.Get(p.QueueBlacklisted ? "canevents:admin-players-blacklisted" : "canevents:admin-players-allowed");

            var row = new ColumnRow(p.PlayerName ?? "", p.RoleCode ?? "", spectate, queue, null)
                .WithColor(0, p.Online ? AdminColors.Value : AdminColors.Muted)
                .WithColor(1, AdminColors.Muted)
                .WithColor(2, p.MaySpectate ? AdminColors.Success : AdminColors.Muted)
                .WithColor(3, p.QueueBlacklisted ? AdminColors.Danger : AdminColors.Muted);

            row.WithCellTooltip(0, Lang.Get(p.Online ? "canevents:admin-players-online-state" : "canevents:admin-players-offline-state"));
            // Where the right comes from decides what "Revoke" has to do, so it is on screen rather
            // than a surprise: a role-granted right needs a per-player denial.
            if (p.MaySpectate && p.SpectateFromRole)
                row.WithCellTooltip(2, Lang.Get("canevents:admin-players-role-hint", p.RoleCode ?? "?"));

            string uid = p.PlayerUid;
            string name = p.PlayerName;

            if (p.MaySpectate)
            {
                row.WithButton(4, Lang.Get("canevents:admin-players-revoke"), () =>
                    Act(Lang.Get("canevents:admin-players-log-revoke", name),
                        () => Net.SendPlayerAccessAction(PlayerAccessAction.RevokeSpectate, uid)));
            }
            else
            {
                row.WithButton(4, Lang.Get("canevents:admin-players-allow"), () =>
                    Act(Lang.Get("canevents:admin-players-log-grant", name),
                        () => Net.SendPlayerAccessAction(PlayerAccessAction.GrantSpectate, uid)));
            }

            if (p.QueueBlacklisted)
            {
                row.WithButton(4, Lang.Get("canevents:admin-players-unblacklist"), () =>
                    Act(Lang.Get("canevents:admin-players-log-unblacklist", name),
                        () => Net.SendPlayerAccessAction(PlayerAccessAction.BlacklistRemove, uid)));
            }
            else
            {
                row.WithButton(4, Lang.Get("canevents:admin-players-blacklist"), () =>
                    Act(Lang.Get("canevents:admin-players-log-blacklist", name),
                        () => Net.SendPlayerAccessAction(PlayerAccessAction.BlacklistAdd, uid)));
            }

            return row;
        }

        // Cut short rather than wrapped; moves y down.
        private static void AddLine(GuiComposer compo, ElementBounds inner, ref double y, string text, double[] color, string key)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color);
            var b = inner.FlatCopy().WithFixedHeight(Card.LineHeight + 2);
            b.fixedY = y;
            Card.AddFitLine(compo, text, font, b, key, key + "-tip");
            y += Card.LineHeight + 3;
        }
    }
}
