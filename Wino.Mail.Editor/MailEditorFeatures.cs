using Windows.Storage;

namespace Wino.Mail.Editor;

[Flags]
public enum MailEditorFeatures
{
    None = 0,
    FontFamily = 1 << 0,
    FontSize = 1 << 1,
    TextStyles = 1 << 2,
    TextColor = 1 << 3,
    HighlightColor = 1 << 4,
    Paragraph = 1 << 5,
    LineHeight = 1 << 6,
    UndoRedo = 1 << 7,
    Hyperlinks = 1 << 8,
    InlineImages = 1 << 9,
    Attachments = 1 << 10,
    Tables = 1 << 11,
    Emoji = 1 << 12,
    SpellCheck = 1 << 13,
    SmimeSigning = 1 << 14,
    SmimeEncryption = 1 << 15,
    All = FontFamily | FontSize | TextStyles | TextColor | HighlightColor | Paragraph | LineHeight | UndoRedo |
          Hyperlinks | InlineImages | Attachments | Tables | Emoji | SpellCheck | SmimeSigning | SmimeEncryption
}

public enum MailEditorCommandKind
{
    AddAttachments,
    InsertInlineImages,
    SmimeSigningChanged,
    SmimeEncryptionChanged
}

public sealed class MailEditorCommandRequestedEventArgs : EventArgs
{
    public MailEditorCommandRequestedEventArgs(MailEditorCommandKind command, bool? toggleValue = null)
    {
        Command = command;
        ToggleValue = toggleValue;
    }

    public MailEditorCommandKind Command { get; }
    public bool? ToggleValue { get; }
    public bool Handled { get; set; }
}

public sealed class MailEditorFilesSelectedEventArgs : EventArgs
{
    public MailEditorFilesSelectedEventArgs(IReadOnlyList<StorageFile> files) => Files = files;
    public IReadOnlyList<StorageFile> Files { get; }
}

public enum EditorToolbarCategory
{
    Formatting,
    Insert,
    Table,
    Security
}
