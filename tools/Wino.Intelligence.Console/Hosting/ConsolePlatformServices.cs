using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Intelligence.ConsoleApp.Hosting;

internal sealed class ConsolePlatformServices(ConsoleNativeAppService? native = null) : IExternalLauncher, IShortcutPlatformService
{
    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Control;
    public bool IsShiftKeyPressed() => false;

    public async Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await native!.LaunchFileAsync(path);
            return new(PlatformOperationStatus.Succeeded);
        }
        catch (Exception exception) { return new(PlatformOperationStatus.Failed, exception.Message); }
    }

    public async Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new(await native!.LaunchUriAsync(uri) ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed);
        }
        catch (Exception exception) { return new(PlatformOperationStatus.Failed, exception.Message); }
    }
}
