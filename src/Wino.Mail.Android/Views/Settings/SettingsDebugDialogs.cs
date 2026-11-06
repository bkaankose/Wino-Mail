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
            "certificate" => dialogs.ShowServerCertificateTrustDialogAsync(SampleCertificatePrompt(dangerous: false)),
            "certificate-dangerous" => dialogs.ShowServerCertificateTrustDialogAsync(SampleCertificatePrompt(dangerous: true)),
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

    private static Wino.Core.Domain.Models.Connectivity.ServerCertificateTrustPrompt SampleCertificatePrompt(bool dangerous)
        => Wino.Core.Domain.Models.Connectivity.ServerCertificateTrustPrompt.From(new Wino.Core.Domain.Models.Connectivity.MailServerCertificateFailure
        {
            Protocol = Wino.Core.Domain.Enums.MailServerProtocol.Imap,
            Host = "imap.example.test",
            Port = 993,
            PolicyErrors = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
            Subject = "CN=imap.example.test",
            SubjectAlternativeNames = "DNS Name=imap.example.test",
            Issuer = "CN=Example Test CA",
            ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidToUtc = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CertificateSha256 = new string('A', 64),
            Tier = dangerous ? Wino.Core.Domain.Enums.MailServerCertificateTrustTier.Dangerous : Wino.Core.Domain.Enums.MailServerCertificateTrustTier.Normal,
            Problems = dangerous
                ? Wino.Core.Domain.Enums.MailServerCertificateProblem.UntrustedRoot | Wino.Core.Domain.Enums.MailServerCertificateProblem.Revoked
                : Wino.Core.Domain.Enums.MailServerCertificateProblem.UntrustedRoot
        });

    private static async Task Observe(Task task, string name)
    {
        try { await task; Serilog.Log.Information("Debug dialog {Name} closed.", name); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Debug dialog {Name} failed.", name); }
    }
}
#endif
