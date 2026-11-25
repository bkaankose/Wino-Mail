using Wino.Core.Domain;
using Wino.Mail.Linux.Infrastructure.Updates;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    public Task<string> ShowUpdateAvailableAsync(LinuxUpdateInfo update)
        => AlertAsync(UpdateAvailableDialog.Offer(update));

    public Task ShowUpdateUpToDateAsync(string version)
        => AlertAsync(new AlertRequest(Translator.AccountHub_SyncUpToDate, Translator.SettingsAboutVersion + version,
            [new AlertResponse(UpdateAvailableDialog.Close, Translator.Buttons_Close)])
        {
            CloseResponse = UpdateAvailableDialog.Close,
        });

    public Task<string?> ShowUpdateDownloadAsync(LinuxUpdateInfo update, Func<IProgress<double>, CancellationToken, Task<string>> download)
        => PresentAsync(owner => UpdateAvailableDialog.DownloadAsync(owner, update, download), (string?)null);
}
