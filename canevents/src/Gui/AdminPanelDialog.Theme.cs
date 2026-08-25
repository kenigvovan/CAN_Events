using System;
using System.Numerics;
using ImGuiNET;

namespace canevents.src.Gui
{
    // Look and feel of the admin panel: one palette and one set of button styles, instead of
    // colours spelled out inline at every call site.
    public partial class AdminPanelDialog
    {
        // Fixed meaning per colour: gold titles, green go, red stop, amber needs-attention.
        private static readonly Vector4 ColAccent   = new Vector4(1.00f, 0.82f, 0.35f, 1f); // titles
        private static readonly Vector4 ColOk       = new Vector4(0.45f, 0.85f, 0.45f, 1f);
        private static readonly Vector4 ColWarn     = new Vector4(1.00f, 0.72f, 0.28f, 1f);
        private static readonly Vector4 ColBad      = new Vector4(0.95f, 0.42f, 0.40f, 1f);
        private static readonly Vector4 ColMuted    = new Vector4(0.62f, 0.60f, 0.55f, 1f);

        // Pressed state is one step brighter than hover; without it the buttons ignored clicks.
        private static readonly ButtonPalette PalGo = new(
            new Vector4(0.20f, 0.45f, 0.22f, 1f),
            new Vector4(0.26f, 0.58f, 0.28f, 1f),
            new Vector4(0.32f, 0.68f, 0.34f, 1f));

        private static readonly ButtonPalette PalStop = new(
            new Vector4(0.50f, 0.18f, 0.16f, 1f),
            new Vector4(0.64f, 0.24f, 0.21f, 1f),
            new Vector4(0.74f, 0.30f, 0.26f, 1f));

        /// <summary>Section heading, used by every tab instead of ad-hoc coloured text.</summary>
        private static void Heading(string text)
        {
            ImGui.TextColored(ColAccent, text);
        }

        /// <summary>Green affirmative-action button.</summary>
        private static bool GoButton(string label) => GuiButtons.Button(label, PalGo);

        private static bool GoButton(string label, Vector2 size) => GuiButtons.Button(label, size, PalGo);

        private static bool GoSmallButton(string label) => GuiButtons.SmallButton(label, PalGo);

        /// <summary>Red destructive/halting button.</summary>
        private static bool StopButton(string label) => GuiButtons.Button(label, PalStop);

        private static bool StopSmallButton(string label) => GuiButtons.SmallButton(label, PalStop);

        /// <summary>
        /// A number field that clamps itself, replacing hand-written clamps at every call site.
        /// Sets its own width too: an unconstrained InputInt takes the whole column.
        /// </summary>
        private static bool IntField(string label, ref int value, int min, int max, float width = 90f)
        {
            ImGui.SetNextItemWidth(width);
            if (!ImGui.InputInt(label, ref value)) return false;

            value = Math.Clamp(value, min, max);
            return true;
        }

        /// <summary>
        /// Opens a scrolling list table with the panel's standard look: borders, striped rows, a
        /// frozen header, and a height that grows with the content up to a cap. Every tab drew this
        /// preamble by hand, and each copy picked its own row height - so the same list looked
        /// different depending on which tab you opened it from.
        ///
        /// Returns false when the table could not open; the caller then skips its rows, exactly as
        /// with a raw BeginTable. Close with ImGui.EndTable() as usual.
        /// </summary>
        private static bool BeginListTable(string id, int columns, int rowCount,
            float rowHeight = 22f, float maxHeight = 180f)
        {
            float height = Math.Min(rowCount * rowHeight + 26f, maxHeight);
            return ImGui.BeginTable(id, columns,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.SizingFixedFit,
                new Vector2(ImGui.GetContentRegionAvail().X, height));
        }

        /// <summary>
        /// The first cell of a row, as a selectable spanning the whole row. Returns true on the
        /// click that selects it.
        /// </summary>
        private static bool SelectableRow(string id, string label, bool selected)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.PushID(id);
            bool clicked = ImGui.Selectable(label, selected, ImGuiSelectableFlags.SpanAllColumns);
            ImGui.PopID();
            return clicked;
        }

        /// <summary>
        /// The panel's delete confirmation. Returns true once, on the click that confirms.
        ///
        /// <paramref name="open"/> has to be re-armed by the caller before OpenPopup: Escape sets it
        /// to false, and BeginPopupModal with a false ref never opens again.
        /// </summary>
        private static bool ConfirmDeleteModal(string popupId, ref bool open, string question, string subject)
        {
            bool confirmed = false;
            if (!ImGui.BeginPopupModal(popupId, ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar)) return false;

            ImGui.TextColored(ColBad, question);
            if (!string.IsNullOrEmpty(subject)) ImGui.Text(subject);
            ImGui.Spacing();

            if (ImGui.Button("Yes, delete", new Vector2(100, 0)))
            {
                confirmed = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(70, 0))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return confirmed;
        }

        /// <summary>
        /// A destructive button that asks first: the second click confirms. The panel's other style
        /// for this is a modal (see ConfirmDeleteModal); this one is for buttons that sit inline
        /// among ordinary ones, where a modal would be heavier than the action deserves.
        ///
        /// Returns true only on the confirming click. The armed state clears itself the moment the
        /// mouse leaves, so a button left armed by accident does not stay a trap.
        /// </summary>
        private static bool ArmedButton(ref bool armed, string label, string confirmLabel)
        {
            if (!armed)
            {
                if (StopSmallButton(label)) armed = true;
                return false;
            }

            bool confirmed = GuiButtons.SmallButton(confirmLabel, PalStop);
            if (confirmed || (!ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
                armed = false;
            return confirmed;
        }

        /// <summary>Same "(?)" as HelpMarker, without the SameLine - for a caller that has already
        /// placed it on the line itself.</summary>
        private static void HelpMarkerInline(string text)
        {
            ImGui.TextDisabled("(?)");
            if (!ImGui.IsItemHovered()) return;

            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        /// <summary>Tab heading with its refresh button and the grey lines under it.</summary>
        private static void SectionHeader(string title, string refreshId, Action onRefresh,
            params string[] hints)
        {
            Heading(title);
            if (onRefresh != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Refresh##" + refreshId)) onRefresh();
            }
            foreach (var hint in hints) ImGui.TextDisabled(hint);
            ImGui.Spacing();
        }

        /// <summary>"Nothing here yet" plus optional advice on how to make something appear.</summary>
        private static void EmptyState(string text, params string[] hints)
        {
            ImGui.TextColored(ColMuted, text);
            foreach (var hint in hints) ImGui.TextDisabled(hint);
        }

        /// <summary>Little "?" next to a control that explains it on hover.</summary>
        private static void HelpMarker(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        /// <summary>
        /// Pushes the panel-wide look and returns how many style vars/colors to pop. ImGui fails
        /// silently on an unbalanced pop, corrupting the style stack for every later frame.
        /// </summary>
        private static (int vars, int colors) PushTheme()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
            ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 4f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6f, 3f));

            ImGui.PushStyleColor(ImGuiCol.WindowBg,        new Vector4(0.11f, 0.09f, 0.06f, 0.97f));
            ImGui.PushStyleColor(ImGuiCol.PopupBg,         new Vector4(0.13f, 0.11f, 0.08f, 0.98f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg,         new Vector4(0.20f, 0.17f, 0.12f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,  new Vector4(0.27f, 0.23f, 0.16f, 1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,   new Vector4(0.32f, 0.27f, 0.19f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Button,          new Vector4(0.26f, 0.22f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered,   new Vector4(0.36f, 0.30f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,    new Vector4(0.44f, 0.36f, 0.24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Header,          new Vector4(0.34f, 0.28f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered,   new Vector4(0.42f, 0.34f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,    new Vector4(0.48f, 0.39f, 0.25f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Tab,             new Vector4(0.20f, 0.17f, 0.11f, 1f));
            ImGui.PushStyleColor(ImGuiCol.TabHovered,      new Vector4(0.42f, 0.34f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.TabActive,       new Vector4(0.52f, 0.42f, 0.24f, 1f));
            ImGui.PushStyleColor(ImGuiCol.TitleBg,         new Vector4(0.16f, 0.13f, 0.09f, 1f));
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive,   new Vector4(0.24f, 0.19f, 0.12f, 1f));
            ImGui.PushStyleColor(ImGuiCol.TableHeaderBg,   new Vector4(0.22f, 0.18f, 0.12f, 1f));
            ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt,   new Vector4(1f, 1f, 1f, 0.025f));
            ImGui.PushStyleColor(ImGuiCol.Border,          new Vector4(0.38f, 0.32f, 0.22f, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.Separator,       new Vector4(0.38f, 0.32f, 0.22f, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.CheckMark,       ColAccent);
            ImGui.PushStyleColor(ImGuiCol.TextDisabled,    ColMuted);

            return (8, 22);
        }

        /// <summary>Undoes exactly what <see cref="PushTheme"/> pushed, so the counts cannot
        /// go stale in loose Pop calls at the end of Draw.</summary>
        private static void PopTheme((int vars, int colors) pushed)
        {
            ImGui.PopStyleColor(pushed.colors);
            ImGui.PopStyleVar(pushed.vars);
        }
    }
}
