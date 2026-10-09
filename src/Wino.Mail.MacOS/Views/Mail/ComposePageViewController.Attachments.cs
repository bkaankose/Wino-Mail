using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using UniformTypeIdentifiers;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The attachment tray (Windows AttachmentsTray): a summary row with "Remove all", then one tile per file
/// with its file-type icon, a content-type warning when the bytes don't match the name, double-click to
/// open, Delete to remove, and a "More options" (…) menu that is also the tile's context menu.
/// </summary>
public sealed partial class ComposePageViewController
{
    private NSStackView _attachmentTray = null!;
    private NSView _attachmentHost = null!;
    private NSTextField _attachmentSummary = null!;
    private readonly List<(MailAttachmentViewModel Attachment, PropertyChangedEventHandler Handler)> _attachmentHandlers = [];

    private void BuildAttachmentTray()
    {
        // ---- Summary row: "N attachments · size" and Remove all ----
        var glyph = new WinoIconView(WinoIconGlyph.Attachment, 14, WinoStyle.SecondaryText);
        _attachmentSummary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _attachmentSummary.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _attachmentSummary.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var removeAll = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        removeAll.AttributedTitle = new NSAttributedString(Translator.Composer_RemoveAllAttachments, new NSStringAttributes
        {
            Font = NSFont.SystemFontOfSize(12),
            ForegroundColor = WinoStyle.Accent
        });
        removeAll.Activated += (_, _) => ViewModel.RemoveAllAttachmentsCommand.Execute(null);
        WinoAccessibility.Label(removeAll, Translator.Composer_RemoveAllAttachments);
        var summaryRow = WinoLayout.HStack(8, glyph, _attachmentSummary, WinoLayout.Spacer(), removeAll);
        summaryRow.EdgeInsets = new NSEdgeInsets(8, 16, 0, 16);

        // ---- Tiles ----
        _attachmentTray = WinoLayout.HStack(10);
        _attachmentTray.AccessibilityElement = true;
        _attachmentTray.AccessibilityRole = NSAccessibilityRoles.ListRole;
        WinoAccessibility.Label(_attachmentTray, Translator.Composer_AttachmentsListLabel);
        var trayScroll = new NSScrollView { DocumentView = _attachmentTray, HasHorizontalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
        trayScroll.HeightAnchor.ConstraintEqualTo(54).Active = true;
        _attachmentTray.TopAnchor.ConstraintEqualTo(trayScroll.ContentView.TopAnchor).Active = true;
        _attachmentTray.LeadingAnchor.ConstraintEqualTo(trayScroll.ContentView.LeadingAnchor).Active = true;
        var traySeparator = new WinoSeparator();
        var trayRow = WinoLayout.HStack(10, trayScroll);
        trayRow.EdgeInsets = new NSEdgeInsets(6, 16, 12, 16);
        _attachmentHost = WinoLayout.VStack(0, traySeparator, summaryRow, trayRow);
        foreach (var view in new NSView[] { traySeparator, summaryRow, trayRow })
            view.WidthAnchor.ConstraintEqualTo(_attachmentHost.WidthAnchor).Active = true;
        trayScroll.WidthAnchor.ConstraintEqualTo(trayRow.WidthAnchor, 1, -32).Active = true;
        _attachmentHost.Hidden = true;
    }

    private void BindAttachments()
    {
        NotifyCollectionChangedEventHandler attachments = (_, _) => _ = Dispatcher.ExecuteOnUIThread(UpdateAttachments);
        ViewModel.IncludedAttachments.CollectionChanged += attachments;
        Bindings.Own(new ActionDisposable(() => ViewModel.IncludedAttachments.CollectionChanged -= attachments));
        Bind(nameof(ViewModel.AttachmentsSummary), vm => vm.AttachmentsSummary, summary =>
        {
            _attachmentSummary.StringValue = summary ?? string.Empty;
            _attachmentTray.AccessibilityValue = new NSString(summary ?? string.Empty);
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
            var tile = new AttachmentTile(attachment, Icon(attachment.FileName), CreateAttachmentMenu,
                () => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment)),
                () => ViewModel.RemoveAttachmentCommand.Execute(attachment));
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

    /// <summary>The Finder icon for the file's type.</summary>
    private static NSImage Icon(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.');
        var type = string.IsNullOrEmpty(extension) ? null : UTType.CreateFromExtension(extension);
        return NSWorkspace.SharedWorkspace.GetIcon(type ?? UTTypes.Data);
    }

    // Mirrors the WinUI composer attachment menu: open, save, then remove.
    private NSMenu CreateAttachmentMenu(MailAttachmentViewModel attachment)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Buttons_Open, (_, _) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment))) { Image = WinoIcons.Image(WinoIconGlyph.OpenInNewWindow, 14) });
        menu.AddItem(new NSMenuItem(Translator.Buttons_Save, (_, _) => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment))) { Image = WinoIcons.Image(WinoIconGlyph.Save, 14) });
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.Buttons_Remove, (_, _) => ViewModel.RemoveAttachmentCommand.Execute(attachment)) { Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 14) });
        return menu;
    }

    /// <summary>
    /// One attachment: file icon, name, size (with the caution glyph on a content-type mismatch) and the
    /// "…" menu. A mismatch also outlines the tile. Focusable, so Delete removes it.
    /// </summary>
    private sealed class AttachmentTile : WinoSurfaceView
    {
        private readonly MailAttachmentViewModel _attachment;
        private readonly Func<MailAttachmentViewModel, NSMenu> _menu;
        private readonly Action _open;
        private readonly Action _remove;
        private readonly WinoIconView _warning;
        private readonly NSButton _more;

        public AttachmentTile(MailAttachmentViewModel attachment, NSImage icon, Func<MailAttachmentViewModel, NSMenu> menu, Action open, Action remove)
        {
            _attachment = attachment;
            _menu = menu;
            _open = open;
            _remove = remove;
            Fill = NSColor.ControlBackground;
            CornerRadius = WinoStyle.GroupRadius;
            StrokeWidth = 1;
            TranslatesAutoresizingMaskIntoConstraints = false;

            var image = new NSImageView { Image = icon, ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Size(image, 22, 22);
            var name = WinoStyle.Label(attachment.FileName, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold));
            name.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
            name.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.SecondaryText);
            _warning = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Caution);
            var sizeRow = WinoLayout.HStack(4, _warning, size);
            var text = WinoLayout.VStack(1, name, sizeRow);
            text.Alignment = NSLayoutAttribute.Leading;
            _more = new NSButton
            {
                Bordered = false,
                BezelStyle = NSBezelStyle.Inline,
                Title = string.Empty,
                Image = WinoIcons.Image(WinoIconGlyph.More, 14, null, Translator.Composer_AttachmentMoreOptions),
                ImagePosition = NSCellImagePosition.ImageOnly,
                ContentTintColor = WinoStyle.SecondaryText,
                ToolTip = Translator.Composer_AttachmentMoreOptions,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoLayout.Size(_more, 24, 24);
            WinoAccessibility.Label(_more, $"{Translator.Composer_AttachmentMoreOptions}, {attachment.FileName}");
            _more.Activated += (_, _) => _menu(_attachment).PopUpMenu(null, new CGPoint(0, _more.Bounds.Height + 4), _more);
            var row = WinoLayout.HStack(8, image, text, _more);
            row.EdgeInsets = new NSEdgeInsets(6, 8, 6, 4);
            WinoLayout.Fill(row, this);

            ToolTip = attachment.FileName;
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.GroupRole;
            WinoAccessibility.Label(this, $"{attachment.FileName}, {attachment.ReadableSize}");
            UpdateWarning();
        }

        public void UpdateWarning()
        {
            var mismatch = _attachment.HasContentTypeMismatch;
            _warning.Hidden = !mismatch;
            _warning.ToolTip = mismatch ? _attachment.ContentTypeWarningText : null;
            WinoAccessibility.Label(_warning, mismatch ? _attachment.ContentTypeWarningText : null);
            Stroke = mismatch ? WinoStyle.Caution.ColorWithAlphaComponent(0.6f) : NSColor.Separator;
            AccessibilityHelp = mismatch ? _attachment.ContentTypeWarningText : null;
        }

        public override NSMenu? MenuForEvent(NSEvent theEvent) => _menu(_attachment);

        public override bool AcceptsFirstResponder() => true;

        public override void MouseDown(NSEvent theEvent)
        {
            Window?.MakeFirstResponder(this);
            if (theEvent.ClickCount == 2) _open();
            else base.MouseDown(theEvent);
        }

        public override void KeyDown(NSEvent theEvent)
        {
            // Delete / forward delete remove the attachment (Windows AttachmentsListView_KeyDown); Return or Space opens it.
            switch (theEvent.KeyCode)
            {
                case 51 or 117: _remove(); return;
                case 36 or 49: _open(); return;
            }
            base.KeyDown(theEvent);
        }

        public override bool BecomeFirstResponder()
        {
            NeedsDisplay = true;
            NoteFocusRingMaskChanged();
            return true;
        }

        public override bool ResignFirstResponder()
        {
            NoteFocusRingMaskChanged();
            return true;
        }

        public override void DrawFocusRingMask() => NSBezierPath.FromRoundedRect(Bounds, (nfloat)CornerRadius, (nfloat)CornerRadius).Fill();

        public override CGRect FocusRingMaskBounds => Bounds;
    }
}
