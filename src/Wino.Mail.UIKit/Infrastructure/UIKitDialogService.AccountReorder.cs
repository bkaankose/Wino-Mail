using System.Collections.ObjectModel;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>Account order (Windows AccountReorderDialog, the Mac reorder sheet).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>
    /// A sheet with the account layout in a table that is always in editing mode: the first section holds the top-level
    /// accounts and groups, then each group has a section with its own accounts. Rows move only inside their section;
    /// moving accounts between groups happens on the group's page. The sheet reads the layout itself, because the flat
    /// account list the settings page passes does not carry the groups. Save stores the layout order; Cancel or
    /// swiping the sheet away changes nothing.
    /// </summary>
    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => PresentAsync(async presenter =>
    {
        var accountService = services.GetRequiredService<IAccountService>();
        var accounts = await accountService.GetAccountsAsync();
        var groups = await accountService.GetAccountGroupsAsync();
        var items = AccountReorderItem.FromLayout(AccountLayoutBuilder.Build(accounts, groups));

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new AccountReorderViewController(items) { Title = Translator.SettingsReorderAccounts_Title };
        var sheet = new UINavigationController(page)
        {
            ModalPresentationStyle = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad ? UIModalPresentationStyle.FormSheet : UIModalPresentationStyle.PageSheet
        };
        var dismissal = new SheetDismissal();
        void Finish(bool saved)
        {
            _dismissals.Remove(dismissal);
            completion.TrySetResult(saved);
        }
        dismissal.Dismissed = () => Finish(false);
        _dismissals.Add(dismissal);

        var save = new UIBarButtonItem { Title = Translator.Buttons_Save, Style = UIBarButtonItemStyle.Done };
        var cancel = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => sheet.DismissViewController(true, () => Finish(false)));
        save.Clicked += async (_, _) =>
        {
            save.Enabled = false;
            cancel.Enabled = false;
            sheet.ModalInPresentation = true;
            try
            {
                await accountService.SaveAccountLayoutOrderAsync(AccountReorderItem.ToOrder(items));
                sheet.DismissViewController(true, () => Finish(true));
            }
            catch (Exception exception)
            {
                Serilog.Log.Warning(exception, "Could not save the account order.");
                save.Enabled = true;
                cancel.Enabled = true;
                sheet.ModalInPresentation = false;
                InfoBarMessage(Translator.GeneralTitle_Error, exception.Message, InfoBarMessageType.Error);
            }
        };
        page.NavigationItem.LeftBarButtonItem = cancel;
        page.NavigationItem.RightBarButtonItem = save;
        if (sheet.PresentationController is { } presentation) presentation.Delegate = dismissal;
        presenter.PresentViewController(sheet, true, null);
        return await completion.Task;
    }, false);

    /// <summary>
    /// The layout as sections with reorder handles: section 0 is the top level, then one section per group in its
    /// current top-level order. Moves stay inside their section and change the item collections in place.
    /// </summary>
    private sealed class AccountReorderViewController(ObservableCollection<AccountReorderItem> items) : UITableViewController(UITableViewStyle.InsetGrouped)
    {
        private const string CellId = "account";

        private List<AccountReorderItem> Groups => items.Where(item => item.IsGroup && item.Children.Count > 0).ToList();

        private IList<AccountReorderItem>? ListFor(nint section)
        {
            if (section == 0) return items;
            var groups = Groups;
            var index = (int)section - 1;
            return index >= 0 && index < groups.Count ? groups[index].Children : null;
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            TableView.RegisterClassForCellReuse(typeof(UITableViewCell), CellId);
            TableView.RowHeight = UITableView.AutomaticDimension;
            TableView.EstimatedRowHeight = 52;
            TableView.AllowsSelectionDuringEditing = false;
            TableView.SetEditing(true, false);
        }

        public override nint NumberOfSections(UITableView tableView) => 1 + Groups.Count;

        public override nint RowsInSection(UITableView tableView, nint section) => ListFor(section)?.Count ?? 0;

        public override string? TitleForHeader(UITableView tableView, nint section)
        {
            if (section == 0) return null;
            var groups = Groups;
            var index = (int)section - 1;
            return index >= 0 && index < groups.Count ? groups[index].GroupEntriesAutomationName : null;
        }

        public override string? TitleForFooter(UITableView tableView, nint section)
            => section == 0 ? Translator.AccountReorderDialog_Description : null;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = tableView.DequeueReusableCell(CellId, indexPath);
            var list = ListFor(indexPath.Section);
            if (list is null || indexPath.Row < 0 || indexPath.Row >= list.Count) return cell;
            var item = list[indexPath.Row];
            var content = UIListContentConfiguration.SubtitleCellConfiguration;
            content.Text = item.Title;
            content.SecondaryText = string.IsNullOrWhiteSpace(item.Subtitle) || item.Subtitle == item.Title ? null : item.Subtitle;
            content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
            if (item.IsGroup)
            {
                var tint = WinoStyle.AccountGroupColor(item.ColorKey);
                content.Image = tint is null ? WinoIcons.TemplateImage(item.IconGlyph, 22) : WinoIcons.MonoImage(item.IconGlyph, 22, tint);
                content.ImageProperties.TintColor = tint ?? WinoStyle.PrimaryText;
                if (tint is not null) content.TextProperties.Color = tint;
                content.TextProperties.Font = WinoStyle.BodyStrong;
            }
            else if (item.Account is { } mail)
            {
                content.Image = WinoIcons.Image(WinoAccountIconView.ProviderGlyph(MailAccountIconInfoFactory.CreateProviderFallback(mail.ProviderType, mail.SpecialImapProvider).Provider, false), 22);
            }
            cell.ContentConfiguration = content;
            cell.ShowsReorderControl = true;
            cell.SelectionStyle = UITableViewCellSelectionStyle.None;
            cell.AccessibilityLabel = item.IsGroup ? string.Format(Translator.AccountGroup_AutomationName, item.Title) : null;
            cell.AccessibilityHint = Translator.AccountReorderDialog_Description;
            return cell;
        }

        public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath) => true;
        public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath) => (ListFor(indexPath.Section)?.Count ?? 0) > 1;
        public override UITableViewCellEditingStyle EditingStyleForRow(UITableView tableView, NSIndexPath indexPath) => UITableViewCellEditingStyle.None;
        public override bool ShouldIndentWhileEditing(UITableView tableView, NSIndexPath indexPath) => false;

        public override NSIndexPath CustomizeMoveTarget(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath proposedIndexPath)
        {
            if (proposedIndexPath.Section == sourceIndexPath.Section) return proposedIndexPath;
            var count = ListFor(sourceIndexPath.Section)?.Count ?? 1;
            var row = proposedIndexPath.Section < sourceIndexPath.Section ? 0 : Math.Max(0, count - 1);
            return NSIndexPath.FromRowSection(row, sourceIndexPath.Section);
        }

        public override void MoveRow(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath destinationIndexPath)
        {
            if (sourceIndexPath.Section != destinationIndexPath.Section) return;
            var list = ListFor(sourceIndexPath.Section);
            var from = sourceIndexPath.Row;
            var to = destinationIndexPath.Row;
            if (list is null || from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return;
            var groupsBefore = sourceIndexPath.Section == 0 ? Groups : null;
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            // Moving a group at the top level changes the order of the group sections below it.
            if (groupsBefore is not null && !groupsBefore.SequenceEqual(Groups))
                BeginInvokeOnMainThread(() => TableView.ReloadSections(NSIndexSet.FromNSRange(new NSRange(1, groupsBefore.Count)), UITableViewRowAnimation.Automatic));
        }
    }
}
