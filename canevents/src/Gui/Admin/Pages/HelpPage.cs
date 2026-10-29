using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.Widgets;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Single rich text block in a scrolled clip. Widgets.Section can't be used: its static parts
    // are baked into the background texture and would not scroll. Headings are links instead.
    public sealed class HelpPage : CANGuiPage
    {
        private const string LangPrefix = "canevents:admin-help-";
        private const string StatePrefix = "help-";
        private const string TextKey = "help-text";
        private const string ScrollbarKey = "help-text-scrollbar";

        private const double TopMargin = 8;
        private const double HeaderHeight = 64;
        private const double ScrollbarWidth = 20;
        private const double ScrollbarGap = 8;

        private const double TextPadding = 6;

        private const double IndentStep = 14;

        private enum Kind
        {
            Section, Tree, Bullet, Text, Hint, Accent, Gap
        }

        private sealed class Item
        {
            public Kind Kind;

            // Lang key suffix. Fold heading is Key + "-title", state key "help-" + Key.
            public string Key;

            public bool DefaultOpen;
            public Item[] Children = Array.Empty<Item>();
        }

        private static Item Sec(string key, bool open, params Item[] children)
            => new Item { Kind = Kind.Section, Key = key, DefaultOpen = open, Children = children };

        private static Item Node(string key, params Item[] children)
            => new Item { Kind = Kind.Tree, Key = key, Children = children };

        private static Item Bullet(string key) => new Item { Kind = Kind.Bullet, Key = key };
        private static Item Para(string key) => new Item { Kind = Kind.Text, Key = key };
        private static Item Hint(string key) => new Item { Kind = Kind.Hint, Key = key };
        private static Item Sub(string key) => new Item { Kind = Kind.Accent, Key = key };
        private static readonly Item Gap = new Item { Kind = Kind.Gap };

        // key-1 .. key-count.
        private static Item[] Bullets(string key, int count)
        {
            var items = new Item[count];
            for (int i = 0; i < count; i++) items[i] = Bullet(key + "-" + (i + 1));
            return items;
        }

        private static Item[] Concat(params Item[][] parts)
        {
            var all = new List<Item>();
            foreach (var part in parts) all.AddRange(part);
            return all.ToArray();
        }

        private static readonly Item[] Guide =
        {
            Sec("quickstart", true, Concat(
                Bullets("qs", 3),
                new[] { Gap, Hint("qs-hint-1"), Hint("qs-hint-2") })),

            Sec("where", true,
                Bullet("where-dashboard"), Bullet("where-events"), Bullet("where-arenas"),
                Bullet("where-newarena"), Bullet("where-content"), Bullet("where-economy"),
                Bullet("where-tournaments"), Bullet("where-players"), Bullet("where-debug")),

            Sec("concepts", false,
                Para("concepts-intro"), Gap,
                Bullet("concepts-event"), Bullet("concepts-place"), Bullet("concepts-template"), Bullet("concepts-loadout"),
                Gap, Para("concepts-types"), Gap,
                Sub("bounds-title"), Para("bounds-intro"),
                Bullet("bounds-1"), Bullet("bounds-2"), Bullet("bounds-3"), Bullet("bounds-4"),
                Para("bounds-outro")),

            Sec("workflows", false,
                Node("wf-1v1", Bullets("wf-1v1", 3)),
                Node("wf-dm", Bullets("wf-dm", 4)),
                Node("wf-spleef", Bullets("wf-spleef", 4)),
                Node("wf-koth", Bullets("wf-koth", 5)),
                Node("wf-parkour", Bullets("wf-parkour", 4)),
                Node("wf-ctf", Bullets("wf-ctf", 5)),
                Node("wf-hg", Concat(
                    new[] { Para("wf-hg-intro"), Gap, Sub("wf-hg-fast-title") },
                    Bullets("wf-hg-fast", 4),
                    new[] { Gap, Sub("wf-hg-hand-title") },
                    Bullets("wf-hg-hand", 5),
                    new[] { Gap, Sub("wf-hg-check-title") },
                    Bullets("wf-hg-check", 6))),
                Node("wf-jugg", Bullets("wf-jugg", 3)),
                Node("wf-mm", Concat(
                    new[] { Sub("wf-mm-dmg-title") },
                    Bullets("wf-mm-dmg", 4),
                    new[] { Gap, Sub("wf-mm-spawn-title") },
                    Bullets("wf-mm-spawn", 7),
                    new[] { Gap, Sub("wf-mm-mark-title") },
                    Bullets("wf-mm-mark", 3))),
                Node("wf-sched", Bullets("wf-sched", 3)),
                Node("wf-loadouts", Bullets("wf-loadouts", 5)),
                Node("wf-templates", Bullets("wf-templates", 4))),

            Sec("trouble", false,
                Node("ts-noplace", Para("ts-noplace-text")),
                Node("ts-koth", Para("ts-koth-text")),
                Node("ts-spleef-fall", Para("ts-spleef-fall-text")),
                Node("ts-parkour", Para("ts-parkour-text")),
                Node("ts-cage", Para("ts-cage-text")),
                Node("ts-hg-storm", Para("ts-hg-storm-text")),
                Node("ts-hg-chests", Para("ts-hg-chests-text")),
                Node("ts-loadouts", Para("ts-loadouts-text")),
                Node("ts-mark", Para("ts-mark-text")),
                Node("ts-item", Para("ts-item-text"))),

            Sec("commands", false, Concat(
                new[] { Hint("cmd-hint") },
                Bullets("cmd", 11)))
        };

        protected override void BuildContent(PageBuildContext ctx)
        {
            var compo = ctx.Compo;
            double x0 = ctx.Main.fixedX;
            double y0 = ctx.Main.fixedY + TopMargin;

            string expandCaption = Lang.Get("canevents:admin-help-expand-all");
            string collapseCaption = Lang.Get("canevents:admin-help-collapse-all");
            double expandWidth = ButtonRow.ButtonWidth(expandCaption) - 10;
            double collapseWidth = ButtonRow.ButtonWidth(collapseCaption) - 10;
            double buttonsWidth = expandWidth + collapseWidth + 8;

            var titleBounds = ElementBounds.Fixed(x0, y0, ctx.Width - buttonsWidth - 10, 28);
            compo.AddStaticText(Lang.Get("canevents:admin-help-title"),
                CairoFont.WhiteMediumText().WithColor(AdminColors.Section), titleBounds, "help-title");

            string hint = Lang.Get("canevents:admin-help-subtitle") + " " + Lang.Get("canevents:admin-help-click-hint");
            var hintBounds = ElementBounds.Fixed(x0, y0 + 34, ctx.Width, 18);
            compo.AddStaticText(Card.Fit(hint, AdminFonts.Hint, ctx.Width), AdminFonts.Hint, hintBounds, "help-hint");

            var collapseBounds = ElementBounds.Fixed(x0 + ctx.Width - collapseWidth, y0, collapseWidth, 22);
            var expandBounds = ElementBounds.Fixed(collapseBounds.fixedX - 8 - expandWidth, y0, expandWidth, 22);
            compo.AddSmallButton(expandCaption, new ActionConsumable(() =>
            {
                SetAll(Guide, true);
                Rebuild();
                return true;
            }), expandBounds, EnumButtonStyle.Small, "help-expand-all");
            compo.AddSmallButton(collapseCaption, new ActionConsumable(() =>
            {
                SetAll(Guide, false);
                State.Scroll[TextKey] = 0f;
                Rebuild();
                return true;
            }), collapseBounds, EnumButtonStyle.Small, "help-collapse-all");

            double top = y0 + HeaderHeight;
            double height = ctx.Bottom - top - 3;
            double width = ctx.Width - ScrollbarWidth - ScrollbarGap - 6;

            var clip = ElementBounds.Fixed(x0 + 3, top, width, height);
            var inset = clip.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
            var scrollbarBounds = ElementBounds.Fixed(x0 + 3 + width + 3 + ScrollbarGap, top - 3, ScrollbarWidth, height + 6);
            var textBounds = ElementBounds.Fixed(TextPadding, TextPadding, width - TextPadding * 2, height)
                .WithParent(clip);

            var components = new List<RichTextComponentBase>();
            var fonts = new HelpFonts();
            foreach (var section in Guide) Emit(components, fonts, section, 0);

            var state = State;
            float restoreTo = state.ScrollOf(TextKey);

            compo.AddInset(inset, 3)
                .BeginClip(clip)
                .AddRichtext(components.ToArray(), textBounds, TextKey)
                .EndClip()
                .AddVerticalScrollbar(value =>
                {
                    state.Scroll[TextKey] = value;
                    var text = compo.GetRichtext(TextKey);
                    if (text == null) return;
                    text.Bounds.fixedY = TextPadding - value;
                    text.Bounds.CalcWorldBounds();
                }, scrollbarBounds, ScrollbarKey);

            // Text height is only known after compose; restore scroll strictly after SetHeights.
            ctx.AfterCompose(() =>
            {
                var scrollbar = compo.GetScrollbar(ScrollbarKey);
                var text = compo.GetRichtext(TextKey);
                if (scrollbar == null || text == null) return;

                float visible = (float)clip.fixedHeight;
                float total = (float)(text.Bounds.fixedHeight + TextPadding * 2);
                scrollbar.SetHeights(visible, total);

                float wanted = Math.Clamp(restoreTo, 0f, Math.Max(0f, total - visible));
                state.Scroll[TextKey] = wanted;
                if (wanted <= 0f) return;
                scrollbar.CurrentYPosition = wanted;
                scrollbar.TriggerChanged();
            });
        }

        // Per build, not static: rich text components keep a reference to the font.
        private sealed class HelpFonts
        {
            public readonly CairoFont Section = CairoFont.WhiteSmallishText().WithFontSize(18);
            public readonly CairoFont Tree = CairoFont.WhiteSmallText();
            public readonly CairoFont Body = CairoFont.WhiteSmallText();
            public readonly CairoFont Hint = CairoFont.WhiteSmallText().WithColor(AdminColors.Muted);
            public readonly CairoFont Accent = CairoFont.WhiteSmallText().WithColor(AdminColors.Section);

            // Empty line in this font = gap between blocks.
            public readonly CairoFont Gap = CairoFont.WhiteSmallText().WithFontSize(7);
        }

        private void Emit(List<RichTextComponentBase> output, HelpFonts fonts, Item item, int depth)
        {
            double indent = depth * IndentStep;

            switch (item.Kind)
            {
                case Kind.Section:
                case Kind.Tree:
                {
                    string stateKey = StatePrefix + item.Key;
                    bool open = State.IsSectionOpen(stateKey, item.DefaultOpen);
                    var font = item.Kind == Kind.Section ? fonts.Section : fonts.Tree;

                    if (item.Kind == Kind.Section && output.Count > 0) AddBreak(output, fonts.Gap);

                    var link = new LinkTextComponent(Capi, (open ? "[-] " : "[+] ") + T(item.Key + "-title"), font,
                        _ => Toggle(stateKey, open));
                    link.PaddingLeft = indent;
                    output.Add(link);
                    AddBreak(output, font);

                    if (!open) return;
                    foreach (var child in item.Children) Emit(output, fonts, child, depth + 1);
                    if (item.Kind == Kind.Tree) AddBreak(output, fonts.Gap);
                    return;
                }
                case Kind.Bullet:
                    AddLine(output, "• " + T(item.Key), fonts.Body, indent);
                    return;
                case Kind.Text:
                    AddLine(output, T(item.Key), fonts.Body, indent);
                    return;
                case Kind.Hint:
                    AddLine(output, T(item.Key), fonts.Hint, indent);
                    return;
                case Kind.Accent:
                    AddLine(output, T(item.Key), fonts.Accent, indent);
                    return;
                case Kind.Gap:
                    AddBreak(output, fonts.Gap);
                    return;
            }
        }

        // Rich text has no hanging indent: wrapped lines start at the left edge.
        private void AddLine(List<RichTextComponentBase> output, string text, CairoFont font, double indent)
        {
            var line = new RichTextComponent(Capi, text, font);
            line.PaddingLeft += indent;
            output.Add(line);
            AddBreak(output, font);
        }

        // Same as VTML <br>; on an empty line it is a gap as tall as the font.
        private void AddBreak(List<RichTextComponentBase> output, CairoFont font)
            => output.Add(new RichTextComponent(Capi, "\r\n", font));

        private static string T(string key) => Lang.Get(LangPrefix + key);

        private void Toggle(string stateKey, bool wasOpen)
        {
            State.Sections[stateKey] = !wasOpen;
            Rebuild();
        }

        private void SetAll(IEnumerable<Item> items, bool open)
        {
            foreach (var item in items)
            {
                if (item.Kind != Kind.Section && item.Kind != Kind.Tree) continue;
                State.Sections[StatePrefix + item.Key] = open;
                SetAll(item.Children, open);
            }
        }
    }
}
