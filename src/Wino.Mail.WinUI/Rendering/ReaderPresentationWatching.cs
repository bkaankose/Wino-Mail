using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wino.Mail.Rendering;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Feeds the high contrast state of the window that shows a reader into its shared <see cref="EngineReaderPresentation"/>.
/// Each presentation gets one watcher, kept beside it for as long as the presentation lives, so the pages keep their
/// two calls.
/// </summary>
internal static class ReaderPresentationWatching
{
    private static readonly ConditionalWeakTable<EngineReaderPresentation, WindowHighContrastWatcher> Watchers = new();

    /// <summary>Paired with <see cref="StopWatching"/> when the view leaves the tree. Call on the UI thread.</summary>
    public static void StartWatching(this EngineReaderPresentation presentation, XamlRoot? xamlRoot)
    {
        var watcher = Watchers.GetValue(presentation, static p =>
        {
            var created = new WindowHighContrastWatcher(DispatcherQueue.GetForCurrentThread());
            created.Changed += (_, _) => p.IsHighContrast = created.IsHighContrast;
            return created;
        });

        watcher.Start(xamlRoot);
        presentation.IsHighContrast = watcher.IsHighContrast;
    }

    public static void StopWatching(this EngineReaderPresentation presentation)
    {
        if (Watchers.TryGetValue(presentation, out var watcher))
            watcher.Stop();
    }
}
