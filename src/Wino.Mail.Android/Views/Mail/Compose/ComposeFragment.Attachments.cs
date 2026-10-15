using TooltipCompat = AndroidX.AppCompat.Widget.TooltipCompat;
using System.Collections.Specialized;
using System.ComponentModel;
using Android.Content;
using Android.Graphics.Drawables;
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
/// all, then one tile per file in a horizontal row with its type glyph, name, size (and the caution glyph when the bytes
/// do not match the name) and a remove button; a tap opens the file. Attach opens the system document picker through the
/// shared AttachFilesCommand; Insert picture in the format bar uses the system photo picker.
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
            var (tile, warning) = CreateTile(context, viewModel, attachment);
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(MailAttachmentViewModel.HasContentTypeMismatch) or nameof(MailAttachmentViewModel.ContentTypeDetection) or null)
                    _ = Entry?.Dispatcher.ExecuteOnUIThread(() => warning.Visibility = attachment.HasContentTypeMismatch ? ViewStates.Visible : ViewStates.Gone);
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

    /// <summary>One attachment tile (compose_attachment_tile.xml): type glyph, name and size (with the caution glyph on a mismatch), and Remove.</summary>
    private (View Tile, View Warning) CreateTile(Context context, ComposePageViewModel viewModel, MailAttachmentViewModel attachment)
    {
        var fileName = attachment.FileName ?? string.Empty;
        var tile = LayoutInflater.From(context)?.Inflate(Resource.Layout.compose_attachment_tile, _attachmentTiles, false)
            ?? throw new InvalidOperationException("The attachment tile layout did not inflate.");
        var fill = new GradientDrawable();
        fill.SetCornerRadius(ComposeStyle.Px(context, Resource.Dimension.wino_compose_tile_radius));
        fill.SetColor(WinoColors.SubtleFill(context));
        fill.SetStroke(ComposeStyle.Px(context, Resource.Dimension.wino_compose_tile_stroke), attachment.HasContentTypeMismatch
            ? WinoColors.WithAlpha(WinoColors.Caution(context), ComposeStyle.Float(context, Resource.Dimension.wino_compose_tile_warning_stroke_alpha))
            : WinoColors.CardStroke(context));
        tile.Background = fill;

        WinoIcons.Apply(tile.Require<TextView>(Resource.Id.tile_icon), IsImage(fileName) ? WinoIconGlyph.Image : WinoIconGlyph.Document,
            ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_tile));
        tile.Require<TextView>(Resource.Id.tile_name).Text = fileName;
        tile.Require<TextView>(Resource.Id.tile_size).Text = attachment.ReadableSize;
        var warning = tile.Require<TextView>(Resource.Id.tile_warning);
        WinoIcons.Apply(warning, WinoIconGlyph.Warning, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_tile_warning));
        warning.Visibility = attachment.HasContentTypeMismatch ? ViewStates.Visible : ViewStates.Gone;
        warning.ContentDescription = attachment.ContentTypeWarningText;

        var remove = tile.Require<ImageButton>(Resource.Id.tile_remove);
        remove.ContentDescription = $"{Translator.Buttons_Remove} {fileName}";
        remove.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Dismiss, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_tile_remove), WinoColors.SecondaryText(context)));
        TooltipCompat.SetTooltipText(remove, Translator.Buttons_Remove);
        remove.Click += (_, _) =>
        {
            viewModel.RemoveAttachmentCommand.Execute(attachment);
            ScheduleAutosave();
        };

        tile.ContentDescription = $"{fileName}, {attachment.ReadableSize}";
        tile.Click += (_, _) => Observe(viewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
        return (tile, warning);
    }

    private static bool IsImage(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" or ".svg";

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
