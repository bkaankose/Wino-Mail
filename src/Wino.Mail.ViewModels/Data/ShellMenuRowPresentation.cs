using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.ViewModels.Data;

/// <summary>How a native shell row draws a menu item (the Windows ShellMenuTemplates row kinds).</summary>
public enum ShellMenuRowKind
{
    NewItem,
    Header,
    Account,
    Folder
}

/// <summary>
/// Everything a native shell row shows for one menu item: kind, glyph, title, the account subtitle and icon, the unread
/// count, the indent, the expansion chevron, the synchronization bar and the attention state.
/// </summary>
public sealed record ShellMenuRowContent(
    ShellMenuRowKind Kind,
    WinoIconGlyph Glyph,
    string Title,
    string? Subtitle,
    int UnreadCount,
    int Indent,
    bool? IsExpanded,
    IAccountIconInfo? AccountIcon,
    bool ShowsSync,
    bool IsSyncIndeterminate,
    double SyncValue,
    bool IsAttentionRequired,
    bool IsEnabled,
    bool IsHighlightable,
    bool ShowsIndicator);

/// <summary>
/// The Windows shell pane templates (ShellMenuTemplates.xaml) as plain row content for the iPad sidebar and the Android
/// drawer, pane and Mailboxes rows: which glyph and title each menu item type shows, how deep it is indented and which
/// rows expand. <see cref="Flatten"/> walks the menu the way the pane does: accounts always show their folders, other
/// parents only while expanded. Visibility, selection and children come from <see cref="ShellMenuItemRules"/>.
/// </summary>
public static class ShellMenuRowPresentation
{
    /// <summary>True for items whose children the row can expand or collapse.</summary>
    public static bool HasChildren(IMenuItem item) => ShellMenuItemRules.Children(item).Any(ShellMenuItemRules.IsShown);

    /// <summary>The visible rows in pane order with their depth.</summary>
    public static List<(IMenuItem Item, int Depth)> Flatten(IEnumerable<IMenuItem>? items, bool includeNewItems = true)
    {
        var rows = new List<(IMenuItem Item, int Depth)>();
        Add(rows, items, 0, includeNewItems);
        return rows;
    }

    private static void Add(List<(IMenuItem Item, int Depth)> rows, IEnumerable<IMenuItem>? items, int depth, bool includeNewItems)
    {
        foreach (var item in items?.ToArray() ?? [])
        {
            if (item is SeperatorItem || !ShellMenuItemRules.IsShown(item)) continue;
            if (!includeNewItems && KindOf(item) == ShellMenuRowKind.NewItem) continue;
            rows.Add((item, depth));
            if (item is IAccountMenuItem || item.IsExpanded) Add(rows, ShellMenuItemRules.Children(item), depth + 1, includeNewItems);
        }
    }

    public static ShellMenuRowKind KindOf(IMenuItem item) => item switch
    {
        NewMailMenuItem or NewContactMenuItem or NewTaskListMenuItem or NewCalendarEventMenuItem or NewAddressListMenuItem => ShellMenuRowKind.NewItem,
        ShellSectionHeaderMenuItem => ShellMenuRowKind.Header,
        MergedAccountMenuItem => ShellMenuRowKind.Account,
        IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account) => ShellMenuRowKind.Account,
        _ => ShellMenuRowKind.Folder
    };

    /// <summary>The row content of <paramref name="item"/> at <paramref name="depth"/> (folders indent one less than their depth).</summary>
    public static ShellMenuRowContent Describe(IMenuItem item, int depth, IPictureStorageService? pictures)
    {
        bool? expanded = HasChildren(item) && item is not IAccountMenuItem ? item.IsExpanded : null;
        // Folders indent by their folder ancestors (the children of More); other rows by their list depth.
        int indent = item is IBaseFolderMenuItem or MergedAccountMoreFolderMenuItem ? ShellMenuItemRules.FolderIndent(item) : Math.Max(0, depth - 1);
        var row = new ShellMenuRowContent(KindOf(item), WinoIconGlyph.Folder, string.Empty, null, 0, indent, expanded, null,
            false, true, 0, false, ShellMenuItemRules.IsEnabled(item), ShellMenuItemRules.IsHighlightable(item), ShellMenuItemRules.ShowsIndicator(item));

        switch (item)
        {
            case NewCalendarEventMenuItem:
                return row with { Glyph = WinoIconGlyph.NewMail, Title = Translator.CalendarEventCompose_NewEventButton };
            case NewMailMenuItem:
                return row with { Glyph = WinoIconGlyph.NewMail, Title = Translator.MenuNewMail };
            case NewContactMenuItem:
                return row with { Glyph = WinoIconGlyph.NewMail, Title = Translator.ContactsPane_NewContact };
            case NewAddressListMenuItem:
                return row with { Glyph = WinoIconGlyph.PersonAdd, Title = Translator.ContactList_NewTitle };
            case NewTaskListMenuItem:
                return row with { Glyph = WinoIconGlyph.Add, Title = Translator.ToDoPage_NewList };
            case ShellSectionHeaderMenuItem header:
                return row with { Title = header.Title };
            case FixAccountIssuesMenuItem fix:
                return row with
                {
                    Glyph = WinoIconGlyph.Warning,
                    Title = fix.Account?.AttentionReason == AccountAttentionReason.MissingSystemFolderConfiguration
                        ? Translator.Shell_FixMissingSystemFolders
                        : Translator.Shell_FixInvalidCredentials,
                    Indent = 0,
                    IsExpanded = null
                };
            case MergedAccountMenuItem merged:
                return row with
                {
                    Title = merged.MergedAccountName ?? string.Empty,
                    Subtitle = merged.IsSynchronizationProgressVisible && !string.IsNullOrWhiteSpace(merged.SynchronizationStatus)
                        ? merged.SynchronizationStatus
                        : merged.MergedAccountCount + Translator.MenuMergedAccountItemAccountsSuffix,
                    UnreadCount = merged.UnreadItemCount,
                    AccountIcon = MailAccountIconInfoFactory.CreateProviderFallback(MailProviderType.IMAP4, SpecialImapProvider.None),
                    ShowsSync = merged.IsSynchronizationProgressVisible,
                    IsSyncIndeterminate = merged.IsProgressIndeterminate,
                    SyncValue = merged.SynchronizationProgressValue
                };
            case IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account):
                return row with
                {
                    Title = account.AccountName ?? string.Empty,
                    Subtitle = account.AccountAddress,
                    UnreadCount = account.UnreadItemCount,
                    AccountIcon = account.Account is not { } mailAccount
                        ? MailAccountIconInfoFactory.CreateProviderFallback(MailProviderType.IMAP4, SpecialImapProvider.None)
                        : pictures is null
                            ? MailAccountIconInfoFactory.CreateProviderFallback(mailAccount.ProviderType, mailAccount.SpecialImapProvider)
                            : MailAccountIconInfoFactory.Create(mailAccount, pictures),
                    ShowsSync = account.IsSynchronizationProgressVisible,
                    IsSyncIndeterminate = account.IsProgressIndeterminate,
                    SyncValue = account.SynchronizationProgressValue,
                    IsAttentionRequired = account.IsAttentionRequired
                };
            case MailCategoryMenuItem category:
                return row with { Glyph = WinoIconGlyph.Tag, Title = category.FolderName ?? string.Empty, UnreadCount = category.ShowUnreadCount ? category.UnreadItemCount : 0, IsExpanded = null };
            case MergedMailCategoryMenuItem mergedCategory:
                return row with { Glyph = WinoIconGlyph.Tag, Title = mergedCategory.FolderName ?? string.Empty, UnreadCount = mergedCategory.UnreadItemCount, IsExpanded = null };
            case MergedAccountMoreFolderMenuItem:
                return row with { Glyph = WinoIconGlyph.SpecialFolderMore, Title = Translator.More };
            case IBaseFolderMenuItem folder:
                return row with
                {
                    Glyph = SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType),
                    Title = folder.FolderName ?? string.Empty,
                    UnreadCount = folder.ShowUnreadCount ? folder.UnreadItemCount : 0
                };
            case UngroupedCalendarMenuItem calendar:
                return row with { Glyph = WinoIconGlyph.Calendar, Title = calendar.Parameter.Name ?? string.Empty, IsExpanded = null };
            case AccountCalendarGroupMenuItem group:
                return row with { Glyph = WinoIconGlyph.Calendar, Title = group.Parameter.Account?.Name ?? string.Empty, IsExpanded = null };
            case ContactFilterViewModel filter:
                return row with { Glyph = filter.IsCategory ? WinoIconGlyph.Tag : WinoIconGlyph.People, Title = filter.Name ?? string.Empty, UnreadCount = filter.Count, IsExpanded = null };
            case MyDayTaskMenuItem myDay:
                return row with { Glyph = WinoIconGlyph.WeatherSunny, Title = Translator.ToDoPage_MyDay, UnreadCount = myDay.Count, IsExpanded = null };
            case PlannedTaskMenuItem planned:
                return row with { Glyph = WinoIconGlyph.Calendar, Title = Translator.ToDoPage_Planned, UnreadCount = planned.Count, IsExpanded = null };
            case ImportantTaskMenuItem important:
                return row with { Glyph = WinoIconGlyph.Star, Title = Translator.ToDoPage_Important, UnreadCount = important.Count, IsExpanded = null };
            case AccountTaskListGroupMenuItem taskGroup:
                return row with { Glyph = WinoIconGlyph.PanelLeft, Title = taskGroup.Title ?? string.Empty };
            case AccountTaskListMenuItem list:
                return row with { Glyph = WinoIconGlyph.Dot, Title = list.Title ?? string.Empty, UnreadCount = list.Count, IsExpanded = null };
            case RateMenuItem:
                return row with { Glyph = WinoIconGlyph.Heart, Title = Translator.MenuRate, Indent = 0, IsExpanded = null };
            case SettingsItem:
                return row with { Glyph = WinoIconGlyph.Settings, Title = Translator.MenuSettings, Indent = 0, IsExpanded = null };
            default:
                return row with { Title = item.ToString() ?? string.Empty };
        }
    }
}
