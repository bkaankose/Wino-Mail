using System.ComponentModel;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Views.Extras;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// Mailboxes (Mail on iPhone), the Mail content of the shell drawer (<see cref="ShellDrawer"/>): the accounts, linked
/// accounts and folders the shared <c>MailAppShellViewModel</c> publishes, as Windows shell rows with unread pills. A
/// folder tap closes the drawer and lists the folder; "More" and parent folders expand in place; a long press offers the
/// shared folder and account actions. The compose button starts a new message, pull to refresh synchronizes the loaded
/// account, a status strip under the rows shows the synchronization, and a pinned Settings row at the foot opens Settings
/// (the account avatar never does).
/// </summary>
public sealed class MailboxesViewController : UIViewController
{
    private const double DisclosureHitWidth = 56;

    private readonly PhoneShellViewController _shell;
    private readonly IServiceProvider _services;
    private readonly MailboxesRows _rows;
    private readonly UITableView _table = new(default, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WinoStateView _empty = new(WinoIconGlyph.Mail, Translator.IOS_MailboxesTitle, null);
    private readonly UIRefreshControl _refresh = new();
    private readonly UIView _status = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UILabel _statusLabel = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly WinoSyncBar _statusBar = new();
    private IShellMenuProvider? _provider;

    public MailboxesViewController(PhoneShellViewController shell, IServiceProvider services)
    {
        _shell = shell;
        _services = services;
        _rows = new MailboxesRows(services.GetService<IPictureStorageService>());
        Title = Translator.IOS_MailboxesTitle;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
    }

    public override void LoadView()
    {
        // The drawer's blur is the surface: the rows sit on it directly, without a zone.
        var root = new UIView { BackgroundColor = UIColor.Clear };

        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.ContentInset = new UIEdgeInsets(4, 0, 6, 0);
        _table.RegisterClassForCellReuse(typeof(WinoShellRowCell), WinoShellRowCell.ReuseIdentifier);
        _table.Source = _rows;
        _table.RefreshControl = _refresh;
        WinoAccessibility.Label(_table, Translator.IOS_MailboxesTitle);
        _refresh.ValueChanged += (_, _) => _ = RefreshAsync();

        // The disclosure chevron of a folder that also opens a list toggles its children instead of opening it.
        var disclosure = new UITapGestureRecognizer(DisclosureTapped) { CancelsTouchesInView = true };
        disclosure.ShouldReceiveTouch = (_, touch) => DisclosureItem(touch.LocationInView(_table)) is not null;
        _table.AddGestureRecognizer(disclosure);

        _statusBar.Hidden = false;
        var statusStack = WinoLayout.Insets(WinoLayout.VStack(4, _statusLabel, _statusBar), 8, 16, 10, 16);
        statusStack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(statusStack, _status);
        WinoAccessibility.Label(_status, Translator.Accessibility_SynchronizationStatus);
        _status.IsAccessibilityElement = true;

        var listHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_table, listHost, 0, 6, 0, 6);
        _empty.Place(listHost);
        _empty.Hidden = true;

        var column = WinoLayout.VStack(0, listHost, _status, DrawerSettingsRow.Create(_shell.OpenSettingsFromDrawer));
        column.Alignment = UIStackViewAlignment.Fill;
        column.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            // Pinned above the home indicator.
            column.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor)
        ]);

        _rows.Changed += (_, _) => Reload();
        _rows.Invoked += (_, item) => RowInvoked(item);
        _rows.MenuProvider = BuildMenu;
        View = root;
        BuildBarItems();
    }

    private void BuildBarItems()
    {
        var items = new List<UIBarButtonItem> { PhoneBarItems.Compose(_shell.Compose) };
        if (_services.GetService<IUIKitDailyBriefingPresenter>() is { } presenter)
        {
            // Shown while the briefing is available (Wino Intelligence access), like the Windows title-bar button.
            var briefing = new UIBarButtonItem(WinoIcons.TemplateImage(WinoIconGlyph.DailyBriefing, 22), UIBarButtonItemStyle.Plain, (_, _) => _shell.ShowBriefing())
            {
                AccessibilityLabel = Translator.DailyBriefing_Title,
                Hidden = !presenter.IsAvailable
            };
            presenter.UnseenItemsChanged += (_, _) => InvokeOnMainThread(() => briefing.Hidden = !presenter.IsAvailable);
            items.Add(briefing);
        }
        NavigationItem.RightBarButtonItems = items.ToArray();
    }

    /// <summary>Shows the menu the Mail provider published (null while another mode is active).</summary>
    public void SetMenu(ShellMenu? menu)
    {
        _ = View;
        _rows.SetMenu(menu);
        Observe(menu is null ? null : _shell.MailProvider);
    }

    /// <summary>The shell's selected folder or account changed.</summary>
    public void SetSelected(object? item)
    {
        _ = View;
        _rows.SetSelected(item);
    }

    private void Reload()
    {
        _table.ReloadData();
        if (_rows.SelectedIndexPath is { } selected) _table.SelectRow(selected, false, UITableViewScrollPosition.None);
        _empty.Hidden = _rows.Rows.Count > 0;
    }

    private void RowInvoked(IMenuItem item)
    {
        if (!ShellMenuItemRules.IsInteractive(item) || !ShellMenuItemRules.IsEnabled(item)) return;
        // The folder already listed (or the loaded account, whose Inbox is listed): close the drawer over the retained
        // list instead of reloading it, like Mail on iPhone. The shared shell would treat the tap as a no-op.
        if ((ReferenceEquals(item, _shell.SelectedMenuItem) || IsListedInbox(item)) && _shell.ShowRetainedMailContent()) return;
        if (MailboxesRows.HasChildren(item) && !MailboxesRows.Navigates(item))
        {
            item.IsExpanded = !item.IsExpanded;
            return;
        }
        _shell.OpenFromMailboxes(item);
    }

    /// <summary>True when <paramref name="item"/> is the loaded account and its Inbox is the listed folder.</summary>
    private bool IsListedInbox(IMenuItem item)
        => item is IAccountMenuItem { IsSelected: true } account
           && _shell.SelectedMenuItem is IBaseFolderMenuItem { SpecialFolderType: SpecialFolderType.Inbox } folder
           && folder.HandlingFolders.All(handling => account.HoldingAccounts.Any(holder => holder.Id == handling.MailAccountId));

    private IMenuItem? DisclosureItem(CoreGraphics.CGPoint point)
    {
        if (point.X < _table.Bounds.Width - (nfloat)DisclosureHitWidth) return null;
        if (_table.IndexPathForRowAtPoint(point) is not { } indexPath || _rows.ItemAt(indexPath) is not { } item) return null;
        return MailboxesRows.HasChildren(item) && MailboxesRows.Navigates(item) ? item : null;
    }

    private void DisclosureTapped(UITapGestureRecognizer recognizer)
    {
        if (DisclosureItem(recognizer.LocationInView(_table)) is { } item) item.IsExpanded = !item.IsExpanded;
    }

    private async Task RefreshAsync()
    {
        try { await _shell.SynchronizeAsync(); }
        finally
        {
            // The account rows and the status strip show the running synchronization; the spinner only acknowledges the pull.
            await Task.Delay(600);
            InvokeOnMainThread(_refresh.EndRefreshing);
        }
    }

    // ---- Synchronization status strip ----

    private void Observe(IShellMenuProvider? provider)
    {
        if (ReferenceEquals(provider, _provider)) return;
        if (_provider is not null) _provider.PropertyChanged -= ProviderChanged;
        _provider = provider;
        if (_provider is not null) _provider.PropertyChanged += ProviderChanged;
        UpdateStatus();
    }

    private void ProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IShellMenuProvider.SynchronizationState) or nameof(IShellMenuProvider.SynchronizationDescription) or null)
            InvokeOnMainThread(UpdateStatus);
    }

    private void UpdateStatus()
    {
        var state = _provider?.SynchronizationState ?? default;
        _status.Hidden = !state.IsSynchronizing;
        if (!state.IsSynchronizing) return;
        _statusLabel.Text = _provider?.SynchronizationDescription ?? string.Empty;
        _statusBar.Update(state.IsIndeterminate, state.ProgressPercentage);
        _status.AccessibilityValue = _statusLabel.Text;
    }

    // ---- Long-press menus (Windows ShellMenuTemplates context flyouts, Mac ShellSidebarContextMenus) ----

    private UIMenu? BuildMenu(IMenuItem item)
    {
        var mail = _services.GetService<IMailShellClient>();
        var actions = new List<UIMenuElement>();
        switch (item)
        {
            case IBaseFolderMenuItem { IsMoveTarget: true } folder when item is FolderMenuItem or MergedAccountFolderMenuItem && mail is not null:
                var group = new List<UIMenuElement>();
                foreach (var action in mail.GetFolderContextMenuActions(folder) ?? [])
                {
                    if (action.Operation == FolderOperation.Seperator)
                    {
                        if (group.Count > 0) actions.Add(PhoneBarItems.Inline(group.ToArray()));
                        group = [];
                        continue;
                    }
                    var title = FolderOperationDisplay.GetTitle(action.Operation);
                    if (string.IsNullOrEmpty(title)) continue;
                    var operation = action.Operation;
                    group.Add(Action(FolderOperationDisplay.AsksForInput(operation) ? $"{title}…" : title, FolderOperationDisplay.GetGlyph(operation),
                        () => mail.PerformFolderOperationAsync(operation, folder), action.IsEnabled, FolderOperationDisplay.IsDestructive(operation)));
                }
                if (group.Count > 0) actions.Add(PhoneBarItems.Inline(group.ToArray()));
                break;
            case MergedAccountMenuItem merged when mail is not null:
                actions.Add(Action(Translator.FolderOperation_MarkAllAsRead, WinoIconGlyph.MarkRead, () => mail.MarkAccountInboxesAsReadAsync(merged)));
                break;
            case IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account):
                if (account.Account is { } details)
                    actions.Add(Action(Translator.AccountContextMenu_ManageAccountSettings, WinoIconGlyph.ManageAccounts, () =>
                    {
                        _services.GetRequiredService<INavigationService>().Navigate(WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(details.Id, account.AccountDetailsTab));
                        return Task.CompletedTask;
                    }));
                if (account.IsAttentionRequired && account.Account is { } attention && mail is not null)
                    actions.Add(Action(Translator.Buttons_FixAccount, WinoIconGlyph.Warning, () => mail.HandleAccountAttentionAsync(attention)));
                else if (account.SupportsAccountSynchronization)
                    actions.Add(Action(Translator.Buttons_Sync, WinoIconGlyph.Sync, account.SynchronizeAccountAsync));
                if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions && mail is not null)
                {
                    actions.Add(Action($"{Translator.AccountContextMenu_CreateFolder}…", WinoIconGlyph.CreateFolder, () => mail.CreateRootFolderAsync(mailAccount)));
                    actions.Add(Action(Translator.FolderOperation_MarkAllAsRead, WinoIconGlyph.MarkRead, () => mail.MarkAccountInboxesAsReadAsync(mailAccount)));
                }
                break;
        }
        return actions.Count == 0 ? null : UIMenu.Create(actions.ToArray());
    }

    private UIAction Action(string title, WinoIconGlyph glyph, Func<Task> run, bool enabled = true, bool destructive = false)
        => PhoneBarItems.Action(title, glyph, () => _ = RunAsync(run), enabled, destructive);

    private async Task RunAsync(Func<Task> run)
    {
        try { await run(); }
        catch (Exception exception) { _services.GetService<IWinoLogger>()?.CaptureException(exception, nameof(MailboxesViewController)); }
    }

#if DEBUG
    internal string DebugDescribe()
        => string.Join(" | ", _rows.Rows.Select(row => new string('>', row.Depth) + row.Item switch
        {
            IAccountMenuItem account => $"[account] {account.HoldingAccounts.FirstOrDefault()?.Name} unread={account.UnreadItemCount}",
            IBaseFolderMenuItem folder => $"{folder.FolderName} unread={folder.UnreadItemCount}{(MailboxesRows.HasChildren(folder) ? folder.IsExpanded ? " (-)" : " (+)" : string.Empty)}",
            _ => row.Item.GetType().Name
        }));

    internal string DebugOpen(string name)
    {
        var row = _rows.Rows.FirstOrDefault(entry => entry.Item is IBaseFolderMenuItem folder && string.Equals(folder.FolderName, name, StringComparison.OrdinalIgnoreCase));
        if (row.Item is null) return "no folder " + name;
        RowInvoked(row.Item);
        return "ok";
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Observe(null);
            _table.Source = null;
            _rows.Dispose();
        }
        base.Dispose(disposing);
    }
}
