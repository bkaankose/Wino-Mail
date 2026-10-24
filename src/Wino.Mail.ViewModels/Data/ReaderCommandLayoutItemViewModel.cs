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
                                             WinoIconGlyph secondIcon = WinoIconGlyph.None)
    {
        Action = action;
        Title = title;
        Icon = icon;
        SecondIcon = secondIcon;
    }

    public ReaderCommandAction Action { get; }
    public string Title { get; }
    public WinoIconGlyph Icon { get; }
    public WinoIconGlyph SecondIcon { get; }
    public bool HasSecondIcon => SecondIcon != WinoIconGlyph.None;
    public string VisibilityAutomationName => string.Format(Translator.ReaderActions_ShowInBarFormat, Title);

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    public ReaderCommandLayoutEntry ToEntry() => new(Action, IsVisible);

    public static ReaderCommandLayoutItemViewModel Create(ReaderCommandLayoutEntry entry)
    {
        var item = entry.Action switch
        {
            ReaderCommandAction.PopOut => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.Buttons_PopOut, WinoIconGlyph.OpenInNewWindow),
            ReaderCommandAction.EditorTheme => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_ThemeToggle, WinoIconGlyph.DarkEditor, WinoIconGlyph.LightEditor),
            ReaderCommandAction.Reply => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Reply, WinoIconGlyph.Reply),
            ReaderCommandAction.ReplyAll => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_ReplyAll, WinoIconGlyph.ReplyAll),
            ReaderCommandAction.Forward => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Forward, WinoIconGlyph.Forward),
            ReaderCommandAction.Archive => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_ArchiveToggle, WinoIconGlyph.Archive, WinoIconGlyph.UnArchive),
            ReaderCommandAction.Delete => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Delete, WinoIconGlyph.Delete),
            ReaderCommandAction.Flag => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_FlagToggle, WinoIconGlyph.Flag, WinoIconGlyph.ClearFlag),
            ReaderCommandAction.ReadState => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_ReadToggle, WinoIconGlyph.MarkRead, WinoIconGlyph.MarkUnread),
            ReaderCommandAction.ReaderView => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.Reader_ReaderView, WinoIconGlyph.Document),
            ReaderCommandAction.SaveAs => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_SaveAs, WinoIconGlyph.Save),
            ReaderCommandAction.Print => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_Print, WinoIconGlyph.Print),
            ReaderCommandAction.ViewMessageSource => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.MailOperation_ViewMessageSource, WinoIconGlyph.ViewMessageSource),
            ReaderCommandAction.Junk => new ReaderCommandLayoutItemViewModel(entry.Action, Translator.ReaderActions_JunkToggle, WinoIconGlyph.Blocked),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Action, null)
        };

        item.IsVisible = entry.IsVisible;
        return item;
    }
}
