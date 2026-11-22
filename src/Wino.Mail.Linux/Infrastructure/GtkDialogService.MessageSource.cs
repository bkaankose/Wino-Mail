using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    public Task ShowMessageSourceDialogAsync(string messageSource) => PresentAsync(async owner =>
    {
        await MessageSourceDialog.PresentAsync(owner, messageSource);
        return true;
    }, false);
}
