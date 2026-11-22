namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    internal EditorInsertItems? InsertItems { get; private set; }

    partial void InitializeInsert()
    {
        if (FormatToolbar is not { } toolbar) return;
        InsertItems = new EditorInsertItems(toolbar, new EditorToolbarFeatures(), () => Root.GetRoot() as Gtk.Window, ReportError);
    }

    partial void DeactivateInsert()
    {
        InsertItems?.Dispose();
        InsertItems = null;
    }
}
