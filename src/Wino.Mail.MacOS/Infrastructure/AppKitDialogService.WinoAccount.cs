using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.MacOS.Views.Account;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Wino Account sheets: server certificate trust, sync secret and purchase channel. Owned by the Wino Account work (WS7).</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>
    /// Certificate prompts can arrive while the account progress sheet is attached (adding an IMAP
    /// account); a second sheet on the same window would wait behind it, so it stacks on that sheet.
    /// </summary>
    private Task<T> PresentOnTopAsync<T>(Func<NSWindow, Task<T>> present)
        => PresentAsync(window => present(window.AttachedSheet ?? window));

    public Task<bool> ShowServerCertificateTrustDialogAsync(string summary, byte[] certificateRawData)
        => PresentOnTopAsync(window => new ServerCertificateTrustSheet(summary, certificateRawData).PresentAsync(window));

    /// <summary>The secret goes straight back to the caller; nothing here logs or keeps it.</summary>
    public Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request)
        => PresentOnTopAsync(window => new SyncSecretSheet(request).PresentAsync(window));

    public async Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync()
    {
        var capabilities = services?.GetService<IPlatformCapabilities>();
        var isWinoAccountAvailable = false;
        try
        {
            if (services?.GetService<IWinoAccountProfileService>() is { } profile)
                isWinoAccountAvailable = await profile.HasActiveAccountAsync();
        }
        catch (Exception exception)
        {
            // An unreadable profile leaves the Wino Account option disabled with its sign-in hint.
            error(exception);
        }

        return await PresentOnTopAsync(window => new PurchaseChannelSheet(isWinoAccountAvailable,
            capabilities?.MicrosoftStore == true, capabilities?.AppleAppStore == true).PresentAsync(window));
    }
}
