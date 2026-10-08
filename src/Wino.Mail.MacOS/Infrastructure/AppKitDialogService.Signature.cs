using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Views.Dialogs;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Signature editor (Windows SignatureEditorDialog). Owned by the signature settings work.</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>
    /// Same contract as Windows DialogService.ShowSignatureEditorDialog: null when cancelled. Save returns
    /// a new signature (new Id, trimmed name, editor HTML, no account id) or, when editing, a copy of
    /// <paramref name="signatureModel"/> (same Id and MailAccountId) with the trimmed name and new HTML.
    /// </summary>
    public async Task<AccountSignature> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
    {
        var accountLine = await AccountLineAsync(signatureModel?.MailAccountId);
        var launcher = services?.GetService<IExternalLauncher>();
        var preferences = services?.GetService<IPreferencesService>();

        var result = await PresentAsync(window =>
            new SignatureEditorSheet(accountLine, signatureModel?.Name, signatureModel?.HtmlBody, launcher, preferences, error).PresentAsync(window));
        if (result is not { } saved) return null!;

        if (signatureModel is null)
            return new AccountSignature { Id = Guid.NewGuid(), Name = saved.Name, HtmlBody = saved.HtmlBody };

        return new AccountSignature
        {
            Id = signatureModel.Id,
            Name = saved.Name,
            MailAccountId = signatureModel.MailAccountId,
            HtmlBody = saved.HtmlBody
        };
    }

    /// <summary>"Account name · address" under the sheet heading, when the signature's account is known.</summary>
    private async Task<string?> AccountLineAsync(Guid? accountId)
    {
        if (accountId is not { } id || id == Guid.Empty || services?.GetService<IAccountService>() is not { } accounts) return null;
        try
        {
            var account = await accounts.GetAccountAsync(id);
            if (account is null) return null;
            return string.IsNullOrWhiteSpace(account.Address) || account.Address == account.Name ? account.Name : $"{account.Name} · {account.Address}";
        }
        catch (Exception exception)
        {
            error(exception);
            return null;
        }
    }
}
