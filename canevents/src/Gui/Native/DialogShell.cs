using System;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Native
{
    // Caller adds the body and finishes with EndChildElements().Compose().
    public static class DialogShell
    {
        // Background children start 20px below the top but the title bar is 31px tall.
        public const double BodyTop = 14;

        // System.Action spelled out: Vintagestory.API.Common has an Action<> of its own.
        public static GuiComposer Begin(ICoreClientAPI capi, string composerKey, string title, System.Action onClose)
        {
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            return capi.Gui
                .CreateCompo(composerKey, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(title, onClose)
                .BeginChildElements(bgBounds);
        }
    }
}
