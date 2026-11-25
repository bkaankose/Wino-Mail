using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    internal EditorFormatToolbar? FormatToolbar { get; private set; }

    partial void InitializeToolbar()
    {
        if (Editor is not { } editor) return;
        var toolbar = FormatToolbar = new EditorFormatToolbar(EditorToolbarLayout.Tabbed, new EditorToolbarFeatures(), ViewModel.PreferencesService, ReportError);
        var undo = new EditorFormatButton(WinoIconGlyph.ArrowUndo, Translator.MacOSMenu_Undo, () => toolbar.Run(EditorCommand.Undo()), tooltip: $"{Translator.MacOSMenu_Undo} (Ctrl+Z)");
        var redo = new EditorFormatButton(WinoIconGlyph.ArrowRedo, Translator.MacOSMenu_Redo, () => toolbar.Run(EditorCommand.Redo()), tooltip: $"{Translator.MacOSMenu_Redo} (Ctrl+Y)");
        Bindings.Own(undo);
        Bindings.Own(redo);
        toolbar.AddTrailingItem(EditorFormatToolbar.Item(undo));
        toolbar.AddTrailingItem(EditorFormatToolbar.Item(redo));
        toolbar.Root.SetMarginTop(4);
        toolbar.Root.SetMarginBottom(2);
        toolbar.Root.SetMarginStart(12);
        toolbar.Root.SetMarginEnd(12);
        ToolbarSlot.Append(toolbar.Root);
        toolbar.Attach(editor);
    }

    partial void DeactivateToolbar()
    {
        if (FormatToolbar is not { } toolbar) return;
        toolbar.Dispose();
        ToolbarSlot.Remove(toolbar.Root);
        FormatToolbar = null;
    }
}
