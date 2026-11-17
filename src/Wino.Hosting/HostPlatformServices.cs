using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Hosting;

/// <summary>Opening files and addresses: recorded and reported unavailable, because nothing is shown to a user.</summary>
internal sealed class HostExternalLauncher(HostInteractionLog log) : IExternalLauncher
{
    private static readonly PlatformOperationResult Unavailable = new(PlatformOperationStatus.Unavailable, "This host has no screen to open anything on.");

    public Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
    {
        log.Record(HostInteractionKind.Launch, nameof(LaunchFileAsync), path, Unavailable.Status.ToString());
        return Task.FromResult(Unavailable);
    }

    public Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        log.Record(HostInteractionKind.Launch, nameof(LaunchUriAsync), uri?.ToString(), Unavailable.Status.ToString());
        return Task.FromResult(Unavailable);
    }

    public Task<PlatformOperationResult> LaunchFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        log.Record(HostInteractionKind.Launch, nameof(LaunchFolderAsync), path, Unavailable.Status.ToString());
        return Task.FromResult(Unavailable);
    }
}

internal sealed class HostClipboardService(HostInteractionLog log) : IClipboardService
{
    public Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        log.Record(HostInteractionKind.Launch, nameof(CopyTextAsync), null, PlatformOperationStatus.Unavailable.ToString());
        return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable, "This host has no clipboard."));
    }
}

/// <summary>No keyboard: Control is the primary modifier, as on Windows and Linux, and Shift is never held.</summary>
internal sealed class HostShortcutPlatformService : IShortcutPlatformService
{
    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Control;

    public bool IsShiftKeyPressed() => false;
}

/// <summary>There is no HTML reader runtime (WebView) in a host.</summary>
internal sealed class HostReaderRuntimeService : IReaderRuntimeService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

internal sealed class HostTaskCompletionSound : ITaskCompletionSound
{
    public void Play() { }
}

/// <summary>A host never presents and has no quiet hours, so notification policy decides as it would for a user at the desk.</summary>
internal sealed class HostUserPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;

    public bool IsSystemQuietTimeActive() => false;
}

internal sealed class HostUnderlyingThemeService : IUnderlyingThemeService
{
    public bool IsUnderlyingThemeDark() => false;
}

/// <summary>No store: nothing is owned or sold, and there are no updates.</summary>
internal sealed class HostStoreService : IMicrosoftStoreService
{
    public bool HasAvailableUpdate => false;

    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);

    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
        => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("This host has no store."));

    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);

    public Task PromptRatingDialogAsync() => Task.CompletedTask;

    public Task LaunchStorePageForReviewAsync() => Task.CompletedTask;

    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);

    public Task<bool> StartUpdateAsync(IProgress<double>? progress = null) => Task.FromResult(false);
}
