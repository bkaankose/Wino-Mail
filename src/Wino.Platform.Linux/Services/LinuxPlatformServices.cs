using System.Runtime.InteropServices;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.Voice;

namespace Wino.Platform.Linux.Services;

/// <summary>
/// Opens addresses, files and folders through <c>Gtk.UriLauncher</c> and <c>Gtk.FileLauncher</c>, which use
/// xdg-desktop-portal (OpenURI) inside a sandbox and the default handler outside one.
/// </summary>
public sealed class LinuxExternalLauncher(IDispatcher dispatcher, Func<Gtk.Window?> currentWindow) : IExternalLauncher
{
    public Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return LaunchAsync(window => Gtk.UriLauncher.New(uri.AbsoluteUri).LaunchAsync(window));
    }

    public Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
        => LaunchPathAsync(path, File.Exists(path));

    public Task<PlatformOperationResult> LaunchFolderAsync(string path, CancellationToken cancellationToken = default)
        => LaunchPathAsync(path, Directory.Exists(path));

    private Task<PlatformOperationResult> LaunchPathAsync(string path, bool exists)
    {
        if (!exists)
            return Task.FromResult(new PlatformOperationResult(PlatformOperationStatus.Failed, $"'{path}' does not exist."));
        return LaunchAsync(window => Gtk.FileLauncher.New(Gio.FileHelper.NewForPath(path)).LaunchAsync(window));
    }

    private async Task<PlatformOperationResult> LaunchAsync(Func<Gtk.Window, Task<bool>> launch)
    {
        var completion = new TaskCompletionSource<PlatformOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatcher.ExecuteOnUIThread(async () =>
        {
            try
            {
                if (currentWindow() is not { } window)
                {
                    completion.SetResult(new PlatformOperationResult(PlatformOperationStatus.Unavailable, "No window is open."));
                    return;
                }
                var launched = await launch(window);
                completion.SetResult(new PlatformOperationResult(launched ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed));
            }
            catch (Exception exception)
            {
                // A dismissed portal chooser and a missing handler both arrive as a GLib error.
                completion.SetResult(new PlatformOperationResult(PlatformOperationStatus.Failed, exception.Message));
            }
        });
        return await completion.Task;
    }
}

public sealed class LinuxClipboardService(IDispatcher dispatcher) : IClipboardService
{
    public async Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var status = PlatformOperationStatus.Unavailable;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (Gdk.Display.GetDefault() is not { } display) return;
            display.GetClipboard().SetText(text ?? string.Empty);
            status = PlatformOperationStatus.Succeeded;
        });
        return new PlatformOperationResult(status);
    }
}

/// <summary>Dark or light as libadwaita resolved it from the Settings portal (<c>org.freedesktop.appearance color-scheme</c>).</summary>
public sealed class LinuxUnderlyingThemeService : IUnderlyingThemeService
{
    public bool IsUnderlyingThemeDark() => Adw.StyleManager.GetDefault().Dark;
}

public sealed class LinuxShortcutPlatformService : IShortcutPlatformService
{
    private volatile bool _shift;

    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Control;

    public bool IsShiftKeyPressed() => _shift;

    /// <summary>The head's key router reports the Shift state of the last key event; GDK has no thread-safe query.</summary>
    public void SetShift(bool pressed) => _shift = pressed;
}

/// <summary>The reader needs WebKitGTK 6.0, which distributions package separately from GTK.</summary>
public sealed class LinuxReaderRuntimeService : IReaderRuntimeService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var available = NativeLibrary.TryLoad("libwebkitgtk-6.0.so.4", out var handle);
        if (available) NativeLibrary.Free(handle);
        return Task.FromResult(available);
    }
}

public sealed class LinuxTaskCompletionSound : ITaskCompletionSound
{
    public void Play() { }
}

public sealed class LinuxUserPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;

    public bool IsSystemQuietTimeActive() => false;
}

/// <summary>No store on Linux: nothing is owned or sold here, and updates come from the package.</summary>
public sealed class LinuxStoreService : IMicrosoftStoreService
{
    public bool HasAvailableUpdate => false;

    public Task<bool> HasProductAsync(WinoAddOnProductType productType) => Task.FromResult(false);

    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
        => Task.FromException<StorePurchaseResult>(new PlatformNotSupportedException("There is no store on Linux."));

    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId) => Task.FromResult<string?>(null);

    public Task PromptRatingDialogAsync() => Task.CompletedTask;

    public Task LaunchStorePageForReviewAsync() => Task.CompletedTask;

    public Task<bool> RefreshAvailabilityAsync() => Task.FromResult(false);

    public Task<bool> StartUpdateAsync(IProgress<double>? progress = null) => Task.FromResult(false);
}

public sealed class LinuxMicrophoneService : IMicrophoneService
{
    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MicrophoneDevice>>(Array.Empty<MicrophoneDevice>());

    public MicrophoneAccess GetAccessStatus() => MicrophoneAccess.NoDevice;

    public Task<MicrophoneSessionResult> StartLevelMonitorAsync(string? deviceId, Action<double> levelChanged, CancellationToken cancellationToken = default)
        => Task.FromResult(MicrophoneSessionResult.Failed(MicrophoneAccess.NoDevice));

    public Task<MicrophoneSessionResult> StartCaptureAsync(string? deviceId, int sampleRate, Action<ReadOnlyMemory<byte>> audioAvailable,
        Action<double> levelChanged, CancellationToken cancellationToken = default)
        => Task.FromResult(MicrophoneSessionResult.Failed(MicrophoneAccess.NoDevice));
}
