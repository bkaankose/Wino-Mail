using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Infrastructure;

// Account reorder, alias and category dialogs.
internal sealed partial class GtkDialogService
{
    public async Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
    {
        if (services.GetService<IAccountService>() is not { } accountService) return;
        ObservableCollection<AccountReorderItem> items;
        try
        {
            var accounts = await accountService.GetAccountsAsync();
            var groups = await accountService.GetAccountGroupsAsync();
            items = AccountReorderItem.FromLayout(AccountLayoutBuilder.Build(accounts, groups));
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The account layout could not be loaded.");
            InfoBarMessage(Translator.GeneralTitle_Error, exception.Message, InfoBarMessageType.Error);
            return;
        }

        if (!await PresentAsync(owner => AccountReorderDialog.PresentAsync(owner, items, services), false)) return;
        try
        {
            await accountService.SaveAccountLayoutOrderAsync(AccountReorderItem.ToOrder(items));
            Serilog.Log.Information("Account order saved: {Order}", string.Join(", ", items.Select(item => item.Title)));
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The account order could not be saved.");
            InfoBarMessage(Translator.GeneralTitle_Error, exception.Message, InfoBarMessageType.Error);
        }
    }

    public async Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
        => new GtkAliasDialogResult { CreatedAccountAlias = await PresentAsync(CreateAliasDialog.PresentAsync, null) };

    public Task<MailCategoryDialogResult?> ShowEditMailCategoryDialogAsync(MailCategory? category = null)
        => PresentAsync(owner => EditMailCategoryDialog.PresentAsync(owner, category), null);

    private sealed class GtkAliasDialogResult : ICreateAccountAliasDialog
    {
        public MailAccountAlias? CreatedAccountAlias { get; set; }
    }
}
