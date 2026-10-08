using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Message source viewer (Windows MessageSourceDialog): a resizable sheet, at most 80% of the
/// owner's height, with the raw MIME source in a monospaced, read-only, selectable text view that
/// scrolls both ways, and Copy and Close. Copy puts the source on the pasteboard and confirms
/// inline; Close is the default button and Escape closes too.
/// </summary>
public sealed partial class AppKitDialogService
{
    public Task ShowMessageSourceDialogAsync(string messageSource) => PresentAsync(window =>
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maxHeight = Math.Max(260, (double)window.Frame.Height * 0.8);
        var height = Math.Min(560, maxHeight);
        var width = Math.Min(760, Math.Max(480, (double)window.Frame.Width * 0.7));
        var sheet = new MessageSourceSheet(new CGRect(0, 0, width, height), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = Translator.MessageSourceDialog_Title,
            MinSize = new CGSize(420, 240),
            MaxSize = new CGSize(10000, maxHeight)
        };
        sheet.ReleaseWhenClosed(false);

        var source = messageSource ?? string.Empty;
        var title = WinoStyle.Label(Translator.MessageSourceDialog_Title, WinoStyle.Heading);

        var text = new NSTextView(new CGRect(0, 0, width - 40, height - 120))
        {
            Editable = false,
            Selectable = true,
            RichText = false,
            Font = NSFont.MonospacedSystemFont(12, NSFontWeight.Regular),
            TextColor = NSColor.Label,
            BackgroundColor = NSColor.TextBackground,
            DrawsBackground = true,
            HorizontallyResizable = true,
            VerticallyResizable = true,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            MaxSize = new CGSize(float.MaxValue, float.MaxValue),
            Value = source
        };
        // Header lines and encoded bodies keep their line breaks; long lines scroll horizontally.
        text.TextContainer!.WidthTracksTextView = false;
        text.TextContainer.Size = new CGSize(float.MaxValue, float.MaxValue);
        text.TextContainerInset = new CGSize(6, 6);
        text.AccessibilityLabel = Translator.MessageSourceDialog_Title;
        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            HasHorizontalScroller = true,
            AutohidesScrollers = true,
            BorderType = NSBorderType.BezelBorder,
            DocumentView = text,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        scroll.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Vertical);

        var copied = WinoStyle.Label(Translator.MessageSourceDialog_Copied, WinoStyle.Description, WinoStyle.SecondaryText);
        copied.Hidden = true;
        var copy = new NSButton { Title = Translator.Buttons_Copy, BezelStyle = NSBezelStyle.Rounded };
        copy.Image = WinoIcons.Image(WinoIconGlyph.Copy, 14, null, Translator.Buttons_Copy);
        copy.ImagePosition = NSCellImagePosition.ImageLeading;
        var close = new NSButton { Title = Translator.Buttons_Close, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };

        void Finish()
        {
            if (completion.TrySetResult(true)) window.EndSheet(sheet);
        }

        copy.Activated += (_, _) =>
        {
            var pasteboard = NSPasteboard.GeneralPasteboard;
            pasteboard.ClearContents();
            pasteboard.SetStringForType(source, NSPasteboard.NSPasteboardTypeString);
            copied.Hidden = false;
            NSAccessibility.PostNotification(copied, new NSString("AXAnnouncementRequested"),
                NSDictionary.FromObjectAndKey(new NSString(Translator.MessageSourceDialog_Copied), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
        };
        close.Activated += (_, _) => Finish();
        sheet.Cancelled = Finish;

        var buttons = WinoLayout.HStack(WinoStyle.Space2, copied, WinoLayout.Spacer(), copy, close);
        var stack = WinoLayout.VStack(WinoStyle.Space3, title, scroll, buttons);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in new NSView[] { scroll, buttons })
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        WinoLayout.Fill(stack, sheet.ContentView!);
        sheet.InitialFirstResponder = text;

        window.BeginSheet(sheet, _ => { completion.TrySetResult(true); sheet.Dispose(); });
        return completion.Task;
    });

    /// <summary>Escape (cancelOperation:) closes the message source sheet.</summary>
    private sealed class MessageSourceSheet(CGRect contentRect, NSWindowStyle style, NSBackingStore backing, bool defer)
        : NSWindow(contentRect, style, backing, defer)
    {
        public Action? Cancelled { get; set; }

        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => Cancelled?.Invoke();

        // The read-only text view keeps the focus and maps Escape to completion, so catch it first.
        public override bool PerformKeyEquivalent(NSEvent theEvent)
        {
            const ushort EscapeKeyCode = 53;
            if (theEvent.Type == NSEventType.KeyDown && theEvent.KeyCode == EscapeKeyCode)
            {
                Cancelled?.Invoke();
                return true;
            }
            return base.PerformKeyEquivalent(theEvent);
        }
    }
}
