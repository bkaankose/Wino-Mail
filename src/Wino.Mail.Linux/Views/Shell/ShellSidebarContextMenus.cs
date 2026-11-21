using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell.Contracts;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>One entry of a sidebar menu, independent of the widget that shows it. Children makes it a submenu.</summary>
internal sealed record SidebarMenuEntry(string Title, Func<Task>? Run, bool IsEnabled = true, bool IsSeparator = false,
    WinoIconGlyph Glyph = WinoIconGlyph.None, bool IsDestructive = false, SidebarMenuEntry[]? Children = null)
{
    public static SidebarMenuEntry Separator { get; } = new(string.Empty, null, IsSeparator: true);
}

/// <summary>What the menus act through. Mail is null outside a configured mail shell.</summary>
internal sealed record ShellSidebarMenuContext(
    Func<IMailShellClient?> Mail,
    INavigationService Navigation,
    Func<IAccountNavigationMenuItem, Task> FixAccount,
    Action<Exception> Error);

/// <summary>
/// The sidebar's context menus as data, built from the same shared services as the Windows ShellMenuTemplates context
/// flyouts and the macOS pane menus: folder operations, account actions and the merged inbox. Other features add their
/// entries through <see cref="AddExtension"/>, once at startup.
/// </summary>
internal static class ShellSidebarContextMenus
{
    private static readonly List<Func<IMenuItem, ShellSidebarMenuContext, SidebarMenuEntry[]>> Extensions = [];

    public static void AddExtension(Func<IMenuItem, ShellSidebarMenuContext, SidebarMenuEntry[]> provider)
    {
        lock (Extensions) Extensions.Add(provider);
    }

    /// <summary>The row's entries: built-in ones, then each extension's after a separator. Empty when the row has no menu.</summary>
    public static SidebarMenuEntry[] Build(IMenuItem item, ShellSidebarMenuContext context)
    {
        var entries = new List<SidebarMenuEntry>(BuiltIn(item, context));
        Func<IMenuItem, ShellSidebarMenuContext, SidebarMenuEntry[]>[] extensions;
        lock (Extensions) extensions = [.. Extensions];
        foreach (var extension in extensions)
        {
            SidebarMenuEntry[] added;
            try { added = extension(item, context); }
            catch (Exception exception)
            {
                context.Error(exception);
                continue;
            }
            if (added.Length == 0) continue;
            entries.Add(SidebarMenuEntry.Separator);
            entries.AddRange(added);
        }
        return Tidy(entries);
    }

    private static SidebarMenuEntry[] BuiltIn(IMenuItem item, ShellSidebarMenuContext context)
    {
        switch (item)
        {
            case FolderMenuItem or MergedAccountFolderMenuItem when item is IBaseFolderMenuItem { IsMoveTarget: true } folder:
                return FolderEntries(folder, context);
            case MergedAccountMenuItem merged:
                return context.Mail() is { } mergedMail ? [MarkInboxes(merged, mergedMail)] : [];
            case IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account):
                return AccountEntries(account, context);
            default:
                return [];
        }
    }

    private static SidebarMenuEntry[] FolderEntries(IBaseFolderMenuItem folder, ShellSidebarMenuContext context)
    {
        if (context.Mail() is not { } mail) return [];
        var actions = mail.GetFolderContextMenuActions(folder);
        if (actions is null) return [];
        var entries = new List<SidebarMenuEntry>();
        foreach (var action in actions)
        {
            var operation = action.Operation;
            if (operation == FolderOperation.Seperator)
            {
                entries.Add(SidebarMenuEntry.Separator);
                continue;
            }
            var title = FolderOperationDisplay.GetTitle(operation);
            if (string.IsNullOrEmpty(title)) continue;
            if (FolderOperationDisplay.AsksForInput(operation)) title += "…";
            entries.Add(new SidebarMenuEntry(title, () => mail.PerformFolderOperationAsync(operation, folder), action.IsEnabled,
                Glyph: FolderOperationDisplay.GetGlyph(operation), IsDestructive: FolderOperationDisplay.IsDestructive(operation)));
        }
        return [.. entries];
    }

    private static SidebarMenuEntry[] AccountEntries(IAccountNavigationMenuItem account, ShellSidebarMenuContext context)
    {
        var entries = new List<SidebarMenuEntry>
        {
            new(Translator.AccountContextMenu_ManageAccountSettings, () => OpenAccountSettings(account, context), account.Account is not null,
                Glyph: WinoIconGlyph.ManageAccounts)
        };
        // A pending sign-in can only make Sync fail, so the menu offers the fix in its place.
        if (account.IsAttentionRequired) entries.Add(new(Translator.Buttons_FixAccount, () => context.FixAccount(account), Glyph: WinoIconGlyph.Warning));
        else if (account.SupportsAccountSynchronization) entries.Add(new(Translator.Buttons_Sync, account.SynchronizeAccountAsync, Glyph: WinoIconGlyph.Sync));

        if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions && context.Mail() is { } mail)
        {
            entries.Add(new(Translator.AccountContextMenu_CreateFolder + "…", () => mail.CreateRootFolderAsync(mailAccount), Glyph: WinoIconGlyph.CreateFolder));
            entries.Add(MarkInboxes(mailAccount, mail));
        }
        return [.. entries];
    }

    private static SidebarMenuEntry MarkInboxes(IAccountMenuItem account, IMailShellClient mail)
        => new(Translator.FolderOperation_MarkAllAsRead, () => mail.MarkAccountInboxesAsReadAsync(account), Glyph: WinoIconGlyph.MarkRead);

    private static Task OpenAccountSettings(IAccountNavigationMenuItem account, ShellSidebarMenuContext context)
    {
        if (account.Account is not { } mailAccount) return Task.CompletedTask;
        if (!context.Navigation.ChangeApplicationMode(WinoApplicationMode.Settings, new ShellModeActivationContext
            {
                Parameter = new SettingsPageActivationContext(WinoPage.ManageAccountsPage, new AccountDetailsNavigationContext(mailAccount.Id, account.AccountDetailsTab)),
                SuppressStartupFlows = true
            }))
            Serilog.Log.Information("Account settings are not available.");
        return Task.CompletedTask;
    }

    private static SidebarMenuEntry[] Tidy(List<SidebarMenuEntry> entries)
    {
        var tidy = new List<SidebarMenuEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsSeparator && (tidy.Count == 0 || tidy[^1].IsSeparator)) continue;
            tidy.Add(entry);
        }
        while (tidy.Count > 0 && tidy[^1].IsSeparator) tidy.RemoveAt(tidy.Count - 1);
        return [.. tidy];
    }
}
