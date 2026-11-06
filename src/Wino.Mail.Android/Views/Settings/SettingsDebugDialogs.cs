#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>Opens a dialog of the dialog service without awaiting it (Debug builds; <see cref="SettingsDebugActivity"/>).</summary>
public static class SettingsDebugDialogs
{
    public static void Open(IServiceProvider services, string name)
    {
        var dialogs = services.GetRequiredService<IMailDialogService>();
        Task task = name switch
        {
            "confirm" => dialogs.ShowConfirmationDialogAsync("Question", "Title", "Confirm"),
            "alias" => dialogs.ShowCreateAccountAliasDialogAsync(),
            "category" => dialogs.ShowEditMailCategoryDialogAsync(),
            "signature" => dialogs.ShowSignatureEditorDialog(),
            "reorder" => ReorderAsync(services, dialogs),
            "folders" => FoldersAsync(services, dialogs),
            "login" => dialogs.ShowWinoAccountLoginDialogAsync(),
            "register" => dialogs.ShowWinoAccountRegistrationDialogAsync(),
            "secret" => dialogs.ShowWinoAccountSyncSecretDialogAsync(new Wino.Core.Domain.Models.Accounts.SyncSnapshotSecretRequest(false, false, false)),
            "secretnew" => dialogs.ShowWinoAccountSyncSecretDialogAsync(new Wino.Core.Domain.Models.Accounts.SyncSnapshotSecretRequest(true, true, false)),
            "certificate" => dialogs.ShowServerCertificateTrustDialogAsync("Issuer: Debug CA\nExpires: 2030", []),
            "text" => dialogs.ShowTextInputDialogAsync("Text", "Title", "Description", "OK"),
            _ => Task.CompletedTask
        };
        _ = Observe(task, name);
    }

    private static async Task ReorderAsync(IServiceProvider services, IMailDialogService dialogs)
    {
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
        var items = new System.Collections.ObjectModel.ObservableCollection<IAccountProviderDetailViewModel>(
            accounts.Select(account => (IAccountProviderDetailViewModel)new Wino.Mail.ViewModels.Data.AccountProviderDetailViewModel(
                services.GetRequiredService<IKnownImapProviderCatalog>().GetProviderDetail(account.ProviderType), account)));
        await dialogs.ShowAccountReorderDialogAsync(items);
    }

    private static async Task FoldersAsync(IServiceProvider services, IMailDialogService dialogs)
    {
        var account = (await services.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault();
        if (account is not null) await dialogs.HandleSystemFolderConfigurationDialogAsync(account.Id, services.GetRequiredService<IFolderService>());
    }

    private static async Task Observe(Task task, string name)
    {
        try { await task; Serilog.Log.Information("Debug dialog {Name} closed.", name); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Debug dialog {Name} failed.", name); }
    }
}
#endif
