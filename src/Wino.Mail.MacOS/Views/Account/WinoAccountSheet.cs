using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Base for the Wino Account sheets (port of the Windows ContentDialogs): a sheet on the issuing
/// window with 22/24/20 padding and 20pt spacing, the header, the buttons bottom-right in Mac order
/// (secondary, then the Return default), an inline busy spinner, and errors as an alert sheet on
/// top of this sheet (Burak's decision, 7 Oct 2026).
/// </summary>
internal abstract class WinoAccountSheet<TResult>
{
    private readonly TaskCompletionSource<TResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NSWindow? _parent;
    private bool _busy;

    protected WinoAccountSheet(double width)
    {
        Width = width;
        Sheet = new NSWindow(new CGRect(0, 0, width, 300), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
        Sheet.ReleaseWhenClosed(false);
        Body = WinoLayout.VStack(20);
        Body.Alignment = NSLayoutAttribute.Leading;
        Body.EdgeInsets = new NSEdgeInsets(22, 24, 20, 24);
        Spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true, IsDisplayedWhenStopped = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(Spinner, 20, 20);
        SpinnerRow = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        SpinnerRow.AddSubview(Spinner);
        SpinnerCenter = Spinner.CenterXAnchor.ConstraintEqualTo(SpinnerRow.CenterXAnchor);
        SpinnerLeading = Spinner.LeadingAnchor.ConstraintEqualTo(SpinnerRow.LeadingAnchor);
        NSLayoutConstraint.ActivateConstraints([SpinnerCenter, Spinner.TopAnchor.ConstraintEqualTo(SpinnerRow.TopAnchor), Spinner.BottomAnchor.ConstraintEqualTo(SpinnerRow.BottomAnchor)]);
        Secondary = new NSButton { BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b", TranslatesAutoresizingMaskIntoConstraints = false };
        Primary = new NSButton { BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", TranslatesAutoresizingMaskIntoConstraints = false };
        Secondary.Activated += (_, _) => { if (!_busy) Finish(default); };
        Primary.Activated += async (_, _) =>
        {
            if (_busy || !Primary.Enabled) return;
            try { await PrimaryAsync(); }
            catch (Exception exception) { SetBusy(false); await ShowErrorAsync(exception.Message); }
        };
        ButtonRow = WinoLayout.HStack(8, WinoLayout.Spacer(), Secondary, Primary);
    }

    protected double Width { get; }
    protected NSWindow Sheet { get; }
    protected NSStackView Body { get; }
    protected NSProgressIndicator Spinner { get; }
    protected NSView SpinnerRow { get; }
    private NSLayoutConstraint SpinnerCenter { get; }
    private NSLayoutConstraint SpinnerLeading { get; }

    /// <summary>The busy ring is centred (sign-in, registration) or at the leading edge (email confirmation).</summary>
    protected bool SpinnerAtLeadingEdge
    {
        set { SpinnerCenter.Active = !value; SpinnerLeading.Active = value; }
    }
    protected NSStackView ButtonRow { get; }
    protected NSButton Primary { get; }
    protected NSButton Secondary { get; }
    protected bool IsBusy => _busy;

    /// <summary>Runs when the default button or Return is pressed and the sheet is not busy.</summary>
    protected abstract Task PrimaryAsync();

    /// <summary>Views stretched to the content width (fields, text, the busy row, the buttons).</summary>
    protected void AddRow(NSView view, bool stretch = true)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        Body.AddArrangedSubview(view);
        if (stretch) view.WidthAnchor.ConstraintEqualTo(Body.WidthAnchor, 1, -48).Active = true;
    }

    public Task<TResult?> PresentAsync(NSWindow parent)
    {
        _parent = parent;
        AddRow(SpinnerRow);
        AddRow(ButtonRow);
        WinoLayout.Fill(Body, Sheet.ContentView!);
        Body.WidthAnchor.ConstraintEqualTo((nfloat)Width).Active = true;
        if (InitialResponder is { } responder) Sheet.InitialFirstResponder = responder;
        Resize();
        parent.BeginSheet(Sheet, _ =>
        {
            _completion.TrySetResult(default);
            Closed();
            Sheet.Dispose();
        });
        Opened();
        return _completion.Task;
    }

    protected virtual NSView? InitialResponder => null;
    protected virtual void Opened() { }
    protected virtual void Closed() { }

    /// <summary>Fits the sheet height to the content after a mode change.</summary>
    protected void Resize()
    {
        Body.LayoutSubtreeIfNeeded();
        var height = Body.FittingSize.Height;
        Sheet.SetContentSize(new CGSize(Width, height));
    }

    protected void Finish(TResult? result)
    {
        if (_completion.Task.IsCompleted) return;
        _completion.TrySetResult(result);
        _parent?.EndSheet(Sheet);
    }

    /// <summary>Windows SetBusyState: spinner on, fields and both buttons off.</summary>
    protected virtual void SetBusy(bool busy)
    {
        if (_completion.Task.IsCompleted) return;
        _busy = busy;
        SpinnerRow.Hidden = !busy;
        if (busy) Spinner.StartAnimation(null); else Spinner.StopAnimation(null);
        Secondary.Enabled = !busy;
        Primary.Enabled = !busy && CanSubmit;
        foreach (var field in Fields) field.Enabled = !busy;
        Resize();
    }

    protected virtual bool CanSubmit => true;
    protected virtual IEnumerable<NSTextField> Fields => [];

    /// <summary>An alert sheet over this sheet; completes when it is dismissed.</summary>
    protected Task ShowErrorAsync(string message)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alert = new NSAlert { MessageText = Translator.GeneralTitle_Error, InformativeText = message ?? string.Empty, AlertStyle = NSAlertStyle.Warning };
        alert.AddButton(Translator.Buttons_Close);
        alert.BeginSheet(Sheet, () => { completion.TrySetResult(); alert.Dispose(); });
        NSAccessibility.PostNotification(Sheet, new NSString("AXAnnouncementRequested"),
            NSDictionary.FromObjectAndKey(new NSString(message ?? string.Empty), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
        return completion.Task;
    }

#if DEBUG
    /// <summary>Debug snapshots: the busy state or a sample error without a request.</summary>
    internal virtual void ShowDebugState(string state)
    {
        if (state == "busy") SetBusy(true);
        if (state == "error") _ = ShowErrorAsync(Translator.WinoAccount_Error_InvalidCredentials);
    }
#endif

    /// <summary>The 56pt app icon, the 18pt semibold title and the centred 12pt description (max 340).</summary>
    protected static NSStackView Header(string title, string description, out NSTextField titleLabel, out NSTextField descriptionLabel, bool mark = true)
    {
        var icon = new NSImageView { Image = NSImage.ImageNamed("AppIcon") ?? NSApplication.SharedApplication.ApplicationIconImage, ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(icon, 56, 56);
        titleLabel = WinoStyle.Label(title, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        titleLabel.Alignment = NSTextAlignment.Center;
        titleLabel.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        descriptionLabel = WinoStyle.Label(description, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        descriptionLabel.Alignment = NSTextAlignment.Center;
        descriptionLabel.PreferredMaxLayoutWidth = 340;
        descriptionLabel.WidthAnchor.ConstraintLessThanOrEqualTo(340).Active = true;
        var text = WinoLayout.VStack(4, titleLabel, descriptionLabel);
        text.Alignment = NSLayoutAttribute.CenterX;
        var stack = mark ? WinoLayout.VStack(12, icon, text) : text;
        stack.Alignment = NSLayoutAttribute.CenterX;
        icon.Hidden = !mark;
        return stack;
    }

    /// <summary>A 12pt label above a large rounded text field (or a secure field).</summary>
    protected static NSStackView Field(string label, out NSTextField field, string? placeholder = null, bool secure = false)
    {
        field = secure ? new NSSecureTextField() : new NSTextField();
        field.TranslatesAutoresizingMaskIntoConstraints = false;
        field.Bezeled = true;
        field.BezelStyle = NSTextFieldBezelStyle.Rounded;
        field.ControlSize = NSControlSize.Large;
        field.Font = NSFont.SystemFontOfSize(13);
        field.PlaceholderString = placeholder ?? string.Empty;
        field.UsesSingleLineMode = true;
        field.Cell.Scrollable = true;
        WinoAccessibility.Label(field, label);
        var caption = WinoStyle.Label(label, NSFont.SystemFontOfSize(12), WinoStyle.PrimaryText);
        var stack = WinoLayout.VStack(5, caption, field);
        stack.Alignment = NSLayoutAttribute.Leading;
        field.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    /// <summary>A left-aligned accent 12pt link-style button.</summary>
    protected static NSButton Link(string title, Action action)
    {
        var button = new NSButton { Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetButtonType(NSButtonType.MomentaryChange);
        SetLinkTitle(button, title);
        button.Activated += (_, _) => action();
        return button;
    }

    protected static void SetLinkTitle(NSButton button, string title)
    {
        button.Title = title;
        button.AttributedTitle = new NSAttributedString(title, new NSStringAttributes { ForegroundColor = WinoStyle.Accent, Font = NSFont.SystemFontOfSize(12) });
    }

    /// <summary>True while the user is typing in <paramref name="field"/>.</summary>
    protected static bool IsEditing(NSTextField field) => field.CurrentEditor is not null;
}
