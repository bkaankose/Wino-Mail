using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Platform.iOS.Services;

/// <summary>Plain text on the general pasteboard. Success is read back from the pasteboard, not assumed.</summary>
public sealed class IOSClipboardService(IDispatcher dispatcher) : IClipboardService
{
    public async Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        bool copied = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pasteboard = UIPasteboard.General;
            pasteboard.String = text;
            // HasStrings does not trigger the paste permission prompt. An empty string leaves no string item.
            copied = text.Length == 0 || pasteboard.HasStrings;
        });
        return new(copied ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed);
    }
}
