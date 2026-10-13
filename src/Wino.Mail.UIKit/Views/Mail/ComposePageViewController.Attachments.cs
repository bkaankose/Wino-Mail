using System.Collections.Specialized;
using System.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The attachment tray (Mac Attachments partial, Windows AttachmentsTray): a summary row with "Remove all", then one
/// tile per file in a horizontally scrolling row with its type glyph, a content-type warning when the bytes don't match
/// the name, a tap to open, and a "More options" menu (open, save, remove) that is also the tile's context menu.
/// </summary>
public sealed partial class ComposePageViewController
{
    private readonly UIStackView _attachmentTray = WinoLayout.HStack(10);
    private readonly UIView _attachmentHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UILabel _attachmentSummary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly List<(MailAttachmentViewModel Attachment, PropertyChangedEventHandler Handler)> _attachmentHandlers = [];

    private UIView BuildAttachmentTray()
    {
        // ---- Summary row: "N attachments · size" and Remove all ----
        var glyph = new WinoIconView(WinoIconGlyph.Attachment, 14, WinoStyle.SecondaryText);
        _attachmentSummary.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var removeConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        removeConfiguration.Title = Translator.Composer_RemoveAllAttachments;
        removeConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        removeConfiguration.BaseForegroundColor = WinoStyle.Accent;
        var removeAll = new UIButton { Configuration = removeConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        removeAll.PrimaryActionTriggered += (_, _) => { ViewModel.RemoveAllAttachmentsCommand.Execute(null); ScheduleAutosave(); };
        var summaryRow = WinoLayout.HStack(8, glyph, _attachmentSummary, WinoLayout.Spacer(), removeAll);
        WinoLayout.Insets(summaryRow, 6, 16, 0, 10);

        // ---- Tiles ----
        _attachmentTray.AccessibilityLabel = Translator.Composer_AttachmentsListLabel;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false, AlwaysBounceHorizontal = true };
        scroll.AddSubview(_attachmentTray);
        _attachmentTray.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _attachmentTray.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor, 16),
            _attachmentTray.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor, -16),
            _attachmentTray.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor),
            _attachmentTray.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor),
            _attachmentTray.HeightAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.HeightAnchor),
            scroll.HeightAnchor.ConstraintEqualTo(56)
        ]);

        var stack = WinoLayout.VStack(6, summaryRow, scroll, new WinoSeparator());
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, _attachmentHost, 0, 0, 0, 0);
        return _attachmentHost;
    }

    private void BindAttachments()
    {
        NotifyCollectionChangedEventHandler attachments = (_, _) => _ = Dispatcher.ExecuteOnUIThread(UpdateAttachments);
        ViewModel.IncludedAttachments.CollectionChanged += attachments;
        Bindings.Own(new ActionDisposable(() => ViewModel.IncludedAttachments.CollectionChanged -= attachments));
        Bind(nameof(ViewModel.AttachmentsSummary), vm => vm.AttachmentsSummary, summary =>
        {
            _attachmentSummary.Text = summary ?? string.Empty;
            _attachmentTray.AccessibilityValue = summary ?? string.Empty;
        });
        UpdateAttachments();
    }

    private void UpdateAttachments()
    {
        if (Bindings.IsDisposed) return;
        UnsubscribeAttachments();
        foreach (var view in _attachmentTray.ArrangedSubviews)
        {
            _attachmentTray.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        foreach (var attachment in ViewModel.IncludedAttachments.ToArray())
        {
            if (attachment is null) continue;
            var tile = new AttachmentTile(attachment, CreateAttachmentMenu,
                () => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment)));
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(MailAttachmentViewModel.ContentTypeDetection) or nameof(MailAttachmentViewModel.HasContentTypeMismatch)
                    or nameof(MailAttachmentViewModel.ContentTypeWarningText) or null)
                    _ = Dispatcher.ExecuteOnUIThread(tile.UpdateWarning);
            };
            attachment.PropertyChanged += handler;
            _attachmentHandlers.Add((attachment, handler));
            _attachmentTray.AddArrangedSubview(tile);
        }
        _attachmentHost.Hidden = ViewModel.IncludedAttachments.Count == 0;
    }

    private void UnsubscribeAttachments()
    {
        foreach (var (attachment, handler) in _attachmentHandlers) attachment.PropertyChanged -= handler;
        _attachmentHandlers.Clear();
    }

    private void DisposeAttachments() => UnsubscribeAttachments();

    // Mirrors the WinUI composer attachment menu: open, save, then remove.
    private UIMenu CreateAttachmentMenu(MailAttachmentViewModel attachment)
    {
        var remove = EditorFormatToolbar.Action(Translator.Buttons_Remove, () =>
        {
            ViewModel.RemoveAttachmentCommand.Execute(attachment);
            ScheduleAutosave();
        }, image: WinoIcons.Image(WinoIconGlyph.Dismiss, 16), destructive: true);
        return UIMenu.Create(attachment.FileName ?? string.Empty,
        [
            EditorFormatToolbar.Action(Translator.Buttons_Open, () => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment)), image: WinoIcons.Image(WinoIconGlyph.OpenInNewWindow, 16)),
            EditorFormatToolbar.Action(Translator.Buttons_Save, () => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment)), image: WinoIcons.Image(WinoIconGlyph.Save, 16)),
            EditorFormatToolbar.Inline([remove])
        ]);
    }

    /// <summary>
    /// One attachment: type glyph, name, size (with the caution glyph on a content-type mismatch) and the "…" menu.
    /// A mismatch also outlines the tile.
    /// </summary>
    private sealed class AttachmentTile : WinoSurfaceView
    {
        private readonly MailAttachmentViewModel _attachment;
        private readonly WinoIconView _warning = new(WinoIconGlyph.Warning, 12, WinoStyle.Caution);
        private readonly UILabel? _nameLabel;

        public AttachmentTile(MailAttachmentViewModel attachment, Func<MailAttachmentViewModel, UIMenu> menu, Action open)
        {
            _attachment = attachment;
            Fill = WinoStyle.GroupFill;
            CornerRadius = WinoStyle.GroupRadius;
            StrokeWidth = 1;

            var fileName = attachment.FileName ?? string.Empty;
            var icon = new WinoIconView(EditorDialogs.IsImage(fileName) ? WinoIconGlyph.Image : WinoIconGlyph.Document, 22);
            var name = WinoStyle.Label(fileName, WinoStyle.Fixed(13, UIFontWeight.Semibold));
            name.LineBreakMode = UILineBreakMode.MiddleTruncation;
            name.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.SecondaryText);
            var sizeRow = WinoLayout.HStack(4, _warning, size);
            var text = WinoLayout.VStack(1, name, sizeRow);

            var configuration = UIButtonConfiguration.PlainButtonConfiguration;
            configuration.Image = WinoIcons.Image(WinoIconGlyph.More, 14, null, Translator.Composer_AttachmentMoreOptions);
            configuration.BaseForegroundColor = WinoStyle.SecondaryText;
            var more = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, ShowsMenuAsPrimaryAction = true };
            more.Menu = menu(attachment);
            more.AccessibilityLabel = $"{Translator.Composer_AttachmentMoreOptions}, {fileName}";
            WinoLayout.Size(more, 32, 44);

            var row = WinoLayout.HStack(8, icon, text, more);
            WinoLayout.Insets(row, 6, 10, 6, 2);
            WinoLayout.Fill(row, this);
            AddGestureRecognizer(new UITapGestureRecognizer(open));
            AddInteraction(new UIContextMenuInteraction(new TileMenu(() => menu(attachment))));

            // VoiceOver reads the name (activating it opens the file) and the "More options" button separately.
            ShouldGroupAccessibilityChildren = true;
            name.AccessibilityTraits = UIAccessibilityTrait.Button;
            name.AccessibilityLabel = $"{fileName}, {attachment.ReadableSize}";
            _nameLabel = name;
            UpdateWarning();
        }

        public void UpdateWarning()
        {
            var mismatch = _attachment.HasContentTypeMismatch;
            _warning.Hidden = !mismatch;
            Stroke = mismatch ? WinoStyle.Caution.ColorWithAlpha(0.6f) : WinoStyle.GroupStroke;
            if (_nameLabel is not null) _nameLabel.AccessibilityHint = mismatch ? _attachment.ContentTypeWarningText : null;
        }

        private sealed class TileMenu(Func<UIMenu> menu) : UIContextMenuInteractionDelegate
        {
            public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CoreGraphics.CGPoint location)
                => UIContextMenuConfiguration.Create(null, null, _ => menu());
        }
    }
}
