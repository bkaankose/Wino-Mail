using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    /// <summary>
    /// Windows/macOS contract: null when cancelled; a new signature gets a new id and no account, an edited one keeps its id
    /// and account and takes the trimmed name and the editor HTML.
    /// </summary>
    public async Task<AccountSignature?> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
    {
        var accountLine = await AccountLineAsync(signatureModel?.MailAccountId);
        var launcher = services.GetService<IExternalLauncher>();
        var preferences = services.GetService<IPreferencesService>();
        var translations = services.GetService<ITranslationService>();
        var result = await PresentAsync<(string Name, string HtmlBody)?>(owner =>
            new SignatureEditorDialog(accountLine, signatureModel?.Name, signatureModel?.HtmlBody, dispatcher, launcher, preferences, translations,
                exception => Serilog.Log.Error(exception, "Signature editor failed.")).PresentAsync(owner), null);
        if (result is not { } saved) return null;

        if (signatureModel is null) return new AccountSignature { Id = Guid.NewGuid(), Name = saved.Name, HtmlBody = saved.HtmlBody };
        return new AccountSignature
        {
            Id = signatureModel.Id,
            Name = saved.Name,
            MailAccountId = signatureModel.MailAccountId,
            HtmlBody = saved.HtmlBody
        };
    }

    private async Task<string?> AccountLineAsync(Guid? accountId)
    {
        if (accountId is not { } id || id == Guid.Empty || services.GetService<IAccountService>() is not { } accounts) return null;
        try
        {
            var account = await accounts.GetAccountAsync(id);
            if (account is null) return null;
            return string.IsNullOrWhiteSpace(account.Address) || account.Address == account.Name ? account.Name : $"{account.Name} · {account.Address}";
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The signature's account could not be read.");
            return null;
        }
    }
}
