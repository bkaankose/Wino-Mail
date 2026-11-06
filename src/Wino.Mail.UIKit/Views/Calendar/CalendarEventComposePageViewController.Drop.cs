using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// Files dropped on the event composer become attachments (Mac Drop partial, Windows CalendarEventComposePage AllowDrop).
/// A drop interaction on the zone accepts file items from Files and other apps; drags over the notes keep WebKit's own
/// handling. A dropped file is readable only during the drop, and the ViewModel reads attachment files when the event is
/// saved, so each one is copied into the app's temporary folder first.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private static readonly string DroppedAttachmentsRoot = Path.Combine(Path.GetTempPath(), "WinoEventAttachments");

    /// <summary>Adds the drop interaction to the zone. Called once at the end of LoadView.</summary>
    private void SetUpAttachmentDrop() => _zone.AddInteraction(new UIDropInteraction(new EventFileDropDelegate(this)));

    private void SyncDropState(bool active) => _zone.Alpha = active ? 0.85f : 1;

    /// <summary>Copies one dropped item's file into <paramref name="folder"/>; null when it has no file representation.</summary>
    private static Task<string?> CopyDroppedFileAsync(NSItemProvider provider, string folder)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var name = string.IsNullOrWhiteSpace(provider.SuggestedName)
                    ? Path.GetFileName(path)
                    : provider.SuggestedName + (Path.HasExtension(provider.SuggestedName) ? string.Empty : Path.GetExtension(path));
                // The URL is deleted when this handler returns: copy the file now.
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Path.GetFileName(name));
                File.Copy(path, copy, overwrite: true);
                completion.TrySetResult(copy);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private async Task DropAsync(IUIDropSession session)
    {
        var folder = Path.Combine(DroppedAttachmentsRoot, Guid.NewGuid().ToString("N"));
        var copies = new List<string>();
        foreach (var item in session.Items)
        {
            try
            {
                if (await CopyDroppedFileAsync(item.ItemProvider, folder) is { } copy) copies.Add(copy);
            }
            catch (IOException exception) { ReportError(exception); }
            catch (UnauthorizedAccessException exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            foreach (var copy in copies)
            {
                try { ViewModel.TryAddAttachment(copy, new FileInfo(copy).Length); }
                catch (IOException exception) { ReportError(exception); }
            }
        });
    }

    private sealed class EventFileDropDelegate(CalendarEventComposePageViewController owner) : UIDropInteractionDelegate
    {
        /// <summary>Files only (documents, pictures, archives), and only when the selected calendar takes attachments.</summary>
        public override bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
            => owner.ViewModel.CanAddAttachments && session.Items.Length > 0 && session.Items.All(static item => IsFile(item.ItemProvider));

        private static bool IsFile(NSItemProvider provider)
            => (provider.RegisteredTypeIdentifiers ?? []).Any(static identifier => UTType.CreateFromIdentifier(identifier) is { } type
                && type.ConformsTo(UTTypes.Data) && !type.ConformsTo(UTTypes.Text) && !type.ConformsTo(UTTypes.Url));

        public override UIDropProposal SessionDidUpdate(UIDropInteraction interaction, IUIDropSession session)
        {
            // Over the notes WebKit handles the drop itself.
            var location = session.LocationInView(owner._notesWebView);
            var overNotes = !owner._notesWebView.Hidden && owner._notesWebView.PointInside(location, null);
            owner.SyncDropState(!overNotes);
            return new UIDropProposal(overNotes ? UIDropOperation.Cancel : UIDropOperation.Copy);
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
