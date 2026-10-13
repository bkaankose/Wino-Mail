using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Views.Dialogs;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>Signature editor (Windows SignatureEditorDialog, the Mac AppKitDialogService.Signature).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>
    /// Same contract as Windows and the Mac: null when cancelled. Save returns a new signature (new Id, trimmed name,
    /// editor HTML, no account id) or, when editing, a copy of <paramref name="signatureModel"/> (same Id and
    /// MailAccountId) with the trimmed name and new HTML.
    /// </summary>
    public async Task<AccountSignature?> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
    {
        var accountLine = await AccountLineAsync(signatureModel?.MailAccountId);
        var launcher = services.GetService<IExternalLauncher>();
        var preferences = services.GetService<IPreferencesService>();

        var result = await PresentAsync(presenter =>
            new SignatureEditorSheet(accountLine, signatureModel?.Name, signatureModel?.HtmlBody, launcher, preferences, SignatureEditorFailed).PresentAsync(presenter), null);
        if (result is not { } saved) return null;

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

    private static void SignatureEditorFailed(Exception exception) => Serilog.Log.Warning(exception, "The signature editor failed.");

    /// <summary>"Account name · address" under the sheet title, when the signature's account is known.</summary>
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
            Serilog.Log.Warning(exception, "Could not read the signature's account.");
            return null;
        }
    }
}
