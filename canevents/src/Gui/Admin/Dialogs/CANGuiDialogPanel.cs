using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.Widgets;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Dialogs
{
    // Form in the side window next to the admin panel. One instance per opening, it holds the form state.
    public abstract class CANGuiDialogPanel
    {
        protected AdminPanelGui Gui { get; private set; }

        internal void Build(AdminPanelGui gui)
        {
            Gui = gui;
            var layout = DialogFrame.Create(gui, Width);
            gui.Composers[DialogFrame.ComposerKey] = layout.Compo;

            BuildContent(layout);

            layout.Compo
                  .AddDialogTitleBar(Title ?? "", () => Close())
                  .Compose(false);

            AfterCompose(layout.Compo);
        }

        protected abstract void BuildContent(DialogLayout l);

        // E.g. SetValue on text inputs, which needs composed elements.
        protected virtual void AfterCompose(GuiComposer compo) { }

        // Already translated.
        protected virtual string Title => null;
        protected virtual double Width => 260;

        // Writes into the current row without advancing; the row grows to fit wrapped text.
        protected static void Text(DialogLayout l, string text, double[] color = null)
        {
            CairoFont font = CairoFont.WhiteDetailText();
            if (color != null) font = font.WithColor(color);
            l.FitRowToText(text, font);
            l.Compo.AddStaticText(text, font, l.Row, l.NextKey("text"));
        }

        protected static void TextRow(DialogLayout l, string text, double[] color = null)
        {
            l.NextRow();
            Text(l, text, color);
        }

        protected static void Button(DialogLayout l, string label, Func<bool> onClick, string key = null)
        {
            var bounds = l.NextRow(8);
            l.Compo.AddButton(label, new ActionConsumable(onClick), bounds, EnumButtonStyle.Normal, key ?? l.NextKey("btn"));
        }

        // Small font: AddButton draws the big one whatever the style, and long captions spill over.
        protected static void SmallButton(DialogLayout l, ElementBounds bounds, string caption, Action onClick)
        {
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            var pressed = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            pressed.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();
            l.Compo.AddInteractiveElement(new GuiElementTextButton(l.Compo.Api, Card.Fit(caption, font, bounds.fixedWidth - 8),
                font, pressed, new ActionConsumable(() => { onClick(); return true; }), bounds, EnumButtonStyle.Small),
                l.NextKey("sbtn"));
        }

        // A piece of a row at x, for several elements side by side.
        protected static ElementBounds Child(DialogLayout l, ElementBounds row, double x, double width, double height = 26)
        {
            var bounds = ElementBounds.Fixed(row.fixedX + x, row.fixedY, width, height);
            l.Bg.WithChildren(bounds);
            return bounds;
        }

        protected void ConfirmDeclineRow(DialogLayout l, Func<bool> onConfirm,
                                         string confirmLangKey = "canevents:admin-confirm",
                                         string declineLangKey = "canevents:admin-cancel")
        {
            ElementBounds yes = l.SplitRow(out ElementBounds no);

            l.Compo.AddButton(Lang.Get(confirmLangKey), new ActionConsumable(onConfirm), yes, EnumButtonStyle.Normal, l.NextKey("yes"));
            l.Compo.AddButton(Lang.Get(declineLangKey), new ActionConsumable(() =>
            {
                Close();
                return true;
            }), no, EnumButtonStyle.Normal, l.NextKey("no"));
        }

        protected void Close() => Gui.CloseSideDialog();
    }

    public sealed class ConfirmPanel : CANGuiDialogPanel
    {
        private readonly string title;
        private readonly string question;
        private readonly Action onConfirm;
        private readonly string confirmLangKey;

        public ConfirmPanel(string title, string question, Action onConfirm,
                            string confirmLangKey = "canevents:admin-confirm")
        {
            this.title = title;
            this.question = question;
            this.onConfirm = onConfirm;
            this.confirmLangKey = confirmLangKey;
        }

        protected override string Title => title ?? Lang.Get("canevents:admin-confirm-title");

        protected override void BuildContent(DialogLayout l)
        {
            Text(l, question);

            ConfirmDeclineRow(l, () =>
            {
                Close();
                onConfirm?.Invoke();
                return true;
            }, confirmLangKey);
        }
    }

    // Pick list for a SettingKind.Choice row; applies on click.
    public sealed class ChoicePanel : CANGuiDialogPanel
    {
        private readonly SettingRow row;
        private readonly Action onPicked;

        public ChoicePanel(SettingRow row, Action onPicked = null)
        {
            this.row = row;
            this.onPicked = onPicked;
        }

        protected override string Title => row.Label;

        protected override void BuildContent(DialogLayout l)
        {
            if (row.Options == null || row.Options.Length == 0)
            {
                Text(l, Lang.Get("canevents:admin-choice-missing"));
                Button(l, Lang.Get("canevents:admin-cancel"), () => { Close(); return true; });
                return;
            }

            Text(l, Lang.Get("canevents:admin-choice-hint"), AdminColors.Muted);

            string current = row.GetText?.Invoke();
            for (int i = 0; i < row.Options.Length; i++)
            {
                SettingOption option = row.Options[i];
                bool on = string.Equals(option.Value, current, StringComparison.OrdinalIgnoreCase);
                string label = on ? "> " + option.Label + " <" : option.Label;

                Button(l, label, () =>
                {
                    row.SetText(option.Value);
                    onPicked?.Invoke();
                    Close();
                    Gui.RequestRebuild();
                    return true;
                }, "choice-" + i);
            }

            Button(l, Lang.Get("canevents:admin-cancel"), () => { Close(); return true; });
        }
    }

    // Newest first. RichTextComponent instead of VTML so '<' and '>' in server replies show as-is.
    public sealed class ActionLogPanel : CANGuiDialogPanel
    {
        private const double ViewHeight = 360;
        private const double ScrollbarWidth = 20;
        private const string TextKey = "side-log-text";
        private const string ScrollKey = "side-log-scroll";

        private ElementBounds clipBounds;
        private bool hasEntries;

        protected override string Title => Lang.Get("canevents:admin-log-title");
        protected override double Width => 420;

        protected override void BuildContent(DialogLayout l)
        {
            var log = Gui.ActionLog;
            hasEntries = log.Count > 0;
            if (!hasEntries)
            {
                Text(l, Lang.Get("canevents:admin-log-empty"), AdminColors.Muted);
            }
            else
            {
                var capi = Gui.Capi;
                var comps = new List<RichTextComponentBase>();
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    var (time, ok, text) = log[i];
                    comps.Add(new RichTextComponent(capi, time + "  ",
                        CairoFont.WhiteDetailText().WithColor(AdminColors.Muted)));
                    comps.Add(new RichTextComponent(capi, text + "\n",
                        CairoFont.WhiteDetailText().WithColor(ok ? AdminColors.Success : AdminColors.Danger)));
                }

                var row = l.Row;
                row.fixedHeight = ViewHeight;
                double textWidth = row.fixedWidth - ScrollbarWidth - 6;
                clipBounds = ElementBounds.Fixed(row.fixedX, row.fixedY, textWidth, ViewHeight);
                var textBounds = ElementBounds.Fixed(0, 0, textWidth, ViewHeight);
                clipBounds.WithChildren(textBounds);
                l.Bg.WithChildren(clipBounds);
                var scrollBounds = ElementBounds.Fixed(row.fixedX + textWidth + 6, row.fixedY, ScrollbarWidth, ViewHeight);
                l.Bg.WithChildren(scrollBounds);

                l.Compo
                    .BeginClip(clipBounds)
                    .AddRichtext(comps.ToArray(), textBounds, TextKey)
                    .EndClip()
                    .AddVerticalScrollbar(OnScrolled, scrollBounds, ScrollKey);
            }

            string close = Lang.Get("canevents:admin-close");
            var row2 = l.NextRow(8);
            double w = ButtonRow.ButtonWidth(close);
            var closeBounds = ElementBounds.Fixed(row2.fixedX + row2.fixedWidth - w, row2.fixedY, w, ButtonRow.ButtonHeight);
            l.Bg.WithChildren(closeBounds);
            l.Compo.AddButton(close, new ActionConsumable(() => { Close(); return true; }), closeBounds,
                EnumButtonStyle.Normal, l.NextKey("close"));
        }

        protected override void AfterCompose(GuiComposer compo)
        {
            if (!hasEntries) return;
            var text = compo.GetRichtext(TextKey);
            var bar = compo.GetScrollbar(ScrollKey);
            if (text == null || bar == null) return;
            // TotalHeight is scaled, the scrollbar uses unscaled.
            float total = (float)(text.TotalHeight / RuntimeEnv.GUIScale);
            text.Bounds.fixedHeight = System.Math.Max(total, ViewHeight);
            bar.SetHeights((float)ViewHeight, (float)System.Math.Max(total, ViewHeight));
        }

        private void OnScrolled(float value)
        {
            var text = Gui.Composers[DialogFrame.ComposerKey]?.GetRichtext(TextKey);
            if (text == null) return;
            text.Bounds.fixedY = -value;
            text.Bounds.CalcWorldBounds();
        }
    }
}
