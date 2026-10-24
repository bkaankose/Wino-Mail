namespace Wino.Core.Domain.Enums;

/// <summary>
/// One arrangeable entry of the reader command bar. Toggle pairs (read state, flag, archive, junk, theme)
/// are one entry; the bar shows whichever state fits the open message.
/// </summary>
/// <remarks>Persisted by name in <see cref="Interfaces.IPreferencesService.ReaderCommandLayout"/>.</remarks>
public enum ReaderCommandAction
{
    PopOut,
    EditorTheme,
    Reply,
    ReplyAll,
    Forward,
    Archive,
    Delete,
    Flag,
    ReadState,
    ReaderView,
    SaveAs,
    Print,
    ViewMessageSource,
    Junk
}
