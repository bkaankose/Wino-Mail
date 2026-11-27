using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Infrastructure;

// Import from other mail clients (Thunderbird, Evolution, GNOME Online Accounts).
internal sealed partial class GtkDialogService
{
    public Task<List<MailClientImportCandidate>?> ShowMailClientImportDialogAsync()
    {
        var model = new MailClientImportDialogModel(services.GetRequiredService<IMailClientAccountImportService>());
        return PresentAsync(owner => MailClientImportDialog.PresentAsync(owner, model, dispatcher), null);
    }
}
