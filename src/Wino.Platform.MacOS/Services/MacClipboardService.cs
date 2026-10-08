using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Platform.MacOS.Services;

public sealed class MacClipboardService(IDispatcher dispatcher) : IClipboardService
{
    public async Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        bool copied = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipboard = NSPasteboard.GeneralPasteboard;
            clipboard.ClearContents();
            copied = clipboard.SetStringForType(text, NSPasteboard.NSPasteboardTypeString);
        });
        return new(copied ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed);
    }
}
