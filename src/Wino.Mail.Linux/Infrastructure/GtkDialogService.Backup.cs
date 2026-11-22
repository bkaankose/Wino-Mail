using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    public Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request)
        => PresentAsync<string?>(owner => SyncSecretDialog.PresentAsync(owner, request), null);
}
