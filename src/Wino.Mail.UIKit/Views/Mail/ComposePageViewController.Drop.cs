using Foundation;
using UIKit;
using UniformTypeIdentifiers;
using Wino.Core.Domain.Models.Common;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Files dropped on the composer (Mac Drop partial, Windows ComposePage drop zones). A drop interaction on the header
/// area accepts file items from Files and other apps and attaches them; text and HTML drags inside the WKWebView body
/// keep WebKit's own handling. Dropped files are only readable during the drop, so their bytes are read at once.
/// The Mac "images zone" (inline pictures) is not offered: pictures go in through Insert › Picture.
/// </summary>
public sealed partial class ComposePageViewController
{
    private UIDropInteraction? _dropInteraction;

    /// <summary>Adds the drop interaction to the zone. Called once at the end of LoadView.</summary>
    private void SetUpFileDrop()
    {
        _dropInteraction = new UIDropInteraction(new FileDropDelegate(this));
        _zone.AddInteraction(_dropInteraction);
    }

    private void SyncDropState(bool active)
    {
        ViewModel.IsDraggingOverComposerGrid = active;
        ViewModel.IsDraggingOverFilesDropZone = active;
        ViewModel.IsDraggingOverImagesDropZone = false;
        _zone.Alpha = active ? 0.85f : 1;
    }

    private void AttachDroppedFiles(IReadOnlyList<SharedFile> files)
    {
        if (Bindings.IsDisposed) return;
        foreach (var file in files) ViewModel.AddAttachment(file);
        if (files.Count > 0) ScheduleAutosave();
    }

    /// <summary>Reads one dropped item's file; null when it has no file representation.</summary>
    private static Task<SharedFile?> LoadFileAsync(NSItemProvider provider)
    {
        var completion = new TaskCompletionSource<SharedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identifiers = provider.RegisteredTypeIdentifiers ?? [];
        var type = identifiers.FirstOrDefault(static identifier => UTType.CreateFromIdentifier(identifier) is { } item
                       && item.ConformsTo(UTTypes.Data) && !item.ConformsTo(UTTypes.Text) && !item.ConformsTo(UTTypes.Url))
                   ?? identifiers.FirstOrDefault();
        if (type is null)
        {
            completion.TrySetResult(null);
            return completion.Task;
        }
        provider.LoadFileRepresentation(type, (url, error) =>
        {
            try
            {
                if (url?.Path is not { } path || !File.Exists(path))
                {
                    completion.TrySetResult(null);
                    return;
                }
                var name = string.IsNullOrWhiteSpace(provider.SuggestedName) ? Path.GetFileName(path) : provider.SuggestedName + ExtensionFor(path, provider.SuggestedName);
                // The URL is deleted when this handler returns: read the bytes now.
                completion.TrySetResult(new SharedFile(Path.Combine(Path.GetTempPath(), name), File.ReadAllBytes(path)));
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private static string ExtensionFor(string path, string suggestedName)
        => Path.HasExtension(suggestedName) ? string.Empty : Path.GetExtension(path);

    private async Task DropAsync(IUIDropSession session)
    {
        var files = new List<SharedFile>();
        foreach (var item in session.Items)
        {
            try
            {
                if (await LoadFileAsync(item.ItemProvider) is { } file) files.Add(file);
            }
            catch (Exception exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() => AttachDroppedFiles(files));
    }

    private sealed class FileDropDelegate(ComposePageViewController owner) : UIDropInteractionDelegate
    {
        /// <summary>Files only (documents, pictures, archives): plain text, HTML and links stay with the editor.</summary>
        public override bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
            => session.Items.Length > 0 && session.Items.All(static item => IsFile(item.ItemProvider));

        private static bool IsFile(NSItemProvider provider)
            => (provider.RegisteredTypeIdentifiers ?? []).Any(static identifier => UTType.CreateFromIdentifier(identifier) is { } type
                && type.ConformsTo(UTTypes.Data) && !type.ConformsTo(UTTypes.Text) && !type.ConformsTo(UTTypes.Url));

        public override UIDropProposal SessionDidUpdate(UIDropInteraction interaction, IUIDropSession session)
        {
            // Over the body WebKit handles the drop itself.
            var location = session.LocationInView(owner._webView);
            var overBody = owner._webView.PointInside(location, null);
            owner.SyncDropState(!overBody);
            return new UIDropProposal(overBody ? UIDropOperation.Cancel : UIDropOperation.Copy);
        }

        public override void SessionDidExit(UIDropInteraction interaction, IUIDropSession session) => owner.SyncDropState(false);

        public override void SessionDidEnd(UIDropInteraction interaction, IUIDropSession session) => owner.SyncDropState(false);

        public override void PerformDrop(UIDropInteraction interaction, IUIDropSession session)
        {
            owner.SyncDropState(false);
            owner.Observe(owner.DropAsync(session));
        }
    }
}
