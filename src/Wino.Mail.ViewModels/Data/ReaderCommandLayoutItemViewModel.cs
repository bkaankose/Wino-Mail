using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One reorderable action on the Reader actions page.
/// </summary>
public partial class ReaderCommandLayoutItemViewModel : ObservableObject
{
    private ReaderCommandLayoutItemViewModel(ReaderCommandAction action,
                                             string title,
                                             WinoIconGlyph icon,
                                             WinoIconGlyph secondIcon = WinoIconGlyph.None,
                                             bool isTogglePair = false,
                                             string note = "")
    {
        Action = action;
        Title = title;
        Icon = icon;
        SecondIcon = secondIcon;
        IsTogglePair = isTogglePair;
        Note = note;
    }

    public ReaderCommandAction Action { get; }
    public string Title { get; }
    public WinoIconGlyph Icon { get; }
    public WinoIconGlyph SecondIcon { get; }
    public bool HasSecondIcon => SecondIcon != WinoIconGlyph.None;
    public bool IsTogglePair { get; }
    public string Note { get; }
    public string VisibilityAutomationName => string.Format(Translator.ReaderActions_ShowInBarFormat, Title);

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    public ReaderCommandLayoutEntry ToEntry() => new(Action, IsVisible);

    public static ReaderCommandLayoutItemViewModel Create(ReaderCommandLayoutEntry entry)
    {
        var item = entry.Action switch
        {
            ReaderCommandAction.PopOut => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.Buttons_PopOut, WinoIconGlyph.OpenInNewWindow, note: Translator.ReaderActions_NoteMainWindowOnly),
            ReaderCommandAction.EditorTheme => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_ThemeToggle, WinoIconGlyph.DarkEditor, WinoIconGlyph.LightEditor, true),
            ReaderCommandAction.Reply => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Reply, WinoIconGlyph.Reply, note: Translator.ReaderActions_NoteNotOnDrafts),
            ReaderCommandAction.ReplyAll => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_ReplyAll, WinoIconGlyph.ReplyAll, note: Translator.ReaderActions_NoteNotOnDrafts),
            ReaderCommandAction.Forward => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Forward, WinoIconGlyph.Forward, note: Translator.ReaderActions_NoteNotOnDrafts),
            ReaderCommandAction.Archive => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_ArchiveToggle, WinoIconGlyph.Archive, WinoIconGlyph.UnArchive, true),
            ReaderCommandAction.Delete => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Delete, WinoIconGlyph.Delete),
            ReaderCommandAction.Flag => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_FlagToggle, WinoIconGlyph.Flag, WinoIconGlyph.ClearFlag, true),
            ReaderCommandAction.ReadState => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_ReadToggle, WinoIconGlyph.MarkRead, WinoIconGlyph.MarkUnread, true),
            ReaderCommandAction.ReaderView => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.Reader_ReaderView, WinoIconGlyph.Document, note: Translator.ReaderActions_NoteReaderView),
            ReaderCommandAction.SaveAs => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_SaveAs, WinoIconGlyph.Save, note: Translator.ReaderActions_NoteSaveAs),
            ReaderCommandAction.Print => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Print, WinoIconGlyph.Print),
            ReaderCommandAction.ViewMessageSource => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_ViewMessageSource, WinoIconGlyph.ViewMessageSource),
            ReaderCommandAction.Junk => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_JunkToggle, WinoIconGlyph.Blocked, isTogglePair: true, note: Translator.ReaderActions_NoteNotOnDraftsOrSent),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Action, null)
        };

        item.IsVisible = entry.IsVisible;
        return item;
    }
}
