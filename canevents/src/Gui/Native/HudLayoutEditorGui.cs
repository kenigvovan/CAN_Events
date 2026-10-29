using System;
using System.Collections.Generic;
using canevents.src.Gui;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    // Draggable ghost dialogs over the HUD: HUD elements get no mouse input. Positions stored normalised.
    public class HudLayoutEditorGui : GuiDialog
    {
        private readonly HudLayout layout;
        private readonly Action onChanged;

        private readonly List<Ghost> ghosts = new List<Ghost>();
        private Ghost dragging;
        private double grabOffsetX, grabOffsetY;
        private bool dirty;

        // Low alpha so the HUD underneath stays visible.
        private static readonly double[] GhostIdle = { 1.00, 0.83, 0.42, 0.16 };
        private static readonly double[] GhostDragged = { 0.47, 0.78, 0.36, 0.30 };

        private class Ghost
        {
            public string Key;
            public string Label;
            public HudLayout.PanelPos Pos;
            public double Width, Height;
            public double DefaultX, DefaultY;
            public double X, Y;      // unscaled screen coords, top-left
        }

        internal static Log LogFor(ICoreClientAPI capi) => Log.For(capi?.Logger, "hudedit");

        private readonly Log log;

        public HudLayoutEditorGui(ICoreClientAPI capi, HudLayout layout, Action onChanged) : base(capi)
        {
            this.layout = layout;
            this.onChanged = onChanged;
            log = LogFor(capi);
        }

        public override string ToggleKeyCombinationCode => null;
        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            // Logged step by step: this editor once crashed the client with nothing in the log.
            try
            {
                log.Notification("opening");
                BuildGhosts();
                log.Notification("{0} ghost(s) built", ghosts.Count);
                Compose();
                log.Notification("composed");
            }
            catch (Exception e)
            {
                log.Error("failed to open: {0}", e);
                TryClose();
            }
        }

        // Sizes and defaults must match what the HUD composes.
        private void BuildGhosts()
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;

            ghosts.Clear();
            ghosts.Add(new Ghost
            {
                Key = "timer", Label = Lang.Get("canevents:hud-panel-timer"), Pos = layout.Timer,
                Width = 300, Height = 54, DefaultX = screenW / 2 - 150, DefaultY = 10
            });
            ghosts.Add(new Ghost
            {
                Key = "zone", Label = Lang.Get("canevents:hud-panel-zone"), Pos = layout.Zone,
                Width = 240, Height = 48, DefaultX = screenW / 2 - 120, DefaultY = 78
            });
            ghosts.Add(new Ghost
            {
                Key = "score", Label = Lang.Get("canevents:hud-panel-scoreboard"), Pos = layout.Scoreboard,
                Width = 240, Height = 94, DefaultX = screenW - 250, DefaultY = 10
            });
            ghosts.Add(new Ghost
            {
                Key = "feed", Label = Lang.Get("canevents:hud-panel-killfeed"), Pos = layout.KillFeed,
                Width = 360, Height = 72, DefaultX = 10, DefaultY = screenH - 132
            });
            ghosts.Add(new Ghost
            {
                Key = "shop", Label = Lang.Get("canevents:hud-panel-shop"), Pos = layout.ShopPoints,
                Width = ShopPointsHud.Width, Height = ShopPointsHud.Height,
                DefaultX = ShopPointsHud.DefaultX(screenW), DefaultY = ShopPointsHud.DefaultY(screenH)
            });
            if (layout.InviteMode != HudLayout.InviteDisplay.Off)
            {
                bool mini = layout.InviteMode == HudLayout.InviteDisplay.Mini;
                ghosts.Add(new Ghost
                {
                    Key = "invite", Label = Lang.Get("canevents:hud-panel-invite"), Pos = layout.Invite,
                    Width = 300, Height = mini ? 30 : 96, DefaultX = screenW - 310, DefaultY = 110
                });
            }

            foreach (var g in ghosts)
            {
                g.X = g.Pos != null && g.Pos.IsSet ? g.Pos.X * screenW : g.DefaultX;
                g.Y = g.Pos != null && g.Pos.IsSet ? g.Pos.Y * screenH : g.DefaultY;
                Clamp(g);
            }
        }

        private void Clamp(Ghost g)
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            g.X = GameMath.Clamp(g.X, 0, Math.Max(0, screenW - g.Width));
            g.Y = GameMath.Clamp(g.Y, 0, Math.Max(0, screenH - g.Height));
        }

        private void Compose()
        {
            Composers.ClearComposers();

            foreach (var g in ghosts)
            {
                log.Debug("composing ghost {0} at {1}/{2} size {3}x{4}",
                    g.Key, g.X, g.Y, g.Width, g.Height);
                var bounds = ElementBounds.Fixed(g.X, g.Y, g.Width, g.Height);
                var inner = ElementBounds.Fixed(0, 0, g.Width, g.Height);

                Composers[g.Key] = capi.Gui
                    .CreateCompo("canevents-hudghost-" + g.Key, bounds)
                    .AddGameOverlay(inner, dragging == g ? GhostDragged : GhostIdle)
                    .BeginChildElements(inner)
                        .AddStaticText(g.Label, CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center),
                            ElementBounds.Fixed(0, g.Height / 2 - 12, g.Width, 22))
                    .EndChildElements()
                    .Compose();
            }

            ComposeToolbox();
        }

        private void ComposeToolbox()
        {

            // Fixed dialog around a FitToChildren background hangs the layout pass; keep it autosized.
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterBottom)
                .WithFixedAlignmentOffset(0, -60);
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            Composers["toolbox"] = capi.Gui
                .CreateCompo("canevents-hudedit", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("canevents:hud-edit-title"), OnDone)
                .BeginChildElements(bgBounds)
                    // Fixed height: self-measuring text in a FitToChildren parent hangs too.
                    .AddStaticText(Lang.Get("canevents:hud-edit-hint"), CairoFont.WhiteDetailText(),
                        ElementBounds.Fixed(0, 0, 320, 40))
                    .AddSmallButton(Lang.Get("canevents:hud-edit-reset"), OnReset,
                        ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, 46, 0, 0).WithFixedPadding(9, 4))
                    .AddSmallButton(Lang.Get("canevents:hud-edit-done"), () => { OnDone(); return true; },
                        ElementBounds.Fixed(EnumDialogArea.LeftFixed, 110, 46, 0, 0).WithFixedPadding(9, 4))
                    .AddSmallButton(InviteModeLabel(), OnCycleInviteMode,
                        ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, 78, 0, 0).WithFixedPadding(9, 4))
                .EndChildElements()
                .Compose();
        }

        private string InviteModeLabel()
            => Lang.Get("canevents:hud-invite-mode-button",
                Lang.Get("canevents:hud-invite-mode-" + layout.InviteMode.ToString().ToLowerInvariant()));

        // Full -> mini -> off -> full.
        private bool OnCycleInviteMode()
        {
            layout.InviteMode = layout.InviteMode switch
            {
                HudLayout.InviteDisplay.Full => HudLayout.InviteDisplay.Mini,
                HudLayout.InviteDisplay.Mini => HudLayout.InviteDisplay.Off,
                _ => HudLayout.InviteDisplay.Full
            };
            dirty = true;
            Save();
            onChanged?.Invoke();
            // Ghost size depends on the mode.
            BuildGhosts();
            Compose();
            return true;
        }

        private bool OnReset()
        {
            layout.ResetAll();
            dirty = true;
            BuildGhosts();
            Compose();
            onChanged?.Invoke();
            return true;
        }

        private void OnDone()
        {
            Save();
            TryClose();
        }

        private void Save()
        {
            if (!dirty) return;
            layout.Save(capi);
            dirty = false;
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            Save();
        }

        public override void OnMouseDown(MouseEvent args)
        {
            double mx = capi.Input.MouseX / RuntimeEnv.GUIScale;
            double my = capi.Input.MouseY / RuntimeEnv.GUIScale;

            // Reverse: the last composed ghost draws on top.
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                var g = ghosts[i];
                if (mx < g.X || mx > g.X + g.Width || my < g.Y || my > g.Y + g.Height) continue;

                dragging = g;
                grabOffsetX = mx - g.X;
                grabOffsetY = my - g.Y;
                Compose();
                args.Handled = true;
                return;
            }

            base.OnMouseDown(args);
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (dragging == null) { base.OnMouseMove(args); return; }

            dragging.X = capi.Input.MouseX / RuntimeEnv.GUIScale - grabOffsetX;
            dragging.Y = capi.Input.MouseY / RuntimeEnv.GUIScale - grabOffsetY;
            Clamp(dragging);

            // Not Compose(): that re-uploads every GUI texture on each mouse move.
            var composer = Composers[dragging.Key];
            if (composer?.Bounds != null)
            {
                composer.Bounds.fixedX = dragging.X;
                composer.Bounds.fixedY = dragging.Y;
                composer.Bounds.CalcWorldBounds();
            }
            args.Handled = true;
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (dragging == null) { base.OnMouseUp(args); return; }

            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            HudLayout.Store(dragging.Pos, new System.Numerics.Vector2((float)screenW, (float)screenH),
                new System.Numerics.Vector2((float)dragging.X, (float)dragging.Y));

            dirty = true;
            dragging = null;
            Compose();
            Save();
            onChanged?.Invoke();
            args.Handled = true;
        }
    }
}


