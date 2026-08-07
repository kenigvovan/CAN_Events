using System.Numerics;
using ImGuiNET;

namespace canevents.src.Gui
{
    /// <summary>
    /// Button colour push/pop bookkeeping shared by the dialogs; each passes in its own palette.
    /// Previously duplicated, and the copies had drifted over whether to push ButtonActive.
    /// </summary>
    public readonly struct ButtonPalette
    {
        public readonly Vector4 Normal;
        public readonly Vector4 Hover;
        public readonly Vector4 Active;

        public ButtonPalette(Vector4 normal, Vector4 hover, Vector4 active)
        {
            Normal = normal;
            Hover = hover;
            Active = active;
        }
    }

    public static class GuiButtons
    {
        private static void Push(ButtonPalette p)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, p.Normal);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, p.Hover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, p.Active);
        }

        public static bool Button(string label, ButtonPalette palette)
        {
            Push(palette);
            bool clicked = ImGui.Button(label);
            ImGui.PopStyleColor(3);
            return clicked;
        }

        public static bool Button(string label, Vector2 size, ButtonPalette palette)
        {
            Push(palette);
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleColor(3);
            return clicked;
        }

        public static bool SmallButton(string label, ButtonPalette palette)
        {
            Push(palette);
            bool clicked = ImGui.SmallButton(label);
            ImGui.PopStyleColor(3);
            return clicked;
        }
    }
}
