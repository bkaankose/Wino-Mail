using Wino.Mail.UIKit.Views.Settings;
using System.Collections.ObjectModel;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>Account order (Windows AccountReorderDialog, the Mac reorder sheet).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>
    /// A sheet with the accounts in a table that is always in editing mode, so every row shows its reorder handle.
    /// Save stores the order the way the Mac and Windows do (start-up entity id to index) and mirrors it in the bound
    /// collection so the settings page updates in place; Cancel or swiping the sheet away changes nothing.
    /// </summary>
    public Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts) => PresentAsync(presenter =>
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new AccountReorderViewController((availableAccounts ?? []).ToList()) { Title = Translator.SettingsReorderAccounts_Title };
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
            var order = page.Order;
            save.Enabled = false;
            cancel.Enabled = false;
            sheet.ModalInPresentation = true;
            try
            {
                if (services.GetService<IAccountService>() is { } accounts)
                    await accounts.UpdateAccountOrdersAsync(order.ToDictionary(account => account.StartupEntityId, account => order.IndexOf(account)));
                // Mirror the new order in the bound collection so the settings page updates in place.
                if (availableAccounts is not null)
                {
                    for (int index = 0; index < order.Count; index++)
                    {
                        var current = availableAccounts.IndexOf(order[index]);
                        if (current >= 0 && current != index) availableAccounts.Move(current, index);
                    }
                }
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
        return completion.Task;
    }, false);

    /// <summary>The accounts as rows with reorder handles; <see cref="Order"/> follows the user's drags.</summary>
    private sealed class AccountReorderViewController(List<IAccountProviderDetailViewModel> order) : UITableViewController(UITableViewStyle.InsetGrouped)
    {
        private const string CellId = "account";

        public List<IAccountProviderDetailViewModel> Order => order;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            TableView.RegisterClassForCellReuse(typeof(UITableViewCell), CellId);
            TableView.RowHeight = UITableView.AutomaticDimension;
            TableView.EstimatedRowHeight = 52;
            TableView.AllowsSelectionDuringEditing = false;
            TableView.SetEditing(true, false);
        }

        public override nint NumberOfSections(UITableView tableView) => 1;
        public override nint RowsInSection(UITableView tableView, nint section) => order.Count;
        public override string? TitleForFooter(UITableView tableView, nint section) => Translator.SettingsReorderAccounts_Description;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var account = order[indexPath.Row];
            var cell = tableView.DequeueReusableCell(CellId, indexPath);
            var content = UIListContentConfiguration.SubtitleCellConfiguration;
            content.Text = account.StartupEntityTitle ?? account.Account?.Name ?? string.Empty;
            var addresses = account.StartupEntityAddresses;
            content.SecondaryText = string.IsNullOrWhiteSpace(addresses) || addresses == content.Text ? null : addresses;
            content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
            if (account.Account is { } mail)
                content.Image = WinoIcons.Image(WinoAccountIconView.ProviderGlyph(Wino.Mail.ViewModels.Data.MailAccountIconInfoFactory.CreateProviderFallback(mail.ProviderType, mail.SpecialImapProvider).Provider, false), 22);
            cell.ContentConfiguration = content;
            cell.ShowsReorderControl = true;
            cell.SelectionStyle = UITableViewCellSelectionStyle.None;
            cell.AccessibilityHint = Translator.SettingsReorderAccounts_Description;
            return cell;
        }

        public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath) => true;
        public override bool CanMoveRow(UITableView tableView, NSIndexPath indexPath) => true;
        public override UITableViewCellEditingStyle EditingStyleForRow(UITableView tableView, NSIndexPath indexPath) => UITableViewCellEditingStyle.None;
        public override bool ShouldIndentWhileEditing(UITableView tableView, NSIndexPath indexPath) => false;

        public override void MoveRow(UITableView tableView, NSIndexPath sourceIndexPath, NSIndexPath destinationIndexPath)
        {
            var from = sourceIndexPath.Row;
            var to = destinationIndexPath.Row;
            if (from < 0 || from >= order.Count || to < 0 || to >= order.Count || from == to) return;
            var item = order[from];
            order.RemoveAt(from);
            order.Insert(to, item);
        }
    }
}
