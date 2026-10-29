using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Shared by the standalone /eventadmin inv gui window and the browser's admin tab.
    public static class InventoryAuditCards
    {
        // onOpen gets (uid, parked).
        public static List<EventCard> Holders(ClientNetworkHandler net, Action<string, bool> onOpen)
        {
            var cards = new List<EventCard>();
            var list = net.InventoryAudit.Value;

            if (list?.Rows == null)
            {
                cards.Add(Plain("canevents:gui-invaudit-loading", Palette.Idle));
                return cards;
            }
            if (list.Rows.Length == 0)
            {
                cards.Add(new EventCard
                {
                    Title = Lang.Get("canevents:gui-invaudit-nobody"),
                    Detail = Lang.Get("canevents:gui-invaudit-nobody-detail"),
                    Accent = Palette.Go
                });
                return cards;
            }

            // Parked first.
            foreach (var row in list.Rows.OrderByDescending(r => r.HasParked)
                                         .ThenByDescending(r => r.UnsettledInHistory)
                                         .ThenBy(r => r.PlayerName, StringComparer.OrdinalIgnoreCase))
                cards.Add(HolderCard(row, onOpen));

            return cards;
        }

        private static EventCard HolderCard(InventoryAuditRowMsg row, Action<string, bool> onOpen)
        {
            var detail = new List<string>();

            if (row.Unreadable) detail.Add(Lang.Get("canevents:gui-invaudit-unreadable"));

            if (row.HasParked)
                detail.Add(Lang.Get("canevents:gui-invaudit-parked-line", row.ParkedStacks,
                    AgeText(row.ParkedAgeSeconds), row.ParkedEventCode ?? "?"));

            if (row.HasActive)
                detail.Add(row.ActiveKeepLive
                    ? Lang.Get("canevents:gui-invaudit-active-owngear", row.ActiveStacks,
                        AgeText(row.ActiveAgeSeconds), row.ActiveEventCode ?? "?")
                    : Lang.Get("canevents:gui-invaudit-active-line", row.ActiveStacks,
                        AgeText(row.ActiveAgeSeconds), row.ActiveEventCode ?? "?"));

            // A snapshot held after the match is over is the bug.
            if (row.InMatch) detail.Add(Lang.Get("canevents:gui-invaudit-in-match"));
            else if (row.HasActive) detail.Add(Lang.Get("canevents:gui-invaudit-not-in-match"));

            if (!row.Online) detail.Add(Lang.Get("canevents:gui-invaudit-offline"));

            bool holdsSomething = row.HasActive || row.HasParked;
            string uid = row.PlayerUid;
            bool parked = row.HasParked;

            return new EventCard
            {
                Title = row.PlayerName ?? uid,
                Detail = detail.Count > 0
                    ? string.Join("\n", detail)
                    : Lang.Get("canevents:gui-invaudit-nothing-held"),
                BadgeText = holdsSomething ? Lang.Get("canevents:gui-invaudit-open") : null,
                Accent = row.HasParked ? Palette.Stop
                    : row.HasActive && !row.InMatch ? Palette.Live
                    : row.Unreadable ? Palette.Idle
                    : Palette.Title,
                OnClick = holdsSomething ? () => onOpen(uid, parked) : (Action)null
            };
        }

        // confirmingUid: hand-back pressed once already; the caller keeps that state.
        public static List<EventCard> Detail(ICoreClientAPI capi, ClientNetworkHandler net,
            string detailUid, bool detailParked, string confirmingUid,
            Action<string, bool> onOpenOther, Action<string> onHandBack)
        {
            var cards = new List<EventCard>();
            var detail = net.InventoryBackupDetail.Value;

            if (detail == null || detail.PlayerUid != detailUid)
            {
                cards.Add(Plain("canevents:gui-invaudit-loading", Palette.Idle));
                return cards;
            }

            if (!detail.Present)
            {
                cards.Add(new EventCard
                {
                    Title = detail.PlayerName ?? detailUid,
                    Detail = Lang.Get("canevents:gui-invaudit-gone"),
                    Accent = Palette.Idle
                });
                return cards;
            }

            var header = new List<string>
            {
                Lang.Get("canevents:gui-invaudit-detail-taken",
                    detail.EventCode ?? "?", AgeText(AgeOf(detail.SavedAtUnix)), detail.StackCount)
            };
            if (detail.MaxHealth > 0)
                header.Add(Lang.Get("canevents:gui-invaudit-detail-health",
                    Math.Round(detail.Health, 1), Math.Round(detail.MaxHealth, 1)));
            if (detail.KeepLive) header.Add(Lang.Get("canevents:gui-invaudit-detail-owngear"));

            cards.Add(new EventCard
            {
                Title = Lang.Get(detail.Parked
                    ? "canevents:gui-invaudit-detail-parked"
                    : "canevents:gui-invaudit-detail-active", detail.PlayerName ?? detailUid),
                Detail = string.Join("\n", header),
                Accent = detail.Parked ? Palette.Stop : Palette.Title,
                Icons = ResolveIcons(capi, detail)
            });

            // Player can hold both kinds (failed restore + newer match); the list always opens parked.
            string other = OtherSnapshotOf(net, detail.PlayerUid, detailParked);
            if (other != null)
            {
                string uid = detail.PlayerUid;
                bool wantParked = !detailParked;
                cards.Add(new EventCard
                {
                    Title = other,
                    BadgeText = Lang.Get("canevents:gui-invaudit-open"),
                    Accent = Palette.Info,
                    OnClick = () => onOpenOther(uid, wantParked)
                });
            }

            // Parked only: handing back an active snapshot would duplicate gear.
            if (detail.Parked && !detail.KeepLive)
            {
                bool confirming = confirmingUid == detail.PlayerUid;
                string uid = detail.PlayerUid;
                cards.Add(new EventCard
                {
                    Title = Lang.Get("canevents:gui-invaudit-handback"),
                    Detail = confirming
                        ? Lang.Get("canevents:gui-invaudit-handback-confirm")
                        : Lang.Get("canevents:gui-invaudit-handback-detail", detail.PlayerName ?? uid),
                    BadgeText = Lang.Get(confirming
                        ? "canevents:gui-invaudit-handback-yes"
                        : "canevents:gui-invaudit-handback-button"),
                    Accent = confirming ? Palette.Stop : Palette.Go,
                    OnClick = () => onHandBack(uid)
                });
            }
            else if (detail.Parked && detail.KeepLive)
            {
                cards.Add(new EventCard
                {
                    Title = Lang.Get("canevents:gui-invaudit-handback"),
                    Detail = Lang.Get("canevents:gui-invaudit-handback-owngear"),
                    Accent = Palette.Idle
                });
            }

            if (detail.ItemCodes != null && detail.ItemCodes.Length > 0)
            {
                cards.Add(new EventCard
                {
                    Title = Lang.Get("canevents:gui-invaudit-contents"),
                    IsHeader = true
                });

                for (int i = 0; i < detail.ItemCodes.Length; i++)
                {
                    int qty = detail.Quantities != null && i < detail.Quantities.Length ? detail.Quantities[i] : 1;
                    bool resolved = detail.Resolved == null || i >= detail.Resolved.Length || detail.Resolved[i];
                    cards.Add(new EventCard
                    {
                        Title = qty + " x " + NameOfCode(capi, detail.ItemCodes[i], resolved),
                        Detail = resolved
                            ? detail.ItemCodes[i]
                            : Lang.Get("canevents:gui-invaudit-unresolved", detail.ItemCodes[i]),
                        Accent = resolved ? Palette.Detail : Palette.Live
                    });
                }
            }

            return cards;
        }

        // Newest first.
        public static List<EventCard> History(ClientNetworkHandler net)
        {
            var cards = new List<EventCard>();
            var list = net.InventoryAudit.Value;

            if (list == null)
            {
                cards.Add(Plain("canevents:gui-invaudit-loading", Palette.Idle));
                return cards;
            }
            if (!list.AuditEnabled)
            {
                cards.Add(new EventCard
                {
                    Title = Lang.Get("canevents:gui-invaudit-off"),
                    Detail = Lang.Get("canevents:gui-invaudit-off-detail"),
                    Accent = Palette.Live
                });
                return cards;
            }
            if (list.History == null || list.History.Length == 0)
            {
                cards.Add(Plain("canevents:gui-invaudit-history-empty", Palette.Idle));
                return cards;
            }

            foreach (var e in list.History)
            {
                var kind = (InventoryAuditKind)e.Kind;
                var parts = new List<string>
                {
                    Lang.Get("canevents:gui-invaudit-ago", AgeText(AgeOf(e.AtUnix)))
                };
                if (!string.IsNullOrEmpty(e.EventCode)) parts.Add(e.EventCode);
                if (e.StackCount > 0) parts.Add(Lang.Get("canevents:gui-invaudit-stacks", e.StackCount));
                if (!string.IsNullOrEmpty(e.Detail)) parts.Add(e.Detail);

                cards.Add(new EventCard
                {
                    Title = KindText(kind) + "  -  " + (e.PlayerName ?? e.PlayerUid),
                    Detail = string.Join("  -  ", parts),
                    Accent = KindColor(kind)
                });
            }

            return cards;
        }

        public static string StatusLine(ClientNetworkHandler net, out bool warn)
        {
            var list = net.InventoryAudit.Value;
            warn = list != null && !list.AuditEnabled;

            if (list == null) return Lang.Get("canevents:gui-invaudit-loading");
            if (!list.AuditEnabled) return Lang.Get("canevents:gui-invaudit-off");
            return Lang.Get("canevents:gui-invaudit-checked", list.PlayersChecked, list.Unreadable);
        }

        private static EventCard Plain(string langKey, double[] accent)
            => new EventCard { Title = Lang.Get(langKey), Accent = accent };

        private static string OtherSnapshotOf(ClientNetworkHandler net, string uid, bool showingParked)
        {
            var row = net.InventoryAudit.Value?.Rows?.FirstOrDefault(r => r.PlayerUid == uid);
            if (row == null) return null;

            if (showingParked && row.HasActive)
                return Lang.Get("canevents:gui-invaudit-other-active", row.ActiveStacks);
            if (!showingParked && row.HasParked)
                return Lang.Get("canevents:gui-invaudit-other-parked", row.ParkedStacks);
            return null;
        }

        private static List<ItemStack> ResolveIcons(ICoreClientAPI capi, InventoryBackupDetailMsg detail)
        {
            if (detail.ItemCodes == null) return null;

            var icons = new List<ItemStack>();
            for (int i = 0; i < detail.ItemCodes.Length && icons.Count < 40; i++)
            {
                bool resolved = detail.Resolved == null || i >= detail.Resolved.Length || detail.Resolved[i];
                if (!resolved) continue;
                int qty = detail.Quantities != null && i < detail.Quantities.Length ? detail.Quantities[i] : 1;
                var stack = GuiItemUtil.Resolve(capi, detail.ItemCodes[i], qty);
                if (stack != null) icons.Add(stack);
            }
            return icons.Count > 0 ? icons : null;
        }

        private static string NameOfCode(ICoreClientAPI capi, string code, bool resolved)
        {
            if (!resolved) return code;
            return GuiItemUtil.Resolve(capi, code, 1)?.GetName() ?? code;
        }

        public static long AgeOf(long atUnix)
            => atUnix > 0 ? Math.Max(0, UnixTime.Now - atUnix) : -1;

        public static string AgeText(long seconds)
            => seconds >= 0 ? TimeText.Duration(seconds) : Lang.Get("canevents:gui-invaudit-undated");

        private static string KindText(InventoryAuditKind kind) => Lang.Get(kind switch
        {
            InventoryAuditKind.Taken => "canevents:gui-invaudit-kind-taken",
            InventoryAuditKind.Returned => "canevents:gui-invaudit-kind-returned",
            InventoryAuditKind.RestoreFailed => "canevents:gui-invaudit-kind-failed",
            InventoryAuditKind.RefusedOverwrite => "canevents:gui-invaudit-kind-refused",
            InventoryAuditKind.ParkedRestored => "canevents:gui-invaudit-kind-byadmin",
            _ => "canevents:gui-invaudit-kind-unknown"
        });

        private static double[] KindColor(InventoryAuditKind kind) => kind switch
        {
            InventoryAuditKind.Returned => Palette.Go,
            InventoryAuditKind.Taken => Palette.Info,
            InventoryAuditKind.ParkedRestored => Palette.Title,
            _ => Palette.Stop
        };
    }
}
