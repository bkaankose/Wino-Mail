using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.MacOS.Views.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Companion;

/// <summary>Shared building blocks of the companion popover.</summary>
internal static class CompanionStyle
{
    public const double Width = 400;
    public const double MinHeight = 460;
    public const double MaxHeight = 720;
    public const double SideInset = 12;

    /// <summary>The Windows CompanionSubtleBackgroundBrush.</summary>
    public static NSColor SubtleFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06));

    /// <summary>The Windows CompanionCardBackgroundBrush.</summary>
    public static NSColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.85), WinoStyle.Hex(0xFFFFFF, 0.06));

    public static NSFont SectionHeader => NSFont.SystemFontOfSize(12, NSFontWeight.Bold);

    /// <summary>A borderless icon button that shows its bezel on hover, like the Windows SubtleButtonStyle.</summary>
    public static NSButton IconButton(WinoIconGlyph glyph, string tooltip, Action clicked, double size = 28, double glyphSize = 15)
    {
        var button = new NSButton
        {
            Title = string.Empty,
            Image = WinoIcons.Image(glyph, glyphSize, accessibilityDescription: tooltip),
            ImagePosition = NSCellImagePosition.ImageOnly,
            BezelStyle = NSBezelStyle.Recessed,
            ShowsBorderOnlyWhileMouseInside = true,
            ToolTip = tooltip,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, tooltip);
        WinoLayout.Size(button, size, size);
        button.Activated += (_, _) => clicked();
        return button;
    }

    /// <summary>A rounded push button tinted with the Wino accent (Windows AccentButtonStyle).</summary>
    public static NSButton AccentButton(string title, Action clicked)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, BezelColor = WinoStyle.Accent, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => clicked();
        return button;
    }

    /// <summary>A small capsule with text, used for the countdown and online badges and the unread count.</summary>
    public static WinoSurfaceView Pill(NSTextField label, NSColor fill, double horizontal = 9, double vertical = 2, WinoIconView? icon = null)
    {
        var pill = new CompanionPill { Fill = fill };
        NSView content = label;
        if (icon is not null) content = WinoLayout.HStack(5, icon, label);
        WinoLayout.Fill(content, pill, vertical, horizontal, vertical, horizontal);
        pill.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        pill.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        label.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        return pill;
    }

    public static NSColor OnAccent => NSColor.White;
}

/// <summary>A surface whose corners stay fully rounded at any height.</summary>
internal sealed class CompanionPill : WinoSurfaceView
{
    public override void Layout()
    {
        base.Layout();
        var radius = Bounds.Height / 2;
        if (Math.Abs(CornerRadius - radius) > 0.1) CornerRadius = radius;
    }
}

/// <summary>
/// One unread message (Windows CompactSingleMailItemTemplate): the sender's initials, sender,
/// time, subject and preview. Clicking the row opens the message; Archive and Mark as read appear
/// on hover at the trailing edge as on Windows.
/// </summary>
internal sealed class CompanionMailRowView : WinoPressableView
{
    private readonly NSStackView _hoverActions;
    private NSTrackingArea? _tracking;

    public CompanionMailRowView(MailItemViewModel mail, Action open, Action archive, Action markRead)
    {
        CornerRadius = WinoStyle.ControlRadius;
        WinoLayout.Size(this, height: 64);
        var picture = new WinoContactPicture(32);
        picture.SetIdentity(string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName, mail.FromAddress);

        var sender = WinoStyle.Label(string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName, WinoStyle.BodyStrong);
        var time = WinoStyle.Label(MailRowMapper.FormatListDate(mail.CreationDate), WinoStyle.Caption, WinoStyle.SecondaryText);
        time.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        time.SetContentHuggingPriorityForOrientation(760, NSLayoutConstraintOrientation.Horizontal);
        var subject = WinoStyle.Label(string.IsNullOrWhiteSpace(mail.Subject) ? Translator.MailItemNoSubject : mail.Subject, WinoStyle.Body, WinoStyle.Accent);
        var preview = WinoStyle.Label(mail.PreviewText, WinoStyle.Caption, WinoStyle.SecondaryText);
        var top = WinoLayout.HStack(6, sender, WinoLayout.Spacer(), time);
        var text = WinoLayout.VStack(1, top, subject, preview);
        text.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in new NSView[] { top, subject, preview })
            view.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        sender.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        subject.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        preview.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(10, picture, text);
        WinoLayout.Fill(row, this, 8, 8, 8, 8);

        var archiveButton = CompanionStyle.IconButton(WinoIconGlyph.Archive, Translator.Companion_Archive, archive);
        var readButton = CompanionStyle.IconButton(WinoIconGlyph.MarkRead, Translator.Companion_MarkRead, markRead);
        _hoverActions = WinoLayout.HStack(2, archiveButton, readButton);
        _hoverActions.EdgeInsets = new NSEdgeInsets(3, 3, 3, 3);
        var actionsBackground = new WinoSurfaceView
        {
            Fill = NSColor.WindowBackground,
            Stroke = WinoStyle.Separator,
            CornerRadius = WinoStyle.ControlRadius
        };
        WinoLayout.Fill(_hoverActions, actionsBackground);
        AddSubview(actionsBackground);
        NSLayoutConstraint.ActivateConstraints(
        [
            actionsBackground.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            actionsBackground.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
        actionsBackground.Hidden = true;
        _hoverActionsHost = actionsBackground;

        Clicked += (_, _) => open();
        AccessibilityLabel = $"{sender.StringValue}, {subject.StringValue}";
        // VoiceOver reaches the hover actions as custom actions.
        AccessibilityCustomActions =
        [
            new NSAccessibilityCustomAction(Translator.Companion_Archive, () => { archive(); return true; }),
            new NSAccessibilityCustomAction(Translator.Companion_MarkRead, () => { markRead(); return true; })
        ];
    }

    private readonly WinoSurfaceView _hoverActionsHost;

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent)
    {
        Fill = CompanionStyle.SubtleFill;
        _hoverActionsHost.Hidden = false;
    }

    public override void MouseExited(NSEvent theEvent)
    {
        Fill = null;
        _hoverActionsHost.Hidden = true;
    }
}

/// <summary>One My Day task: the round checkbox, title, and either Overdue or the list and due date.</summary>
internal sealed class CompanionTaskRowView : NSView
{
    public CompanionTaskRowView(TaskItemViewModel task, Action toggle)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var checkbox = new WinoRoundCheckbox(18) { Checked = task.IsCompleted, Label = task.Title };
        checkbox.Toggled += (_, _) => toggle();

        var title = WinoStyle.Label(task.Title, WinoStyle.BodyStrong, task.IsCompleted ? WinoStyle.SecondaryText : WinoStyle.PrimaryText);
        if (task.IsCompleted)
        {
            title.AttributedStringValue = new NSAttributedString(task.Title ?? string.Empty, new NSStringAttributes
            {
                Font = WinoStyle.BodyStrong,
                ForegroundColor = WinoStyle.SecondaryText,
                StrikethroughStyle = (int)NSUnderlineStyle.Single
            });
        }
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        NSTextField detail;
        if (task.IsOverdue)
            detail = WinoStyle.Label(Translator.Companion_TaskOverdue, NSFont.SystemFontOfSize((nfloat)11.5, NSFontWeight.Semibold), WinoStyle.Critical);
        else
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(task.ListName)) parts.Add(task.ListName);
            if (task.HasDueDate && !string.IsNullOrWhiteSpace(task.DueDisplayText)) parts.Add(task.DueDisplayText);
            detail = WinoStyle.Label(string.Join("  ", parts), WinoStyle.Description, WinoStyle.TertiaryText);
            detail.Hidden = parts.Count == 0;
        }
        detail.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var text = WinoLayout.VStack(1, title, detail);
        var row = WinoLayout.HStack(10, checkbox, text);
        WinoLayout.Fill(row, this, 5, 4, 5, 4);
        HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;
    }
}

/// <summary>A favorite contact: the picture with an unread badge and the first name (Windows: 56pt wide).</summary>
internal sealed class CompanionContactButton : WinoPressableView
{
    public CompanionContactButton(AccountContactViewModel contact, NSImage? image, Action clicked)
    {
        CornerRadius = WinoStyle.ControlRadius;
        WinoLayout.Size(this, 56);
        var picture = new WinoContactPicture(34);
        picture.SetIdentity(contact.Name, contact.Address);
        picture.Image = image;
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(picture);
        NSLayoutConstraint.ActivateConstraints(
        [
            picture.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            picture.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            picture.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
            picture.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor)
        ]);
        if (contact.HasUnread)
        {
            var count = WinoStyle.Label(contact.UnreadCountText, NSFont.SystemFontOfSize(10, NSFontWeight.Bold), CompanionStyle.OnAccent);
            count.Alignment = NSTextAlignment.Center;
            var badge = CompanionStyle.Pill(count, WinoStyle.Accent, 4, 0);
            badge.WidthAnchor.ConstraintGreaterThanOrEqualTo(16).Active = true;
            host.AddSubview(badge);
            NSLayoutConstraint.ActivateConstraints(
            [
                badge.TopAnchor.ConstraintEqualTo(host.TopAnchor, -2),
                badge.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor, 4)
            ]);
        }
        var name = WinoStyle.Label(string.IsNullOrWhiteSpace(contact.FirstName) ? contact.Name : contact.FirstName, NSFont.SystemFontOfSize(11), WinoStyle.SecondaryText);
        name.Alignment = NSTextAlignment.Center;
        name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var stack = WinoLayout.VStack(3, host, name);
        stack.Alignment = NSLayoutAttribute.CenterX;
        WinoLayout.Fill(stack, this, 4, 2, 4, 2);
        ToolTip = contact.Name;
        AccessibilityLabel = contact.HasUnread ? $"{contact.Name}, {contact.UnreadCountText}" : contact.Name;
        Clicked += (_, _) => clicked();
    }

    protected override void OnPressedChanged() => Fill = IsPressed ? CompanionStyle.SubtleFill : null;
}

/// <summary>A footer button that opens one app mode with its colorful app-mode glyph.</summary>
internal sealed class CompanionModeButton : WinoPressableView
{
    private NSTrackingArea? _tracking;

    public CompanionModeButton(WinoIconGlyph glyph, string title, Action clicked)
    {
        CornerRadius = WinoStyle.ControlRadius;
        WinoLayout.Size(this, height: 40);
        var icon = new WinoIconView(glyph, 26) { Colorful = true };
        AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            icon.WidthAnchor.ConstraintEqualTo(26),
            icon.HeightAnchor.ConstraintEqualTo(26)
        ]);
        ToolTip = title;
        AccessibilityLabel = title;
        Clicked += (_, _) => clicked();
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) => Fill = CompanionStyle.SubtleFill;
    public override void MouseExited(NSEvent theEvent) => Fill = null;
}
