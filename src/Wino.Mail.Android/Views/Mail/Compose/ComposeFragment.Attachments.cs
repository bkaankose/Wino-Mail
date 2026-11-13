using System.Collections.Specialized;
using System.ComponentModel;
using Android.Content;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor.Android;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The attachment strip (Mac and iOS Attachments partials, Windows AttachmentsTray): "N attachments · size" with Remove
/// all, then one tile per file in a horizontal row with its type badge, name, "size · type" (and the caution glyph when
/// the bytes do not match the name) and a remove button; a tap opens the file. Attach opens the system document picker
/// through the shared AttachFilesCommand; Insert picture in the format bar uses the system photo picker.
/// </summary>
public sealed partial class ComposeFragment
{
    private readonly List<(MailAttachmentViewModel Attachment, PropertyChangedEventHandler Handler)> _attachmentHandlers = [];
    private View? _attachmentStrip;
    private LinearLayout? _attachmentTiles;
    private TextView? _attachmentSummary;

    private void BuildAttachments(View view, Context context, ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        _attachmentStrip = view.FindViewById(Resource.Id.compose_attachments);
        _attachmentTiles = view.FindViewById<LinearLayout>(Resource.Id.compose_attachment_tiles);
        _attachmentSummary = view.FindViewById<TextView>(Resource.Id.compose_attachments_summary);
        _attachmentSummary?.SetCompoundDrawablesRelativeWithIntrinsicBounds(WinoIcons.Drawable(context, WinoIconGlyph.Attachment, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_attachment_summary), WinoColors.SecondaryText(context)), null, null, null);
        if (_attachmentTiles is not null) _attachmentTiles.ContentDescription = Translator.Composer_AttachmentsListLabel;
        if (view.FindViewById<MaterialButton>(Resource.Id.compose_remove_attachments) is { } removeAll)
        {
            removeAll.Text = Translator.Composer_RemoveAllAttachments;
            OnClick(removeAll, () =>
            {
                viewModel.RemoveAllAttachmentsCommand.Execute(null);
                ScheduleAutosave();
            });
        }

        NotifyCollectionChangedEventHandler changed = (_, args) => _ = dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            UpdateAttachments();
            ScheduleAutosave();
            // A new file is shown: the header scrolls to the strip when it is taller than its share of the page.
            if (args.Action == NotifyCollectionChangedAction.Add && _attachmentStrip is { } strip
                && View?.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.compose_header_scroll) is { } header)
                header.Post(() => header.SmoothScrollTo(0, strip.Top));
        });
        viewModel.IncludedAttachments.CollectionChanged += changed;
        Bindings.Own(new ActionDisposable(() =>
        {
            viewModel.IncludedAttachments.CollectionChanged -= changed;
            UnsubscribeAttachments();
        }));
        Bind(viewModel, dispatcher, nameof(viewModel.AttachmentsSummary), vm => vm.AttachmentsSummary, summary =>
        {
            if (_attachmentSummary is not null) _attachmentSummary.Text = summary ?? string.Empty;
        });
        UpdateAttachments();
    }

    private void UpdateAttachments()
    {
        if (_attachmentTiles?.Context is not { } context || ViewModel is not { } viewModel) return;
        UnsubscribeAttachments();
        _attachmentTiles.RemoveAllViews();
        foreach (var attachment in viewModel.IncludedAttachments.ToArray())
        {
            if (attachment is null) continue;
            var tile = CreateTile(context, viewModel, attachment);
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(MailAttachmentViewModel.HasContentTypeMismatch) or nameof(MailAttachmentViewModel.ContentTypeDetection) or null)
                    _ = Entry?.Dispatcher.ExecuteOnUIThread(() => AttachmentTiles.ApplyWarning(tile, attachment));
            };
            attachment.PropertyChanged += handler;
            _attachmentHandlers.Add((attachment, handler));
            _attachmentTiles.AddView(tile);
        }
        if (_attachmentStrip is not null)
            _attachmentStrip.Visibility = viewModel.IncludedAttachments.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private void UnsubscribeAttachments()
    {
        foreach (var (attachment, handler) in _attachmentHandlers) attachment.PropertyChanged -= handler;
        _attachmentHandlers.Clear();
    }

    /// <summary>One attachment tile (AttachmentTiles): a tap opens the file, Remove drops it from the draft.</summary>
    private View CreateTile(Context context, ComposePageViewModel viewModel, MailAttachmentViewModel attachment)
    {
        var parent = _attachmentTiles ?? throw new InvalidOperationException("The attachment strip is not built.");
        return AttachmentTiles.Create(context, parent, attachment,
            open: () => Observe(viewModel.OpenAttachmentCommand.ExecuteAsync(attachment)),
            remove: () =>
            {
                viewModel.RemoveAttachmentCommand.Execute(attachment);
                ScheduleAutosave();
            });
    }

    /// <summary>Insert picture: the system photo picker, then each picture inline as a data URI.</summary>
    private async Task InsertPicturesAsync()
    {
        if (_services?.GetService<AndroidDocumentPicker>() is not { } picker || _editor is not { } editor || _editorDisposed) return;
        var resolver = Context?.ContentResolver;
        if (resolver is null) return;
        var uris = await picker.PickImagesAsync();
        if (uris.Count == 0) return;
        var images = await Task.Run(() => uris.Select(uri =>
        {
            try { return AndroidHtmlMailEditorSession.ReadImage(resolver, uri); }
            catch (Exception exception) when (exception is IOException or Java.Lang.Exception)
            {
                Serilog.Log.Warning(exception, "A picked picture could not be read.");
                return null;
            }
        }).OfType<Wino.Editor.EditorImageInfo>().ToList());
        if (images.Count > 0 && !_editorDisposed) await editor.InsertImagesAsync(images);
    }
}
