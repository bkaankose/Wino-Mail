using Android.Content;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Platform.Android.Services;

/// <summary>Plain text on the primary clip. Success is read back from the clipboard, not assumed.</summary>
public sealed class AndroidClipboardService(Context context, IDispatcher dispatcher) : IClipboardService
{
    private const string ClipLabel = "Wino Mail";

    public async Task<PlatformOperationResult> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var copied = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard) return;
            var clip = ClipData.NewPlainText(ClipLabel, text);
            if (clip is null) return;
            clipboard.PrimaryClip = clip;
            // HasPrimaryClip does not read the content, so it raises no clipboard access notice.
            copied = clipboard.HasPrimaryClip;
        });
        return new(copied ? PlatformOperationStatus.Succeeded : PlatformOperationStatus.Failed);
    }
}
