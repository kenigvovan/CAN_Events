using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.Widgets;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    public sealed class TourCancelPanel : CANGuiDialogPanel
    {
        private readonly long tournamentId;
        private readonly Action<bool> onCancel;
        private readonly Action onDelete;

        // onCancel gets true when stakes are refunded.
        public TourCancelPanel(long tournamentId, Action<bool> onCancel, Action onDelete)
        {
            this.tournamentId = tournamentId;
            this.onCancel = onCancel;
            this.onDelete = onDelete;
        }

        protected override string Title => Lang.Get("canevents:admin-tour-cancel-title", tournamentId);
        protected override double Width => 280;

        protected override void BuildContent(DialogLayout l)
        {
            Text(l, Lang.Get("canevents:admin-tour-cancel-question"), AdminColors.Warning);

            Button(l, Lang.Get("canevents:admin-tour-cancel-refund"), () =>
            {
                Close();
                onCancel(true);
                return true;
            }, "tour-cancel-refund");
            Button(l, Lang.Get("canevents:admin-tour-cancel-keep"), () =>
            {
                Close();
                onCancel(false);
                return true;
            }, "tour-cancel-keep");

            // Delete leaves no history row; stakes are still refunded first.
            TextRow(l, Lang.Get("canevents:admin-tour-delete-hint"), AdminColors.Muted);
            Button(l, Lang.Get("canevents:admin-tour-delete"), () =>
            {
                Close();
                onDelete();
                return true;
            }, "tour-delete");

            Button(l, Lang.Get("canevents:admin-tour-never-mind"), () => { Close(); return true; }, "tour-cancel-back");
        }
    }

    public sealed class TourMatchPanel : CANGuiDialogPanel
    {
        private readonly string title;
        private readonly string subtitle;
        private readonly List<(string id, string name)> sides;
        private readonly Action<string> onWinner;
        private readonly Action<string> onDisqualify;

        public TourMatchPanel(string title, string subtitle, List<(string id, string name)> sides,
                              Action<string> onWinner, Action<string> onDisqualify)
        {
            this.title = title;
            this.subtitle = subtitle;
            this.sides = sides;
            this.onWinner = onWinner;
            this.onDisqualify = onDisqualify;
        }

        protected override string Title => title;
        protected override double Width => 280;

        protected override void BuildContent(DialogLayout l)
        {
            Text(l, subtitle ?? "", AdminColors.Muted);

            if (sides.Count == 0)
            {
                TextRow(l, Lang.Get("canevents:admin-tour-match-empty"));
                Button(l, Lang.Get("canevents:admin-close"), () => { Close(); return true; });
                return;
            }

            for (int i = 0; i < sides.Count; i++)
            {
                string side = sides[i].id;
                Button(l, Lang.Get("canevents:admin-tour-match-winner", sides[i].name), () =>
                {
                    Close();
                    onWinner(side);
                    return true;
                }, "tour-match-win-" + i);
            }

            TextRow(l, Lang.Get("canevents:admin-tour-match-dq-hint"), AdminColors.Muted);
            for (int i = 0; i < sides.Count; i++)
            {
                string side = sides[i].id;
                Button(l, Lang.Get("canevents:admin-tour-match-dq", sides[i].name), () =>
                {
                    Close();
                    onDisqualify(side);
                    return true;
                }, "tour-match-dq-" + i);
            }

            Button(l, Lang.Get("canevents:admin-cancel"), () => { Close(); return true; }, "tour-match-back");
        }
    }
}
