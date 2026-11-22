using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.AppLock;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    /// <summary>The PIN prompt the app lock platform shows before a sensitive step.</summary>
    public Task<bool> ShowAppLockVerificationDialogAsync()
        => PresentAsync(owner => AppLockVerificationDialog.PresentAsync(owner, services.GetRequiredService<IAppLockService>()), false);
}
