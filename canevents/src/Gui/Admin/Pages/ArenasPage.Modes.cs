using System;
using System.Collections.Generic;
using System.Globalization;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages.Sections;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Modes sub-tab. HG and MM are separate section classes. Buttons target the place by id.
    public sealed partial class ArenasPage
    {
        // King of the Hill
        private double kothRadius = 5;
        private double kothHeight = 5;

        // Parkour
        private int parkourEditIndex;
        private int parkourLives = 3;

        // CTF team, 1-based like the command.
        private int ctfPostTeam = 1;

        // Spleef cage (+Cage)
        private int cageWidth = 5;
        private int cageDepth = 5;
        private int cageHeight = 4;
        private string cageBlock = "game:glass-plain";

        // Spleef platform edit
        private int platformFillRadius = 5;
        private string platformFillBlock = "game:stickslayer-wooden-ew-free";

        // Card draw order.
        internal static readonly string[] ModeTypes =
        {
            PlaceTags.Koth, PlaceTags.Parkour, PlaceTags.HungerGames, PlaceTags.Ctf, PlaceTags.MurderMystery, PlaceTags.Spleef
        };

        private double BuildModesTab(PageBuildContext ctx, PlaceListEntryMsg place)
        {
            var column = ctx.Column(0, ctx.Width);
            double width = column.fixedWidth - Card.Padding * 2;
            double y = 4;

            bool any = WaitingRoomEventFor(place) != null || OpenMatchEventFor(place) != null;
            foreach (var t in ModeTypes) any |= PlaceHighlighter.HasType(place, t);

            if (!any)
            {
                string text = Lang.Get("canevents:admin-arenas-modes-none",
                    place.Types != null && place.Types.Length > 0 ? string.Join(", ", place.Types) : "-");
                double h = ButtonRow.MultiLineHeight(text, width);
                var inner = Card.Frame(ctx.Compo, column, y, Card.HeightAround(h), (place.Name ?? "?") + " (#" + place.Id + ")");
                Wrapped(ctx, inner.fixedX, inner.fixedY, width, text, AdminFonts.Hint);
                return y + Card.HeightAround(h) + Card.Gap;
            }

            Line(ctx, column.fixedX, y, column.fixedWidth,
                Lang.Get("canevents:admin-arenas-modes-for", place.Name ?? "?", place.Id), AdminColors.Section);
            y += LineHeight + 6;

            return BuildModeCards(ctx, column, place, y, hg, mm);
        }

        // HG/MM sections are passed in since the wizard uses its own instances (they hold buffers).
        // includeWaitingRoom=false: the wizard draws its waiting-room and pad cards itself.
        internal double BuildModeCards(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y,
                                       HungerGamesSection hgSection, MurderMysterySection mmSection,
                                       bool includeWaitingRoom = true)
        {
            if (includeWaitingRoom)
            {
                var roomEvent = WaitingRoomEventFor(place);
                if (roomEvent != null) y = BuildWaitingRoom(ctx, column, place, y, roomEvent);
                var openEvent = OpenMatchEventFor(place);
                if (openEvent != null) y = BuildZoneCard(ctx, column, y, OpenPadsSpec(openEvent));
            }
            if (PlaceHighlighter.HasType(place, PlaceTags.Koth)) y = BuildKoth(ctx, column, place, y);
            if (PlaceHighlighter.HasType(place, PlaceTags.Parkour)) y = BuildParkour(ctx, column, place, y);
            if (HungerGamesSection.AppliesTo(place)) y = hgSection.Build(ctx, place, y, column);
            if (PlaceHighlighter.HasType(place, PlaceTags.Ctf)) y = BuildCtf(ctx, column, place, y);
            if (MurderMysterySection.AppliesTo(place)) y = mmSection.Build(ctx, place, y, column);
            if (PlaceHighlighter.HasType(place, PlaceTags.Spleef)) y = BuildSpleef(ctx, column, place, y);
            return y;
        }

        private double BuildHelp(PageBuildContext ctx, ElementBounds column, double y, string key, string titleKey, string textKey)
        {
            string text = Lang.Get(textKey);
            double width = column.fixedWidth - Card.Padding * 2;
            double height = ButtonRow.MultiLineHeight(text, width) + 4;
            var inner = Section.Begin(ctx, column, y, key, Lang.Get(titleKey), height, out double nextY, defaultOpen: false);
            if (inner != null)
                ctx.Compo.AddStaticText(text, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(height), K(key + "-text"));
            return nextY;
        }

        private double BuildKoth(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            y = BuildHelp(ctx, column, y, "ar-koth-help", "canevents:admin-arenas-koth-help-title", "canevents:admin-arenas-koth-help");

            double width = column.fixedWidth - Card.Padding * 2;
            string setCap = Lang.Get("canevents:admin-arenas-koth-set");
            double outsideH = OutsideWarning(ctx, place, 0, 0, width, "canevents:admin-arenas-koth-offarena", measureOnly: true);
            double content = LineHeight + 4 + outsideH + InputHeight + 8 + ButtonRow.HeightFor(width, 0, setCap);

            var inner = Section.Begin(ctx, column, y, "ar-koth", Lang.Get("canevents:admin-arenas-koth-title"), content, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            if (string.IsNullOrEmpty(place.KothHill))
            {
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-koth-nohill"), AdminColors.Danger);
            }
            else
            {
                var f = place.KothHill.Split(',');
                Line(ctx, inner.fixedX, cy, width, f.Length >= 5
                    ? Lang.Get("canevents:admin-arenas-koth-hill", f[0], f[1], f[2], f[3], f[4])
                    : Lang.Get("canevents:admin-arenas-koth-hill-set"), AdminColors.Success);
            }
            cy += LineHeight + 4;

            cy += OutsideWarning(ctx, place, inner.fixedX, cy, width, "canevents:admin-arenas-koth-offarena");

            double x = inner.fixedX;
            x += FieldLabel(ctx, x, cy, Lang.Get("canevents:admin-arenas-koth-radius"));
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 70, InputHeight), "ar-koth-radius",
                () => kothRadius, v => kothRadius = v, 0.1, 1000);
            x += 70 + 16;
            x += FieldLabel(ctx, x, cy, Lang.Get("canevents:admin-arenas-koth-height"));
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 70, InputHeight), "ar-koth-height",
                () => kothHeight, v => kothHeight = v, 0.1, 1000);
            cy += InputHeight + 8;

            long id = place.Id;
            string name = place.Name ?? "?";
            var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-koth-btn");
            row.Add(setCap, () =>
            {
                float r = (float)kothRadius, h = (float)kothHeight;
                Act(Lang.Get("canevents:admin-arenas-log-koth",
                        r.ToString("0.#", CultureInfo.InvariantCulture), h.ToString("0.#", CultureInfo.InvariantCulture), name),
                    () => Net.SendSetKothHill(id, r, h));
            }, Lang.Get("canevents:admin-arenas-koth-set-tip"));

            return nextY;
        }

        // Unpacked from PlaceListEntryMsg.ParkourCheckpoints ("x,y,z,dim,index,finish").
        private struct ParkourCheckpoint
        {
            public int X, Y, Z, Dim, Index;
            public bool IsFinish;
        }

        private static List<ParkourCheckpoint> ParseParkourCheckpoints(PlaceListEntryMsg place)
        {
            var list = new List<ParkourCheckpoint>();
            if (place.ParkourCheckpoints == null) return list;

            foreach (var packed in place.ParkourCheckpoints)
            {
                var f = packed?.Split(',');
                if (f == null || f.Length < 6) continue;
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    || !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)
                    || !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dim)
                    || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) continue;
                list.Add(new ParkourCheckpoint { X = x, Y = y, Z = z, Dim = dim, Index = index, IsFinish = f[5] == "1" });
            }

            // By index, finish last.
            list.Sort((a, b) => a.IsFinish != b.IsFinish ? (a.IsFinish ? 1 : -1) : a.Index.CompareTo(b.Index));
            return list;
        }

        private double BuildParkour(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            y = BuildHelp(ctx, column, y, "ar-pk-help", "canevents:admin-arenas-pk-help-title", "canevents:admin-arenas-pk-help");

            double width = column.fixedWidth - Card.Padding * 2;
            var checkpoints = ParseParkourCheckpoints(place);
            bool hasFinish = checkpoints.Exists(c => c.IsFinish);

            string markCap = Lang.Get("canevents:admin-arenas-pk-mark");
            string finishCap = Lang.Get("canevents:admin-arenas-pk-finish");
            string applyLivesCap = Lang.Get("canevents:admin-arenas-pk-lives-apply");
            double outsideH = OutsideWarning(ctx, place, 0, 0, width, "canevents:admin-arenas-pk-offarena", measureOnly: true);
            double markHeight = ButtonRow.HeightFor(width, 0, markCap, finishCap);

            int count = checkpoints.Count;
            double listHeight = Math.Min(Math.Max(count, 1), 6) * ColumnTable.RowPitch + 10;
            double tableHeight = ColumnTable.HeaderHeight + 4 + listHeight + 10;

            double listBlock;
            if (count == 0) listBlock = LineHeight + 4;
            else listBlock = (hasFinish ? 0 : LineHeight) + LineHeight + 4 + InputHeight + 8 + tableHeight;

            double content = outsideH + markHeight + 6 + listBlock + 6 + LineHeight + 4 + ButtonRow.ButtonExtent;

            var inner = Section.Begin(ctx, column, y, "ar-pk", Lang.Get("canevents:admin-arenas-pk-title"), content, out double nextY);
            if (inner == null) return nextY;

            long id = place.Id;
            string name = place.Name ?? "?";
            double cy = inner.fixedY;
            cy += OutsideWarning(ctx, place, inner.fixedX, cy, width, "canevents:admin-arenas-pk-offarena");

            // No index sent, server appends after the last checkpoint.
            var marks = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-pk-mark");
            marks.Add(markCap, () => Act(Lang.Get("canevents:admin-arenas-log-pk-mark", name),
                () => Net.SendSetParkourCheckpoint(id, isFinish: false)), Lang.Get("canevents:admin-arenas-pk-mark-tip"));
            marks.Add(finishCap, () => Act(Lang.Get("canevents:admin-arenas-log-pk-finish", name),
                () => Net.SendSetParkourCheckpoint(id, isFinish: true)), Lang.Get("canevents:admin-arenas-pk-finish-tip"));
            cy += markHeight + 6;

            if (count == 0)
            {
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-pk-none"), AdminColors.Danger);
                cy += LineHeight + 4;
            }
            else
            {
                if (!hasFinish)
                {
                    Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-pk-nofinish"), AdminColors.Danger);
                    cy += LineHeight;
                }
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-pk-index-hint"), AdminColors.Muted);
                cy += LineHeight + 4;

                double ix = inner.fixedX + FieldLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-arenas-pk-index"));
                NumberField.AddInt(ctx, ElementBounds.Fixed(ix, cy, 70, InputHeight), "ar-pk-index",
                    () => parkourEditIndex, v => parkourEditIndex = v, 0, 9999);
                cy += InputHeight + 8;

                var spec = new ColumnSpec(
                    new ColumnDef(Lang.Get("canevents:admin-arenas-pk-col-cp"), 80),
                    new ColumnDef(Lang.Get("canevents:admin-arenas-pk-col-pos"), 0),
                    new ColumnDef(null, 230));

                var rows = new List<ColumnRow>();
                foreach (var cp in checkpoints)
                {
                    var t = cp;
                    var row = new ColumnRow(t.IsFinish ? Lang.Get("canevents:admin-arenas-pk-finish-label") : "#" + t.Index,
                            "(" + t.X + ", " + t.Y + ", " + t.Z + ")", null)
                        .WithColor(0, t.IsFinish ? AdminColors.Success : AdminColors.Value)
                        .WithColor(1, AdminColors.Muted);

                    if (!t.IsFinish)
                    {
                        // Index is read at click time.
                        row.WithButton(2, Lang.Get("canevents:admin-arenas-pk-set-index"), () =>
                        {
                            int idx = parkourEditIndex;
                            Act(Lang.Get("canevents:admin-arenas-log-pk-index", t.X, t.Y, t.Z, idx),
                                () => Net.SendSetParkourCheckpointIndex(id, t.X, t.Y, t.Z, t.Dim, idx));
                        }, Lang.Get("canevents:admin-arenas-pk-set-index-tip"));
                    }

                    ArmedTextButton.AddToRow(ctx, row, 2, "ar-pk-rm-" + id + "-" + t.X + "-" + t.Y + "-" + t.Z + "-" + t.Dim,
                        Lang.Get("canevents:admin-arenas-remove"), Lang.Get("canevents:admin-arenas-remove-armed"), null,
                        () => Act(Lang.Get("canevents:admin-arenas-log-pk-remove", t.X, t.Y, t.Z),
                            () => Net.SendRemoveParkourCheckpoint(id, t.X, t.Y, t.Z, t.Dim)));
                    rows.Add(row);
                }

                ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                    new ScrollableListOptions { Key = "ar-pk-list" });
                cy += tableHeight;
            }
            cy += 6;

            // Lives
            Line(ctx, inner.fixedX, cy, width, place.ParkourLives > 0
                ? Lang.Get("canevents:admin-arenas-pk-lives", place.ParkourLives)
                : Lang.Get("canevents:admin-arenas-pk-lives-unset"), AdminColors.Muted);
            cy += LineHeight + 4;

            double lx = inner.fixedX + FieldLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-arenas-pk-lives-label"));
            NumberField.AddInt(ctx, ElementBounds.Fixed(lx, cy, 70, InputHeight), "ar-pk-lives",
                () => parkourLives, v => parkourLives = v, 1, 999);
            var livesRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(lx + 80, cy - 1, width - (lx + 80 - inner.fixedX), 0),
                width - (lx + 80 - inner.fixedX), 0, "ar-pk-lives-btn");
            livesRow.Add(applyLivesCap, () =>
            {
                int lives = parkourLives;
                Act(Lang.Get("canevents:admin-arenas-log-pk-lives", lives, name), () => Net.SendSetParkourLives(id, lives));
            });

            return nextY;
        }

        private double BuildCtf(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            y = BuildHelp(ctx, column, y, "ar-ctf-help", "canevents:admin-arenas-ctf-help-title", "canevents:admin-arenas-ctf-help");

            double width = column.fixedWidth - Card.Padding * 2;
            // Unset slots included, a gap means that team has no post.
            var posts = place.CtfPosts ?? new string[0];
            var postLines = new List<(string text, bool set)>();
            for (int i = 0; i < posts.Length; i++)
            {
                bool set = !string.IsNullOrEmpty(posts[i]);
                postLines.Add((set
                    ? Lang.Get("canevents:admin-arenas-ctf-post-at", i + 1, posts[i])
                    : Lang.Get("canevents:admin-arenas-ctf-post-unset", i + 1), set));
            }

            string setCap = Lang.Get("canevents:admin-arenas-ctf-set");
            string clearCap = Lang.Get("canevents:admin-arenas-ctf-clear");
            string clearArmed = Lang.Get("canevents:admin-arenas-ctf-clear-armed");
            string refreshCap = Lang.Get("canevents:admin-arenas-refresh");
            double outsideH = OutsideWarning(ctx, place, 0, 0, width, "canevents:admin-arenas-ctf-offarena", measureOnly: true);
            double buttonsHeight = ButtonRow.HeightFor(width, 0, setCap,
                ArmedTextButton.WiderCaption(clearCap, clearArmed), refreshCap);

            double content = LineHeight + postLines.Count * LineHeight + 6 + outsideH + InputHeight + 8 + buttonsHeight;
            string title = Lang.Get("canevents:admin-arenas-ctf-title", place.Name ?? "?", place.Id);

            var inner = Section.Begin(ctx, column, y, "ar-ctf", title, content, out double nextY);
            if (inner == null) return nextY;

            long id = place.Id;
            string name = place.Name ?? "?";
            double cy = inner.fixedY;

            // No event here, so spawn count stands in for team count.
            int postCount = CountCtfPosts(place);
            int wantPosts = Math.Max(2, place.SpawnCount);
            if (postCount == 0)
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-ctf-none"), AdminColors.Danger);
            else if (postCount < wantPosts)
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-ctf-some", postCount, wantPosts), AdminColors.Danger);
            else
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-ctf-all", postCount), AdminColors.Success);
            cy += LineHeight;

            foreach (var (text, set) in postLines)
            {
                Line(ctx, inner.fixedX + 12, cy, width - 12, text, set ? AdminColors.Muted : AdminColors.Danger);
                cy += LineHeight;
            }
            cy += 6;

            cy += OutsideWarning(ctx, place, inner.fixedX, cy, width, "canevents:admin-arenas-ctf-offarena");

            double tx = inner.fixedX + FieldLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-arenas-ctf-team"));
            NumberField.AddInt(ctx, ElementBounds.Fixed(tx, cy, 70, InputHeight), "ar-ctf-team",
                () => ctfPostTeam, v => ctfPostTeam = v, 1, 16, Lang.Get("canevents:admin-arenas-ctf-team-tip"));
            cy += InputHeight + 8;

            var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-ctf-btn");
            row.Add(setCap, () =>
            {
                int team = ctfPostTeam;
                Act(Lang.Get("canevents:admin-arenas-log-ctf-set", team, name), () => Net.SendSetCtfPost(id, team));
            }, Lang.Get("canevents:admin-arenas-ctf-set-tip"));
            ArmedTextButton.Add(ctx, row.Next(ArmedTextButton.Width(clearCap, clearArmed)), "ar-ctf-clear-" + id, clearCap,
                () => Act(Lang.Get("canevents:admin-arenas-log-ctf-clear", name), () => Net.SendClearCtfPosts(id)),
                clearArmed, Lang.Get("canevents:admin-arenas-ctf-clear-tip"));
            row.Add(refreshCap, () => Net.RequestPlaceList());

            return nextY;
        }

        internal static int CountCtfPosts(PlaceListEntryMsg place)
        {
            if (place?.CtfPosts == null) return 0;
            int n = 0;
            foreach (var post in place.CtfPosts) if (!string.IsNullOrEmpty(post)) n++;
            return n;
        }

        private double BuildSpleef(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            double width = column.fixedWidth - Card.Padding * 2;
            var edit = Net.PlatformEditState.Value;
            bool editing = edit != null && edit.Active && edit.PlaceId == place.Id;

            string editCap = Lang.Get("canevents:admin-arenas-spleef-edit");
            string sqCap = Lang.Get("canevents:admin-arenas-spleef-fill-square");
            string circCap = Lang.Get("canevents:admin-arenas-spleef-fill-circle");
            string floodCap = Lang.Get("canevents:admin-arenas-spleef-fill-flood");
            string saveCap = Lang.Get("canevents:admin-arenas-spleef-save");
            string cancelCap = Lang.Get("canevents:admin-arenas-spleef-cancel");
            string cancelArmed = Lang.Get("canevents:admin-arenas-spleef-cancel-armed");
            string cageCap = Lang.Get("canevents:admin-arenas-spleef-cage");
            string cageClear = Lang.Get("canevents:admin-arenas-spleef-cage-clear");
            string cageClearArmed = Lang.Get("canevents:admin-arenas-spleef-cage-clear-armed");

            double platformHeight = editing
                ? InputHeight + 8 + ButtonRow.HeightFor(width, 0, sqCap, circCap, floodCap, saveCap,
                    ArmedTextButton.WiderCaption(cancelCap, cancelArmed))
                : ButtonRow.ButtonExtent;
            double cageHeightPx = LineHeight + 4 + InputHeight + 8 + LineHeight + 4
                                  + ButtonRow.HeightFor(width, 0, cageCap,
                                      ArmedTextButton.WiderCaption(cageClear, cageClearArmed));
            double content = LineHeight + 4 + platformHeight + 12 + cageHeightPx;

            var inner = Section.Begin(ctx, column, y, "ar-spleef", Lang.Get("canevents:admin-arenas-spleef-title"), content, out double nextY);
            if (inner == null) return nextY;

            long id = place.Id;
            string name = place.Name ?? "?";
            double cy = inner.fixedY;

            // Platform
            Line(ctx, inner.fixedX, cy, width, editing
                ? Lang.Get("canevents:admin-arenas-spleef-editing", edit.Count)
                : Lang.Get("canevents:admin-arenas-spleef-platform"), editing ? AdminColors.Success : AdminColors.Label);
            cy += LineHeight + 4;

            if (!editing)
            {
                var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-pf-start");
                row.Add(editCap, () => Act(Lang.Get("canevents:admin-arenas-log-pf-start", name), () => Net.SendPlatformEditStart(id)),
                    Lang.Get("canevents:admin-arenas-spleef-edit-tip"));
                cy += ButtonRow.ButtonExtent;
            }
            else
            {
                double x = inner.fixedX + FieldLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-arenas-spleef-radius"));
                NumberField.AddInt(ctx, ElementBounds.Fixed(x, cy, 60, InputHeight), "ar-pf-radius",
                    () => platformFillRadius, v => platformFillRadius = v, 1, 256);
                x += 60 + 16;
                x += FieldLabel(ctx, x, cy, Lang.Get("canevents:admin-arenas-spleef-block"));
                double pfField = Math.Max(80, Math.Min(220, inner.fixedX + width - x - FromHandWidth - 8));
                NumberField.AddText(ctx, ElementBounds.Fixed(x, cy, pfField, InputHeight), "ar-pf-block",
                    () => platformFillBlock, v => platformFillBlock = (v ?? "").Trim());
                AddFromHandButton(ctx, x + pfField + 8, cy, "ar-pf-block", code => platformFillBlock = code);
                cy += InputHeight + 8;

                var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-pf-act");
                string FillLog(string shape) => Lang.Get("canevents:admin-arenas-log-pf-fill", shape, platformFillRadius, platformFillBlock);
                row.Add(sqCap, () => Act(FillLog(sqCap),
                    () => Net.SendPlatformFillShape(id, false, platformFillRadius, platformFillBlock)));
                row.Add(circCap, () => Act(FillLog(circCap),
                    () => Net.SendPlatformFillShape(id, true, platformFillRadius, platformFillBlock)));
                row.Add(floodCap, () => Act(FillLog(floodCap),
                    () => Net.SendPlatformFloodFill(id, platformFillRadius, platformFillBlock)),
                    Lang.Get("canevents:admin-arenas-spleef-fill-flood-tip"));
                row.Add(saveCap, () => Act(Lang.Get("canevents:admin-arenas-log-pf-save", name), () => Net.SendPlatformEditStop(id, true)),
                    Lang.Get("canevents:admin-arenas-spleef-save-tip"));
                ArmedTextButton.Add(ctx, row.Next(ArmedTextButton.Width(cancelCap, cancelArmed)), "ar-pf-cancel-" + id, cancelCap,
                    () => Act(Lang.Get("canevents:admin-arenas-log-pf-cancel", name), () => Net.SendPlatformEditStop(id, false)),
                    cancelArmed, Lang.Get("canevents:admin-arenas-spleef-cancel-tip"));
                cy += row.Bounds.fixedY - cy + ButtonRow.ButtonExtent;
            }
            cy += 12;

            // Cage
            Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-spleef-cage-title"), AdminColors.Label);
            cy += LineHeight + 4;

            double cx = inner.fixedX;
            cx += FieldLabel(ctx, cx, cy, Lang.Get("canevents:admin-arenas-spleef-cage-w"));
            NumberField.AddInt(ctx, ElementBounds.Fixed(cx, cy, 50, InputHeight), "ar-cage-w", () => cageWidth, v => cageWidth = v, 3, 64);
            cx += 50 + 12;
            cx += FieldLabel(ctx, cx, cy, Lang.Get("canevents:admin-arenas-spleef-cage-d"));
            NumberField.AddInt(ctx, ElementBounds.Fixed(cx, cy, 50, InputHeight), "ar-cage-d", () => cageDepth, v => cageDepth = v, 3, 64);
            cx += 50 + 12;
            cx += FieldLabel(ctx, cx, cy, Lang.Get("canevents:admin-arenas-spleef-cage-h"));
            NumberField.AddInt(ctx, ElementBounds.Fixed(cx, cy, 50, InputHeight), "ar-cage-h", () => cageHeight, v => cageHeight = v, 4, 64);
            cx += 50 + 12;
            cx += FieldLabel(ctx, cx, cy, Lang.Get("canevents:admin-arenas-spleef-block"));
            double cageField = Math.Max(80, Math.Min(200, inner.fixedX + width - cx - FromHandWidth - 8));
            NumberField.AddText(ctx, ElementBounds.Fixed(cx, cy, cageField, InputHeight), "ar-cage-block",
                () => cageBlock, v => cageBlock = (v ?? "").Trim());
            AddFromHandButton(ctx, cx + cageField + 8, cy, "ar-cage-block", code => cageBlock = code);
            cy += InputHeight + 8;

            Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-spleef-cage-hint"), AdminColors.Muted);
            cy += LineHeight + 4;

            var cageRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-cage-btn");
            cageRow.Add(cageCap, () =>
            {
                int w = cageWidth, d = cageDepth, h = cageHeight;
                string block = cageBlock;
                Act(Lang.Get("canevents:admin-arenas-log-cage", w, d, h, block), () => Net.SendSetCagePos(id, w, d, h, block));
            }, Lang.Get("canevents:admin-arenas-spleef-cage-tip"));
            ArmedTextButton.Add(ctx, cageRow.Next(ArmedTextButton.Width(cageClear, cageClearArmed)), "ar-cage-clear-" + id, cageClear,
                () => Act(Lang.Get("canevents:admin-arenas-log-cage-clear", name), () => Net.SendClearCagePos(id)), cageClearArmed);

            return nextY;
        }

        private static double FromHandWidth => ButtonRow.ButtonWidth(Lang.Get("canevents:admin-arenas-block-from-hand"));

        // Fills a block-code field from the active hotbar slot.
        private void AddFromHandButton(PageBuildContext ctx, double x, double y, string fieldKey, Action<string> set)
        {
            string caption = Lang.Get("canevents:admin-arenas-block-from-hand");
            var bounds = ElementBounds.Fixed(x, y - 1, FromHandWidth, ButtonRow.ButtonHeight);
            string key = fieldKey + "-hand";
            ctx.Compo.AddButton(caption, new Vintagestory.API.Common.ActionConsumable(() =>
            {
                var block = Capi.World?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Block;
                if (block == null)
                {
                    Gui.SetStatus(false, Lang.Get("canevents:admin-arenas-block-from-hand-none"));
                    return true;
                }
                set(block.Code.ToString());
                // Drafts win over the value, so drop it.
                State.Drafts.Remove(fieldKey);
                Rebuild();
                return true;
            }), bounds, EnumButtonStyle.Normal, key);
            Tooltip.Add(ctx.Compo, Lang.Get("canevents:admin-arenas-block-from-hand-tip"), bounds, "tip-" + key);
        }
    }
}
