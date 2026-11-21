using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Dialogs whose package has not landed: each is answered as the user dismissing it (confirmations declined, pickers
/// empty) and logged.
/// </summary>
internal sealed partial class GtkDialogService
{
    private static T Dismiss<T>(string name, string? detail, T answer)
    {
        Serilog.Log.Information("Dialog {Dialog} is not available on Linux yet and was dismissed.", name);
        return answer;
    }

    private static Task<T> DismissAsync<T>(string name, string? detail, T answer) => Task.FromResult(Dismiss(name, detail, answer));

    private static Task DismissAsync(string name, string? detail = null)
    {
        Dismiss(name, detail, true);
        return Task.CompletedTask;
    }

    public Task<AccountSignature?> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
        => DismissAsync<AccountSignature?>(nameof(ShowSignatureEditorDialog), null, null);


    public Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync() => DismissAsync<WinoAccount?>(nameof(ShowWinoAccountRegistrationDialogAsync), null, null);

    public Task<WinoAccount?> ShowWinoAccountLoginDialogAsync() => DismissAsync<WinoAccount?>(nameof(ShowWinoAccountLoginDialogAsync), null, null);

    public Task<bool> ShowWinoAccountExportDialogAsync() => DismissAsync(nameof(ShowWinoAccountExportDialogAsync), null, false);

    public Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync()
        => DismissAsync<UnlimitedAccountsPurchaseChannel?>(nameof(ShowUnlimitedAccountsPurchaseChannelDialogAsync), null, null);
}
